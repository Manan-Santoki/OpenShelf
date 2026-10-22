import AppKit
import Observation
import Sparkle

/// In-app updates via Sparkle. The feed is the `appcast.xml` attached to the
/// latest GitHub release (see `SUFeedURL` in Info.plist), and every update must
/// be signed with the project's EdDSA key (`SUPublicEDKey`).
///
/// Sparkle asks on the second launch whether to check automatically; the choice
/// can be changed in Settings. A background check that finds an update doesn't
/// interrupt: the menu-bar icon turns blue and the menu offers the update.
@MainActor
@Observable
final class Updater: NSObject {
    static let shared = Updater()

    /// The version a check found and that hasn't been installed or skipped yet.
    private(set) var availableVersion: String?

    @ObservationIgnored private var controller: SPUStandardUpdaterController!

    private override init() {
        super.init()
        controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: self, userDriverDelegate: self)
    }

    var updater: SPUUpdater { controller.updater }

    /// Starts the updater; call once at launch so scheduled checks run.
    func start() {
        _ = controller
    }

    /// Checks now, or shows the update a background check already found.
    func checkForUpdates() {
        NSApp.activate()
        controller.checkForUpdates(nil)
    }
}

extension Updater: SPUUpdaterDelegate {
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        availableVersion = item.displayVersionString
    }

    func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        availableVersion = nil
    }

    func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice, forUpdate updateItem: SUAppcastItem, state: SPUUserUpdateState) {
        // "Remind Me Later" keeps the reminder; skipping the version clears it.
        if choice == .skip {
            availableVersion = nil
        }
    }
}

extension Updater: @preconcurrency SPUStandardUserDriverDelegate {
    /// Lets Sparkle hand scheduled updates to the gentle reminder below
    /// instead of interrupting in this menu-bar app.
    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// Background checks only show Sparkle's window when OpenShelf is already
    /// in front; otherwise the icon and menu say an update is waiting.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        immediateFocus
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState) {
        // A menu-bar app isn't frontmost, so bring Sparkle's window forward.
        if handleShowingUpdate {
            NSApp.activate()
        } else {
            availableVersion = update.displayVersionString
        }
    }
}
