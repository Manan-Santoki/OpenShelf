import Carbon.HIToolbox
import ServiceManagement
import SwiftUI

/// Settings → General: opening at login, and the guided tour.
struct GeneralSettingsPane: View {
    @State private var opensAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        Form {
            Section {
                Toggle("Open at login", isOn: $opensAtLogin)
                    .onChange(of: opensAtLogin) { _, enabled in
                        setOpensAtLogin(enabled)
                    }
            }
            Section {
                LabeledContent("Learn OpenShelf") {
                    HStack {
                        Button("Start Over") { TourWindowController.present(startingOver: true) }
                        Button("Take the Tour") { TourWindowController.present() }
                    }
                }
            } footer: {
                Text("A hands-on tour with practice files: converting, the Tools wheel, PDFs, video, shelves and the keyboard shortcut. Pick up where you left off, or start over.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .settingsPane()
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

/// Settings → Wheels: the keys that open each wheel during a drag, and the
/// shortcut that opens one for selected files.
struct WheelsSettingsPane: View {
    @Bindable private var preferences = Preferences.shared

    var body: some View {
        Form {
            Section {
                ForEach(WheelKind.allCases, id: \.self) { kind in
                    TriggerRow(kind: kind, preferences: preferences)
                }
            } header: {
                Text("While dragging")
            } footer: {
                Text("Start dragging any file, then tap the keys to open a wheel. Drop on a wedge to run it, or press Esc to cancel.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                LabeledContent("Open a wheel for selected files") {
                    ShortcutRecorder(combo: $preferences.openWheelShortcut)
                }
            } header: {
                Text("Without dragging")
            } footer: {
                Text("Select files in Finder or on a shelf and press the shortcut. Arrows choose, Return runs, Tab switches wheels, Esc closes. Hold Command to see number shortcuts.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .settingsPane()
    }
}

/// One wheel's trigger keys, with a warning when another wheel has the same.
private struct TriggerRow: View {
    let kind: WheelKind
    let preferences: Preferences

    var body: some View {
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
}

/// Settings → Shelves.
struct ShelvesSettingsPane: View {
    @Bindable private var preferences = Preferences.shared

    var body: some View {
        Form {
            Section {
                Toggle("Remove files from a shelf after dragging them out", isOn: $preferences.removeAfterDragOut)
                Toggle("Restore shelves when OpenShelf opens", isOn: $preferences.restoresShelves)
                Toggle("Hide docked shelves to the screen edge", isOn: $preferences.hidesDockedShelves)
            }
        }
        .settingsPane()
    }
}

/// Settings → Updates: Sparkle's automatic checks and installs.
struct UpdatesSettingsPane: View {
    @State private var checksForUpdates = Updater.shared.updater.automaticallyChecksForUpdates
    @State private var installsUpdates = Updater.shared.updater.automaticallyDownloadsUpdates

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    var body: some View {
        Form {
            Section {
                Toggle("Check for updates automatically", isOn: $checksForUpdates)
                    .onChange(of: checksForUpdates) { _, enabled in
                        Updater.shared.updater.automaticallyChecksForUpdates = enabled
                    }
                Toggle("Download and install updates automatically", isOn: $installsUpdates)
                    .disabled(!checksForUpdates)
                    .onChange(of: installsUpdates) { _, enabled in
                        Updater.shared.updater.automaticallyDownloadsUpdates = enabled
                    }
            }
            Section {
                LabeledContent("Version \(version)") {
                    Button("Check Now") { Updater.shared.checkForUpdates() }
                }
            }
        }
        .settingsPane()
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
