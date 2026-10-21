import Foundation

/// An entry on the Tools wheel. Which tools appear depends on the dragged files.
enum FileTool: CaseIterable, Hashable {
    // Images, in wheel order (clockwise from 12 o'clock).
    case compress
    case metadata
    case edit
    case annotate
    case addBackground
    case crop
    case resize
    case removeBackground
    case redact
    case readQR
    // PDFs (and images, for Combine).
    case combinePDF
    case organizePages
    case splitPDF
    case compressPDF
    case pdfMetadata
    // Video and audio.
    case trim
    case speed
    case cropVideo
    case compressVideo
    case mute
    case mergeMedia
    case splitMedia
    case rotateVideo

    var title: String {
        switch self {
        case .compress: "Compress"
        case .metadata: "Metadata"
        case .edit: "Edit"
        case .annotate: "Annotate"
        case .addBackground: "Add BG"
        case .crop: "Crop"
        case .resize: "Resize"
        case .removeBackground: "Remove BG"
        case .redact: "Redact"
        case .readQR: "Read QR"
        case .combinePDF: "Combine"
        case .organizePages: "Pages"
        case .splitPDF: "Split"
        case .compressPDF: "Compress"
        case .pdfMetadata: "Metadata"
        case .trim: "Trim"
        case .speed: "Speed"
        case .cropVideo: "Crop"
        case .compressVideo: "Compress"
        case .mute: "Mute"
        case .mergeMedia: "Merge"
        case .splitMedia: "Split"
        case .rotateVideo: "Rotate"
        }
    }

    var systemImage: String {
        switch self {
        case .compress, .compressPDF, .compressVideo: "arrow.down.right.and.arrow.up.left"
        case .metadata, .pdfMetadata: "tag"
        case .edit: "slider.horizontal.3"
        case .annotate: "pencil.tip.crop.circle"
        case .addBackground: "photo.on.rectangle"
        case .crop, .cropVideo: "crop"
        case .resize: "arrow.up.left.and.arrow.down.right"
        case .removeBackground: "person.crop.rectangle"
        case .redact: "eye.slash"
        case .readQR: "qrcode.viewfinder"
        case .combinePDF: "doc.on.doc"
        case .organizePages: "square.grid.2x2"
        case .splitPDF: "scissors"
        case .trim: "timeline.selection"
        case .speed: "gauge.with.dots.needle.67percent"
        case .mute: "speaker.slash"
        case .mergeMedia: "arrow.triangle.merge"
        case .splitMedia: "scissors"
        case .rotateVideo: "rotate.right"
        }
    }

    /// Containers Apple's media framework can edit. Others (MKV, WebM, AVI, WMV,
    /// FLV, MKA, WMA, …) only convert, through ffmpeg; AVI is listed by
    /// AVFoundation but its common codecs don't open, so it's left out too.
    static let editableMediaExtensions: Set<String> = [
        "mp4", "mov", "m4v", "3gp", "m4a", "mp3", "wav", "aif", "aiff", "caf", "aac", "flac", "ogg", "opus",
    ]

    /// Whether every file is audio or video the media tools can't open.
    static func needsConversionForTools(_ urls: [URL]) -> Bool {
        !urls.isEmpty && urls.allSatisfy { url in
            let kind = FileKind(url: url)
            return (kind == .video || kind == .audio)
                && !editableMediaExtensions.contains(url.resolvingSymlinksInPath().pathExtension.lowercased())
        }
    }

    /// Tools that apply to every file in `urls`, in wheel order.
    static func available(for urls: [URL]) -> [FileTool] {
        guard !urls.isEmpty else { return [] }
        let kinds = Set(urls.map { FileKind(url: $0) })
        let combine: [FileTool] = urls.count > 1 && kinds.isSubset(of: [.image, .pdf]) ? [.combinePDF] : []

        if kinds == [.image] {
            return [.compress, .metadata, .edit, .annotate, .addBackground, .crop, .resize, .removeBackground, .redact, .readQR] + combine
        }
        if kinds == [.pdf] {
            let singlePage: [FileTool] = urls.count == 1 ? [.organizePages, .splitPDF] : []
            return combine + singlePage + [.compressPDF, .pdfMetadata]
        }
        if kinds.isSubset(of: [.video, .audio]),
           !urls.allSatisfy({ editableMediaExtensions.contains($0.resolvingSymlinksInPath().pathExtension.lowercased()) }) {
            return []
        }
        let merge: [FileTool] = urls.count > 1 ? [.mergeMedia] : []
        if kinds == [.video] {
            return merge + [.trim, .speed, .cropVideo, .rotateVideo, .splitMedia, .compressVideo, .mute]
        }
        if kinds == [.audio] {
            return merge + [.trim, .speed, .splitMedia]
        }
        return combine
    }
}
