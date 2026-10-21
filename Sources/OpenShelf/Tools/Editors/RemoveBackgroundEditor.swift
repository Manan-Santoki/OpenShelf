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
        guard let result = CIContext().createCGImage(output, from: output.extent, format: .RGBA8, colorSpace: space) else {
            throw BackgroundRemovalError.failed
        }
        return result
    }
}

/// Removes the background from a photo, with a before/after preview.
struct RemoveBackgroundEditor: View {
    static let windowSize = CGSize(width: 520, height: 560)

    let image: EditableImage
    let actions: EditorActions
    @State private var result: CGImage?
    @State private var errorMessage: String?
    @State private var showsOriginal = false
    @State private var isSaving = false

    var body: some View {
        EditorChrome(
            title: "Remove Background",
            primaryTitle: "Save",
            isPrimaryEnabled: result != nil && !isSaving,
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
        .task { await detect() }
    }

    private func detect() async {
        let source = image.cgImage
        let outcome = await Task.detached { Result { try BackgroundRemover.removeBackground(from: source) } }.value
        switch outcome {
        case .success(let image): result = image
        case .failure(let error): errorMessage = error.localizedDescription
        }
    }

    private func save() {
        guard let result else { return }
        isSaving = true
        do {
            let url = try image.save(result, suffix: "No Background", as: .png)
            actions.saved([url])
        } catch {
            isSaving = false
            actions.failed(error)
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
