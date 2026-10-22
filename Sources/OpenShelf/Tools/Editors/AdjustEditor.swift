import CoreImage
import SwiftUI

/// Colour and orientation adjustments. Slider values are percentages where 0
/// means "no change".
struct AdjustSettings: Equatable, Sendable {
    var exposure = 0.0
    var brightness = 0.0
    var contrast = 0.0
    var saturation = 0.0
    var warmth = 0.0
    var vibrance = 0.0
    var sharpness = 0.0
    var vignette = 0.0
    /// Clockwise quarter turns.
    var quarterTurns = 0
    var flipsHorizontally = false
    var flipsVertically = false

    /// Whether these settings change the image at all. A slider that reads 0
    /// is no change, and turns and flips that cancel out (four turns, or both
    /// flips plus a half turn) leave the image as it was.
    var changesImage: Bool {
        let sliders: [Double] = [exposure, brightness, contrast, saturation, warmth, vibrance, sharpness, vignette]
        if sliders.contains(where: { $0.rounded() != 0 }) { return true }
        return !keepsOrientation
    }

    /// Whether the turns and flips together leave the image the way up it was.
    var keepsOrientation: Bool {
        // One flip on its own always mirrors the image.
        guard flipsHorizontally == flipsVertically else { return false }
        // Flipping both ways is the same as a half turn.
        let turns: Int = quarterTurns + (flipsHorizontally ? 2 : 0)
        return ((turns % 4) + 4) % 4 == 0
    }
}

/// Applies `AdjustSettings` with Core Image.
enum AdjustRenderer {
    static let context = CIContext(options: [.cacheIntermediates: false])

    static func apply(_ settings: AdjustSettings, to input: CIImage) -> CIImage {
        let extent = input.extent
        var image = input

        if settings.exposure != 0 {
            image = image.applyingFilter("CIExposureAdjust", parameters: [kCIInputEVKey: settings.exposure / 100 * 2])
        }
        if settings.brightness != 0 || settings.contrast != 0 || settings.saturation != 0 {
            image = image.applyingFilter("CIColorControls", parameters: [
                kCIInputBrightnessKey: settings.brightness / 100 * 0.25,
                kCIInputContrastKey: 1 + settings.contrast / 100 * 0.5,
                kCIInputSaturationKey: 1 + settings.saturation / 100,
            ])
        }
        if settings.warmth != 0 {
            // A higher assumed neutral temperature warms the image.
            image = image.applyingFilter("CITemperatureAndTint", parameters: [
                "inputNeutral": CIVector(x: 6500 + settings.warmth / 100 * 2000, y: 0),
                "inputTargetNeutral": CIVector(x: 6500, y: 0),
            ])
        }
        if settings.vibrance != 0 {
            image = image.applyingFilter("CIVibrance", parameters: ["inputAmount": settings.vibrance / 100])
        }
        if settings.sharpness != 0 {
            image = image.applyingFilter("CISharpenLuminance", parameters: [kCIInputSharpnessKey: settings.sharpness / 100 * 2])
        }
        if settings.vignette != 0 {
            image = image.applyingFilter("CIVignetteEffect", parameters: [
                kCIInputCenterKey: CIVector(x: extent.midX, y: extent.midY),
                kCIInputRadiusKey: max(extent.width, extent.height) * 0.7,
                kCIInputIntensityKey: settings.vignette / 100,
            ])
        }
        image = image.cropped(to: extent)

        if settings.flipsHorizontally {
            image = image.transformed(by: CGAffineTransform(scaleX: -1, y: 1))
        }
        if settings.flipsVertically {
            image = image.transformed(by: CGAffineTransform(scaleX: 1, y: -1))
        }
        let turns = ((settings.quarterTurns % 4) + 4) % 4
        if turns > 0 {
            // Core Image is y-up, so a negative angle turns clockwise.
            image = image.transformed(by: CGAffineTransform(rotationAngle: -CGFloat(turns) * .pi / 2))
        }
        return image.transformed(by: CGAffineTransform(translationX: -image.extent.minX, y: -image.extent.minY))
    }

    /// Renders `cgImage` with `settings`, optionally scaled down to `maxDimension`.
    static func render(_ cgImage: CGImage, with settings: AdjustSettings, maxDimension: CGFloat? = nil) -> CGImage? {
        var input = CIImage(cgImage: cgImage)
        if let maxDimension {
            let scale = min(1, maxDimension / CGFloat(max(cgImage.width, cgImage.height)))
            if scale < 1 {
                input = input.applyingFilter("CILanczosScaleTransform", parameters: [kCIInputScaleKey: scale])
            }
        }
        let output = apply(settings, to: input)
        let colorSpace = cgImage.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!
        return context.createCGImage(output, from: output.extent, format: .RGBA8, colorSpace: colorSpace)
    }
}

