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
        // Check ffmpeg (usually known already) before doing any work.
        var encoders: Set<String> = []
        if tools.cwebp == nil, let ffmpeg = tools.ffmpeg {
            encoders = await FFmpegEncoders.load(ffmpeg)
        }
        let encoder = try WebPEncoder.choose(cwebp: tools.cwebp, ffmpeg: tools.ffmpeg, ffmpegEncoders: encoders)

        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: temporary) }
        try EditableImage.write(image.cgImage, to: temporary, type: .png)

        switch encoder {
        case .cwebp(let cwebp):
            try await ProcessRunner.run(cwebp, ["-quiet", "-q", "88", temporary.path, "-o", url.path])
        case .ffmpeg(let ffmpeg):
            try await ProcessRunner.run(ffmpeg, ["-y", "-loglevel", "error", "-i", temporary.path, "-c:v", "libwebp", "-quality", "88", url.path])
        }
    }
}

/// Which tool writes WebP, which ImageIO can't: cwebp, or ffmpeg when it was
/// built with libwebp (Homebrew's isn't).
enum WebPEncoder: Equatable {
    case cwebp(URL)
    case ffmpeg(URL)

    static let ffmpegEncoder = "libwebp"

    /// Whether to put WebP on the wheel. `ffmpegEncoders` is what
    /// `FFmpegEncoders` already knows, nil before it has asked; until then
    /// ffmpeg is assumed able, and `choose` checks before encoding.
    static func isOffered(cwebp: URL?, ffmpeg: URL?, ffmpegEncoders: Set<String>?) -> Bool {
        if cwebp != nil { return true }
        guard ffmpeg != nil else { return false }
        return ffmpegEncoders?.contains(ffmpegEncoder) ?? true
    }

    /// The tool to encode with, preferring cwebp.
    static func choose(cwebp: URL?, ffmpeg: URL?, ffmpegEncoders: Set<String>) throws -> WebPEncoder {
        if let cwebp { return .cwebp(cwebp) }
        guard let ffmpeg else { throw MissingToolError(name: "ffmpeg") }
        guard ffmpegEncoders.contains(ffmpegEncoder) else {
            throw ConversionError.failed("This ffmpeg can’t write WebP. Install cwebp, or ffmpeg with libwebp.")
        }
        return .ffmpeg(ffmpeg)
    }
}
