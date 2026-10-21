import AppKit
import Observation

enum ShelfEdge: String, Codable {
    case left, right
}

enum ShelfViewMode: String, Codable {
    case grid, list

    mutating func toggle() {
        self = self == .grid ? .list : .grid
    }
}

/// The contents and UI state of one shelf.
@MainActor
@Observable
final class Shelf: Identifiable {
    let id = UUID()
    private(set) var items: [ShelfItem] = []
    var selection: Set<ShelfItem.ID> = []
    var isExpanded = false
    var viewMode = ShelfViewMode.grid
    var dockedEdge: ShelfEdge?
    /// An item the expanded view should scroll into view, e.g. after type-select.
    var scrollTarget: ShelfItem.ID?
    /// Whether files are being dragged over the shelf, for the drop highlight.
    var isDropTargeted = false
    /// Whether the shelf has ever held items, so an emptied shelf can close itself
    /// while a freshly created empty one stays open.
    private(set) var hasHadItems = false
    @ObservationIgnored private var selectionAnchor: ShelfItem.ID?

    /// Adds file URLs, skipping ones already on the shelf.
    func add(_ urls: [URL]) {
        let existing = Set(items.map { $0.url.standardizedFileURL })
        var seen = existing
        for url in urls where url.isFileURL {
            let standardized = url.standardizedFileURL
            guard seen.insert(standardized).inserted else { continue }
            items.append(ShelfItem(url: standardized))
        }
        if !items.isEmpty { hasHadItems = true }
    }

    func remove(_ ids: Set<ShelfItem.ID>) {
        guard !ids.isEmpty else { return }
        items.removeAll { ids.contains($0.id) }
        selection.subtract(ids)
    }

    func removeMissingFiles() {
        remove(Set(items.filter { !$0.exists }.map(\.id)))
    }

    /// Cleans up after files were dragged off the shelf: drops items whose file was
    /// moved away, and optionally every dragged item.
    func didDragOut(_ dragged: [ShelfItem], operation: NSDragOperation, removeDragged: Bool) {
        guard !operation.isEmpty else { return }
        if removeDragged {
            remove(Set(dragged.map(\.id)))
        }
        removeMissingFiles()
        // The destination may finish moving the files after the drag session ends.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(1))
            self?.removeMissingFiles()
        }
    }

    var selectedItems: [ShelfItem] {
        items.filter { selection.contains($0.id) }
    }

    /// The items an action on `item` applies to: the whole selection if `item` is
    /// part of it, otherwise just `item`.
    func targets(for item: ShelfItem) -> [ShelfItem] {
        selection.contains(item.id) ? selectedItems : [item]
    }

    /// The selection, or every item when nothing is selected.
    var selectionOrAll: [ShelfItem] {
        selection.isEmpty ? items : selectedItems
    }

    /// Updates the selection for a click, following Finder's ⌘/⇧ conventions.
    func select(_ item: ShelfItem, modifiers: NSEvent.ModifierFlags) {
        if modifiers.contains(.command) {
            if selection.remove(item.id) == nil { selection.insert(item.id) }
            selectionAnchor = item.id
        } else if modifiers.contains(.shift),
                  let anchor = selectionAnchor,
                  let from = items.firstIndex(where: { $0.id == anchor }),
                  let to = items.firstIndex(where: { $0.id == item.id }) {
            selection = Set(items[min(from, to)...max(from, to)].map(\.id))
        } else {
            selection = [item.id]
            selectionAnchor = item.id
        }
    }

    func selectAll() {
        selection = Set(items.map(\.id))
    }

    /// A short name for menus: the first file, and how many others there are.
    var summary: String {
        guard let first = items.first else { return "Empty shelf" }
        return items.count == 1 ? first.name : "\(first.name) + \(items.count - 1) more"
    }

    /// Selects the first item whose name starts with `prefix` and scrolls to it.
    @discardableResult
    func typeSelect(_ prefix: String) -> Bool {
        guard let index = TypeSelect.match(prefix, in: items.map(\.name)) else { return false }
        select(items[index], modifiers: [])
        scrollTarget = items[index].id
        return true
    }
}
