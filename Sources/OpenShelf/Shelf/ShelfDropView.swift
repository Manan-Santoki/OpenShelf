import AppKit
import SwiftUI

/// The shelf window's content view, which takes files dropped anywhere on it.
///
/// The URLs are read straight off the drag pasteboard, so a dropped file is on
/// the shelf the moment the mouse comes up. (SwiftUI's `dropDestination` loads
/// them through item providers instead, which took one to three seconds.)
/// Thumbnails then fill in asynchronously.
final class ShelfDropView: NSView {
    private let shelf: Shelf

    init(shelf: Shelf) {
        self.shelf = shelf
        super.init(frame: .zero)
        registerForDraggedTypes([.fileURL])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        let canDrop = !fileURLs(in: sender).isEmpty
        shelf.isDropTargeted = canDrop
        return canDrop ? .copy : []
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        shelf.isDropTargeted ? .copy : []
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        shelf.isDropTargeted = false
    }

    override func draggingEnded(_ sender: any NSDraggingInfo) {
        shelf.isDropTargeted = false
    }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        shelf.isDropTargeted = false
        let urls = fileURLs(in: sender)
        guard !urls.isEmpty else { return false }
        withAnimation(.snappy) { shelf.add(urls) }
        return true
    }

    private func fileURLs(in info: any NSDraggingInfo) -> [URL] {
        let objects = info.draggingPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        )
        return objects as? [URL] ?? []
    }
}
