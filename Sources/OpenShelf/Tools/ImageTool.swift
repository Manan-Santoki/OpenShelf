import Foundation

/// An entry on the Tools wheel, in wheel order (clockwise from 12 o'clock).
enum ImageTool: CaseIterable, Hashable {
    case compress
    case metadata
    case edit
    case annotate
    case addBackground
    case crop
    case redact

    var title: String {
        switch self {
        case .compress: "Compress"
        case .metadata: "Metadata"
        case .edit: "Edit"
        case .annotate: "Annotate"
        case .addBackground: "Add BG"
        case .crop: "Crop"
        case .redact: "Redact"
        }
    }

    var systemImage: String {
        switch self {
        case .compress: "arrow.down.right.and.arrow.up.left"
        case .metadata: "tag"
        case .edit: "slider.horizontal.3"
        case .annotate: "pencil.tip.crop.circle"
        case .addBackground: "photo.on.rectangle"
        case .crop: "crop"
        case .redact: "eye.slash"
        }
    }

    /// Whether the tool works on several files at once rather than one at a time.
    var handlesBatches: Bool {
        self == .compress || self == .metadata
    }

    /// Tools that apply to every file in `urls`.
    static func available(for urls: [URL]) -> [ImageTool] {
        guard !urls.isEmpty, urls.allSatisfy({ FileKind(url: $0) == .image }) else { return [] }
        return allCases
    }
}
