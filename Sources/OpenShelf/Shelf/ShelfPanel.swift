import AppKit
import Quartz

/// Borderless floating panel that hosts a shelf. It can become key (for keyboard
/// shortcuts and Quick Look) without activating the app or stealing focus from
/// the app the user is working in.
final class ShelfPanel: NSPanel {
    /// Return true when the event was handled.
    var keyDownHandler: ((NSEvent) -> Bool)?

    init(size: CGSize) {
        super.init(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isFloatingPanel = true
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
        animationBehavior = .utilityWindow
    }

    override var canBecomeKey: Bool { true }

    override func keyDown(with event: NSEvent) {
        if keyDownHandler?(event) != true {
            super.keyDown(with: event)
        }
    }

    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool {
        true
    }

    // Quick Look calls these on the main thread, but the SDK doesn't annotate them.
    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = QuickLookCoordinator.shared
        }
    }

    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = nil
        }
    }
}