/// The "Edit" tool: exposure, colour and orientation adjustments with a live
/// preview. Given several images, a filmstrip moves between them, and one set
/// of adjustments can apply to all of them or each can have its own.
struct AdjustEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 480, height: count > 1 ? 700 + EditorFilmstrip.height : 700)
    }

    let images: [EditableImage]
    let actions: EditorActions

    @State private var index = 0
    /// Each image's adjustments.
    @State private var allSettings: [AdjustSettings]
    /// One set of adjustments for every image.
    @State private var sameForAll = true
    @State private var preview: CGImage?
    @State private var previewTask: Task<Void, Never>?
    @State private var isSaving = false
    @State private var thumbnails: [CGImage]

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        _allSettings = State(initialValue: images.map { _ in AdjustSettings() })
        _thumbnails = State(initialValue: images.count > 1 ? images.map { $0.thumbnail() } : [])
    }

    private var image: EditableImage { images[index] }
    private var isBatch: Bool { images.count > 1 }

    /// The shown image's adjustments. With Same for All on, changing them
    /// changes every image's.
    private var settings: AdjustSettings {
        get { allSettings[index] }
        nonmutating set {
            guard isBatch, sameForAll else {
                allSettings[index] = newValue
                return
            }
            allSettings = images.map { _ in newValue }
        }
    }

    private var settingsBinding: Binding<AdjustSettings> {
        Binding(get: { settings }, set: { settings = $0 })
    }

    /// Images the adjustments would change.
    private var edited: [Bool] { allSettings.map(\.changesImage) }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: isBatch ? "Edit Images" : "Edit Image",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: !isSaving && editedCount > 0,
            actions: actions,
            // Resets the shown image, or every image while they share settings.
            onReset: { settings = AdjustSettings() },
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                previewView
                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: images.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll
                    )
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on spreads the shown image's settings to the rest.
                        if isOn { settings = allSettings[index] }
                    }
                }
                transformButtons
                VStack(spacing: 6) {
                    EditorSlider(label: "Exposure", value: settingsBinding.exposure, range: -100...100, unit: "")
                    EditorSlider(label: "Brightness", value: settingsBinding.brightness, range: -100...100, unit: "")
                    EditorSlider(label: "Contrast", value: settingsBinding.contrast, range: -100...100, unit: "")
                    EditorSlider(label: "Saturation", value: settingsBinding.saturation, range: -100...100, unit: "")
                    EditorSlider(label: "Warmth", value: settingsBinding.warmth, range: -100...100, unit: "")
                    EditorSlider(label: "Vibrance", value: settingsBinding.vibrance, range: -100...100, unit: "")
                    EditorSlider(label: "Sharpness", value: settingsBinding.sharpness, range: 0...100, unit: "%")
                    EditorSlider(label: "Vignette", value: settingsBinding.vignette, range: 0...100, unit: "%")
                }
            }
        }
        .onAppear { renderPreview(after: .zero) }
        .onChange(of: settings) { renderPreview(after: .milliseconds(30)) }
        .onChange(of: index) { renderPreview(after: .zero) }
    }

    private var primaryTitle: String {
        if isSaving { return "Saving…" }
        guard isBatch else { return "Save" }
        switch editedCount {
        case 0: return "Save Images"
        case 1: return "Save 1 Image"
        default: return "Save \(editedCount) Images"
        }
    }

    private var previewView: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(.primary.opacity(0.05))
            if let preview {
                Image(decorative: preview, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                    .padding(8)
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 300)
        .accessibilityLabel("Preview of \(image.url.lastPathComponent)")
    }

    private var transformButtons: some View {
        HStack(spacing: 8) {
            transformButton("Rotate Left", systemImage: "rotate.left") { settings.quarterTurns -= 1 }
            transformButton("Rotate Right", systemImage: "rotate.right") { settings.quarterTurns += 1 }
            transformButton("Flip Horizontal", systemImage: "arrow.left.and.right.righttriangle.left.righttriangle.right") {
                settings.flipsHorizontally.toggle()
            }
            transformButton("Flip Vertical", systemImage: "arrow.up.and.down.righttriangle.up.righttriangle.down") {
                settings.flipsVertically.toggle()
            }
        }
    }

    private func transformButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .medium))
                .frame(maxWidth: .infinity, minHeight: 26)
        }
        .buttonStyle(.bordered)
        .help(title)
        .accessibilityLabel(title)
    }

    /// Renders a downscaled preview off the main thread. Rapid slider changes
    /// cancel the previous render.
    private func renderPreview(after delay: Duration) {
        previewTask?.cancel()
        let source = image
        let settings = settings
        previewTask = Task {
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            let rendered = await Task.detached(priority: .userInitiated) {
                AdjustRenderer.render(source.cgImage, with: settings, maxDimension: 1000).map(RenderedPreview.init)
            }.value
            guard !Task.isCancelled, let rendered else { return }
            preview = rendered.image
        }
    }

    private func save() {
        isSaving = true
        let jobs = images.indices.map { (images[$0], allSettings[$0]) }
        let isBatch = isBatch
        Task {
            let result = await Task.detached(priority: .userInitiated) { () -> Result<SaveOutcome, Error> in
                Result {
                    guard isBatch else { return SaveOutcome(saved: [try ImageAdjuster.save(jobs[0].0, with: jobs[0].1)]) }
                    return try ImageAdjuster.run(jobs)
                }
            }.value
            isSaving = false
            switch result {
            case .success(let outcome): actions.finished(outcome)
            case .failure(let error): actions.failed(error)
            }
        }
    }
}

/// Saves adjusted copies next to the originals.
enum ImageAdjuster {
    /// Saves `image` with `settings` applied as "<name> Edited".
    static func save(_ image: EditableImage, with settings: AdjustSettings) throws -> URL {
        guard let rendered = AdjustRenderer.render(image.cgImage, with: settings) else {
            throw EditableImageError.unwritable(image.url)
        }
        return try image.save(rendered, suffix: "Edited")
    }

    /// Adjusts and saves each image; images whose settings change nothing are
    /// left alone and counted.
    static func run(_ jobs: [(EditableImage, AdjustSettings)]) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "edited", unchangedLabel: "not edited")
        for (image, settings) in jobs {
            guard settings.changesImage else {
                outcome.unchanged += 1
                continue
            }
            outcome.saved.append(try save(image, with: settings))
        }
        return outcome
    }
}

/// Carries a rendered `CGImage` back from a background task.
private struct RenderedPreview: @unchecked Sendable {
    let image: CGImage
}
