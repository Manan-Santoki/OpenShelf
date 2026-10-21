import SwiftUI

@main
struct OpenShelfApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra {
            Button("New Shelf") { appDelegate.shelves.newShelf() }
                .keyboardShortcut("n")
            Button("Close All Shelves") { appDelegate.shelves.closeAll() }
            Divider()
            Button("How to Use OpenShelf") { WelcomeView.present() }
            SettingsLink { Text("Settings…") }
                .keyboardShortcut(",")
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
