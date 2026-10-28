import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let shelves = ShelfManager()
    private let wheel = WheelController()
    private let dragMonitor = DragMonitor(preferences: .shared)
    private var hasShownMoreFormatsHint = false
    private var emptyShortcutPresses = RepeatedEmptyPress()

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Keeps the app out of the Dock when launched without the bundle's LSUIElement (e.g. `swift run`).
        NSApp.setActivationPolicy(.accessory)

        wheel.onDrop = { [weak self] item, urls, modifiers, sourceShelfID in
            self?.perform(item.command, on: urls, modifiers: modifiers, sourceShelfID: sourceShelfID)
        }
        ConversionRunner.shared.onSaved = { [weak self] urls, sourceShelfID in
            self?.shelves.addConversionResults(urls, sourceShelfID: sourceShelfID)
        }
        ConversionRunner.shared.onJobSaved = { urls in
            WatchedFolders.shared.noteOutputs(urls)
        }
        EditorWindowController.onSaved = { [weak self] urls, sourceShelfID in
            WatchedFolders.shared.noteOutputs(urls)
            self?.shelves.addConversionResults(urls, sourceShelfID: sourceShelfID)
        }
        WatchedFolders.shared.onArrival = { [weak self] urls, folder in
            guard let self else { return }
            let shelf = self.shelves.addArrivals(urls, fromWatchedFolder: folder.id)
            folder.action.run(on: urls, sourceShelfID: shelf.id)
        }
        IntentShelves.manager = shelves
        wheel.itemsProvider = { [weak self] kind, urls in
            self?.items(for: kind, urls: urls) ?? []
        }
        GlobalHotKey.shared.onPress = { [weak self] in
            self?.openWheelForSelection()
        }
        GlobalHotKey.shared.register(Preferences.shared.openWheelShortcut)
        dragMonitor.onTrigger = { [weak self] kind, location, urls in
            self?.showWheel(kind, at: location, for: urls)
        }
        dragMonitor.onDragEnded = { [weak self] in
            self?.wheel.dragDidEnd()
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
        wheel.prepare()
        // Detecting tools also starts learning ffmpeg's encoders in the
        // background, so the first wheel knows whether to offer WebP.
        _ = ExternalTools.current
        dragMonitor.start()
        Updater.shared.start()
        if Preferences.shared.restoresShelves {
            shelves.restoreSavedShelves()
        }
        WatchedFolders.shared.start()
        TourWindowController.presentIfNeeded()
    }

    func applicationWillTerminate(_ notification: Notification) {
        shelves.saveNow()
    }

    /// Opening OpenShelf again (from Finder, Spotlight or Launchpad) shows
    /// Settings, so it can be reached when the menu bar icon is out of sight:
    /// behind the notch or in the menu bar's overflow.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        SettingsWindowController.shared.show()
        return false
    }

    /// Opens a keyboard-driven wheel for the selected shelf items or, when no
    /// shelf has focus with a selection, the files selected in Finder.
    func openWheelForSelection() {
        let keySelection = shelves.keyShelfSelection
        let source = WheelSelectionSource.choose(
            keyShelfSelection: keySelection,
            recentShelfSelection: shelves.recentShelfSelection,
            isOpenShelfFrontmost: NSApp.isActive
        )
        guard case .shelf(let urls) = source else {
            openWheelForFinderSelection()
            return
        }
        emptyShortcutPresses.reset()
        // Whichever selection `choose` picked names the shelf it came from.
        let sourceShelf = (keySelection == urls) ? shelves.keyShelf : shelves.recentShelf
        let location = NSEvent.mouseLocation
        if wheel.presentForKeyboard(urls: urls, at: location, sourceShelfID: sourceShelf?.id) {
            TourProgress.shared.record(.keyboardWheelOpened(urls))
        } else {
            ToastController.shared.show(Toast(systemImage: "nosign", title: "Nothing to do with these files"), near: location)
        }
    }

    /// Opens a keyboard-driven wheel for the files selected in Finder.
    func openWheelForFinderSelection() {
        let location = NSEvent.mouseLocation
        let outcome = FinderSelection.resolve(frontmostBundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier) {
            try FinderSelection.fetchFromFinder()
        }
        if outcome != .noSelection && outcome != .finderNotFrontmost {
            emptyShortcutPresses.reset()
        } else if emptyShortcutPresses.record(at: .now) {
            SettingsWindowController.shared.show()
            return
        }
        if case .files(let urls) = outcome {
            if wheel.presentForKeyboard(urls: urls, at: location) {
                TourProgress.shared.record(.keyboardWheelOpened(urls))
            } else {
                ToastController.shared.show(Toast(systemImage: "nosign", title: "Nothing to do with these files"), near: location)
            }
            return
        }
        if let toast = FinderSelection.toast(for: outcome, shortcut: Preferences.shared.openWheelShortcut) {
            ToastController.shared.show(toast, near: location, for: .seconds(outcome == .notPermitted ? 5 : 2))
        }
        if outcome == .notPermitted, let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation") {
            NSWorkspace.shared.open(url)
        }
    }

    private func showWheel(_ kind: WheelKind, at location: NSPoint, for urls: [URL]) {
        // A drag always gets a fresh wheel under the pointer.
        wheel.endKeyboardMode(hidingWheel: true)
        let tools = ExternalTools.current
        let items = items(for: kind, urls: urls, tools: tools)
        if kind == .convert,
           let tool = MissingToolAdvice.blockingTool(for: urls, formats: ConversionCatalog.formats(in: items), tools: tools) {
            // A lone ZIP wedge would suggest that's all OpenShelf can do with an
            // MKV or a PowerPoint deck; say what's missing instead.
            wheel.dismiss(after: .zero)
            ToastController.shared.show(MissingToolAdvice.toast(for: urls, needing: tool, tools: tools), near: location, for: .seconds(4))
            return
        }
        guard !items.isEmpty else {
            wheel.dismiss(after: .zero)
            if kind == .tools, FileTool.needsConversionForTools(urls) {
                // MKV, WebM, AVI and friends convert fine but can't be edited directly.
                let convertKeys = TriggerKey.describe(Preferences.shared.trigger(for: .convert))
                ToastController.shared.show(
                    FileTool.convertFirstToast(for: urls, convertKeys: convertKeys, tools: tools),
                    near: location,
                    for: .seconds(3)
                )
                return
            }
            let title = kind == .convert ? "No conversions for this file" : "No tools for this file yet"
            ToastController.shared.show(Toast(systemImage: "nosign", title: title), near: location)
            return
        }
        wheel.present(items, at: location)
        TourProgress.shared.record(.wheelOpened(kind, urls))
        if kind == .convert, !hasShownMoreFormatsHint,
           let hint = MissingToolAdvice.moreFormatsHint(for: urls, tools: tools) {
            // Once per launch: OpenShelf stays open for days, so this is an
            // occasional pointer to Settings, not something seen on every drag.
            hasShownMoreFormatsHint = true
            wheel.showHint(hint)
        }
    }

    private func items(for kind: WheelKind, urls: [URL], tools: ExternalTools = .current) -> [WheelItem] {
        switch kind {
        case .convert:
            WheelOrdering.formats(ConversionCatalog.formats(for: urls, tools: tools), for: urls).map(WheelItem.convert)
        case .tools:
            WheelOrdering.tools(FileTool.available(for: urls)).map(WheelItem.tool)
        case .actions:
            FileAction.allCases
                .filter { $0 != .addToShelf || shelves.hasShelves }
                .map(WheelItem.action)
        }
    }

    /// Runs a wedge. With ⌥ held, a format with output settings opens them
    /// for this conversion first (see `ConvertOptionsView`).
    /// - Parameter sourceShelfID: The shelf `urls` were dragged or selected
    ///   from, if any; a conversion or tool's results are added back to it.
    private func perform(_ command: WheelCommand, on urls: [URL], modifiers: NSEvent.ModifierFlags, sourceShelfID: Shelf.ID?) {
        TourProgress.shared.record(.ran(command, urls, option: modifiers.contains(.option)))
        switch command {
        case .action(let action):
            perform(action, on: urls)
        case .convert(let format):
            if modifiers.contains(.option), ConvertOptionsView.present(urls, to: format, sourceShelfID: sourceShelfID) { return }
            ConversionRunner.shared.convert(urls, to: format, sourceShelfID: sourceShelfID)
        case .tool(let tool):
            ToolLauncher.open(tool, with: urls, sourceShelfID: sourceShelfID)
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
            shelves.relocate(result.moves)
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
