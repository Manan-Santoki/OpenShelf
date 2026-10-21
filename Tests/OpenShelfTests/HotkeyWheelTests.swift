import AppKit
import Carbon.HIToolbox
import Testing
@testable import OpenShelf

struct KeyComboTests {
    @Test func defaultIsControlOptionSpace() {
        let combo = KeyCombo.defaultOpenWheel
        #expect(combo.displayString == "⌃⌥Space")
        #expect(combo.carbonModifiers == UInt32(controlKey | optionKey))
        #expect(combo.keyEquivalent == .space)
        #expect(combo.isValid)
    }

    @Test func needsControlOptionOrCommand() {
        #expect(!KeyCombo(keyCode: UInt32(kVK_ANSI_K), modifiers: [], keyName: "K").isValid)
        #expect(!KeyCombo(keyCode: UInt32(kVK_ANSI_K), modifiers: .shift, keyName: "K").isValid)
        #expect(KeyCombo(keyCode: UInt32(kVK_ANSI_K), modifiers: [.shift, .command], keyName: "K").isValid)
    }

    @Test func displaysModifiersInMenuOrder() {
        let combo = KeyCombo(keyCode: UInt32(kVK_ANSI_K), modifiers: [.command, .shift, .option, .control], keyName: "K")
        #expect(combo.displayString == "⌃⌥⇧⌘K")
    }

    @Test func dropsNonShortcutFlags() {
        let combo = KeyCombo(keyCode: UInt32(kVK_ANSI_K), modifiers: [.command, .capsLock, .function], keyName: "K")
        #expect(combo.modifiers == .command)
    }

    @Test func persistenceRoundTrips() {
        let combo = KeyCombo(keyCode: UInt32(kVK_ANSI_O), modifiers: [.command, .option], keyName: "O")
        #expect(Preferences.decode(Preferences.encode(combo)) == combo)
    }

    @Test func persistenceDefaultsAndOff() {
        #expect(Preferences.decode(nil) == .defaultOpenWheel)
        #expect(Preferences.encode(nil) == "off")
        #expect(Preferences.decode("off") == nil)
        #expect(Preferences.decode("garbage") == .defaultOpenWheel)
    }
}

struct WheelCycleTests {
    @Test func skipsEmptyWheels() {
        #expect(WheelCycle.available { $0 != .tools } == [.convert, .actions])
    }

    @Test func tabWrapsBothWays() {
        let all = WheelCycle.order
        #expect(WheelCycle.next(after: .convert, in: all) == .tools)
        #expect(WheelCycle.next(after: .actions, in: all) == .convert)
        #expect(WheelCycle.next(after: .convert, in: all, backwards: true) == .actions)
    }

    @Test func singleWheelHasNowhereToGo() {
        #expect(WheelCycle.next(after: .actions, in: [.actions]) == nil)
    }
}

struct ModifierTapTrackerTests {
    @Test func tapReportsTheWholeCombination() {
        var tracker = ModifierTapTracker()
        #expect(tracker.flagsChanged(.shift) == nil)
        #expect(tracker.flagsChanged([.shift, .option]) == nil)
        #expect(tracker.flagsChanged(.option) == nil)
        #expect(tracker.flagsChanged([]) == [.shift, .option])
    }

    @Test func keyWhileHeldIsNotATap() {
        var tracker = ModifierTapTracker()
        _ = tracker.flagsChanged(.shift)
        tracker.keyPressed()  // ⇧Tab
        #expect(tracker.flagsChanged([]) == nil)
        // The next clean tap counts again.
        _ = tracker.flagsChanged(.control)
        #expect(tracker.flagsChanged([]) == .control)
    }

    @Test func ignoresCapsLock() {
        var tracker = ModifierTapTracker()
        #expect(tracker.flagsChanged(.capsLock) == nil)
        #expect(tracker.flagsChanged([]) == nil)
    }

    /// Letting go of ⌃⌥ after ⌃⌥Space opened the wheel used to read as a ⌃ tap
    /// and switch to the Actions wheel whenever ⌥ came up first.
    @Test func releasingTheShortcutsModifiersIsNotATap() {
        var optionFirst = ModifierTapTracker(alreadyHeld: [.control, .option])
        #expect(optionFirst.flagsChanged(.control) == nil)
        #expect(optionFirst.flagsChanged([]) == nil)

        var controlFirst = ModifierTapTracker(alreadyHeld: [.control, .option])
        #expect(controlFirst.flagsChanged(.option) == nil)
        #expect(controlFirst.flagsChanged([]) == nil)
    }

    @Test func tapsCountOnceTheShortcutsModifiersAreUp() {
        var tracker = ModifierTapTracker(alreadyHeld: [.control, .option])
        _ = tracker.flagsChanged([])
        _ = tracker.flagsChanged(.shift)
        #expect(tracker.flagsChanged([]) == .shift)
    }

    @Test func keysPressedWhileTheShortcutIsStillHeldDontCount() {
        var tracker = ModifierTapTracker(alreadyHeld: .control)
        _ = tracker.flagsChanged([.control, .shift])
        _ = tracker.flagsChanged(.shift)
        #expect(tracker.flagsChanged([]) == nil)
    }

    @Test func modifiersLetGoBeforeTheWheelTookFocusDontSwallowATap() {
        // ⌃ came up before the wheel could see it, so its release never arrives.
        var tracker = ModifierTapTracker(alreadyHeld: .control)
        _ = tracker.flagsChanged(.shift)
        #expect(tracker.flagsChanged([]) == .shift)
    }
}

struct FinderSelectionTests {
    private let files = [URL(fileURLWithPath: "/tmp/a.jpg"), URL(fileURLWithPath: "/tmp/b.pdf")]

    @Test func usesFinderSelectionWhenFinderIsFrontmost() {
        #expect(FinderSelection.resolve(frontmostBundleID: "com.apple.finder") { files } == .files(files))
    }

    @Test func otherAppsDontAskFinder() {
        var asked = false
        let outcome = FinderSelection.resolve(frontmostBundleID: "com.apple.Safari") {
            asked = true
            return files
        }
        #expect(outcome == .finderNotFrontmost)
        #expect(!asked)
    }

    @Test func emptySelection() {
        #expect(FinderSelection.resolve(frontmostBundleID: "com.apple.finder") { [] } == .noSelection)
    }

    @Test func automationDenied() {
        let outcome = FinderSelection.resolve(frontmostBundleID: "com.apple.finder") {
            throw FinderSelectionError(code: -1743, message: "Not authorized")
        }
        #expect(outcome == .notPermitted)
        #expect(FinderSelection.toast(for: outcome)?.subtitle?.contains("Automation") == true)
    }

    @Test func otherFailuresKeepTheirMessage() {
        let outcome = FinderSelection.resolve(frontmostBundleID: "com.apple.finder") {
            throw FinderSelectionError(code: -600, message: "Finder isn't running")
        }
        #expect(outcome == .failed("Finder isn't running"))
    }

    @Test func toastsAskForASelection() {
        #expect(FinderSelection.toast(for: .noSelection)?.title == "Select files in Finder first")
        #expect(FinderSelection.toast(for: .finderNotFrontmost)?.title == "Select files in Finder first")
        #expect(FinderSelection.toast(for: .files(files)) == nil)
    }
}
