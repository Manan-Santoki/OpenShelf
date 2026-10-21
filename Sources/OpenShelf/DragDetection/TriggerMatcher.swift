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

/// Turns modifier readings taken during a drag into the combinations that
/// should open a wheel. A quick tap counts the moment a key is let go, with
/// every key that was down; a held combination counts once it has been steady
/// for `settleDelay`, so pressing ⇧ on the way to ⇧⌥ doesn't flash Convert.
struct ModifierChord {
    var settleDelay: Duration

    /// Every key held since all were last up.
    private var chord: NSEvent.ModifierFlags = []
    private var held: NSEvent.ModifierFlags = []
    private var heldSince: ContinuousClock.Instant?
    private var lastReported: NSEvent.ModifierFlags?

    init(settleDelay: Duration) {
        self.settleDelay = settleDelay
    }

    /// Records the keys held at `now`; returns a combination when one is chosen.
    mutating func update(_ flags: NSEvent.ModifierFlags, at now: ContinuousClock.Instant) -> NSEvent.ModifierFlags? {
        let flags = flags.intersection(TriggerKey.relevantFlags)
        var chosen: NSEvent.ModifierFlags?
        if flags != held {
            let didRelease = !held.isSubset(of: flags)
            held = flags
            heldSince = now
            if didRelease, !chord.isEmpty, chord != lastReported {
                chosen = chord
            }
            chord.formUnion(flags)
        } else if !held.isEmpty, held == chord, held != lastReported,
                  let heldSince, heldSince.duration(to: now) >= settleDelay {
            chosen = held
        }
        if let chosen { lastReported = chosen }
        if flags.isEmpty {
            chord = []
            lastReported = nil
        }
        return chosen
    }
}
