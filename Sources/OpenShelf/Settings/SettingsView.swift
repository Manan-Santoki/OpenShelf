import Carbon.HIToolbox
import ServiceManagement
import SwiftUI

struct SettingsView: View {
    @Bindable private var preferences = Preferences.shared
    @State private var opensAtLogin = SMAppService.mainApp.status == .enabled
    @State private var checksForUpdates = Updater.shared.updater.automaticallyChecksForUpdates
    @State private var installsUpdates = Updater.shared.updater.automaticallyDownloadsUpdates

    var body: some View {
        Form {
            Section {
                ForEach(WheelKind.allCases, id: \.self) { kind in
                    LabeledContent {
                        TriggerPicker(flags: Binding(
                            get: { preferences.trigger(for: kind) },
                            set: { preferences.setTrigger($0, for: kind) }
                        ))
                    } label: {
                        Text(kind.title)
                        if preferences.conflictingWheels.contains(kind) {
                            Text("Same keys as another wheel")
                                .foregroundStyle(.red)
                        }
                    }
                }
            } header: {
                Text("Wheels")
            } footer: {
                Text("Start dragging any file, then tap the keys to open a wheel. Drop on a wedge to run it, or press Esc to cancel.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                LabeledContent("Open a wheel for Finder's selection") {
                    ShortcutRecorder(combo: $preferences.openWheelShortcut)
                }
            } header: {
                Text("Keyboard")
            } footer: {
                Text("Select files in Finder and press the shortcut. Arrows choose, Return runs, Tab switches wheels, Esc closes.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Shelves") {
                Toggle("Remove files from a shelf after dragging them out", isOn: $preferences.removeAfterDragOut)
                Toggle("Restore shelves when OpenShelf opens", isOn: $preferences.restoresShelves)
                Toggle("Hide docked shelves to the screen edge", isOn: $preferences.hidesDockedShelves)
            }

            Section("Updates") {
                Toggle("Check for updates automatically", isOn: $checksForUpdates)
                    .onChange(of: checksForUpdates) { _, enabled in
                        Updater.shared.updater.automaticallyChecksForUpdates = enabled
                    }
                Toggle("Download and install updates automatically", isOn: $installsUpdates)
                    .disabled(!checksForUpdates)
                    .onChange(of: installsUpdates) { _, enabled in
                        Updater.shared.updater.automaticallyDownloadsUpdates = enabled
                    }
                LabeledContent("Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")") {
                    Button("Check Now") { Updater.shared.checkForUpdates() }
                }
            }

            Section("General") {
                Toggle("Open at login", isOn: $opensAtLogin)
                    .onChange(of: opensAtLogin) { _, enabled in
                        setOpensAtLogin(enabled)
                    }
            }
        }
        .formStyle(.grouped)
        .frame(width: Self.width)
    }

    static let width: CGFloat = 460

    /// The height the whole form needs, without scrolling.
    @MainActor
    static var idealHeight: CGFloat {
        NSHostingView(rootView: SettingsView().fixedSize(horizontal: false, vertical: true)).fittingSize.height
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

/// Records a global shortcut: click, then press a key with ⌃, ⌥ or ⌘.
private struct ShortcutRecorder: View {
    @Binding var combo: KeyCombo?
    @State private var isRecording = false
    @State private var monitor: Any?
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .trailing, spacing: 4) {
            HStack(spacing: 6) {
                Button(isRecording ? "Type a shortcut…" : (combo?.displayString ?? "Off")) {
                    isRecording ? stopRecording() : startRecording()
                }
                .help("Click, then press a key together with ⌃, ⌥ or ⌘. Esc cancels.")
                .accessibilityLabel(isRecording ? "Recording shortcut" : "Shortcut \(combo?.displayString ?? "off")")
                if combo != .defaultOpenWheel {
                    Button("Default") { apply(.defaultOpenWheel) }
                }
                if combo != nil {
                    Button("Turn Off") { apply(nil) }
                }
            }
            if let problem {
                Text(problem)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .onDisappear { if isRecording { stopRecording() } }
    }

    private func startRecording() {
        problem = nil
        isRecording = true
        // Pause the live shortcut so pressing it here records it instead.
        GlobalHotKey.shared.register(nil)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            MainActor.assumeIsolated {
                if Int(event.keyCode) == kVK_Escape {
                    stopRecording()
                    return
                }
                let candidate = KeyCombo(event: event)
                if candidate.isValid {
                    apply(candidate)
                    stopRecording()
                } else {
                    problem = "Include ⌃, ⌥ or ⌘ in the shortcut."
                }
            }
            return nil
        }
    }

    private func stopRecording() {
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
        isRecording = false
        if !GlobalHotKey.shared.register(combo) {
            problem = "Another app already uses that shortcut."
        }
    }

    private func apply(_ newCombo: KeyCombo?) {
        combo = newCombo
        problem = GlobalHotKey.shared.register(newCombo) ? nil : "Another app already uses that shortcut."
    }
}
