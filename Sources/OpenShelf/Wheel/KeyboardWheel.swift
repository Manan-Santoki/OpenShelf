import AppKit

/// The wheels a keyboard-opened session can show, and how Tab moves between them.
enum WheelCycle {
    static let order: [WheelKind] = [.convert, .tools, .actions]

    /// The kinds that have at least one wedge, in wheel order.
    static func available(where hasItems: (WheelKind) -> Bool) -> [WheelKind] {
        order.filter(hasItems)
    }

    /// The wheel after `kind`, wrapping around; nil when there's nowhere to go.
    static func next(after kind: WheelKind, in available: [WheelKind], backwards: Bool = false) -> WheelKind? {
        guard available.count > 1, let index = available.firstIndex(of: kind) else { return nil }
        let offset = backwards ? -1 : 1
        return available[(index + offset + available.count) % available.count]
    }
}

/// Recognises a tap of modifier keys (pressed and released with no other key in
/// between), e.g. ⇧⌥ to switch to the Tools wheel while it's open.
struct ModifierTapTracker {
    private var pressed: NSEvent.ModifierFlags = []
    private var interrupted = false
    /// Keys that were already down when tracking began, such as the ⌃⌥ of the
    /// shortcut that opened the wheel. Letting go of them is not a tap.
    private var heldOver: NSEvent.ModifierFlags

    init(alreadyHeld: NSEvent.ModifierFlags = []) {
        heldOver = alreadyHeld.intersection(TriggerKey.relevantFlags)
    }

    /// Feed every modifier change; returns the combination that was tapped when
    /// the last key comes up.
    mutating func flagsChanged(_ flags: NSEvent.ModifierFlags) -> NSEvent.ModifierFlags? {
        let flags = flags.intersection(TriggerKey.relevantFlags)
        // Nothing counts until every key held over has come up.
        heldOver.formIntersection(flags)
        if !heldOver.isEmpty { interrupted = true }
        guard flags.isEmpty else {
            pressed.formUnion(flags)
            return nil
        }
        defer {
            pressed = []
            interrupted = false
        }
        return interrupted || pressed.isEmpty ? nil : pressed
    }

    /// A regular key while modifiers are held means a shortcut, not a tap.
    mutating func keyPressed() {
        if !pressed.isEmpty { interrupted = true }
    }
}

extension WheelKind {
    /// The wheel's name on its own, for the label above the wheel.
    var shortTitle: String {
        switch self {
        case .convert: "Convert"
        case .tools: "Tools"
        case .actions: "Actions"
        }
    }
}
