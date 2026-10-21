import AppKit

/// Maps the currently held modifier keys to the wheel they trigger.
struct TriggerMatcher {
    var triggers: [WheelKind: NSEvent.ModifierFlags]

    /// Returns the wheel whose trigger exactly equals the held modifiers, ignoring
    /// non-modifier flags such as Caps Lock or Fn.
    func wheel(for held: NSEvent.ModifierFlags) -> WheelKind? {
        let held = held.intersection(TriggerKey.relevantFlags)
        guard !held.isEmpty else { return nil }
        return triggers.first { $0.value.intersection(TriggerKey.relevantFlags) == held }?.key
    }
}
