import Foundation
import UniformTypeIdentifiers

/// A conversion target.
enum OutputFormat: String, CaseIterable, Hashable, Sendable {
    // Images
    case png, jpg, webp, heic, tiff, avif, bmp, gif
    // Documents
    case pdf, docx, txt, rtf, html
    // Audio and video
    case mp4, mov, m4a, mp3, wav, aiff, flac
    // Subtitles
    case srt, vtt
    // Archives
    case zip, extract

    var title: String {
        switch self {
        case .extract: "UNZIP"
        default: rawValue.uppercased()
        }
    }

    /// Extension of the written file; nil for formats that produce a folder.
    var fileExtension: String? {
        switch self {
        case .extract: nil
        default: rawValue
        }
    }

    /// Whether a file with this extension is already in this format.
    func matches(extension ext: String) -> Bool {
        let ext = ext.lowercased()
        switch self {
        case .jpg: return ["jpg", "jpeg", "jpe"].contains(ext)
        case .tiff: return ["tif", "tiff"].contains(ext)
        case .heic: return ["heic", "heif"].contains(ext)
        case .html: return ["html", "htm"].contains(ext)
        case .aiff: return ["aif", "aiff"].contains(ext)
        case .txt: return ["txt", "text"].contains(ext)
        default: return ext == rawValue
        }
    }

    /// The ImageIO type for image formats ImageIO can encode.
    var imageIOType: UTType? {
        switch self {
        case .png: .png
        case .jpg: .jpeg
        case .heic: .heic
        case .tiff: .tiff
        case .avif: UTType("public.avif")
        case .bmp: .bmp
        case .gif: .gif
        default: nil
        }
    }
}

/// Which conversions apply to which files.
enum ConversionCatalog {
    /// Formats every file in `urls` can be converted to, in wheel order.
    static func formats(for urls: [URL], tools: ExternalTools) -> [OutputFormat] {
        guard let first = urls.first else { return [] }
        var common = formats(for: first, tools: tools)
        for url in urls.dropFirst() {
            let available = Set(formats(for: url, tools: tools))
            common.removeAll { !available.contains($0) }
        }
        return common
    }

    static func formats(for url: URL, tools: ExternalTools) -> [OutputFormat] {
        let ext = url.pathExtension
        var formats: [OutputFormat]
        switch FileKind(url: url) {
        case .image:
            formats = [.png, .jpg, .heic, .tiff, .avif, .bmp, .pdf, .docx]
            if tools.canEncodeWebP { formats.insert(.webp, at: 2) }
        case .pdf:
            formats = [.docx, .jpg, .png, .txt]
        case .document:
            formats = [.pdf, .docx, .txt, .rtf, .html]
        case .audio:
            formats = [.m4a, .wav, .aiff, .flac]
            if tools.ffmpeg != nil { formats.insert(.mp3, at: 0) }
        case .video:
            formats = [.mp4, .mov, .m4a]
            if tools.ffmpeg != nil { formats += [.gif, .mp3] }
        case .subtitle:
            formats = [.txt, .srt, .vtt]
        case .archive:
            return [.extract]
        case .folder, .other:
            return [.zip]
        }
        formats.removeAll { $0.matches(extension: ext) }
        return formats + [.zip]
    }
}

/// Optional command-line encoders found on this Mac.
struct ExternalTools: Sendable {
    var ffmpeg: URL?
    var cwebp: URL?

    var canEncodeWebP: Bool { cwebp != nil || ffmpeg != nil }

    /// Looks in the usual Homebrew and MacPorts locations; GUI apps don't inherit
    /// the shell's PATH.
    static func detect() -> ExternalTools {
        ExternalTools(ffmpeg: find("ffmpeg"), cwebp: find("cwebp"))
    }

    private static func find(_ name: String) -> URL? {
        let directories = ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"]
            + (ProcessInfo.processInfo.environment["PATH"]?.split(separator: ":").map(String.init) ?? [])
        return directories
            .map { URL(fileURLWithPath: $0).appendingPathComponent(name) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }
}
