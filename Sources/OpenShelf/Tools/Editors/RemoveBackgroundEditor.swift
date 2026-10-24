import CoreImage
import SwiftUI
import Vision

enum BackgroundRemovalError: LocalizedError, Equatable {
    case noSubject
    case failed

    var errorDescription: String? {
        switch self {
        case .noSubject:
            "No clear subject was found. Remove Background works best on photos of people, animals or objects against a background."
        case .failed:
            "The background couldn’t be removed from this image."
        }
    }
}

/// Cuts the subject out of a photo with Vision's foreground segmentation.
enum BackgroundRemover {
    /// `image` with everything but the detected subjects made transparent,
    /// at the original size.
    static func removeBackground(from image: CGImage) throws -> CGImage {
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: image)
        try handler.perform([request])
        guard let observation = request.results?.first, !observation.allInstances.isEmpty else {
            throw BackgroundRemovalError.noSubject
        }
        let masked = try observation.generateMaskedImage(
            ofInstances: observation.allInstances,
            from: handler,
            croppedToInstancesExtent: false
        )
        let output = CIImage(cvPixelBuffer: masked)
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let result = ImageContexts.standard.createCGImage(output, from: output.extent, format: .RGBA8, colorSpace: space) else {
            throw BackgroundRemovalError.failed
        }
        return result
    }

    /// Saves `cutout`, `image` without its background, next to it as
    /// "<name> No Background.png".
    static func save(_ cutout: CGImage, of image: EditableImage) throws -> URL {
        try image.save(cutout, suffix: "No Background", as: .png)
    }

    /// Cuts out and saves each image; images with no clear subject are left
    /// alone and counted.
    static func run(_ images: [EditableImage]) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "saved", unchangedLabel: "without a subject")
        for image in images {
            // Each cutout is a full-size copy; let it go before the next.
            try autoreleasepool {
                do {
                    outcome.saved.append(try save(try removeBackground(from: image.cgImage), of: image))
                } catch BackgroundRemovalError.noSubject {
                    outcome.unchanged += 1
                }
            }
        }
        return outcome
    }
}

/// Removes the background from a photo, with a before/after preview. Given
/// several photos, a filmstrip previews each one's result, and saving cuts
/// out all of them.
struct RemoveBackgroundEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 520, height: count > 1 ? 560 + EditorFilmstrip.height : 560)
    }

    let images: [EditableImage]
    let actions: EditorActions
    @State private var index = 0
    /// The shown image without its background. Only the shown image's is
    /// kept, so many large photos don't pile up in memory.
    @State private var result: CGImage?
    @State private var errorMessage: String?
    /// Whether a subject was found in each image, once it has been looked at.
    @State private var subjects: [Bool?]
    @State private var showsOriginal = false
    @State private var isSaving = false
    @State private var thumbnails: [CGImage]

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        _subjects = State(initialValue: images.map { _ in nil })
        _thumbnails = State(initialValue: images.count > 1 ? images.map { $0.thumbnail() } : [])
    }

    private var image: EditableImage { images[index] }
    private var isBatch: Bool { images.count > 1 }

    /// Images saving would cut out: all but those already found to have no subject.
    private var edited: [Bool] { subjects.map { $0 != false } }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: "Remove Background",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: isBatch ? !isSaving && editedCount > 0 : result != nil && !isSaving,
            actions: actions,
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                ZStack {
                    Checkerboard()
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                    if let errorMessage {
                        Image(decorative: image.cgImage, scale: 1)
                            .resizable()
                            .interpolation(.high)
                            .scaledToFit()
                            .padding(8)
                            .opacity(0.35)
                        VStack(spacing: 10) {
                            Image(systemName: "person.crop.rectangle.badge.xmark")
                                .font(.system(size: 30, weight: .light))
                            Text(errorMessage)
                                .font(.system(size: 13))
                                .multilineTextAlignment(.center)
                                .frame(maxWidth: 340)
                        }
                        .foregroundStyle(.secondary)
                        .padding(20)
                        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.regularMaterial))
                    } else if let shown = showsOriginal ? image.cgImage : result {
                        Image(decorative: shown, scale: 1)
                            .resizable()
                            .interpolation(.high)
                            .scaledToFit()
                            .padding(8)
                            .accessibilityLabel(showsOriginal ? "Original image" : "Image without background")
                    } else {
                        VStack(spacing: 10) {
                            ProgressView()
                            Text("Finding the subject…")
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                        }
                        .padding(18)
                        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.regularMaterial))
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: images.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited
                    )
                }

                Picker("Preview", selection: $showsOriginal) {
                    Text("Result").tag(false)
                    Text("Original").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 220)
                .disabled(result == nil)

                Text(result == nil ? " " : "Saved as a PNG with a transparent background.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        }
        // Restarts for each image shown, dropping the last one's result.
        .task(id: index) { await detect() }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Save" }
        if isSaving { return "Saving…" }
        switch editedCount {
        case 0: return "Save Images"
        case 1: return "Save 1 Image"
        default: return "Save \(editedCount) Images"
        }
    }

    private func detect() async {
        result = nil
        errorMessage = nil
        let shown = index
        let source = image.cgImage
        let outcome = await Task.detached { Result { try BackgroundRemover.removeBackground(from: source) } }.value
        if case .failure(BackgroundRemovalError.noSubject) = outcome {
            subjects[shown] = false
        } else if case .success = outcome {
            subjects[shown] = true
        }
        // Another image is showing now.
        guard !Task.isCancelled else { return }
        switch outcome {
        case .success(let image): result = image
        case .failure(let error): errorMessage = error.localizedDescription
        }
    }

    private func save() {
        guard isBatch else {
            guard let result else { return }
            isSaving = true
            do {
                actions.saved([try BackgroundRemover.save(result, of: image)])
            } catch {
                isSaving = false
                actions.failed(error)
            }
            return
        }
        isSaving = true
        let images = images
        Task.detached {
            let result = Result { try BackgroundRemover.run(images) }
            await MainActor.run {
                isSaving = false
                switch result {
                case .success(let outcome):
                    actions.finished(outcome)
                case .failure(let error):
                    actions.failed(error)
                }
            }
        }
    }
}

/// The grey-and-white squares that show transparency.
struct Checkerboard: View {
    var size: CGFloat = 10

    var body: some View {
        Canvas { context, canvasSize in
            context.fill(Path(CGRect(origin: .zero, size: canvasSize)), with: .color(Color(white: 0.92)))
            let columns = Int(canvasSize.width / size) + 1
            let rows = Int(canvasSize.height / size) + 1
            var path = Path()
            for row in 0..<rows {
                for column in 0..<columns where (row + column).isMultiple(of: 2) {
                    path.addRect(CGRect(x: CGFloat(column) * size, y: CGFloat(row) * size, width: size, height: size))
                }
            }
            context.fill(path, with: .color(Color(white: 0.8)))
        }
        .accessibilityHidden(true)
    }
}
