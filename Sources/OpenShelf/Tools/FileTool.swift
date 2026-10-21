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

    var title: String {
        switch self {
        case .compress: "Compress"
        case .metadata: "Metadata"
        case .edit: "Edit"
        case .annotate: "Annotate"
        case .addBackground: "Add BG"
        case .crop: "Crop"
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
        case .redact: "eye.slash"
        case .readQR: "qrcode.viewfinder"
        case .combinePDF: "doc.on.doc"
        case .organizePages: "square.grid.2x2"
        case .splitPDF: "scissors"
        case .trim: "timeline.selection"
        case .speed: "gauge.with.dots.needle.67percent"
        case .mute: "speaker.slash"
        }
    }

    /// Tools that apply to every file in `urls`, in wheel order.
    static func available(for urls: [URL]) -> [FileTool] {
        guard !urls.isEmpty else { return [] }
        let kinds = Set(urls.map { FileKind(url: $0) })
        let combine: [FileTool] = urls.count > 1 && kinds.isSubset(of: [.image, .pdf]) ? [.combinePDF] : []

        if kinds == [.image] {
            return [.compress, .metadata, .edit, .annotate, .addBackground, .crop, .redact, .readQR] + combine
        }
        if kinds == [.pdf] {
            let singlePage: [FileTool] = urls.count == 1 ? [.organizePages, .splitPDF] : []
            return combine + singlePage + [.compressPDF, .pdfMetadata]
        }
        if kinds == [.video] {
            return [.trim, .speed, .cropVideo, .compressVideo, .mute]
        }
        if kinds == [.audio] {
            return [.trim, .speed]
        }
        return combine
    }
}
