import AppKit
import SwiftUI

/// The Settings window, shown in front and focused every time it's asked for.
///
/// OpenShelf is an accessory (menu-bar) app, so it isn't active when Settings…
/// is chosen. SwiftUI's Settings scene left an already-open window behind the
/// app that was in front (e.g. Chrome) even after activating OpenShelf. This
/// window is ordered front right away and again once activation completes,
/// since activation finishes asynchronously.
@MainActor
final class SettingsWindowController: NSWindowController {
    static let shared = SettingsWindowController()

    private var activationObserver: (any NSObjectProtocol)?
    private var activationTimeout: Task<Void, Never>?

    private init() {
        let hostingController = NSHostingController(rootView: SettingsView())
        // The window follows the form's height, e.g. when a warning appears.
        hostingController.sizingOptions = .preferredContentSize
        let window = NSWindow(contentViewController: hostingController)
        window.title = "OpenShelf Settings"
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        // Open on the Space the user is on rather than switching to another.
        window.collectionBehavior = [.moveToActiveSpace]
        super.init(window: window)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /// Opens the window, or brings it back in front if it's already open.
    func show() {
        guard let window else { return }
        if !window.isVisible {
            window.center()
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
