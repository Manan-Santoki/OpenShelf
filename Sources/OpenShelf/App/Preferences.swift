import AppKit
import Observation

/// User settings, persisted in `UserDefaults`.
@MainActor
@Observable
final class Preferences {
    static let shared = Preferences()

    private enum Key {
        static let removeAfterDragOut = "removeAfterDragOut"

        static func trigger(_ kind: WheelKind) -> String {
            switch kind {
            case .convert: "convertWheelTrigger"
            case .tools: "toolsWheelTrigger"
            case .actions: "actionsWheelTrigger"
            }
        }
    }

    @ObservationIgnored private let defaults: UserDefaults

    /// Modifier keys that open each wheel during a file drag.
    private(set) var triggers: [WheelKind: NSEvent.ModifierFlags]

    /// Remove files from a shelf once they have been dragged out of it.
    var removeAfterDragOut: Bool {
        didSet { defaults.set(removeAfterDragOut, forKey: Key.removeAfterDragOut) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        var triggers: [WheelKind: NSEvent.ModifierFlags] = [:]
        for kind in WheelKind.allCases {
            let stored = NSEvent.ModifierFlags(rawValue: UInt(defaults.integer(forKey: Key.trigger(kind))))
                .intersection(TriggerKey.relevantFlags)
            triggers[kind] = stored.isEmpty ? Self.defaultTrigger(for: kind) : stored
        }
        self.triggers = triggers
        removeAfterDragOut = defaults.bool(forKey: Key.removeAfterDragOut)
    }

    static func defaultTrigger(for kind: WheelKind) -> NSEvent.ModifierFlags {
        switch kind {
        case .convert: .shift
        case .tools: [.shift, .option]
        case .actions: .control
        }
    }

    func trigger(for kind: WheelKind) -> NSEvent.ModifierFlags {
        triggers[kind] ?? Self.defaultTrigger(for: kind)
    }

    func setTrigger(_ flags: NSEvent.ModifierFlags, for kind: WheelKind) {
        triggers[kind] = flags
        defaults.set(Int(flags.rawValue), forKey: Key.trigger(kind))
    }

    /// Wheels whose trigger is also used by another wheel; only one of them can open.
    var conflictingWheels: Set<WheelKind> {
        var conflicts: Set<WheelKind> = []
        for kind in WheelKind.allCases {
            for other in WheelKind.allCases where other != kind && trigger(for: other) == trigger(for: kind) {
                conflicts.insert(kind)
            }
        }
        return conflicts
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
