import AppKit
import Sparkle

/// In-app updates via Sparkle. The feed is the `appcast.xml` attached to the
/// latest GitHub release (see `SUFeedURL` in Info.plist), and every update must
/// be signed with the project's EdDSA key (`SUPublicEDKey`).
///
/// Sparkle asks on the second launch whether to check automatically; the choice
/// can be changed in Settings.
@MainActor
final class Updater: NSObject {
    static let shared = Updater()

    private var controller: SPUStandardUpdaterController!

    private override init() {
        super.init()
        controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: self)
    }

    var updater: SPUUpdater { controller.updater }

    /// Starts the updater; call once at launch so scheduled checks run.
    func start() {
        _ = controller
    }

    func checkForUpdates() {
        NSApp.activate()
        controller.checkForUpdates(nil)
    }
}

extension Updater: @preconcurrency SPUStandardUserDriverDelegate {
    /// Lets Sparkle show a quiet reminder instead of interrupting when a
    /// scheduled check finds an update in this menu-bar app.
    var supportsGentleScheduledUpdateReminders: Bool { true }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState) {
        // A menu-bar app isn't frontmost, so bring Sparkle's window forward.
        if handleShowingUpdate {
            NSApp.activate()
        }
    }
}
