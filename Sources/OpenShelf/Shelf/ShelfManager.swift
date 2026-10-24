import AppKit
import Observation

/// Creates shelves, keeps track of which one was used most recently, and saves
/// them so they come back after a relaunch.
@MainActor
@Observable
final class ShelfManager {
    /// Ordered from least to most recently used.
    private var controllers: [ShelfWindowController] = []
    @ObservationIgnored private var saveTask: Task<Void, Never>?
    @ObservationIgnored private let storeURL: URL

    init(storeURL: URL = ShelfStore.defaultURL) {
        self.storeURL = storeURL
    }

    var hasShelves: Bool { !controllers.isEmpty }

    /// Open shelves, most recently used first, for the menu-bar menu.
    var openShelves: [Shelf] { controllers.reversed().map(\.shelf) }

    /// The selected files of the shelf that has keyboard focus, or nil when none has.
    var keyShelfSelection: [URL]? {
        controllers.first { $0.window?.isKeyWindow == true }.map { $0.shelf.selectedItems.map(\.url) }
    }

    /// The selected files of the most recently used shelf.
    var recentShelfSelection: [URL] {
        controllers.last?.shelf.selectedItems.map(\.url) ?? []
    }

    /// Brings every shelf to the front, sliding out any hidden at an edge, and
    /// focuses the most recently used one.
    func showAll() {
        for controller in controllers {
            controller.bringForward(focus: controller === controllers.last)
        }
    }

    func show(_ shelf: Shelf) {
        controllers.first { $0.shelf === shelf }?.bringForward(focus: true)
    }

    @discardableResult
    func newShelf(with urls: [URL] = [], near location: NSPoint = NSEvent.mouseLocation) -> Shelf {
        let shelf = Shelf()
        shelf.add(urls)
        open(ShelfWindowController(shelf: shelf, near: location))
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

    /// Updates every open reference after files move through the Actions wheel.
    func relocate(_ moves: [URL: URL]) {
        guard !moves.isEmpty else { return }
        for controller in controllers { controller.shelf.relocate(moves) }
        scheduleSave()
    }

    func closeAll() {
        for controller in controllers {
            controller.dismiss()
        }
    }

    // MARK: Saving and restoring

    /// Reopens the shelves saved at the end of the last session. Files that no
    /// longer exist are skipped, and shelves left with nothing aren't reopened.
    func restoreSavedShelves() {
        for snapshot in ShelfStore.load(from: storeURL) {
            let urls = snapshot.items.compactMap { ShelfStore.resolve($0) }
            guard !urls.isEmpty else { continue }
            let shelf = Shelf()
            shelf.add(urls)
            shelf.isExpanded = snapshot.isExpanded
            shelf.viewMode = snapshot.viewMode
            shelf.dockedEdge = snapshot.dockedEdge
            open(ShelfWindowController(shelf: shelf, near: NSEvent.mouseLocation, restoringFrame: snapshot.frame))
        }
        scheduleSave()
    }

    /// Writes every open shelf to disk right away, e.g. when quitting.
    func saveNow() {
        saveTask?.cancel()
        saveTask = nil
        try? ShelfStore.save(controllers.compactMap(\.snapshot), to: storeURL)
    }

    /// Coalesces bursts of changes (resizing, moving, adding files) into one write.
    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(600))
            guard !Task.isCancelled else { return }
            self?.saveNow()
        }
    }

    private func open(_ controller: ShelfWindowController) {
        controller.onClose = { [weak self] closed in
            self?.controllers.removeAll { $0 === closed }
            self?.scheduleSave()
        }
        controller.onBecomeKey = { [weak self] active in
            self?.markRecentlyUsed(active)
        }
        controller.onStateChange = { [weak self] in
            self?.scheduleSave()
        }
        controllers.append(controller)
        controller.present()
        scheduleSave()
    }

    private func markRecentlyUsed(_ controller: ShelfWindowController) {
        guard let index = controllers.firstIndex(where: { $0 === controller }) else { return }
        controllers.append(controllers.remove(at: index))
    }
}
