import SwiftUI

@main
struct OpenShelfApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
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
            Button("How to Use OpenShelf") { WelcomeView.present() }
            SettingsLink { Text("Settings…") }
                .keyboardShortcut(",")
            Button("Check for Updates…") { Updater.shared.checkForUpdates() }
            Divider()
            Button("Quit OpenShelf") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
        } label: {
            Image(nsImage: MenuBarIcon.image)
        }

        Settings {
            SettingsView()
        }
    }
}
