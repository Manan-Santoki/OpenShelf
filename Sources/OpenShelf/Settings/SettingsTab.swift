import SwiftUI

/// The tabs of the Settings window, in toolbar order.
enum SettingsTab: String, CaseIterable, Sendable {
    case general
    case wheels
    case shelves
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
        case .extraFormats: "Extra Formats"
        case .updates: "Updates"
        }
    }

    /// Wheels shows the wheel's wedges; Shelves and Extra Formats use the
    /// symbols their toasts already use.
    var symbolName: String {
        switch self {
        case .general: "gearshape"
        case .wheels: "chart.pie"
        case .shelves: "tray.full"
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
        case .extraFormats: ExtraFormatsPane(installer: .shared)
        case .updates: UpdatesSettingsPane()
        }
    }

    /// The state that changes this pane's height (a conflict warning, a
    /// failed install's message and buttons), read through Observation so
    /// the window can follow it. Empty for panes that never change height.
    @MainActor
    var heightSignature: [String] {
        switch self {
        case .wheels:
            Preferences.shared.conflictingWheels.map(\.title).sorted()
        case .extraFormats:
            ExtraTool.allCases.map { ExtraFormatsPane.layout(of: $0, installer: .shared) }
        case .general, .shelves, .updates:
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
