import ImageIO
import SwiftUI
import UniformTypeIdentifiers

/// How to compress: a fixed quality, or a target size relative to the original.
struct CompressOptions: Equatable, Sendable {
    enum Mode: Hashable, Sendable {
        case quality
        case targetSize
    }

    enum Format: Hashable, Sendable {
        case keepOriginal
        case jpeg
        case heic
    }

    var mode = Mode.quality
    /// 10–100.
    var quality = 75.0
    /// Percentage of the original file size, 10–90.
    var targetPercent = 50.0
    var format = Format.keepOriginal
    /// Longest edge in pixels; nil keeps the original size.
    var maxDimension: Int?
}

/// Encodes images to meet `CompressOptions`.
enum ImageCompressor {
    static let lossyTypes = Set([UTType.jpeg, .heic] + [UTType("public.heif"), UTType("public.avif")].compactMap { $0 })

    static func outputType(for image: EditableImage, format: CompressOptions.Format) -> UTType {
        switch format {
        case .keepOriginal: lossyTypes.contains(image.type) ? image.type : .jpeg
        case .jpeg: .jpeg
        case .heic: .heic
        }
    }

    static func originalSize(of url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
    }

    /// Encodes `image` according to `options`, returning the file data.
    static func compress(_ image: EditableImage, options: CompressOptions) throws -> Data {
        let type = outputType(for: image, format: options.format)
        var pixels = image.cgImage
        if let maxDimension = options.maxDimension {
            pixels = resized(pixels, maxDimension: CGFloat(maxDimension)) ?? pixels
        }

        switch options.mode {
        case .quality:
            return try encode(pixels, as: type, quality: options.quality / 100, metadata: image.properties, source: image.url)
        case .targetSize:
            let target = Int(Double(originalSize(of: image.url)) * options.targetPercent / 100)
            // Find the highest quality that fits; shrink the pixels if even the
            // lowest quality is too big.
            for _ in 0..<4 {
                var low = 0.05, high = 0.95
                var best: Data?
                for _ in 0..<7 {
                    let quality = (low + high) / 2
                    let data = try encode(pixels, as: type, quality: quality, metadata: image.properties, source: image.url)
                    if data.count <= target {
                        best = data
                        low = quality
                    } else {
                        high = quality
                    }
                }
                if let best { return best }
                let smallest = try encode(pixels, as: type, quality: 0.05, metadata: image.properties, source: image.url)
                if smallest.count <= target { return smallest }
                let scale = max(0.2, (Double(target) / Double(smallest.count)).squareRoot() * 0.95)
                let longest = CGFloat(max(pixels.width, pixels.height)) * scale
                guard let smaller = resized(pixels, maxDimension: longest) else { return smallest }
                pixels = smaller
            }
            return try encode(pixels, as: type, quality: 0.05, metadata: image.properties, source: image.url)
        }
    }

    static func encode(_ image: CGImage, as type: UTType, quality: Double, metadata: [CFString: Any], source: URL) throws -> Data {
        let image = type == .jpeg ? EditableImage.flattened(image) ?? image : image
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, type.identifier as CFString, 1, nil) else {
            throw EditableImageError.unwritable(source)
        }
        var properties = metadata
        properties[kCGImagePropertyOrientation] = 1
        properties[kCGImageDestinationLossyCompressionQuality] = quality
        properties.removeValue(forKey: kCGImagePropertyPixelWidth)
        properties.removeValue(forKey: kCGImagePropertyPixelHeight)
        if var tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any] {
            tiff[kCGImagePropertyTIFFOrientation] = 1
            properties[kCGImagePropertyTIFFDictionary] = tiff
        }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw EditableImageError.unwritable(source) }
        return data as Data
    }

    static func resized(_ image: CGImage, maxDimension: CGFloat) -> CGImage? {
        let longest = CGFloat(max(image.width, image.height))
        guard longest > maxDimension else { return image }
        let scale = maxDimension / longest
        let width = max(1, Int((CGFloat(image.width) * scale).rounded()))
        let height = max(1, Int((CGFloat(image.height) * scale).rounded()))
        guard let context = CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: image.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    /// Writes the compressed copy next to the original, e.g. "Photo Compressed.jpg".
    static func save(_ image: EditableImage, options: CompressOptions) throws -> URL {
        let data = try compress(image, options: options)
        let type = outputType(for: image, format: options.format)
        let ext = type == image.type && !image.url.pathExtension.isEmpty
            ? image.url.pathExtension
            : type.preferredFilenameExtension ?? "jpg"
        let url = OutputNaming.destination(for: image.url, suffix: "Compressed", pathExtension: ext)
        try data.write(to: url, options: .withoutOverwriting)
        return url
    }
}

/// The "Compress" tool: smaller copies of one or more images.
struct CompressEditor: View {
    static let windowSize = CGSize(width: 420, height: 420)

    let images: [EditableImage]
    let actions: EditorActions

    @State private var options = CompressOptions()
    @State private var estimate: Int64?
    @State private var estimateTask: Task<Void, Never>?
    @State private var isWorking = false

