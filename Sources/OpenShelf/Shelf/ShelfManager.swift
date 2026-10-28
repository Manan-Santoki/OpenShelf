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
    /// The shelf each watched folder's new files go on, while it's open.
    @ObservationIgnored private var watchedFolderShelves: [WatchedFolder.ID: Shelf.ID] = [:]

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

    /// The shelf with keyboard focus, or nil when none has one.
    var keyShelf: Shelf? {
        controllers.first { $0.window?.isKeyWindow == true }?.shelf
    }

    /// The most recently used shelf, or nil when none is open.
    var recentShelf: Shelf? { controllers.last?.shelf }

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

    /// Puts files that just arrived in a watched folder on that folder's
    /// shelf, sliding it out if it's hidden at an edge, or on a new shelf
    /// when the folder doesn't have one open (yet, or any more).
    @discardableResult
    func addArrivals(_ urls: [URL], fromWatchedFolder folderID: WatchedFolder.ID) -> Shelf {
        if let shelfID = watchedFolderShelves[folderID],
           let controller = controllers.first(where: { $0.shelf.id == shelfID }) {
            controller.shelf.add(urls)
            controller.present()
            scheduleSave()
            return controller.shelf
        }
        let shelf = newShelf(with: urls)
        watchedFolderShelves[folderID] = shelf.id
        return shelf
    }

    /// Adds the files a conversion or tool wrote back to the shelf the
    /// operation was started from, when Settings → Shelves allows it and
    /// that shelf is still open. Does nothing otherwise — including when the
    /// shelf was closed or removed while the operation ran, which is never
    /// treated as a reason to reopen it.
    /// - Parameter isEnabled: nil reads Settings → Shelves' toggle; tests
    ///   pass an explicit value instead.
    func addConversionResults(_ urls: [URL], sourceShelfID: Shelf.ID?, isEnabled: Bool? = nil) {
        let isEnabled = isEnabled ?? Preferences.shared.addsConversionResultsToShelf
        guard !urls.isEmpty,
              let shelf = ShelfConversionTarget.resolve(sourceShelfID: sourceShelfID, isEnabled: isEnabled, openShelves: openShelves)
        else { return }
        shelf.add(urls)
        scheduleSave()
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
