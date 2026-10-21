import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum EditableImageError: LocalizedError {
    case unreadable(URL)
    case unwritable(URL)

    var errorDescription: String? {
        switch self {
        case .unreadable(let url): "“\(url.lastPathComponent)” couldn’t be opened as an image."
        case .unwritable(let url): "“\(url.lastPathComponent)” couldn’t be saved."
        }
    }
}

/// An image file loaded for editing: pixels already rotated upright, plus the
/// original metadata and format so edits can be saved alongside it.
struct EditableImage: @unchecked Sendable {
    let url: URL
    /// Upright, full-resolution pixels.
    let cgImage: CGImage
    /// ImageIO properties of the first frame (EXIF, GPS, TIFF, …).
    let properties: [CFString: Any]
    /// The source file's type, e.g. `public.jpeg`.
    let type: UTType

    var pixelSize: CGSize { CGSize(width: cgImage.width, height: cgImage.height) }

    var hasAlpha: Bool {
        switch cgImage.alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast: false
        default: true
        }
    }

    static func load(_ url: URL) throws -> EditableImage {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let typeIdentifier = CGImageSourceGetType(source) as String?,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        else { throw EditableImageError.unreadable(url) }

        let width = properties[kCGImagePropertyPixelWidth] as? Int ?? 0
        let height = properties[kCGImagePropertyPixelHeight] as? Int ?? 0
        // A full-size "thumbnail" with the EXIF transform applied gives upright pixels.
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: max(width, height, 1),
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            throw EditableImageError.unreadable(url)
        }
        return EditableImage(
            url: url,
            cgImage: image,
            properties: properties,
            type: UTType(typeIdentifier) ?? .png
        )
    }

    /// Saves `image` next to the original as "<name> <suffix>.<ext>".
    ///
    /// - Parameters:
    ///   - type: Output type; defaults to the original's type when ImageIO can
    ///     write it, otherwise PNG.
    ///   - quality: Lossy compression quality from 0 to 1, for JPEG/HEIC/AVIF.
    ///   - keepsMetadata: Copy the original's metadata (with orientation reset,
    ///     since the pixels are already upright).
    @discardableResult
    func save(
        _ image: CGImage,
        suffix: String,
        as type: UTType? = nil,
        quality: Double = 0.92,
        keepsMetadata: Bool = true
    ) throws -> URL {
        let outputType = type ?? (Self.writableTypes.contains(self.type) ? self.type : .png)
        // Keep the original's spelling ("jpg", not "jpeg") when the format is unchanged.
        let pathExtension = outputType == self.type && !url.pathExtension.isEmpty
            ? url.pathExtension
            : outputType.preferredFilenameExtension ?? "png"
        let destinationURL = OutputNaming.destination(for: url, suffix: suffix, pathExtension: pathExtension)
        try Self.write(image, to: destinationURL, type: outputType, quality: quality, metadata: keepsMetadata ? properties : nil)
        return destinationURL
    }

    /// Encodes `image` with ImageIO. JPEG and BMP have no alpha channel, so
    /// transparent images are flattened onto white for them.
    static func write(_ image: CGImage, to url: URL, type: UTType, quality: Double = 0.92, metadata: [CFString: Any]? = nil) throws {
        let image = (type == .jpeg || type == .bmp) ? flattened(image) ?? image : image
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else {
            throw EditableImageError.unwritable(url)
        }
        var properties = metadata ?? [:]
        properties[kCGImagePropertyOrientation] = 1
        properties[kCGImageDestinationLossyCompressionQuality] = quality
        // Stale pixel dimensions would contradict the edited image.
        properties.removeValue(forKey: kCGImagePropertyPixelWidth)
        properties.removeValue(forKey: kCGImagePropertyPixelHeight)
        if var tiff = properties[kCGImagePropertyTIFFDictionary] as? [CFString: Any] {
            tiff[kCGImagePropertyTIFFOrientation] = 1
            properties[kCGImagePropertyTIFFDictionary] = tiff
        }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw EditableImageError.unwritable(url) }
    }

    /// The image drawn over an opaque white background.
    static func flattened(_ image: CGImage) -> CGImage? {
        guard let context = CGContext(
            data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ) else { return nil }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        context.setFillColor(.white)
        context.fill(rect)
        context.draw(image, in: rect)
        return context.makeImage()
    }

    private static let writableTypes: Set<UTType> = Set(
        (CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []).compactMap { UTType($0) }
    )
}
