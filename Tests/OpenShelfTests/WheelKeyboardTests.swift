import Testing
@testable import OpenShelf

struct WheelKeyboardNavigatorTests {
    @Test func firstPressPicksAnEndOfTheWheel() {
        #expect(WheelKeyboardNavigator.step(from: nil, count: 7, direction: .clockwise) == 0)
        #expect(WheelKeyboardNavigator.step(from: nil, count: 7, direction: .counterClockwise) == 6)
    }

    @Test func stepsAndWrapsAround() {
        #expect(WheelKeyboardNavigator.step(from: 2, count: 7, direction: .clockwise) == 3)
        #expect(WheelKeyboardNavigator.step(from: 6, count: 7, direction: .clockwise) == 0)
        #expect(WheelKeyboardNavigator.step(from: 0, count: 7, direction: .counterClockwise) == 6)
    }

    @Test func emptyWheelOrStaleIndex() {
        #expect(WheelKeyboardNavigator.step(from: nil, count: 0, direction: .clockwise) == nil)
        // An index left over from a bigger wheel restarts like a first press.
        #expect(WheelKeyboardNavigator.step(from: 9, count: 4, direction: .clockwise) == 0)
    }
}

struct KeyRepeatTests {
    let start = ContinuousClock.now

    private func fires(_ repeater: inout KeyRepeat, at milliseconds: Int, isDown: Bool = true) -> Bool {
        repeater.update(isDown: isDown, at: start + .milliseconds(milliseconds))
    }

    @Test func firesOncePerPress() {
        var repeater = KeyRepeat()
        #expect(fires(&repeater, at: 0))
        #expect(!fires(&repeater, at: 25))
        #expect(!fires(&repeater, at: 50, isDown: false))
        #expect(fires(&repeater, at: 75))
    }

    @Test func repeatsAfterInitialDelayThenAtInterval() {
        var repeater = KeyRepeat()
        #expect(fires(&repeater, at: 0))
        #expect(!fires(&repeater, at: 340))
        #expect(fires(&repeater, at: 360))
        #expect(!fires(&repeater, at: 470))
        #expect(fires(&repeater, at: 490))
        #expect(fires(&repeater, at: 615))
    }

    @Test func releasingResetsTheDelay() {
        var repeater = KeyRepeat()
        #expect(fires(&repeater, at: 0))
        #expect(fires(&repeater, at: 400))
        #expect(!fires(&repeater, at: 425, isDown: false))
        #expect(fires(&repeater, at: 450))
        #expect(!fires(&repeater, at: 600))
    }
}
