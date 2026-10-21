import ServiceManagement
import SwiftUI

/// First-launch introduction to the three drag gestures. Also available from
/// the menu bar as "How to Use OpenShelf".
struct WelcomeView: View {
    static let windowSize = CGSize(width: 460, height: 560)
    private static let shownKey = "hasShownWelcome"

    let actions: EditorActions
    @State private var opensAtLogin = SMAppService.mainApp.status == .enabled

    /// Shows the welcome window unless it has been shown before.
    @MainActor
    static func presentIfNeeded() {
        guard !UserDefaults.standard.bool(forKey: shownKey) else { return }
        UserDefaults.standard.set(true, forKey: shownKey)
        present()
    }

    @MainActor
    static func present() {
        let center = NSScreen.main.map { NSPoint(x: $0.visibleFrame.midX, y: $0.visibleFrame.midY) } ?? NSEvent.mouseLocation
        EditorWindowController.present(size: windowSize, near: center) { WelcomeView(actions: $0) }
    }

    var body: some View {
        EditorChrome(title: "", primaryTitle: "Get Started", actions: actions, onPrimary: actions.close) {
            VStack(spacing: 18) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 84, height: 84)
                    .accessibilityHidden(true)
                VStack(spacing: 4) {
                    Text("Welcome to OpenShelf")
                        .font(.system(size: 20, weight: .bold))
                    Text("Start dragging any file, then tap a key.")
                        .foregroundStyle(.secondary)
                }

                VStack(spacing: 10) {
                    GestureRow(keys: "⇧", title: "Convert", detail: "Turn it into JPG, PNG, PDF, DOCX, MP3, ZIP…")
                    GestureRow(keys: "⇧⌥", title: "Tools", detail: "Crop, annotate, compress, trim, split PDFs…")
                    GestureRow(keys: "⌃", title: "Actions", detail: "Put it on a shelf, AirDrop, copy, move…")
                }

                Text("Drop on a wedge to run it. Results are saved next to the original. Press Esc to cancel.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)

                Toggle("Open OpenShelf at login", isOn: $opensAtLogin)
                    .onChange(of: opensAtLogin) { _, enabled in
                        do {
                            if enabled {
                                try SMAppService.mainApp.register()
                            } else {
                                try SMAppService.mainApp.unregister()
                            }
                        } catch {
                            opensAtLogin = SMAppService.mainApp.status == .enabled
                        }
                    }
            }
            .padding(.top, 4)
        }
    }
}

private struct GestureRow: View {
    let keys: String
    let title: String
    let detail: String

    var body: some View {
        HStack(spacing: 12) {
            Text(keys)
                .font(.system(size: 17, weight: .semibold, design: .rounded))
                .frame(width: 52, height: 40)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.15)))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(detail).font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.primary.opacity(0.05)))
        .accessibilityElement(children: .combine)
    }
}
