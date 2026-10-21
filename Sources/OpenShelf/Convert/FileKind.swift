import Foundation
import UniformTypeIdentifiers

/// Broad category of a file, used to decide which conversions and tools apply.
enum FileKind: Hashable {
    case image
    case pdf
    case document
    case spreadsheet
    case presentation
    case audio
    case video
    case subtitle
    case archive
    case folder
    case other

    init(url: URL) {
        // Classify what a symlink points to, not the link itself.
        let url = url.resolvingSymlinksInPath()
        let extensionName = url.pathExtension.lowercased()
        // Formats the system types don't describe well (or at all) go by name.
        // RTFD is a folder on disk but a document to people.
        if let kind = Self.kindsByExtension[extensionName] {
            self = kind
            return
        }
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .contentTypeKey])
        if values?.isDirectory == true {
            self = .folder
            return
        }
        var type = values?.contentType ?? UTType(filenameExtension: extensionName)
        // Without a telling extension the system only knows "data"; look inside.
        if type == nil || type == .data || extensionName.isEmpty {
            type = FileSniffer.type(of: url) ?? type
        }
        guard let type else {
            self = .other
            return
        }
        self = Self.kind(of: type)
    }

    /// Extensions classified before asking the system: subtitles, spreadsheets
    /// whose types conform to plain text (CSV), and media containers that
    /// macOS doesn't play itself.
    static let kindsByExtension: [String: FileKind] = {
        var kinds: [String: FileKind] = ["srt": .subtitle, "vtt": .subtitle, "rtfd": .document]
        for ext in ["csv", "tsv", "xlsx", "xls", "ods"] { kinds[ext] = .spreadsheet }
        for ext in ["ppt", "pptx", "odp", "key"] { kinds[ext] = .presentation }
        for ext in MediaFormats.ffmpegOnlyVideoExtensions { kinds[ext] = .video }
        for ext in MediaFormats.ffmpegOnlyAudioExtensions { kinds[ext] = .audio }
        return kinds
    }()

    static func kind(of type: UTType) -> FileKind {
        if type.conforms(to: .pdf) { return .pdf }
        if type.conforms(to: .spreadsheet) { return .spreadsheet }
        if type.conforms(to: .presentation) { return .presentation }
        if type.conforms(to: .image) { return .image }
        if type.conforms(to: .movie) || type.conforms(to: .video) { return .video }
        if type.conforms(to: .audio) { return .audio }
        if type.conforms(to: .archive) || type.conforms(to: .zip) || type.conforms(to: .gzip) { return .archive }
        if documentTypes.contains(where: { type.conforms(to: $0) }) { return .document }
        return .other
    }

    /// Text formats NSAttributedString can read.
    private static let documentTypes: [UTType] = [
        .plainText, .rtf, .rtfd, .html, .flatRTFD,
        UTType("org.openxmlformats.wordprocessingml.document"),
        UTType("com.microsoft.word.doc"),
        UTType("org.oasis-open.opendocument.text"),
        UTType("net.daringfireball.markdown"),
    ].compactMap { $0 }
}

/// Recognises common formats from their first bytes, for files whose name
/// doesn't say what they are.
enum FileSniffer {
    static func type(of url: URL) -> UTType? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let header = try? handle.read(upToCount: 16) else { return nil }
        return type(ofHeader: header)
    }

    static func type(ofHeader data: Data) -> UTType? {
        let bytes = [UInt8](data)
        func starts(with prefix: [UInt8], at offset: Int = 0) -> Bool {
            bytes.count >= offset + prefix.count && Array(bytes[offset..<offset + prefix.count]) == prefix
        }
        func ascii(_ string: String, at offset: Int = 0) -> Bool {
            starts(with: Array(string.utf8), at: offset)
        }

        if starts(with: [0xFF, 0xD8, 0xFF]) { return .jpeg }
        if starts(with: [0x89, 0x50, 0x4E, 0x47]) { return .png }
        if ascii("GIF8") { return .gif }
        if ascii("%PDF") { return .pdf }
        if starts(with: [0x49, 0x49, 0x2A, 0x00]) || starts(with: [0x4D, 0x4D, 0x00, 0x2A]) { return .tiff }
        // "BM" alone is too common in text; also require the zeroed reserved field.
        if ascii("BM") && starts(with: [0, 0, 0, 0], at: 6) { return .bmp }
        if ascii("RIFF") && ascii("WEBP", at: 8) { return .webP }
        if ascii("RIFF") && ascii("WAVE", at: 8) { return .wav }
        if ascii("FORM") && (ascii("AIFF", at: 8) || ascii("AIFC", at: 8)) { return .aiff }
        if ascii("ID3") || starts(with: [0xFF, 0xFB]) { return .mp3 }
        if ascii("fLaC") { return UTType("org.xiph.flac") ?? .audio }
        if ascii("OggS") { return .audio }
        // Matroska and WebM (EBML), AVI, ASF (WMV/WMA) and Flash video.
        if starts(with: [0x1A, 0x45, 0xDF, 0xA3]) { return .movie }
        if ascii("RIFF") && ascii("AVI ", at: 8) { return .avi }
        if starts(with: [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11]) { return .movie }
        if ascii("FLV") { return .movie }
        if starts(with: [0x50, 0x4B, 0x03, 0x04]) { return .zip }
        if starts(with: [0x1F, 0x8B]) { return .gzip }
        if ascii("ftyp", at: 4), bytes.count >= 12 {
            let brand = String(decoding: bytes[8..<12], as: UTF8.self)
            switch brand {
            case "heic", "heix", "heim", "heis", "mif1", "msf1": return .heic
            case "avif", "avis": return UTType("public.avif") ?? .image
            case "M4A ", "M4B ": return .mpeg4Audio
            case "qt  ": return .quickTimeMovie
            default: return .mpeg4Movie
            }
        }
        return nil
    }
}
