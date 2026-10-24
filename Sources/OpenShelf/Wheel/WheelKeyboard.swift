/// Which way an arrow key moves the wheel's highlight.
enum WheelStepDirection: Equatable {
    case clockwise
    case counterClockwise
}

/// Moves a wheel's highlight one wedge at a time, wrapping around.
enum WheelKeyboardNavigator {
    /// The wedge to highlight after one step from `current`, passing over
    /// wedges that can't be chosen. With nothing highlighted yet, clockwise
    /// starts at wedge 0 and counter-clockwise at the last wedge. Nil when no
    /// wedge can be chosen.
    static func step(
        from current: Int?,
        count: Int,
        direction: WheelStepDirection,
        isSelectable: (Int) -> Bool = { _ in true }
    ) -> Int? {
        guard count > 0 else { return nil }
        let offset = direction == .clockwise ? 1 : -1
        // One step before the first wedge to try.
        var index: Int = direction == .clockwise ? -1 : count
        if let current, (0..<count).contains(current) {
            index = current
        }
        for _ in 0..<count {
            index = (index + offset + count) % count
            if isSelectable(index) { return index }
        }
        return nil
    }

    /// One step over a page of `slots`, skipping empty ones.
    static func step(from current: Int?, in slots: [WheelSlot], direction: WheelStepDirection) -> Int? {
        step(from: current, count: slots.count, direction: direction) { !slots[$0].isEmpty }
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

/// Number keys for a keyboard-opened wheel: holding ⌘ shows each wedge's
/// number, and ⌘ with that number runs it. A page holds at most seven wedges,
/// so items are 1 to 7 clockwise from 12 o'clock, and More or Back is 0.
enum WheelBadges {
    /// The key shown on each wedge of `slots`, by slot index; empty wedges have none.
    static func keys(for slots: [WheelSlot]) -> [Int: String] {
        var keys: [Int: String] = [:]
        var number = 1
        for (index, slot) in slots.enumerated() {
            switch slot {
            case .item:
                keys[index] = "\(number)"
                number += 1
            case .more, .back:
                keys[index] = "0"
            case .empty:
                continue
            }
        }
        return keys
    }

    /// The wedge `key` ("1"…"9", "0") picks on `slots`, or nil.
    static func slot(forKey key: String, in slots: [WheelSlot]) -> Int? {
        keys(for: slots).first { $0.value == key }?.key
    }
}
