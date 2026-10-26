import SwiftUI

@main
struct OpenShelfApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            if let version = Updater.shared.availableVersion {
                Button {
                    Updater.shared.checkForUpdates()
                } label: {
                    Label("Update to Version \(version)…", systemImage: "arrow.down.circle.fill")
                }
                Divider()
            }
            Button("New Shelf") { appDelegate.shelves.newShelf() }
                .keyboardShortcut("n")
            Button("Show Shelves") { appDelegate.shelves.showAll() }
                .disabled(!appDelegate.shelves.hasShelves)
            Button("Close All Shelves") { appDelegate.shelves.closeAll() }
                .disabled(!appDelegate.shelves.hasShelves)
            if appDelegate.shelves.hasShelves {
                Section("Open Shelves") {
                    ForEach(appDelegate.shelves.openShelves) { shelf in
                        Button(shelf.summary) { appDelegate.shelves.show(shelf) }
                    }
                }
            }
            Divider()
            openWheelButton
            Button("Take the Tour…") { TourWindowController.present() }
            Button("Settings…") { SettingsWindowController.shared.show() }
                .keyboardShortcut(",")
            Button("Check for Updates…") { Updater.shared.checkForUpdates() }
            Divider()
            Button("Quit OpenShelf") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
        } label: {
            Image(nsImage: Updater.shared.availableVersion == nil ? MenuBarIcon.image : MenuBarIcon.updateAvailableImage)
        }
    }

    /// Shows the global shortcut next to the item when it can be expressed.
    @ViewBuilder private var openWheelButton: some View {
        let button = Button("Open Wheel for Selection") { appDelegate.openWheelForSelection() }
        if let combo = Preferences.shared.openWheelShortcut, let key = combo.keyEquivalent {
            button.keyboardShortcut(key, modifiers: combo.eventModifiers)
        } else {
            button
        }
    }
}
