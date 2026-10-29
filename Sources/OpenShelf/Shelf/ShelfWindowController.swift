import AppKit
import Carbon.HIToolbox
import Observation
import Quartz
import SwiftUI

/// Owns one shelf's panel: sizing, placement, edge docking, hiding at the
/// screen edge and keyboard handling.
@MainActor
final class ShelfWindowController: NSWindowController, NSWindowDelegate {
    let shelf: Shelf
    var onClose: ((ShelfWindowController) -> Void)?
    var onBecomeKey: ((ShelfWindowController) -> Void)?
    /// Called when anything worth saving between launches changes.
    var onStateChange: (() -> Void)?

    private let docking = EdgeDocking()
    private var isApplyingFrame = false
    private var settleTask: Task<Void, Never>?
    private var isClosing = false
    private var typeSelect = TypeSelect()

    // Hiding at the screen edge.
    private let edgeTab = EdgeTab()
    private var edgeWatchTask: Task<Void, Never>?
    private var pointerAwaySince: ContinuousClock.Instant?
    /// The on-screen frame while the shelf is tucked behind the edge.
    private var shownFrame: CGRect?
    private var isHidden: Bool { shownFrame != nil }
    /// Set when the user asked to see the shelf; it stays out until the pointer
    /// has been over it, rather than tucking away again before they find it.
    private var holdsOpenUntilVisited = false

    private var panel: ShelfPanel? { window as? ShelfPanel }

    /// The frame to save and restore: where the shelf shows, even while hidden.
    var snapshot: ShelfSnapshot? {
        guard !shelf.items.isEmpty, let frame = shownFrame ?? panel?.frame else { return nil }
        return ShelfSnapshot(
            items: shelf.items.map { ShelfSnapshot.Item(path: $0.url.path, bookmark: $0.bookmark) },
            frame: frame,
            dockedEdge: shelf.dockedEdge,
            isExpanded: shelf.isExpanded,
            viewMode: shelf.viewMode
        )
    }

