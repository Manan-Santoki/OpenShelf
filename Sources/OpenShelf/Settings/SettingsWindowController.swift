import AppKit
import Observation
import SwiftUI

/// The Settings window, shown in front and focused every time it's asked for.
///
/// OpenShelf is an accessory (menu-bar) app, so it isn't active when Settings…
/// is chosen. SwiftUI's Settings scene left an already-open window behind the
/// app that was in front (e.g. Chrome) even after activating OpenShelf. This
/// window is ordered front right away and again once activation completes,
/// since activation finishes asynchronously.
///
/// Its panes are toolbar tabs, like System Settings' apps: the window keeps
/// its width and takes each pane's height, with the standard animated resize.
@MainActor
final class SettingsWindowController: NSWindowController {
    static let shared = SettingsWindowController()

    private let tabs = SettingsTabViewController()
    private var activationObserver: (any NSObjectProtocol)?
    private var activationTimeout: Task<Void, Never>?
    private var keyObserver: (any NSObjectProtocol)?
    /// Counts `trackHeight` calls, so only the latest one's change is acted on.
    private var heightTracking = 0
    private var heightSignature: [String] = []

    private init() {
        let window = NSWindow(contentViewController: tabs)
        window.styleMask = [.titled, .closable]
        window.toolbarStyle = .preference
        window.isReleasedWhenClosed = false
        // Open on the Space the user is on rather than switching to another.
        window.collectionBehavior = [.moveToActiveSpace]
        super.init(window: window)
        tabs.onSelect = { [weak self] tab in self?.didSelect(tab) }
        tabs.select(SettingsTab.restored(from: UserDefaults.standard.string(forKey: SettingsTab.defaultsKey)))
        trackHeight()
        // Someone who just ran `brew install ffmpeg` and comes back should see
        // it right away, without restarting OpenShelf.
        keyObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didBecomeKeyNotification, object: window, queue: .main
        ) { _ in
            MainActor.assumeIsolated {
                ToolInstaller.shared.refresh()
            }
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /// Opens the window, or brings it back in front if it's already open,
    /// on `tab` if given, else on the tab chosen last.
    func show(tab: SettingsTab? = nil) {
        guard let window else { return }
        if !window.isVisible {
            // Before measuring: a missing tool's row is taller than an installed one's.
            ToolInstaller.shared.refresh()
            if let tab { tabs.select(tab) }
            fitToScreen(window)
        } else if let tab {
            tabs.select(tab)
        }
        raise()
        guard !NSApp.isActive else { return }
        // Activation finishes asynchronously; raise the window again once it has,
        // so it ends up in front of the previously active app and takes focus.
        removeActivationObserver()
        activationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.raise()
                self?.removeActivationObserver()
            }
        }
        NSApp.activate()
        // If macOS declines to activate the app, don't jump forward on some
        // later, unrelated activation.
        activationTimeout?.cancel()
        activationTimeout = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            self?.removeActivationObserver()
        }
    }

    private func didSelect(_ tab: SettingsTab) {
        UserDefaults.standard.set(tab.rawValue, forKey: SettingsTab.defaultsKey)
        trackHeight()
        guard let window, window.isVisible else { return }
        resize(window, animated: true)
    }

    // MARK: Sizing

    /// Sizes the window for the current pane and centres it on the screen
    /// with the pointer.
    private func fitToScreen(_ window: NSWindow) {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        let visible: NSRect = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let frame = SettingsLayout.frame(
            contentHeight: idealHeight(of: tabs.selectedTab),
            width: SettingsTab.width,
            chrome: chromeHeight(of: window),
            visible: visible
        )
        window.setFrame(SettingsLayout.centered(frame, in: visible), display: false)
    }

    /// Fits the window to the current pane, keeping its top edge in place.
    private func resize(_ window: NSWindow, animated: Bool) {
        guard let visible = (window.screen ?? NSScreen.main)?.visibleFrame else { return }
        let frame = SettingsLayout.frame(
            contentHeight: idealHeight(of: tabs.selectedTab),
            width: SettingsTab.width,
            chrome: chromeHeight(of: window),
            visible: visible,
            keepingTopOf: window.frame
        )
        guard abs(frame.height - window.frame.height) > 0.5 || frame.origin != window.frame.origin else { return }
        window.setFrame(frame, display: true, animate: animated)
    }

    /// The height `tab`'s whole pane needs, without scrolling.
    private func idealHeight(of tab: SettingsTab) -> CGFloat {
        NSHostingView(rootView: tab.pane().fixedSize(horizontal: false, vertical: true)).fittingSize.height
    }

    /// The title bar and toolbar above the pane.
    private func chromeHeight(of window: NSWindow) -> CGFloat {
        window.frame.height - window.contentLayoutRect.height
    }

    /// Follows changes to the current pane's height (see
    /// `SettingsTab.heightSignature`) and resizes the window to match.
    private func trackHeight() {
        heightTracking += 1
        let generation = heightTracking
        let tab = tabs.selectedTab
        heightSignature = withObservationTracking {
            tab.heightSignature
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.heightMayHaveChanged(generation)
            }
        }
    }

    private func heightMayHaveChanged(_ generation: Int) {
        guard generation == heightTracking else { return }
        let previous = heightSignature
        trackHeight()
        guard heightSignature != previous, let window, window.isVisible else { return }
        resize(window, animated: true)
    }

    // MARK: Bringing it forward

    private func raise() {
        guard let window else { return }
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }

    private func removeActivationObserver() {
        if let activationObserver {
            NotificationCenter.default.removeObserver(activationObserver)
        }
        activationObserver = nil
    }
}