    private let originalSizes: [Int64]

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        originalSizes = images.map { ImageCompressor.originalSize(of: $0.url) }
        let allLossy = images.allSatisfy { ImageCompressor.lossyTypes.contains($0.type) }
        _options = State(initialValue: CompressOptions(format: allLossy ? .keepOriginal : .jpeg))
    }

    private var totalOriginalSize: Int64 { originalSizes.reduce(0, +) }

    private var canKeepOriginal: Bool {
        images.allSatisfy { ImageCompressor.lossyTypes.contains($0.type) }
    }

    var body: some View {
        EditorChrome(
            title: "Compress",
            primaryTitle: isWorking ? "Compressing…" : "Compress",
            isPrimaryEnabled: !isWorking,
            actions: actions,
            onReset: { options = CompressOptions(format: canKeepOriginal ? .keepOriginal : .jpeg) },
            onPrimary: compress
        ) {
            VStack(alignment: .leading, spacing: 14) {
                header

                Picker("Mode", selection: $options.mode) {
                    Text("Quality").tag(CompressOptions.Mode.quality)
                    Text("Target size").tag(CompressOptions.Mode.targetSize)
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                switch options.mode {
                case .quality:
                    EditorSlider(label: "Quality", value: $options.quality, range: 10...100, unit: "%")
                case .targetSize:
                    EditorSlider(label: "Target size", value: $options.targetPercent, range: 10...90, unit: "%")
                }

                EditorRow(label: "Format") {
                    Picker("Format", selection: $options.format) {
                        if canKeepOriginal {
                            Text("Keep").tag(CompressOptions.Format.keepOriginal)
                        }
                        Text("JPEG").tag(CompressOptions.Format.jpeg)
                        Text("HEIC").tag(CompressOptions.Format.heic)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                EditorRow(label: "Max size") {
                    Picker("Max size", selection: $options.maxDimension) {
                        Text("Original").tag(Int?.none)
                        ForEach([4096, 2048, 1600, 1080], id: \.self) { size in
                            Text("\(size) px").tag(Int?.some(size))
                        }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 160, alignment: .leading)
                    Spacer(minLength: 0)
                }

                sizeSummary
            }
        }
        .onAppear(perform: updateEstimate)
        .onChange(of: options) { updateEstimate() }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(decorative: images[0].cgImage, scale: 1)
                .resizable()
                .scaledToFill()
                .frame(width: 52, height: 52)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(alignment: .bottomTrailing) {
                    if images.count > 1 {
                        Text("\(images.count)")
                            .font(.system(size: 10, weight: .bold).monospacedDigit())
                            .foregroundStyle(.white)
                            .padding(.horizontal, 5)
                            .frame(minWidth: 18, minHeight: 18)
                            .background(Capsule().fill(Color.accentColor))
                            .offset(x: 5, y: 5)
                    }
                }
            VStack(alignment: .leading, spacing: 2) {
                Text(images.count == 1 ? images[0].url.lastPathComponent : "\(images.count) images")
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(images.count == 1
                     ? "\(images[0].cgImage.width) × \(images[0].cgImage.height) · \(Self.format(totalOriginalSize))"
                     : "\(Self.format(totalOriginalSize)) in total")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var sizeSummary: some View {
        HStack(spacing: 8) {
            Label {
                if let estimate {
                    let saving = totalOriginalSize > 0 ? 100 - Int(Double(estimate) / Double(totalOriginalSize) * 100) : 0
                    Text(images.count == 1
                         ? "≈ \(Self.format(estimate))"
                         : "≈ \(Self.format(estimate)) for \(images.count) images")
                        .font(.system(size: 12, weight: .semibold))
                    + Text(saving > 0 ? "  (\(saving)% smaller)" : "  (no smaller)")
                        .font(.system(size: 12))
                        .foregroundStyle(saving > 0 ? Color.secondary : Color.orange)
                } else {
                    Text("Estimating…")
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                }
            } icon: {
                Image(systemName: "arrow.down.right.and.arrow.up.left")
                    .foregroundStyle(Color.accentColor)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.primary.opacity(0.05)))
    }

    /// Encodes the first image in the background and scales its result to the
    /// whole batch.
    private func updateEstimate() {
        estimateTask?.cancel()
        estimate = nil
        let first = images[0]
        let options = options
        let firstOriginal = originalSizes[0]
        let total = totalOriginalSize
        estimateTask = Task {
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled else { return }
            let size = await Task.detached(priority: .utility) {
                (try? ImageCompressor.compress(first, options: options)).map { Int64($0.count) }
            }.value
            guard !Task.isCancelled, let size else { return }
            estimate = firstOriginal > 0 ? Int64(Double(size) * Double(total) / Double(firstOriginal)) : size
        }
    }

    private func compress() {
        isWorking = true
        estimateTask?.cancel()
        let images = images
        let options = options
        Task {
            let result = await Task.detached(priority: .userInitiated) { () -> Result<[URL], Error> in
                Result { try images.map { try ImageCompressor.save($0, options: options) } }
            }.value
            isWorking = false
            switch result {
            case .success(let urls): actions.saved(urls)
            case .failure(let error): actions.failed(error)
            }
        }
    }

    private static func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
