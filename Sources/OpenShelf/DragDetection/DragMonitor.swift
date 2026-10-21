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
    /// Called with the wheel to show and the pointer location in screen coordinates.
    var onTrigger: ((WheelKind, NSPoint) -> Void)?
    /// Called when the primary button is released or Esc is pressed after a trigger.
    var onDragEnded: (() -> Void)?

    private let preferences: Preferences
    private let dragPasteboard = NSPasteboard(name: .drag)
    private var pollTask: Task<Void, Never>?

    private var wasButtonDown = false
    private var changeCountAtMouseDown = 0
    private var activeWheel: WheelKind?
    private var pendingWheel: WheelKind?
    private var pendingSince: ContinuousClock.Instant?

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

        if activeWheel != nil, CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(kVK_Escape)) {
            onDragEnded?()
            resetDrag()
            return activeInterval
        }

        let matcher = TriggerMatcher(triggers: [.actions: preferences.trigger(for: .actions)])
        let wheel = matcher.wheel(for: NSEvent.modifierFlags)
        if wheel != pendingWheel {
            pendingWheel = wheel
            pendingSince = .now
        } else if let wheel, wheel != activeWheel, let pendingSince, pendingSince.duration(to: .now) >= settleDelay {
            // A tap is enough: the wheel stays open after the keys are released.
            activeWheel = wheel
            onTrigger?(wheel, NSEvent.mouseLocation)
        }
        return activeInterval
    }

    private var isDraggingFiles: Bool {
        dragPasteboard.changeCount != changeCountAtMouseDown
            && dragPasteboard.types?.contains(.fileURL) == true
    }

    private func resetDrag() {
        activeWheel = nil
        pendingWheel = nil
        pendingSince = nil
    }
}
