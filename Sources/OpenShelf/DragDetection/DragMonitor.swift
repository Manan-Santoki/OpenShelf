import AppKit
import Carbon.HIToolbox

/// Watches for system-wide file drags and reports when the user presses a wheel
/// trigger during one.
///
/// Polls global input state instead of tapping events, so it needs no
/// Accessibility or Input Monitoring permission: a drag is in progress when the
/// primary button is down and the drag pasteboard has changed since it went down.
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
    private var pollTask: Task<Void, Never>?

    private var wasButtonDown = false
    private var changeCountAtMouseDown = 0
    private var activeWheel: WheelKind?
    private var pendingWheel: WheelKind?
    private var pendingSince: ContinuousClock.Instant?
    private var clockwiseKey = KeyRepeat()
    private var counterClockwiseKey = KeyRepeat()
    private var wasReturnDown = false
    /// Set once Return has run a wedge, so later keys and triggers in the same
    /// drag do nothing.
    private var didConfirm = false

    /// How long a modifier combination must be held before it counts, so that
    /// pressing ⇧ on the way to ⇧⌥ does not flash the wrong wheel.
    private let settleDelay: Duration = .milliseconds(70)
    private let idleInterval: Duration = .milliseconds(150)
    private let activeInterval: Duration = .milliseconds(25)

    init(preferences: Preferences) {
        self.preferences = preferences
    }

    func start() {
        guard pollTask == nil else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let interval = self?.poll() else { return }
                try? await Task.sleep(for: interval)
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
    }

    private func poll() -> Duration {
        let isButtonDown = NSEvent.pressedMouseButtons & 1 == 1
        defer { wasButtonDown = isButtonDown }

        if isButtonDown && !wasButtonDown {
            changeCountAtMouseDown = dragPasteboard.changeCount
            resetDrag()
        } else if !isButtonDown && wasButtonDown {
            if activeWheel != nil { onDragEnded?() }
            resetDrag()
        }
        guard isButtonDown else { return idleInterval }
        guard isDraggingFiles else { return activeInterval }

        if activeWheel != nil, isKeyDown(kVK_Escape) {
            onDragEnded?()
            resetDrag()
            return activeInterval
        }
        guard !didConfirm else { return activeInterval }
        if activeWheel != nil {
            pollWheelKeys()
            guard !didConfirm else { return activeInterval }
        }

        let matcher = TriggerMatcher(triggers: preferences.triggers)
        let wheel = matcher.wheel(for: NSEvent.modifierFlags)
        if wheel != pendingWheel {
            pendingWheel = wheel
            pendingSince = .now
        } else if let wheel, wheel != activeWheel, let pendingSince, pendingSince.duration(to: .now) >= settleDelay {
            // A tap is enough: the wheel stays open after the keys are released.
            // Pressing another trigger mid-drag switches to that wheel.
            activeWheel = wheel
            onTrigger?(wheel, NSEvent.mouseLocation, draggedFileURLs)
        }
        return activeInterval
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
        dragPasteboard.changeCount != changeCountAtMouseDown
            && dragPasteboard.types?.contains(.fileURL) == true
    }

    private var draggedFileURLs: [URL] {
        let objects = dragPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])
        return objects as? [URL] ?? []
    }

    private func resetDrag() {
        activeWheel = nil
        pendingWheel = nil
        pendingSince = nil
        clockwiseKey = KeyRepeat()
        counterClockwiseKey = KeyRepeat()
        wasReturnDown = false
        didConfirm = false
    }
}
