import AppKit
import SwiftUI

/// Places an image on a gradient, colour or picture background with padding,
/// rounded corners and a drop shadow. Given several images, a filmstrip moves
/// between them, and one look can apply to all of them or each can have its own.
struct AddBackgroundEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 460, height: count > 1 ? 660 + EditorFilmstrip.height : 660)
    }

    let images: [EditableImage]
    let actions: EditorActions
    private let gradientSwatches: [CGImage]
    /// Whether the background shows through each image itself.
    private let transparency: [Bool]

    @State private var index = 0
    /// A smaller copy of the shown image so the live preview stays fast.
    @State private var previewSource: CGImage
    /// Each image's background and layout.
    @State private var allSettings: [BackgroundSettings]
    /// One background and layout for every image.
    @State private var sameForAll = true
    @State private var isWorking = false
    @State private var thumbnails: [CGImage]

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        gradientSwatches = GradientStyle.all.compactMap { BackgroundRenderer.swatch(for: .gradient($0)) }
        transparency = images.map(\.isTransparent)
        _previewSource = State(initialValue: Self.previewSource(for: images[0]))
        _allSettings = State(initialValue: images.map { _ in BackgroundSettings() })
        _thumbnails = State(initialValue: images.count > 1 ? images.map { $0.thumbnail() } : [])
    }

    private static func previewSource(for image: EditableImage) -> CGImage {
        BackgroundRenderer.downscaled(image.cgImage, maxDimension: 900) ?? image.cgImage
    }

    private var image: EditableImage { images[index] }
    private var isBatch: Bool { images.count > 1 }

    /// The shown image's background and layout. With Same for All on,
    /// changing them changes every image's.
    private var settings: BackgroundSettings {
        get { allSettings[index] }
        nonmutating set {
            guard isBatch, sameForAll else {
                allSettings[index] = newValue
                return
            }
            allSettings = images.map { _ in newValue }
        }
    }

    private var settingsBinding: Binding<BackgroundSettings> {
        Binding(get: { settings }, set: { settings = $0 })
    }

    /// Picking a custom colour also selects it.
    private var customColorBinding: Binding<Color> {
        Binding(get: { settings.customColor }) { color in
            var changed = settings
            changed.customColor = color
            changed.choice = .customColor
            settings = changed
        }
    }

    /// Images the background would change.
    private var edited: [Bool] {
        images.indices.map { composition(for: allSettings[$0]).changesImage(of: images[$0].pixelSize, isTransparent: transparency[$0]) }
    }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: "Add BG",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: !isWorking && editedCount > 0,
            actions: actions,
            onReset: reset,
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                preview
                    .frame(maxWidth: .infinity)
                    .frame(height: 290)

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: images.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll
                    )
                    .onChange(of: index) { _, newIndex in previewSource = Self.previewSource(for: images[newIndex]) }
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on spreads the shown image's look to the rest.
                        if isOn { settings = allSettings[index] }
                    }
                }

                EditorRow(label: "Background") {
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 3) {
                            ForEach(gradientSwatches.indices, id: \.self) { index in
                                swatch(isSelected: settings.choice == .gradient(index), help: "Gradient \(index + 1)") {
                                    settings.choice = .gradient(index)
                                } content: {
                                    Image(decorative: gradientSwatches[index], scale: 2).resizable()
                                }
                            }
                            swatch(isSelected: settings.choice == .customImage, help: "Choose a background image…", action: chooseBackgroundImage) {
                                if let backgroundImage = settings.backgroundImage {
                                    Image(decorative: backgroundImage, scale: 1).resizable().scaledToFill()
                                } else {
                                    Image(systemName: "photo")
                                        .font(.system(size: 12, weight: .medium))
                                        .foregroundStyle(.secondary)
                                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                                        .background(Color.primary.opacity(0.08))
                                }
                            }
                        }
                        HStack(spacing: 3) {
                            ForEach(SolidColors.all.indices, id: \.self) { index in
                                swatch(isSelected: settings.choice == .solid(index), help: SolidColors.names[index]) {
                                    settings.choice = .solid(index)
                                } content: {
                                    Color(cgColor: SolidColors.all[index].cgColor)
                                }
                            }
                            ColorPicker("Custom colour", selection: customColorBinding, supportsOpacity: false)
                                .labelsHidden()
                                .padding(2)
                                .overlay {
                                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                                        .strokeBorder(settings.choice == .customColor ? Color.accentColor : .clear, lineWidth: 2)
                                }
                                .help("Custom colour")
                        }
                    }
                }

                EditorSlider(label: "Padding", value: settingsBinding.padding, range: 0...1000)
                EditorSlider(label: "Corners", value: settingsBinding.corners, range: 0...400)
                EditorSlider(label: "Shadow", value: settingsBinding.shadow, range: 0...200)

                EditorRow(label: "Ratio") {
                    Picker("Ratio", selection: settingsBinding.ratio) {
                        ForEach(CanvasRatio.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
            }
        }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Save with Background" }
        switch editedCount {
        case 0: return "Save Images"
        case 1: return "Save 1 Image"
        default: return "Save \(editedCount) Images"
        }
    }

    // MARK: Preview

    private var composition: BackgroundComposition { composition(for: settings) }

    private func composition(for settings: BackgroundSettings) -> BackgroundComposition {
        BackgroundComposition(
            paint: paint(for: settings),
            padding: settings.padding,
            cornerRadius: settings.corners,
            shadow: settings.shadow,
            ratio: settings.ratio
        )
    }

    private func paint(for settings: BackgroundSettings) -> BackgroundPaint {
        switch settings.choice {
        case .gradient(let index): .gradient(GradientStyle.all[index])
        case .solid(let index): .solid(SolidColors.all[index].cgColor)
        case .customColor: .solid(NSColor(settings.customColor).usingColorSpace(.sRGB)?.cgColor ?? .white)
        case .customImage: settings.backgroundImage.map(BackgroundPaint.image) ?? .solid(.white)
        }
    }

    @ViewBuilder
    private var preview: some View {
        let canvas = composition.canvasSize(for: image.pixelSize)
        let scale = min(1, 640 / max(canvas.width, canvas.height))
        ZStack {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(Color.primary.opacity(0.05))
            if let rendered = composition.render(previewSource, imageSize: image.pixelSize, scale: scale) {
                Image(decorative: rendered, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    .shadow(color: .black.opacity(0.15), radius: 6, y: 2)
                    .padding(10)
            }
        }
        .accessibilityLabel("Preview")
    }

    private func swatch<Content: View>(
        isSelected: Bool,
        help: String,
        action: @escaping () -> Void,
        @ViewBuilder content: () -> Content
    ) -> some View {
        Button(action: action) {
            content()
                .frame(width: 26, height: 26)
                .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.12), lineWidth: 0.5)
                }
                .padding(2)
                .overlay {
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .strokeBorder(isSelected ? Color.accentColor : .clear, lineWidth: 2)
                }
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    // MARK: Actions

    private func chooseBackgroundImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        panel.message = "Choose a background image"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            var changed = settings
            changed.backgroundImage = try EditableImage.load(url).cgImage
            changed.choice = .customImage
            settings = changed
        } catch {
            actions.failed(error)
        }
    }

    /// Resets the shown image, or every image while they share one look. The
    /// custom colour and picture stay, ready to pick again.
    private func reset() {
        var defaults = BackgroundSettings()
        defaults.customColor = settings.customColor
        defaults.backgroundImage = settings.backgroundImage
        settings = defaults
    }

    private func save() {
        let jobs = images.indices.map { (images[$0], composition(for: allSettings[$0])) }
        guard isBatch else {
            do {
                actions.saved([try BackgroundAdder.save(jobs[0].0, on: jobs[0].1)])
            } catch {
                actions.failed(error)
            }
            return
        }
        isWorking = true
        Task.detached {
            let result = Result { try BackgroundAdder.run(jobs) }
            await MainActor.run {
                isWorking = false
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

/// One image's background and layout.
private struct BackgroundSettings: Equatable {
    var choice = BackgroundChoice.gradient(0)
    var customColor = Color(red: 0.96, green: 0.62, blue: 0.45)
    var backgroundImage: CGImage?
    var padding = 160.0
    var corners = 24.0
    var shadow = 24.0
    var ratio = CanvasRatio.auto
}

private enum BackgroundChoice: Equatable {
    case gradient(Int)
    case solid(Int)
    case customColor
    case customImage
}

/// Saves copies on their backgrounds next to the originals.
enum BackgroundAdder {
    /// Saves `image` laid out by `composition` as "<name> With Background.png".
    static func save(_ image: EditableImage, on composition: BackgroundComposition) throws -> URL {
        guard let rendered = composition.render(image.cgImage, imageSize: image.pixelSize, scale: 1) else {
            throw EditableImageError.unwritable(image.url)
        }
        return try image.save(rendered, suffix: "With Background", as: .png)
    }

    /// Saves each image on its background; images it wouldn't change (no
    /// margin or corners, nothing see-through) are left alone and counted.
    static func run(_ jobs: [(EditableImage, BackgroundComposition)]) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "saved", unchangedLabel: "unchanged")
        for (image, composition) in jobs {
            guard composition.changesImage(of: image.pixelSize, isTransparent: image.isTransparent) else {
                outcome.unchanged += 1
                continue
            }
            outcome.saved.append(try save(image, on: composition))
        }
        return outcome
    }
}

enum CanvasRatio: CaseIterable, Identifiable {
    case auto, square, landscape, portrait

    var id: Self { self }

    var title: String {
        switch self {
        case .auto: "Auto"
        case .square: "1:1"
        case .landscape: "16:9"
        case .portrait: "4:5"
        }
    }

    /// Width divided by height, or nil to fit the padded image exactly.
    var value: CGFloat? {
        switch self {
        case .auto: nil
        case .square: 1
        case .landscape: 16 / 9
        case .portrait: 4 / 5
        }
    }
}

// MARK: - Rendering

/// An sRGB colour stored as 0xRRGGBB.
struct HexColor: Sendable, ExpressibleByIntegerLiteral {
    let hex: UInt32

    init(hex: UInt32) {
        self.hex = hex
    }

    init(integerLiteral value: UInt32) {
        hex = value
    }

    var cgColor: CGColor { cgColor(alpha: 1) }

    func cgColor(alpha: CGFloat) -> CGColor {
        CGColor(
            srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255,
            alpha: alpha
        )
    }
}

private enum SolidColors {
    static let all: [HexColor] = [0xFFFFFF, 0xF5C542, 0x7BC950, 0x2FA38A, 0x5BC0D9, 0x3D6FE0, 0x4B47A8, 0x2C2F36, 0x8E5BD6]
    static let names = ["White", "Yellow", "Green", "Teal", "Cyan", "Blue", "Indigo", "Charcoal", "Purple"]
}

/// A soft background: a diagonal base gradient with blurred colour blobs on top.
struct GradientStyle: Sendable {
    struct Blob: Sendable {
        let color: HexColor
        /// Centre in unit coordinates, origin at the top left.
        let x: CGFloat
        let y: CGFloat
        /// Radius as a fraction of the longer side.
        let radius: CGFloat
    }

    let base: [HexColor]
    let blobs: [Blob]

    static let all: [GradientStyle] = [
        GradientStyle(base: [0xFFB38A, 0xFF6F91], blobs: [Blob(color: 0xFFD86F, x: 0.2, y: 0.2, radius: 0.6), Blob(color: 0xB39DFF, x: 0.85, y: 0.9, radius: 0.6)]),
        GradientStyle(base: [0xF4F1FF, 0xDDE7FF], blobs: [Blob(color: 0xFFC8E1, x: 0.8, y: 0.15, radius: 0.55), Blob(color: 0xA7D3FF, x: 0.15, y: 0.85, radius: 0.55)]),
        GradientStyle(base: [0xBFF0C8, 0xFFF3A6], blobs: [Blob(color: 0x6FD3C7, x: 0.85, y: 0.2, radius: 0.55), Blob(color: 0xFF9FB2, x: 0.2, y: 0.85, radius: 0.55)]),
        GradientStyle(base: [0xFBE3E8, 0xEAD7F7], blobs: [Blob(color: 0xFFD1BA, x: 0.2, y: 0.2, radius: 0.55), Blob(color: 0xCDB4FF, x: 0.8, y: 0.8, radius: 0.55)]),
        GradientStyle(base: [0xE8B5F0, 0xF7C6D9], blobs: [Blob(color: 0xB28DFF, x: 0.2, y: 0.8, radius: 0.6), Blob(color: 0xFFE0F0, x: 0.8, y: 0.2, radius: 0.5)]),
        GradientStyle(base: [0x2B2D5B, 0x7A3E73], blobs: [Blob(color: 0xE8566B, x: 0.8, y: 0.25, radius: 0.55), Blob(color: 0xF4A259, x: 0.25, y: 0.85, radius: 0.5)]),
        GradientStyle(base: [0x5EC8F2, 0x6A7BFF], blobs: [Blob(color: 0xA6F0E4, x: 0.2, y: 0.2, radius: 0.55), Blob(color: 0xC9A7FF, x: 0.85, y: 0.85, radius: 0.55)]),
        GradientStyle(base: [0xDDF6F7, 0xB4E1F0], blobs: [Blob(color: 0xFFFFFF, x: 0.3, y: 0.25, radius: 0.5), Blob(color: 0x9ED8E0, x: 0.8, y: 0.8, radius: 0.55)]),
    ]

    func draw(in rect: CGRect, context: CGContext) {
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        context.saveGState()
        context.clip(to: rect)
        if let gradient = CGGradient(colorsSpace: space, colors: base.map(\.cgColor) as CFArray, locations: nil) {
            // Top left to bottom right; CG's y axis points up.
            context.drawLinearGradient(
                gradient,
                start: CGPoint(x: rect.minX, y: rect.maxY),
                end: CGPoint(x: rect.maxX, y: rect.minY),
                options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
            )
        }
        let longest = max(rect.width, rect.height)
        for blob in blobs {
            let center = CGPoint(x: rect.minX + blob.x * rect.width, y: rect.maxY - blob.y * rect.height)
            let colors = [blob.color.cgColor(alpha: 0.9), blob.color.cgColor(alpha: 0)] as CFArray
            guard let gradient = CGGradient(colorsSpace: space, colors: colors, locations: [0, 1]) else { continue }
            context.drawRadialGradient(gradient, startCenter: center, startRadius: 0, endCenter: center, endRadius: blob.radius * longest, options: [])
        }
        context.restoreGState()
    }
}

enum BackgroundPaint {
    case gradient(GradientStyle)
    case solid(CGColor)
    case image(CGImage)

    func fill(_ rect: CGRect, in context: CGContext) {
        switch self {
        case .gradient(let style):
            style.draw(in: rect, context: context)
        case .solid(let color):
            context.setFillColor(color)
            context.fill(rect)
        case .image(let image):
            // Scale to fill, centred.
            let scale = max(rect.width / CGFloat(image.width), rect.height / CGFloat(image.height))
            let size = CGSize(width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
            context.saveGState()
            context.clip(to: rect)
            context.draw(image, in: CGRect(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2, width: size.width, height: size.height))
            context.restoreGState()
        }
    }
}

/// The finished layout: background canvas with the image centred on it.
/// Lengths are in output pixels.
struct BackgroundComposition {
    var paint: BackgroundPaint
    var padding: CGFloat
    var cornerRadius: CGFloat
    var shadow: CGFloat
    var ratio: CanvasRatio

    /// Whether the result would look any different from the image: only with
    /// a margin, rounded corners, or see-through areas for the background to
    /// show through. (A shadow with no margin falls behind the image.)
    func changesImage(of imageSize: CGSize, isTransparent: Bool) -> Bool {
        let canvas = canvasSize(for: imageSize)
        let sameSize = canvas.width.rounded() == imageSize.width && canvas.height.rounded() == imageSize.height
        return !sameSize || cornerRadius.rounded() > 0 || isTransparent
    }

    func canvasSize(for imageSize: CGSize) -> CGSize {
        let padded = CGSize(width: imageSize.width + 2 * padding, height: imageSize.height + 2 * padding)
        guard let ratio = ratio.value else { return padded }
        if padded.width / padded.height < ratio {
            return CGSize(width: padded.height * ratio, height: padded.height)
        }
        return CGSize(width: padded.width, height: padded.width / ratio)
    }

    /// Renders at `scale` × the output size. `source` may be a downscaled copy of
    /// an image whose full size is `imageSize`.
    func render(_ source: CGImage, imageSize: CGSize, scale: CGFloat) -> CGImage? {
        let canvas = canvasSize(for: imageSize)
        guard let context = CGContext(
            data: nil,
            width: max(1, Int((canvas.width * scale).rounded())),
            height: max(1, Int((canvas.height * scale).rounded())),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.scaleBy(x: scale, y: scale)

        paint.fill(CGRect(origin: .zero, size: canvas), in: context)

        let imageRect = CGRect(
            x: (canvas.width - imageSize.width) / 2,
            y: (canvas.height - imageSize.height) / 2,
            width: imageSize.width,
            height: imageSize.height
        )
        let radius = min(cornerRadius, min(imageSize.width, imageSize.height) / 2)
        let path = CGPath(roundedRect: imageRect, cornerWidth: radius, cornerHeight: radius, transform: nil)

        context.saveGState()
        if shadow > 0 {
            // Shadow geometry is in device pixels, unaffected by the scale transform.
            context.setShadow(
                offset: CGSize(width: 0, height: -shadow * 0.3 * scale),
                blur: shadow * scale,
                color: CGColor(gray: 0, alpha: 0.35)
            )
        }
        // Drawing inside a transparency layer lets the clipped image cast a shadow.
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        context.addPath(path)
        context.clip()
        context.draw(source, in: imageRect)
        context.endTransparencyLayer()
        context.restoreGState()

        return context.makeImage()
    }
}

enum BackgroundRenderer {
    static func downscaled(_ image: CGImage, maxDimension: CGFloat) -> CGImage? {
        let longest = CGFloat(max(image.width, image.height))
        guard longest > maxDimension else { return image }
        let scale = maxDimension / longest
        guard let context = CGContext(
            data: nil,
            width: max(1, Int(CGFloat(image.width) * scale)),
            height: max(1, Int(CGFloat(image.height) * scale)),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: context.width, height: context.height))
        return context.makeImage()
    }

    /// A small square preview of a background, for the swatch row.
    static func swatch(for paint: BackgroundPaint, pixels: Int = 56) -> CGImage? {
        guard let context = CGContext(
            data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        paint.fill(CGRect(x: 0, y: 0, width: pixels, height: pixels), in: context)
        return context.makeImage()
    }
}
