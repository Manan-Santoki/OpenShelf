import AppKit

/// What happened when asking Finder for its selection.
enum FinderSelectionOutcome: Equatable {
    case files([URL])
    case noSelection
    case finderNotFrontmost
    /// The user hasn't allowed OpenShelf to control Finder (Automation).
    case notPermitted
    case failed(String)
}

struct FinderSelectionError: Error, Equatable {
    let code: Int
    let message: String
}

/// Reads the files selected in Finder, for opening a wheel without a drag.
enum FinderSelection {
    static let finderBundleID = "com.apple.finder"
    /// errAEEventNotPermitted: Automation access to Finder was denied.
    static let notPermittedCode = -1743

    /// Decides what to do for the frontmost app; `fetch` is only called when
    /// Finder is in front.
    static func resolve(frontmostBundleID: String?, fetch: () throws -> [URL]) -> FinderSelectionOutcome {
        guard frontmostBundleID == finderBundleID else { return .finderNotFrontmost }
        do {
            let urls = try fetch()
            return urls.isEmpty ? .noSelection : .files(urls)
        } catch let error as FinderSelectionError where error.code == notPermittedCode {
            return .notPermitted
        } catch let error as FinderSelectionError {
            return .failed(error.message)
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// Asks Finder over Apple Events. The first call shows macOS's one-time
    /// "OpenShelf wants to control Finder" prompt.
    @MainActor
    static func fetchFromFinder() throws -> [URL] {
        let source = """
        tell application "Finder"
            set picked to selection as alias list
            set paths to {}
            repeat with anItem in picked
                set end of paths to POSIX path of anItem
            end repeat
            return paths
        end tell
        """
        guard let script = NSAppleScript(source: source) else {
            throw FinderSelectionError(code: 0, message: "Couldn't ask Finder for its selection.")
        }
        var errorInfo: NSDictionary?
        let result = script.executeAndReturnError(&errorInfo)
        if let errorInfo {
            throw FinderSelectionError(
                code: errorInfo[NSAppleScript.errorNumber] as? Int ?? 0,
                message: errorInfo[NSAppleScript.errorMessage] as? String ?? "Finder didn't answer."
            )
        }
        guard result.numberOfItems > 0 else { return [] }
        return (1...result.numberOfItems).compactMap { index in
            result.atIndex(index)?.stringValue.map { URL(fileURLWithPath: $0) }
        }
    }

    /// The toast to show when there's no wheel to open, or nil for `.files`.
    /// - Parameter shortcut: The shortcut that was pressed; when there's no
    ///   selection, the toast says pressing it again opens Settings.
    static func toast(for outcome: FinderSelectionOutcome, shortcut: KeyCombo? = nil) -> Toast? {
        switch outcome {
        case .files:
            nil
        case .noSelection, .finderNotFrontmost:
            Toast(
                systemImage: "cursorarrow.click",
                title: "Select files in Finder first",
                subtitle: shortcut.map { "Press \($0.displayString) again for Settings" }
            )
        case .notPermitted:
            Toast(systemImage: "lock", title: "Allow OpenShelf to control Finder", subtitle: "Privacy & Security → Automation")
        case .failed(let message):
            Toast(systemImage: "exclamationmark.triangle", title: "Couldn't read Finder's selection", subtitle: message)
        }
    }
}

/// Whose selection the wheel shortcut opens a wheel for. Shelves are panels
/// that take focus without bringing OpenShelf forward, so the shelf with
/// keyboard focus is what's "in front": its selection wins. Otherwise, with
/// OpenShelf itself in front (an editor or Settings), the most recently used
/// shelf's; otherwise Finder's, as before.
enum WheelSelectionSource: Equatable {
    case shelf([URL])
    case finder

    static func choose(keyShelfSelection: [URL]?, recentShelfSelection: [URL], isOpenShelfFrontmost: Bool) -> WheelSelectionSource {
        if let keyShelfSelection, !keyShelfSelection.isEmpty { return .shelf(keyShelfSelection) }
        if isOpenShelfFrontmost, !recentShelfSelection.isEmpty { return .shelf(recentShelfSelection) }
        return .finder
    }
}

/// Pressing the wheel shortcut again while "Select files in Finder first" is
/// still showing opens Settings: a way in when the menu bar icon is out of
/// sight, behind the notch or in the menu bar's overflow.
struct RepeatedEmptyPress {
    /// About as long as the toast stays up.
    static let window: Duration = .seconds(2)

    private var lastPress: ContinuousClock.Instant?

    /// Records a press that found nothing to open a wheel for. Returns true
    /// when it follows another such press within `window`; the press after
    /// that starts over.
    mutating func record(at now: ContinuousClock.Instant) -> Bool {
        if let lastPress, now - lastPress <= Self.window {
            self.lastPress = nil
            return true
        }
        lastPress = now
        return false
    }

    /// Forgets the last press, e.g. once a wheel opened.
    mutating func reset() {
        lastPress = nil
    }
}
