import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let shelves = ShelfManager()
    private let wheel = WheelController()
    private let dragMonitor = DragMonitor(preferences: .shared)

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Keeps the app out of the Dock when launched without the bundle's LSUIElement (e.g. `swift run`).
        NSApp.setActivationPolicy(.accessory)

        wheel.onDrop = { [weak self] item, urls in
            self?.perform(item.command, on: urls)
        }
        dragMonitor.onTrigger = { [weak self] kind, location, urls in
            self?.showWheel(kind, at: location, for: urls)
        }
        dragMonitor.onDragEnded = { [weak self] in
            self?.wheel.dismiss()
        }
        dragMonitor.onStep = { [weak self] direction in
            self?.wheel.step(direction)
        }
        dragMonitor.onArrowsBlocked = { [weak self] in
            self?.wheel.showHint("Let go of ⌃ or ⌘, then use the arrow keys")
        }
        dragMonitor.onConfirm = { [weak self] urls in
            self?.wheel.confirmHighlighted(with: urls) ?? false
        }
        dragMonitor.start()
        Updater.shared.start()
        if Preferences.shared.restoresShelves {
            shelves.restoreSavedShelves()
        }
        WelcomeView.presentIfNeeded()
    }

    func applicationWillTerminate(_ notification: Notification) {
        shelves.saveNow()
    }

    private func showWheel(_ kind: WheelKind, at location: NSPoint, for urls: [URL]) {
        let items = items(for: kind, urls: urls)
        guard !items.isEmpty else {
            wheel.dismiss(after: .zero)
            let title = kind == .convert ? "No conversions for this file" : "No tools for this file yet"
            ToastController.shared.show(Toast(systemImage: "nosign", title: title), near: location)
            return
        }
        wheel.present(items, at: location)
    }

    private func items(for kind: WheelKind, urls: [URL]) -> [WheelItem] {
        switch kind {
        case .convert:
            ConversionCatalog.formats(for: urls, tools: .detect()).map(WheelItem.convert)
        case .tools:
            FileTool.available(for: urls).map(WheelItem.tool)
        case .actions:
            FileAction.allCases
                .filter { $0 != .addToShelf || shelves.hasShelves }
                .map(WheelItem.action)
        }
    }

    private func perform(_ command: WheelCommand, on urls: [URL]) {
        switch command {
        case .action(let action):
            perform(action, on: urls)
        case .convert(let format):
            ConversionRunner.shared.convert(urls, to: format)
        case .tool(let tool):
            ToolLauncher.open(tool, with: urls)
        }
    }

    private func perform(_ action: FileAction, on urls: [URL]) {
        let toasts = ToastController.shared
        let count = urls.count == 1 ? "“\(urls[0].lastPathComponent)”" : "\(urls.count) files"
        switch action {
        case .newShelf:
            shelves.newShelf(with: urls)
        case .addToShelf:
            shelves.addToRecentShelf(urls)
            toasts.show(Toast(systemImage: "tray.full", title: "Added to shelf", subtitle: count))
        case .airDrop:
            FileActions.airDrop(urls)
        case .copy:
            FileActions.copyFiles(urls)
            toasts.show(Toast(systemImage: "doc.on.doc", title: "Copied \(count)", subtitle: "Paste in Finder to make a copy"))
        case .move:
            guard let result = FileActions.moveWithPrompt(urls), result.movedCount > 0 else { return }
            let moved = result.movedCount == 1 ? "1 file" : "\(result.movedCount) files"
            toasts.show(Toast(systemImage: "folder", title: "Moved \(moved)", subtitle: "to \(result.destination.lastPathComponent)"))
        case .copyPath:
            FileActions.copyPaths(urls)
            toasts.show(Toast(systemImage: "link", title: urls.count == 1 ? "Copied path" : "Copied \(urls.count) paths", subtitle: urls.count == 1 ? urls[0].path : nil))
        case .terminal:
            FileActions.openInTerminal(urls)
        }
    }
}