/// The toolbar tabs, one hosted SwiftUI pane each.
private final class SettingsTabViewController: NSTabViewController {
    var onSelect: ((SettingsTab) -> Void)?

    init() {
        super.init(nibName: nil, bundle: nil)
        tabStyle = .toolbar
        for tab in SettingsTab.allCases {
            let pane = NSHostingController(rootView: tab.pane())
            // The window sets the size; a pane scrolls when it's taller than that.
            pane.sizingOptions = []
            pane.title = tab.title
            let item = NSTabViewItem(viewController: pane)
            item.identifier = tab.rawValue
            item.label = tab.title
            item.image = NSImage(systemSymbolName: tab.symbolName, accessibilityDescription: tab.title)
            addTabViewItem(item)
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    var selectedTab: SettingsTab {
        let index = selectedTabViewItemIndex
        return SettingsTab.allCases.indices.contains(index) ? SettingsTab.allCases[index] : .general
    }

    func select(_ tab: SettingsTab) {
        guard let index = SettingsTab.allCases.firstIndex(of: tab) else { return }
        selectedTabViewItemIndex = index
    }

    override func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        super.tabView(tabView, didSelect: tabViewItem)
        guard let identifier = tabViewItem?.identifier as? String, let tab = SettingsTab(rawValue: identifier) else { return }
        onSelect?(tab)
    }
}

/// Where the Settings window goes. Pure, so it can be tested without a screen.
enum SettingsLayout {
    /// Room left between the window and the screen's edges.
    static let margin: CGFloat = 20

    /// The window's frame for a pane `contentHeight` tall below `chrome`:
    /// no taller than the screen allows (the pane scrolls instead), its top
    /// edge where `current`'s was (as when switching tabs), and on screen.
    static func frame(
        contentHeight: CGFloat,
        width: CGFloat,
        chrome: CGFloat,
        visible: NSRect,
        keepingTopOf current: NSRect? = nil
    ) -> NSRect {
        let tallest: CGFloat = visible.height - 2 * margin
        let height: CGFloat = min(contentHeight.rounded(.up) + chrome, tallest)
        guard let current else {
            return NSRect(x: visible.midX - width / 2, y: visible.midY - height / 2, width: width, height: height)
        }
        var origin = NSPoint(x: current.minX, y: current.maxY - height)
        if origin.y < visible.minY + margin {
            origin.y = visible.minY + margin
        }
        if origin.y + height > visible.maxY {
            origin.y = visible.maxY - height
        }
        return NSRect(origin: origin, size: NSSize(width: width, height: height))
    }

    /// `frame` centred in `visible`.
    static func centered(_ frame: NSRect, in visible: NSRect) -> NSRect {
        NSRect(x: (visible.midX - frame.width / 2).rounded(), y: (visible.midY - frame.height / 2).rounded(), width: frame.width, height: frame.height)
    }
}
