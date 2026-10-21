import AppKit
import Carbon.HIToolbox

/// Watches for system-wide file drags and reports when the user presses a wheel
/// trigger during one.
///
/// Needs no Accessibility or Input Monitoring permission. A mouse-down anywhere
/// (mouse events can be monitored without permission) starts following the
/// button, the modifier keys and the drag pasteboard closely until the button
/// comes back up; a drag is in progress while the button is down and the drag
/// pasteboard has changed since before it went down. Between drags nothing runs
/// but a slow fallback check, so the app costs nothing while idle.
@MainActor
final class DragMonitor {
    /// Called with the wheel to show, the pointer location in screen coordinates
    /// and the files being dragged.
    var onTrigger: ((WheelKind, NSPoint, [URL]) -> Void)?
    /// Called when the primary button is released or Esc is pressed after a trigger.
    var onDragEnded: (() -> Void)?
    /// Called when an arrow key moves the open wheel's highlight.
    var onStep: ((WheelStepDirection) -> Void)?
    /// Called when an arrow is pressed while ⌃ or ⌘ is still held.
    var onArrowsBlocked: (() -> Void)?
    /// Called when Return is pressed with a wheel open, with the dragged files.
    /// Returns whether something ran; if so, the rest of the drag is ignored.
    var onConfirm: (([URL]) -> Bool)?

    private let preferences: Preferences
    private let dragPasteboard = NSPasteboard(name: .drag)
    private var eventMonitors: [Any] = []
    private var fallbackTimer: Timer?
    private var followTask: Task<Void, Never>?
    /// Held while following a press, so App Nap can't stretch the polling
    /// interval of an app with no visible windows.
    private var activity: NSObjectProtocol?

    /// The drag pasteboard's change count from before the button went down.
    private var changeCountBeforeDrag = 0
    private var activeWheel: WheelKind?
    private var chord = ModifierChord(settleDelay: DragMonitor.settleDelay)
    private var clockwiseKey = KeyRepeat()
    private var counterClockwiseKey = KeyRepeat()
    private var wasReturnDown = false
    /// Set once Return has run a wedge, so later keys and triggers in the same
    /// drag do nothing.
    private var didConfirm = false

    /// How long a held modifier combination must stay steady before it counts,
    /// so that pressing ⇧ on the way to ⇧⌥ does not flash the wrong wheel. A
    /// tap counts as soon as it's released.
    private static let settleDelay: Duration = .milliseconds(70)
    private static let followInterval: Duration = .milliseconds(16)
    /// Catches a press whose mouse-down event never arrived.
    private static let fallbackInterval: TimeInterval = 1

    init(preferences: Preferences) {
        self.preferences = preferences
    }

    func start() {
        guard eventMonitors.isEmpty else { return }
        changeCountBeforeDrag = dragPasteboard.changeCount
        // Presses in other apps, then presses in OpenShelf's own windows (shelves).
        if let monitor = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDown, handler: { [weak self] _ in
            MainActor.assumeIsolated { self?.followPress() }
        }) {
            eventMonitors.append(monitor)
        }
        if let monitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown, handler: { [weak self] event in
            MainActor.assumeIsolated { self?.followPress() }
            return event
        }) {
            eventMonitors.append(monitor)
        }
        let timer = Timer(timeInterval: Self.fallbackInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, NSEvent.pressedMouseButtons & 1 == 1 else { return }
                self.followPress()
            }
        }
        timer.tolerance = Self.fallbackInterval / 2
        RunLoop.main.add(timer, forMode: .common)
        fallbackTimer = timer
    }

    func stop() {
        eventMonitors.forEach(NSEvent.removeMonitor)
        eventMonitors = []
        fallbackTimer?.invalidate()
        fallbackTimer = nil
        followTask?.cancel()
        endFollowing()
    }

    /// Polls closely until the primary button is released.
    private func followPress() {
        guard followTask == nil else { return }
        resetDrag()
        activity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiatedAllowingIdleSystemSleep, .latencyCritical],
            reason: "Following a drag"
        )
        followTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, self.poll() else { break }
                try? await Task.sleep(for: Self.followInterval)
            }
            self?.endFollowing()
        }
    }

    private func endFollowing() {
        followTask = nil
        resetDrag()
        // Taken after the press, so the next drag is measured from here.
        changeCountBeforeDrag = dragPasteboard.changeCount
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
    }

    /// One reading while the button is down. Returns false once it's released.
    private func poll() -> Bool {
        guard NSEvent.pressedMouseButtons & 1 == 1 else {
            if activeWheel != nil { onDragEnded?() }
            return false
        }
        guard isDraggingFiles else { return true }

        if activeWheel != nil, isKeyDown(kVK_Escape) {
            // Keeps the modifier state, so a trigger still held doesn't reopen
            // the wheel; a fresh tap does.
            onDragEnded?()
            activeWheel = nil
            clockwiseKey = KeyRepeat()
            counterClockwiseKey = KeyRepeat()
            return true
        }
        guard !didConfirm else { return true }
        if activeWheel != nil {
            pollWheelKeys()
            guard !didConfirm else { return true }
        }

        // A tap is enough: the wheel stays open after the keys are released.
        // Pressing another trigger mid-drag switches to that wheel.
        if let flags = chord.update(NSEvent.modifierFlags, at: .now),
           let wheel = TriggerMatcher(triggers: preferences.triggers).wheel(for: flags),
           wheel != activeWheel {
            activeWheel = wheel
            onTrigger?(wheel, NSEvent.mouseLocation, draggedFileURLs)
        }
        return true
    }

    /// Arrow keys step the highlight (with auto-repeat); Return runs it.
    private func pollWheelKeys() {
        let now = ContinuousClock.now
        var clockwise = isKeyDown(kVK_RightArrow) || isKeyDown(kVK_DownArrow)
        var counterClockwise = isKeyDown(kVK_LeftArrow) || isKeyDown(kVK_UpArrow)
        // ⌃ or ⌘ with an arrow is a system shortcut (⌃→ switches desktops), so
        // arrows only count once those keys are released.
        if !NSEvent.modifierFlags.intersection([.control, .command]).isEmpty {
            if clockwise || counterClockwise { onArrowsBlocked?() }
            clockwise = false
            counterClockwise = false
        }
        if clockwiseKey.update(isDown: clockwise, at: now) {
            onStep?(.clockwise)
        }
        if counterClockwiseKey.update(isDown: counterClockwise, at: now) {
            onStep?(.counterClockwise)
        }
        let isReturnDown = isKeyDown(kVK_Return) || isKeyDown(kVK_ANSI_KeypadEnter)
        if isReturnDown && !wasReturnDown, onConfirm?(draggedFileURLs) == true {
            didConfirm = true
        }
        wasReturnDown = isReturnDown
    }

    private func isKeyDown(_ keyCode: Int) -> Bool {
        CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(keyCode))
    }

    private var isDraggingFiles: Bool {
        dragPasteboard.changeCount != changeCountBeforeDrag
            && dragPasteboard.types?.contains(.fileURL) == true
    }

    private var draggedFileURLs: [URL] {
        let objects = dragPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])
        return objects as? [URL] ?? []
    }

    private func resetDrag() {
        activeWheel = nil
        chord = ModifierChord(settleDelay: Self.settleDelay)
        clockwiseKey = KeyRepeat()
        counterClockwiseKey = KeyRepeat()
        wasReturnDown = false
        didConfirm = false
    }
}
