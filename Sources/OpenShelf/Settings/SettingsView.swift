import ServiceManagement
import SwiftUI

struct SettingsView: View {
    @Bindable private var preferences = Preferences.shared
    @State private var opensAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        Form {
            Section {
                LabeledContent("Actions wheel") {
                    TriggerPicker(flags: $preferences.actionsTrigger)
                }
            } header: {
                Text("Wheels")
            } footer: {
                Text("Start dragging any file, then tap \(TriggerKey.describe(preferences.actionsTrigger)) to open the wheel. Drop on a wedge to run it, or press Esc to cancel.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Shelves") {
                Toggle("Remove files from a shelf after dragging them out", isOn: $preferences.removeAfterDragOut)
            }

            Section("General") {
                Toggle("Open at login", isOn: $opensAtLogin)
                    .onChange(of: opensAtLogin) { _, enabled in
                        setOpensAtLogin(enabled)
                    }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { NSApp.activate() }
    }

    private func setOpensAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            // Registration fails outside an app bundle; reflect the real state.
            opensAtLogin = SMAppService.mainApp.status == .enabled
        }
    }
}

/// Toggle buttons for each modifier key. At least one key always stays selected.
private struct TriggerPicker: View {
    @Binding var flags: NSEvent.ModifierFlags

    var body: some View {
        HStack(spacing: 4) {
            ForEach(TriggerKey.allCases) { key in
                Toggle(key.symbol, isOn: binding(for: key))
                    .toggleStyle(.button)
                    .help(key.name)
                    .accessibilityLabel(key.name)
            }
        }
    }

    private func binding(for key: TriggerKey) -> Binding<Bool> {
        Binding {
            flags.contains(key.flag)
        } set: { isOn in
            var updated = flags
            if isOn {
                updated.insert(key.flag)
            } else {
                updated.remove(key.flag)
            }
            if !updated.isEmpty {
                flags = updated
            }
        }
    }
}
