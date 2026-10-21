import AppKit

/// Context menu for shelf items.
@MainActor
enum ShelfMenu {
    static func make(for items: [ShelfItem], in shelf: Shelf, anchor: NSView) -> NSMenu {
        let urls = items.map(\.url)
        let isPlural = items.count > 1
        let menu = NSMenu()

        menu.addItem(ClosureMenuItem("Open") { FileActions.open(urls) })
        menu.addItem(ClosureMenuItem("Quick Look") { QuickLookCoordinator.shared.toggle(urls) })
        menu.addItem(ClosureMenuItem("Show in Finder") { FileActions.reveal(urls) })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem("Copy") { FileActions.copyFiles(urls) })
        menu.addItem(ClosureMenuItem(isPlural ? "Copy Paths" : "Copy Path") { FileActions.copyPaths(urls) })
        menu.addItem(ClosureMenuItem("Share…") { [weak anchor] in
            guard let anchor else { return }
            NSSharingServicePicker(items: urls).show(relativeTo: anchor.bounds, of: anchor, preferredEdge: .minY)
        })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(isPlural ? "Remove \(items.count) Files from Shelf" : "Remove from Shelf") {
            shelf.remove(Set(items.map(\.id)))
        })
        return menu
    }
}

/// An `NSMenuItem` that runs a closure.
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
    }

    @available(*, unavailable)
    required init(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    @objc private func run() {
        handler()
    }
}
