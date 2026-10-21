import AppKit
import Testing
@testable import OpenShelf

struct ModifierChordTests {
    private let start = ContinuousClock.now

    /// Feeds `(milliseconds, flags)` readings and collects what was chosen.
    private func run(_ readings: [(Int, NSEvent.ModifierFlags)]) -> [NSEvent.ModifierFlags] {
        var chord = ModifierChord(settleDelay: .milliseconds(70))
        return readings.compactMap { ms, flags in chord.update(flags, at: start + .milliseconds(ms)) }
    }

    @Test func quickTapCountsOnRelease() {
        #expect(run([(0, []), (25, .shift), (50, .shift), (75, [])]) == [.shift])
    }

    @Test func heldKeyCountsAfterSettling() {
        #expect(run([(0, .shift), (25, .shift), (50, .shift), (75, .shift), (100, .shift)]) == [.shift])
    }

    @Test func heldKeyIsNotRepeatedOnRelease() {
        #expect(run([(0, .shift), (100, .shift), (125, [])]) == [.shift])
    }

    @Test func shiftOnTheWayToShiftOptionDoesNotFlashConvert() {
        #expect(run([(0, .shift), (30, [.shift, .option]), (60, [.shift, .option]), (110, [.shift, .option])]) == [[.shift, .option]])
    }

    @Test func tappedChordCountsWhenTheFirstKeyIsReleased() {
        // ⇧⌥ tapped, with ⇧ let go a moment before ⌥.
        #expect(run([(0, .shift), (20, [.shift, .option]), (50, .option), (60, [])]) == [[.shift, .option]])
    }

    @Test func addingAKeyToAHeldOneSwitches() {
        #expect(run([(0, .shift), (100, .shift), (125, [.shift, .option]), (200, [.shift, .option])]) == [.shift, [.shift, .option]])
    }

    @Test func releasingPartOfAChordDoesNotSwitchBack() {
        #expect(run([(0, [.shift, .option]), (100, [.shift, .option]), (125, .shift), (300, .shift), (325, [])]) == [[.shift, .option]])
    }

    @Test func aNewTapAfterReleasingCountsAgain() {
        #expect(run([(0, .shift), (100, .shift), (125, []), (150, .shift), (175, [])]) == [.shift, .shift])
    }

    @Test func ignoresCapsLock() {
        #expect(run([(0, .capsLock), (25, [.capsLock, .shift]), (50, .capsLock)]) == [.shift])
    }
}
