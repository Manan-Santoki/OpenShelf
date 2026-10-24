import CoreImage
import CoreImage.CIFilterBuiltins
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

/// How new image sizes are worked out.
struct ResizeRule: Equatable, Sendable {
    enum Mode: Hashable, Sendable {
        case percent
        case longEdge
        case exact
    }

    var mode = Mode.percent
    /// 1–400.
    var percent = 50.0
    /// Pixels on the longest side.
    var longEdge = 1920
    /// Pixels; 0 means "follow the other side's proportion".
    var width = 0
    var height = 0
    var allowsEnlarging = false

    /// The new size for an image of `size`, rounded to whole pixels. Without
    /// `allowsEnlarging`, results larger than the original are scaled back to it.
    func targetSize(for size: CGSize) -> CGSize {
        guard size.width > 0, size.height > 0 else { return size }
        var target: CGSize
        switch mode {
        case .percent:
            target = CGSize(width: size.width * percent / 100, height: size.height * percent / 100)
        case .longEdge:
            let scale = Double(longEdge) / max(size.width, size.height)
            target = CGSize(width: size.width * scale, height: size.height * scale)
        case .exact:
            switch (width > 0, height > 0) {
            case (true, true): target = CGSize(width: width, height: height)
            case (true, false): target = CGSize(width: Double(width), height: Double(width) * size.height / size.width)
            case (false, true): target = CGSize(width: Double(height) * size.width / size.height, height: Double(height))
            case (false, false): target = size
            }
        }
        if !allowsEnlarging, target.width > size.width || target.height > size.height {
            let shrink = min(size.width / target.width, size.height / target.height)
            target = CGSize(width: target.width * shrink, height: target.height * shrink)
        }
        return CGSize(width: max(1, target.width.rounded()), height: max(1, target.height.rounded()))
    }

    /// Whether an image of `size` would come out a different size.
    func changes(_ size: CGSize) -> Bool {
        targetSize(for: size) != size
    }

    /// Why an image is left as it is, as in "1 resized · 2 already smaller".
    var unchangedLabel: String {
        allowsEnlarging ? "already that size" : "already smaller"
    }
}

enum ResizeError: LocalizedError, Equatable {
    case failed

    var errorDescription: String? {
        switch self {
        case .failed:
            "The image couldn’t be resized."
        }
    }
}

/// High-quality scaling and saving for the Resize tool.
enum ImageResizer {
    /// `image` scaled to `size` with Lanczos resampling, keeping transparency.
    static func resize(_ image: CGImage, to size: CGSize) -> CGImage? {
        let width = max(1, Int(size.width.rounded())), height = max(1, Int(size.height.rounded()))
        let scaleX = CGFloat(width) / CGFloat(image.width)
        let scaleY = CGFloat(height) / CGFloat(image.height)
        let filter = CIFilter.lanczosScaleTransform()
        // Clamping first stops the filter from fading the edges towards transparent.
        filter.inputImage = CIImage(cgImage: image).clampedToExtent()
        filter.scale = Float(scaleY)
        filter.aspectRatio = Float(scaleX / scaleY)
        let rect = CGRect(x: 0, y: 0, width: width, height: height)
        guard let output = filter.outputImage?.cropped(to: rect) else { return nil }
        let space = image.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!
        return ImageContexts.working(in: space).createCGImage(output, from: rect, format: .RGBA8, colorSpace: space)
    }

    /// Saves `resized` next to `image` as "<name> Resized" in the same format,
    /// with metadata kept and its pixel dimensions updated.
    static func save(_ resized: CGImage, for image: EditableImage) throws -> URL {
        let type = EditableImage.writableTypes.contains(image.type) ? image.type : .png
        let destination = OutputNaming.destination(for: image.url, suffix: "Resized", pathExtension: image.pathExtension(for: type))
        var metadata = image.properties
        if var exif = metadata[kCGImagePropertyExifDictionary] as? [CFString: Any] {
            exif[kCGImagePropertyExifPixelXDimension] = resized.width
            exif[kCGImagePropertyExifPixelYDimension] = resized.height
            metadata[kCGImagePropertyExifDictionary] = exif
        }
        try EditableImage.write(resized, to: destination, type: type, quality: 0.92, metadata: metadata)
        return destination
    }

    /// Resizes and saves each image; images whose size wouldn't change are
    /// left alone and counted.
    static func run(_ images: [EditableImage], rule: ResizeRule) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "resized", unchangedLabel: rule.unchangedLabel)
        for image in images {
            let target = rule.targetSize(for: image.pixelSize)
            guard target != image.pixelSize else {
                outcome.unchanged += 1
                continue
            }
            guard let resized = resize(image.cgImage, to: target) else { throw ResizeError.failed }
            outcome.saved.append(try save(resized, for: image))
        }
        return outcome
    }
}

/// Resizes one or several images by percentage, longest side, or exact size.
struct ResizeEditor: View {
    static let windowSize = CGSize(width: 440, height: 470)

