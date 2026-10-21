import AppKit
import Carbon.HIToolbox
import SwiftUI

/// A key plus modifiers for a global shortcut, e.g. ⌃⌥Space.
struct KeyCombo: Equatable, Codable, Sendable {
    /// Virtual key code (`kVK_…`).
    var keyCode: UInt32
    /// Raw `NSEvent.ModifierFlags`, limited to ⌃ ⌥ ⇧ ⌘.
    var modifierFlags: UInt
    /// How the key is shown, e.g. "Space" or "K".
    var keyName: String

    static let defaultOpenWheel = KeyCombo(keyCode: UInt32(kVK_Space), modifiers: [.control, .option], keyName: "Space")

    init(keyCode: UInt32, modifiers: NSEvent.ModifierFlags, keyName: String) {
        self.keyCode = keyCode
        self.modifierFlags = modifiers.intersection(TriggerKey.relevantFlags).rawValue
        self.keyName = keyName
    }

    init(event: NSEvent) {
        self.init(
            keyCode: UInt32(event.keyCode),
            modifiers: event.modifierFlags,
            keyName: Self.name(forKeyCode: Int(event.keyCode)) ?? event.charactersIgnoringModifiers?.uppercased() ?? "?"
        )
    }

    var modifiers: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifierFlags) }

    /// A global shortcut needs ⌃, ⌥ or ⌘, or it would swallow ordinary typing.
    var isValid: Bool {
        !modifiers.intersection([.control, .option, .command]).isEmpty
    }

    /// In the order macOS menus use: ⌃⌥⇧⌘ then the key.
    var displayString: String {
        let symbols: [(NSEvent.ModifierFlags, String)] = [(.control, "⌃"), (.option, "⌥"), (.shift, "⇧"), (.command, "⌘")]
        return symbols.filter { modifiers.contains($0.0) }.map(\.1).joined() + keyName
    }

    var carbonModifiers: UInt32 {
        var result: UInt32 = 0
        if modifiers.contains(.command) { result |= UInt32(cmdKey) }
        if modifiers.contains(.option) { result |= UInt32(optionKey) }
        if modifiers.contains(.control) { result |= UInt32(controlKey) }
        if modifiers.contains(.shift) { result |= UInt32(shiftKey) }
        return result
    }

    /// The same shortcut for a SwiftUI menu item, when it can be expressed.
    var keyEquivalent: KeyEquivalent? {
        switch Int(keyCode) {
        case kVK_Space: return .space
        case kVK_Return: return .return
        case kVK_Tab: return .tab
        default:
            guard keyName.count == 1, let character = keyName.lowercased().first else { return nil }
            return KeyEquivalent(character)
        }
    }

    var eventModifiers: SwiftUI.EventModifiers {
        var result: SwiftUI.EventModifiers = []
        if modifiers.contains(.command) { result.insert(.command) }
        if modifiers.contains(.option) { result.insert(.option) }
        if modifiers.contains(.control) { result.insert(.control) }
        if modifiers.contains(.shift) { result.insert(.shift) }
        return result
    }

    /// Names for keys whose character isn't printable or meaningful.
    static func name(forKeyCode keyCode: Int) -> String? {
        switch keyCode {
        case kVK_Space: "Space"
        case kVK_Return: "↩"
        case kVK_Tab: "⇥"
        case kVK_Delete: "⌫"
        case kVK_ForwardDelete: "⌦"
        case kVK_Escape: "⎋"
        case kVK_LeftArrow: "←"
        case kVK_RightArrow: "→"
        case kVK_UpArrow: "↑"
        case kVK_DownArrow: "↓"
        case kVK_F1: "F1"
        case kVK_F2: "F2"
        case kVK_F3: "F3"
        case kVK_F4: "F4"
        case kVK_F5: "F5"
        case kVK_F6: "F6"
        case kVK_F7: "F7"
        case kVK_F8: "F8"
        case kVK_F9: "F9"
        case kVK_F10: "F10"
        case kVK_F11: "F11"
        case kVK_F12: "F12"
        default: nil
        }
    }
}

/// One system-wide shortcut, registered with Carbon's hot-key API, which needs
/// no Accessibility or Input Monitoring permission.
@MainActor
final class GlobalHotKey {
    static let shared = GlobalHotKey()

    var onPress: (() -> Void)?

    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?
    private static let signature: OSType = 0x4F53_484B  // "OSHK"

    /// Replaces the registered shortcut; nil turns it off. Returns false when
    /// macOS refuses the combination (usually because another app owns it).
    @discardableResult
    func register(_ combo: KeyCombo?) -> Bool {
        unregister()
        guard let combo, combo.isValid else { return combo == nil }
        installHandlerIfNeeded()
        let id = EventHotKeyID(signature: Self.signature, id: 1)
        let status = RegisterEventHotKey(combo.keyCode, combo.carbonModifiers, id, GetApplicationEventTarget(), 0, &hotKeyRef)
        if status != noErr {
            hotKeyRef = nil
            return false
        }
        return true
    }

    private func unregister() {
        if let hotKeyRef {
            UnregisterEventHotKey(hotKeyRef)
        }
        hotKeyRef = nil
    }

    private func installHandlerIfNeeded() {
        guard handlerRef == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            // Carbon delivers hot-key events on the main thread.
            MainActor.assumeIsolated {
                GlobalHotKey.shared.onPress?()
            }
            return noErr
        }, 1, &spec, nil, &handlerRef)
    }
}
