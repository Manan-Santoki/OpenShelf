import SwiftUI

@main
struct OpenShelfApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra("OpenShelf", systemImage: "tray.and.arrow.down.fill") {
            Button("New Shelf") { appDelegate.shelves.newShelf() }
                .keyboardShortcut("n")
            Button("Close All Shelves") { appDelegate.shelves.closeAll() }
            Divider()
            SettingsLink { Text("Settings…") }
                .keyboardShortcut(",")
            Divider()
            Button("Quit OpenShelf") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
        }

        Settings {
            SettingsView()
        }
    }
}
