import AppIntents

/// The actions offered without setting anything up: in Spotlight (macOS 26
/// and later), Siri, and the Shortcuts app's OpenShelf section. Every phrase
/// has to name the app. The other actions are in Shortcuts' action list.
struct OpenShelfShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: ConvertFilesIntent(),
            phrases: [
                "Convert files with \(.applicationName)",
                "Convert files to \(\.$format) with \(.applicationName)",
            ],
            shortTitle: "Convert Files",
            systemImageName: "arrow.triangle.2.circlepath"
        )
        AppShortcut(
            intent: CompressFilesIntent(),
            phrases: ["Compress files with \(.applicationName)"],
            shortTitle: "Compress",
            systemImageName: "arrow.down.right.and.arrow.up.left"
        )
        AppShortcut(
            intent: ExtractTextIntent(),
            phrases: ["Extract text with \(.applicationName)"],
            shortTitle: "Extract Text",
            systemImageName: "text.viewfinder"
        )
        AppShortcut(
            intent: RemoveBackgroundIntent(),
            phrases: ["Remove background with \(.applicationName)"],
            shortTitle: "Remove Background",
            systemImageName: "person.crop.rectangle"
        )
        AppShortcut(
            intent: CombinePDFIntent(),
            phrases: ["Combine into PDF with \(.applicationName)"],
            shortTitle: "Combine into PDF",
            systemImageName: "doc.on.doc"
        )
        AppShortcut(
            intent: AddToShelfIntent(),
            phrases: ["Add to shelf with \(.applicationName)", "Add files to \(.applicationName)"],
            shortTitle: "Add to Shelf",
            systemImageName: "tray.and.arrow.down"
        )
    }
}