    /// - Parameter restoringFrame: Where a shelf from a previous launch was; when
    ///   nil the shelf opens centred on `location`.
    init(shelf: Shelf, near location: NSPoint, restoringFrame: CGRect? = nil) {
        self.shelf = shelf
        let panel = ShelfPanel(size: Self.size(for: shelf))
        super.init(window: panel)

        panel.delegate = self
        panel.keyDownHandler = { [weak self] event in self?.handleKeyDown(event) ?? false }
        panel.contentView = hostingContainer(
            for: ShelfView(shelf: shelf, close: { [weak self] in self?.dismiss() }),
            size: panel.frame.size,
            in: ShelfDropView(shelf: shelf)
        )

        if let restoringFrame {
            restore(restoringFrame)
        } else {
            place(near: location)
        }
        observeLayout()
        edgeTab.onReveal = { [weak self] in self?.reveal() }
        watchEdge()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func present() {
        if isHidden {
            reveal()
        } else {
            panel?.orderFrontRegardless()
        }
    }

    /// Brings the shelf to the front, sliding it out if it's hidden at an edge.
    func bringForward(focus: Bool) {
        holdsOpenUntilVisited = true
        pointerAwaySince = nil
        present()
        if focus {
            panel?.makeKey()
        }
    }

    func dismiss() {
        guard !isClosing else { return }
        isClosing = true
        settleTask?.cancel()
        edgeWatchTask?.cancel()
        edgeTab.hide()
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

    /// Puts a shelf from a previous launch back, kept on screen in case the
    /// displays changed since.
    private func restore(_ frame: CGRect) {
        let center = CGPoint(x: frame.midX, y: frame.midY)
        let screen = NSScreen.screens.first { $0.frame.contains(center) } ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }
        var sized = frame
        sized.size = Self.size(for: shelf)
        sized.origin.y = frame.maxY - sized.height
        let settled = docking.settle(sized, in: visibleFrame)
        shelf.dockedEdge = settled.edge
        setFrame(settled.frame, animated: false)
    }

    /// Re-runs `layoutDidChange` whenever something that affects the window size changes.
    private func observeLayout() {
        withObservationTracking {
            _ = shelf.items.count
            _ = shelf.isExpanded
            _ = shelf.viewMode
            _ = shelf.dockedEdge
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
        defer { onStateChange?() }
        guard let panel, let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame else { return }

        // Grow or shrink from the top edge, and from the docked side when docked.
        let size = Self.size(for: shelf)
        var frame = shownFrame ?? panel.frame
        frame.origin.y = frame.maxY - size.height
        if shelf.dockedEdge == .right {
            frame.origin.x = frame.maxX - size.width
        } else if shelf.dockedEdge == nil {
            frame.origin.x = frame.midX - size.width / 2
        }
        frame.size = size
        let settled = docking.settle(frame, in: visibleFrame).frame
        if isHidden {
            // Resize where it will reappear; the tab keeps showing the count.
            shownFrame = settled
            if let edge = shelf.dockedEdge {
                edgeTab.show(at: EdgeHiding.tabFrame(for: settled, edge: edge, in: visibleFrame), edge: edge, count: shelf.items.count)
            }
            return
        }
        setFrame(settled, animated: true)
    }

    private func setFrame(_ frame: NSRect, animated: Bool) {
        guard let panel else { return }
        isApplyingFrame = true
        if animated && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Motion.resizeDuration
                context.timingFunction = Motion.move
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
            onStateChange?()
        }
    }

    // MARK: Hiding at the screen edge

    /// Watches the pointer while the shelf is docked, and tucks the shelf away
    /// once the pointer has stayed clear of it for a moment.
    private func watchEdge() {
        edgeWatchTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !isClosing else { return }
                checkEdgeHiding()
            }
        }
    }

    private func checkEdgeHiding() {
        guard let panel, let edge = shelf.dockedEdge, Preferences.shared.hidesDockedShelves else {
            pointerAwaySince = nil
            if isHidden { reveal() }
            return
        }
        guard !isHidden else { return }
        if holdsOpenUntilVisited {
            if EdgeHiding.isPointerNear(NSEvent.mouseLocation, shelf: panel.frame) {
                holdsOpenUntilVisited = false
            }
            return
        }

        let isBusy = NSEvent.pressedMouseButtons != 0
            || isApplyingFrame
            || shelf.items.isEmpty
            || (QLPreviewPanel.sharedPreviewPanelExists() && QLPreviewPanel.shared().isVisible)
        if isBusy || EdgeHiding.isPointerNear(NSEvent.mouseLocation, shelf: panel.frame) {
            pointerAwaySince = nil
            return
        }
        let since = pointerAwaySince ?? .now
        pointerAwaySince = since
        if since.duration(to: .now) >= .seconds(EdgeHiding.hideDelay) {
            hide(to: edge)
        }
    }

    private func hide(to edge: ShelfEdge) {
        guard let panel, !isHidden, let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame else { return }
        let shown = panel.frame
        shownFrame = shown
        pointerAwaySince = nil
        let reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion

        let tabFrame = EdgeHiding.tabFrame(for: shown, edge: edge, in: visibleFrame)
        isApplyingFrame = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = reduceMotion ? 0.15 : Motion.hideDuration
            if !reduceMotion {
                // Visibly slides the whole way: an ease-in alone keeps it still
                // while it fades, then moves it once it can't be seen.
                context.timingFunction = Motion.slideAway
                panel.animator().setFrame(EdgeHiding.hiddenFrame(for: shown, edge: edge, in: visibleFrame), display: true)
            }
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.isHidden else { return }
                self.panel?.orderOut(nil)
                self.isApplyingFrame = false
                // The tab takes over once the shelf has gone, not before.
                self.edgeTab.show(at: tabFrame, edge: edge, count: self.shelf.items.count)
            }
        }
        NSAnimationContext.runAnimationGroup { context in
            // Stays solid for most of the slide and fades as it reaches the
            // edge, so it doesn't linger over a neighbouring display.
            context.duration = reduceMotion ? 0.15 : Motion.hideDuration
            context.timingFunction = reduceMotion ? CAMediaTimingFunction(name: .easeOut) : Motion.lateFade
            panel.animator().alphaValue = 0
        }
    }

    /// Slides a hidden shelf back out from the edge.
    private func reveal() {
        guard let panel, let shown = shownFrame else { return }
        shownFrame = nil
        pointerAwaySince = nil
        edgeTab.hide()
        let reduceMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        if let edge = shelf.dockedEdge, !reduceMotion, let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame {
            panel.setFrame(EdgeHiding.hiddenFrame(for: shown, edge: edge, in: visibleFrame), display: false)
        } else {
            panel.setFrame(shown, display: false)
        }
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        // Draw it now, before the slide's clock starts; otherwise that first
        // draw eats the start of the slide and the shelf appears part way out.
        panel.displayIfNeeded()
        CATransaction.flush()

        isApplyingFrame = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = reduceMotion ? 0.15 : Motion.revealDuration
            context.timingFunction = Motion.move
            panel.animator().setFrame(shown, display: true)
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                self?.isApplyingFrame = false
                self?.panel?.invalidateShadow()
            }
        }
        NSAnimationContext.runAnimationGroup { context in
            // Solid early in the slide, so it reads as sliding out rather than
            // fading in.
            context.duration = reduceMotion ? 0.15 : Motion.revealDuration * 0.4
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = 1
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
            // Typing a name selects the first matching file, as in Finder.
            guard modifiers.isSubset(of: .shift),
                  let characters = event.characters,
                  TypeSelect.isTypingCharacter(characters)
            else { return false }
            shelf.typeSelect(typeSelect.append(characters, at: event.timestamp))
        }
        return true
    }
}

/// Timing for the shelf's own movements: short, with no bounce, like AppKit's
/// own. The window moves frame by frame, so a curve that does most of its
/// travel in the first few frames looks like a jump.
@MainActor
private enum Motion {
    static let resizeDuration = 0.25
    static let revealDuration = 0.25
    static let hideDuration = 0.25

    /// Core Animation's default curve: a short ease in and a long, soft settle.
    static let move = CAMediaTimingFunction(controlPoints: 0.25, 0.1, 0.25, 1)
    /// Speeds up, then slows to a stop just past the edge.
    static let slideAway = CAMediaTimingFunction(name: .easeInEaseOut)
    /// Barely fades until the end.
    static let lateFade = CAMediaTimingFunction(controlPoints: 0.55, 0, 0.9, 0.6)
}
