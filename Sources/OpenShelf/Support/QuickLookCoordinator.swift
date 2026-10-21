import AppKit
import Quartz

/// Feeds the shared Quick Look panel. Shelf panels hand it to the panel through
/// `beginPreviewPanelControl(_:)`.
@MainActor
final class QuickLookCoordinator: NSObject {
    static let shared = QuickLookCoordinator()

    private var urls: [URL] = []

    func toggle(_ urls: [URL]) {
        guard let panel = QLPreviewPanel.shared() else { return }
        if panel.isVisible {
            panel.orderOut(nil)
            return
        }
        guard !urls.isEmpty else { return }
        self.urls = urls
        panel.reloadData()
        panel.makeKeyAndOrderFront(nil)
    }
}

extension QuickLookCoordinator: @preconcurrency QLPreviewPanelDataSource {
    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        urls.count
    }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        urls[index] as NSURL
    }
}
