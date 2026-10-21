import AppKit
import Carbon.HIToolbox
import Observation
import SwiftUI

/// Owns one shelf's panel: sizing, placement, edge docking and keyboard handling.
@MainActor
final class ShelfWindowController: NSWindowController, NSWindowDelegate {
    let shelf: Shelf
    var onClose: ((ShelfWindowController) -> Void)?
    var onBecomeKey: ((ShelfWindowController) -> Void)?

    private let docking = EdgeDocking()
    private var isApplyingFrame = false
    private var settleTask: Task<Void, Never>?
    private var isClosing = false

    private var panel: ShelfPanel? { window as? ShelfPanel }

    init(shelf: Shelf, near location: NSPoint) {
        self.shelf = shelf
        let panel = ShelfPanel(size: Self.size(for: shelf))
        super.init(window: panel)

        panel.delegate = self
        panel.keyDownHandler = { [weak self] event in self?.handleKeyDown(event) ?? false }
        let hostingView = NSHostingView(rootView: ShelfView(shelf: shelf, close: { [weak self] in self?.dismiss() }))
        hostingView.sizingOptions = []
        panel.contentView = hostingView

        place(near: location)
        observeLayout()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        panel?.orderFrontRegardless()
    }

    func dismiss() {
        guard !isClosing else { return }
        isClosing = true
        settleTask?.cancel()
        window?.close()
        onClose?(self)
    }

    // MARK: Layout

    private static func size(for shelf: Shelf) -> CGSize {
        ShelfLayout.size(itemCount: shelf.items.count, isExpanded: shelf.isExpanded, viewMode: shelf.viewMode)
    }

    private func place(near location: NSPoint) {
        guard let panel else { return }
        let size = panel.frame.size
        let frame = NSRect(x: location.x - size.width / 2, y: location.y - size.height / 2, width: size.width, height: size.height)
        let screen = NSScreen.screens.first { $0.frame.contains(location) } ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }
        let settled = docking.settle(frame, in: visibleFrame)
        shelf.dockedEdge = settled.edge
        setFrame(settled.frame, animated: false)
    }

    /// Re-runs `layoutDidChange` whenever something that affects the window size changes.
    private func observeLayout() {
        withObservationTracking {
            _ = shelf.items.count
            _ = shelf.isExpanded
            _ = shelf.viewMode
        } onChange: { [weak self] in
            Task { @MainActor in
                guard let self, !self.isClosing else { return }
                self.layoutDidChange()
                self.observeLayout()
            }
        }
    }

    private func layoutDidChange() {
        if shelf.items.isEmpty && shelf.hasHadItems {
            dismiss()
            return
        }
        guard let panel, let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame else { return }

        // Grow or shrink from the top edge, and from the docked side when docked.
        let size = Self.size(for: shelf)
        var frame = panel.frame
        frame.origin.y = frame.maxY - size.height
        if shelf.dockedEdge == .right {
            frame.origin.x = frame.maxX - size.width
        } else if shelf.dockedEdge == nil {
            frame.origin.x = frame.midX - size.width / 2
        }
        frame.size = size
        setFrame(docking.settle(frame, in: visibleFrame).frame, animated: true)
    }

    private func setFrame(_ frame: NSRect, animated: Bool) {
        guard let panel else { return }
        isApplyingFrame = true
        if animated && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                // Fast start with a long, soft settle.
                context.duration = 0.32
                context.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 0.9, 0.25, 1)
                panel.animator().setFrame(frame, display: true)
            } completionHandler: { [weak self] in
                MainActor.assumeIsolated {
                    self?.isApplyingFrame = false
                    self?.panel?.invalidateShadow()
                }
            }
        } else {
            panel.setFrame(frame, display: true)
            panel.invalidateShadow()
            isApplyingFrame = false
        }
    }

    // MARK: NSWindowDelegate

    func windowDidMove(_ notification: Notification) {
        guard !isApplyingFrame else { return }
        scheduleSettle()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        shelf.removeMissingFiles()
        onBecomeKey?(self)
    }

    /// Docks or clamps the shelf once the user lets go of it. Window moves are
    /// driven by the window server, so poll for the button release.
    private func scheduleSettle() {
        settleTask?.cancel()
        settleTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(100))
            guard let self, !Task.isCancelled else { return }
            if NSEvent.pressedMouseButtons & 1 == 1 {
                scheduleSettle()
                return
            }
            guard let panel, let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame else { return }
            let settled = docking.settle(panel.frame, in: visibleFrame)
            shelf.dockedEdge = settled.edge
            if settled.frame != panel.frame {
                setFrame(settled.frame, animated: true)
            }
        }
    }

    // MARK: Keyboard

    private func handleKeyDown(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection(TriggerKey.relevantFlags)
        switch Int(event.keyCode) {
        case kVK_Space where modifiers.isEmpty:
            QuickLookCoordinator.shared.toggle(shelf.selectionOrAll.map(\.url))
        case kVK_Delete where modifiers.isEmpty, kVK_ForwardDelete where modifiers.isEmpty:
            shelf.remove(shelf.selection)
        case kVK_Return where modifiers.isEmpty:
            FileActions.open(shelf.selectionOrAll.map(\.url))
        case kVK_Escape:
            if shelf.isExpanded {
                shelf.isExpanded = false
            } else {
                dismiss()
            }
        case kVK_ANSI_A where modifiers == .command:
            shelf.selectAll()
        case kVK_ANSI_W where modifiers == .command:
            dismiss()
        default:
            return false
        }
        return true
    }
}
