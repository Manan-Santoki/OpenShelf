import Foundation
import UniformTypeIdentifiers

/// Broad category of a file, used to decide which conversions and tools apply.
enum FileKind: Equatable {
    case image
    case pdf
    case document
    case audio
    case video
    case subtitle
    case archive
    case folder
    case other

    init(url: URL) {
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .contentTypeKey])
        if values?.isDirectory == true {
            self = .folder
            return
        }
        let extensionName = url.pathExtension.lowercased()
        if ["srt", "vtt"].contains(extensionName) {
            self = .subtitle
            return
        }
        guard let type = values?.contentType ?? UTType(filenameExtension: extensionName) else {
            self = .other
            return
        }
        self = Self.kind(of: type)
    }

    static func kind(of type: UTType) -> FileKind {
        if type.conforms(to: .pdf) { return .pdf }
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
