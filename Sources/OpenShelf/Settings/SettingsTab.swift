import SwiftUI

/// The tabs of the Settings window, in toolbar order.
enum SettingsTab: String, CaseIterable, Sendable {
    case general
    case wheels
    case shelves
    case output
    case extraFormats
    case updates

    /// Every pane's width; the window keeps it while its height follows the pane.
    static let width: CGFloat = 460

    /// Where the last chosen tab is remembered across launches.
    static let defaultsKey = "SettingsTab"

    var title: String {
        switch self {
        case .general: "General"
        case .wheels: "Wheels"
        case .shelves: "Shelves"
        case .output: "Output"
        case .extraFormats: "Extra Formats"
        case .updates: "Updates"
        }
    }

    /// Wheels shows the wheel's wedges; Shelves and Extra Formats use the
    /// symbols their toasts already use; Output has encoder-like sliders.
    var symbolName: String {
        switch self {
        case .general: "gearshape"
        case .wheels: "chart.pie"
        case .shelves: "tray.full"
        case .output: "slider.horizontal.3"
        case .extraFormats: "puzzlepiece.extension"
        case .updates: "arrow.down.circle"
        }
    }

    /// The tab Settings opens on: the one chosen last, else General.
    static func restored(from rawValue: String?) -> SettingsTab {
        rawValue.flatMap(SettingsTab.init(rawValue:)) ?? .general
    }

    @MainActor @ViewBuilder
    func pane() -> some View {
        switch self {
        case .general: GeneralSettingsPane()
        case .wheels: WheelsSettingsPane()
        case .shelves: ShelvesSettingsPane()
        case .output: OutputSettingsPane()
        case .extraFormats: ExtraFormatsPane(installer: .shared)
        case .updates: UpdatesSettingsPane()
        }
    }

    /// The state that changes this pane's height (a conflict warning, a
    /// custom bitrate's row, a failed install's message and buttons, the
    /// watched folders), read
    /// through Observation so the window can follow it. Empty for panes that
    /// never change height.
    @MainActor
    var heightSignature: [String] {
        switch self {
        case .wheels:
            Preferences.shared.conflictingWheels.map(\.title).sorted()
        case .output:
            // The custom bitrate's row comes and goes.
            [Preferences.shared.output.video.quality == .custom ? "custom" : ""]
        case .extraFormats:
            ExtraTool.allCases.map { ExtraFormatsPane.layout(of: $0, installer: .shared) }
                + ["typesetter \(ToolInstaller.shared.tools.pdfEngine != nil)"]
        case .shelves:
            // A row per watched folder; a missing folder's row says so.
            WatchedFolders.shared.folders.map { folder in
                "\(folder.id) \(WatchedFolders.shared.unavailable.contains(folder.id))"
            }
        case .general, .updates:
            []
        }
    }
}

extension View {
    /// The look of every Settings pane: a grouped form at the window's width.
    func settingsPane() -> some View {
        formStyle(.grouped)
            .frame(width: SettingsTab.width)
    }
}
