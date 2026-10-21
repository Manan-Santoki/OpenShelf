import AppKit

/// Creates shelves and keeps track of which one was used most recently.
@MainActor
final class ShelfManager {
    /// Ordered from least to most recently used.
    private var controllers: [ShelfWindowController] = []

    var hasShelves: Bool { !controllers.isEmpty }

    @discardableResult
    func newShelf(with urls: [URL] = [], near location: NSPoint = NSEvent.mouseLocation) -> Shelf {
        let shelf = Shelf()
        shelf.add(urls)
        let controller = ShelfWindowController(shelf: shelf, near: location)
        controller.onClose = { [weak self] closed in
            self?.controllers.removeAll { $0 === closed }
        }
        controller.onBecomeKey = { [weak self] active in
            self?.markRecentlyUsed(active)
        }
        controllers.append(controller)
        controller.present()
        return shelf
    }

    func addToRecentShelf(_ urls: [URL]) {
        guard let recent = controllers.last else {
            newShelf(with: urls)
            return
        }
        recent.shelf.add(urls)
        recent.present()
    }

    func closeAll() {
        for controller in controllers {
            controller.dismiss()
        }
    }

    private func markRecentlyUsed(_ controller: ShelfWindowController) {
        guard let index = controllers.firstIndex(where: { $0 === controller }) else { return }
        controllers.append(controllers.remove(at: index))
    }
}