    let images: [EditableImage]
    let actions: EditorActions
    @State private var rule = ResizeRule()
    @State private var keepsProportions = true
    @State private var isWorking = false

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        let size = images.first?.pixelSize ?? .zero
        _rule = State(initialValue: ResizeRule(width: Int(size.width), height: 0))
    }

    private var first: EditableImage? { images.first }

    /// The rule as it will be applied: with proportions kept, height follows width.
    private var effectiveRule: ResizeRule {
        var rule = rule
        if rule.mode == .exact && keepsProportions { rule.height = 0 }
        return rule
    }

    /// How many of the images the current rule would change.
    private var changingCount: Int {
        let rule = effectiveRule
        return images.filter { rule.changes($0.pixelSize) }.count
    }

    var body: some View {
        EditorChrome(
            title: "Resize",
            primaryTitle: "Resize",
            isPrimaryEnabled: !isWorking && changingCount > 0,
            actions: actions,
            onReset: reset,
            onPrimary: save
        ) {
            VStack(alignment: .leading, spacing: 14) {
                header

                HStack(spacing: 6) {
                    presetButton("50%") { rule.mode = .percent; rule.percent = 50 }
                    presetButton("1080 px") { rule.mode = .longEdge; rule.longEdge = 1080 }
                    presetButton("1920 px") { rule.mode = .longEdge; rule.longEdge = 1920 }
                    presetButton("3840 px") { rule.mode = .longEdge; rule.longEdge = 3840 }
                }

                EditorRow(label: "Resize by") {
                    Picker("Resize by", selection: $rule.mode) {
                        Text("Percent").tag(ResizeRule.Mode.percent)
                        Text("Long side").tag(ResizeRule.Mode.longEdge)
                        Text("Exact size").tag(ResizeRule.Mode.exact)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                switch rule.mode {
                case .percent:
                    EditorSlider(label: "Scale", value: $rule.percent, range: 5...(rule.allowsEnlarging ? 400 : 100), unit: "%")
                case .longEdge:
                    EditorRow(label: "Long side") {
                        TextField("Pixels", value: $rule.longEdge, format: .number.grouping(.never))
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 90)
                        Text("px").foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                    }
                case .exact:
                    exactFields
                }

                Toggle("Allow enlarging", isOn: $rule.allowsEnlarging)
                    .font(.system(size: 12))
                    .help("Off keeps images from getting bigger than they are, which only adds blur")

                Text(summary)
                    .font(.system(size: 12).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.05)))

                if isWorking {
                    ProgressView().controlSize(.small)
                }
                Spacer(minLength: 0)
            }
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            if let first {
                Image(decorative: first.cgImage, scale: 1)
                    .resizable()
                    .interpolation(.medium)
                    .scaledToFill()
                    .frame(width: 52, height: 52)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(images.count == 1 ? first?.url.lastPathComponent ?? "" : "\(images.count) images")
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                if let first {
                    Text("\(Int(first.pixelSize.width)) × \(Int(first.pixelSize.height)) px\(images.count > 1 ? " (first)" : "")")
                        .font(.system(size: 11).monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var exactFields: some View {
        EditorRow(label: "Size") {
            Text("W").foregroundStyle(.secondary)
            TextField("Width", value: widthBinding, format: .number.grouping(.never))
                .frame(width: 72)
            Text("H").foregroundStyle(.secondary)
            TextField("Height", value: heightBinding, format: .number.grouping(.never))
                .frame(width: 72)
                .disabled(keepsProportions)
            Toggle(isOn: $keepsProportions) {
                Image(systemName: keepsProportions ? "lock.fill" : "lock.open")
            }
            .toggleStyle(.button)
            .help(keepsProportions ? "Proportions are kept — height follows width" : "Set width and height freely")
            .accessibilityLabel("Keep proportions")
            .onChange(of: keepsProportions) { _, locked in
                if locked { rule.height = 0 } else if let first { rule.height = Int(rule.targetSize(for: first.pixelSize).height) }
            }
            Spacer(minLength: 0)
        }
        .textFieldStyle(.roundedBorder)
        .font(.system(size: 12))
    }

    private var widthBinding: Binding<Int> {
        Binding { rule.width } set: { rule.width = max(1, $0) }
    }

    /// Shows the height that follows from the width while proportions are kept.
    private var heightBinding: Binding<Int> {
        Binding {
            if keepsProportions, let first { return Int(rule.targetSize(for: first.pixelSize).height) }
            return rule.height
        } set: { rule.height = max(1, $0) }
    }

    private var summary: String {
        guard let first else { return "" }
        let rule = effectiveRule
        let target = rule.targetSize(for: first.pixelSize)
        var line = "\(Int(first.pixelSize.width)) × \(Int(first.pixelSize.height))  →  \(Int(target.width)) × \(Int(target.height)) px"
        if target == first.pixelSize {
            line += "  (no change)"
        }
        guard images.count > 1 else { return line }
        let changing = changingCount
        let unchanged = images.count - changing
        if unchanged == 0 {
            return line + "\nThe same rule applies to each image."
        }
        return line + "\n\(changing) of \(images.count) will be resized · \(unchanged) \(rule.unchangedLabel)"
    }

    private func presetButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 12, weight: .semibold).monospacedDigit())
                .frame(maxWidth: .infinity)
                .frame(height: 28)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.07)))
        }
        .buttonStyle(.plain)
    }

    private func reset() {
        let size = first?.pixelSize ?? .zero
        rule = ResizeRule(width: Int(size.width), height: 0)
        keepsProportions = true
    }

    private func save() {
        isWorking = true
        let images = images
        let rule = effectiveRule
        Task.detached {
            let result = Result { try ImageResizer.run(images, rule: rule) }
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
