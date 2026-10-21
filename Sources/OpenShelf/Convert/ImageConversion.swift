import CoreGraphics
import Foundation
import ImageIO

enum ImageConversion {
    static func convert(_ source: URL, to format: OutputFormat, tools: ExternalTools) async throws -> URL {
        guard let pathExtension = format.fileExtension else {
            throw ConversionError.unsupported("image to \(format.title)")
        }
        let image = try EditableImage.load(source)
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)

        switch format {
        case .pdf:
            try writePDF(image, to: destination)
        case .docx:
            try DocxWriter.write(images: [image.cgImage], to: destination)
        case .webp:
            try await writeWebP(image, to: destination, tools: tools)
        default:
            guard let type = format.imageIOType else {
                throw ConversionError.unsupported("image to \(format.title)")
            }
            try EditableImage.write(image.cgImage, to: destination, type: type, quality: 0.9, metadata: image.properties)
        }
        return destination
    }

    /// A one-page PDF the size of the image at its stated resolution.
    private static func writePDF(_ image: EditableImage, to url: URL) throws {
        let dpi = image.properties[kCGImagePropertyDPIWidth] as? Double ?? 72
        let scale = 72 / max(dpi, 1)
        var mediaBox = CGRect(x: 0, y: 0, width: Double(image.cgImage.width) * scale, height: Double(image.cgImage.height) * scale)
        guard let context = CGContext(url as CFURL, mediaBox: &mediaBox, nil) else {
            throw EditableImageError.unwritable(url)
        }
        context.beginPDFPage(nil)
        context.draw(image.cgImage, in: mediaBox)
        context.endPDFPage()
        context.closePDF()
    }

    /// ImageIO can't encode WebP, so go through a temporary PNG and cwebp or ffmpeg.
    private static func writeWebP(_ image: EditableImage, to url: URL, tools: ExternalTools) async throws {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: temporary) }
        try EditableImage.write(image.cgImage, to: temporary, type: .png)

        if let cwebp = tools.cwebp {
            try await ProcessRunner.run(cwebp, ["-quiet", "-q", "88", temporary.path, "-o", url.path])
        } else if let ffmpeg = tools.ffmpeg, FFmpegEncoders.available(in: ffmpeg).contains("libwebp") {
            try await ProcessRunner.run(ffmpeg, ["-y", "-loglevel", "error", "-i", temporary.path, "-quality", "88", url.path])
        } else {
            throw ConversionError.unsupported("WebP needs cwebp or ffmpeg with libwebp (brew install webp)")
        }
    }
}
