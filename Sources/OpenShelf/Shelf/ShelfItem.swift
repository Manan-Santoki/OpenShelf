import AppKit
import Observation
import QuickLookThumbnailing

/// A file reference held by a shelf.
@MainActor
@Observable
final class ShelfItem: Identifiable {
    let id = UUID()
    let url: URL
    private(set) var thumbnail: NSImage
    @ObservationIgnored private var didRequestThumbnail = false

    init(url: URL) {
        self.url = url
        thumbnail = NSWorkspace.shared.icon(forFile: url.path)
    }

    var name: String { url.lastPathComponent }

    var exists: Bool { FileManager.default.fileExists(atPath: url.path) }

    /// Replaces the Finder icon with a Quick Look thumbnail, once.
    func loadThumbnail(side: CGFloat, scale: CGFloat) {
        guard !didRequestThumbnail else { return }
        didRequestThumbnail = true
        let request = QLThumbnailGenerator.Request(
            fileAt: url,
            size: CGSize(width: side, height: side),
            scale: scale,
            representationTypes: .thumbnail
        )
        QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { [weak self] representation, _ in
            guard let cgImage = representation?.cgImage else { return }
            Task { @MainActor in
                self?.thumbnail = NSImage(cgImage: cgImage, size: .zero)
            }
        }
    }
}
