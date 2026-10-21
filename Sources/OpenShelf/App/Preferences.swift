import AppKit
import Observation

/// User settings, persisted in `UserDefaults`.
@MainActor
@Observable
final class Preferences {
    static let shared = Preferences()

    private enum Key {
        static let actionsTrigger = "actionsWheelTrigger"
        static let removeAfterDragOut = "removeAfterDragOut"
    }

    @ObservationIgnored private let defaults: UserDefaults

    /// Modifier keys that open the Actions wheel during a file drag.
    var actionsTrigger: NSEvent.ModifierFlags {
        didSet { defaults.set(Int(actionsTrigger.rawValue), forKey: Key.actionsTrigger) }
    }

    /// Remove files from a shelf once they have been dragged out of it.
    var removeAfterDragOut: Bool {
        didSet { defaults.set(removeAfterDragOut, forKey: Key.removeAfterDragOut) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let storedTrigger = NSEvent.ModifierFlags(rawValue: UInt(defaults.integer(forKey: Key.actionsTrigger)))
            .intersection(TriggerKey.relevantFlags)
        actionsTrigger = storedTrigger.isEmpty ? .control : storedTrigger
        removeAfterDragOut = defaults.bool(forKey: Key.removeAfterDragOut)
    }

    func trigger(for kind: WheelKind) -> NSEvent.ModifierFlags {
        switch kind {
        case .actions: actionsTrigger
        }
    }
}

/// A modifier key that can take part in a wheel trigger.
enum TriggerKey: CaseIterable, Identifiable {
    case control, option, shift, command

    static let relevantFlags: NSEvent.ModifierFlags = [.control, .option, .shift, .command]

    var id: Self { self }

    var flag: NSEvent.ModifierFlags {
        switch self {
        case .control: .control
        case .option: .option
        case .shift: .shift
        case .command: .command
        }
    }

    var symbol: String {
        switch self {
        case .control: "⌃"
        case .option: "⌥"
        case .shift: "⇧"
        case .command: "⌘"
        }
    }

    var name: String {
        switch self {
        case .control: "Control"
        case .option: "Option"
        case .shift: "Shift"
        case .command: "Command"
        }
    }

    static func describe(_ flags: NSEvent.ModifierFlags) -> String {
        allCases.filter { flags.contains($0.flag) }.map(\.symbol).joined()
    }
}
