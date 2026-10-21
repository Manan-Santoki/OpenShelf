/// Which way an arrow key moves the wheel's highlight.
enum WheelStepDirection: Equatable {
    case clockwise
    case counterClockwise
}

/// Moves a wheel's highlight one wedge at a time, wrapping around.
enum WheelKeyboardNavigator {
    /// The wedge to highlight after one step from `current`. With nothing
    /// highlighted yet, clockwise starts at wedge 0 and counter-clockwise at the
    /// last wedge.
    static func step(from current: Int?, count: Int, direction: WheelStepDirection) -> Int? {
        guard count > 0 else { return nil }
        guard let current, (0..<count).contains(current) else {
            return direction == .clockwise ? 0 : count - 1
        }
        let offset = direction == .clockwise ? 1 : -1
        return (current + offset + count) % count
    }
}

/// Turns a polled "is this key down?" reading into key presses with
/// auto-repeat: one press when the key goes down, then repeats after
/// `initialDelay`, every `interval`, while it stays down.
struct KeyRepeat {
    var initialDelay: Duration = .milliseconds(350)
    var interval: Duration = .milliseconds(120)

    private var pressedAt: ContinuousClock.Instant?
    private var lastFiredAt: ContinuousClock.Instant?

    /// Returns true when a press should fire for the key state seen at `now`.
    mutating func update(isDown: Bool, at now: ContinuousClock.Instant) -> Bool {
        guard isDown else {
            pressedAt = nil
            lastFiredAt = nil
            return false
        }
        guard let pressedAt, let lastFiredAt else {
            self.pressedAt = now
            lastFiredAt = now
            return true
        }
        let wait = lastFiredAt == pressedAt ? initialDelay : interval
        guard lastFiredAt.duration(to: now) >= wait else { return false }
        self.lastFiredAt = now
        return true
    }
}
