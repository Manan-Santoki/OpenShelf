import Foundation
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

struct PagedWheelKeyboardTests {
    /// The second page of an 8-item wheel: two items, four empty wedges, Back.
    let secondPage = WheelPaging.slots(for: Array(FileTool.allCases.prefix(8)).map(WheelItem.tool), page: 1)

    @Test func arrowsReachMoreOnTheFirstPage() {
        let firstPage = WheelPaging.slots(for: Array(FileTool.allCases.prefix(8)).map(WheelItem.tool), page: 0)
        #expect(WheelKeyboardNavigator.step(from: 5, in: firstPage, direction: .clockwise) == 6)
        #expect(WheelKeyboardNavigator.step(from: 6, in: firstPage, direction: .clockwise) == 0)
        #expect(WheelKeyboardNavigator.step(from: nil, in: firstPage, direction: .counterClockwise) == 6)
    }

    @Test func arrowsSkipEmptyWedges() {
        #expect(WheelKeyboardNavigator.step(from: 1, in: secondPage, direction: .clockwise) == 6)
        #expect(WheelKeyboardNavigator.step(from: 6, in: secondPage, direction: .counterClockwise) == 1)
        #expect(WheelKeyboardNavigator.step(from: 6, in: secondPage, direction: .clockwise) == 0)
        #expect(WheelKeyboardNavigator.step(from: 0, in: secondPage, direction: .counterClockwise) == 6)
    }

    @Test func nothingSelectableMeansNoStep() {
        #expect(WheelKeyboardNavigator.step(from: nil, in: [.empty, .empty], direction: .clockwise) == nil)
    }
}

@MainActor
struct WheelModelPagingTests {
    private func items(_ count: Int) -> [WheelItem] {
        Array(OutputFormat.allCases.prefix(count)).map(WheelItem.convert)
    }

    @Test func returnOnMoreAndBackTurnsThePageAndKeepsTheHighlight() {
        let controller = WheelController()
        controller.model.show(items(8))
        controller.model.isPresented = true
        controller.model.highlightedSlot = WheelPaging.pageTurnSlot
        let urls = [URL(fileURLWithPath: "/tmp/a.png")]

        // Turning the page isn't running a wedge, so the drag carries on.
        #expect(controller.confirmHighlighted(with: urls) == false)
        #expect(controller.model.page == 1)
        #expect(controller.model.highlighted == .back(toPage: 0, of: 2))

        #expect(controller.confirmHighlighted(with: urls) == false)
        #expect(controller.model.page == 0)
        #expect(controller.model.highlighted == .more(toPage: 1, of: 2))
    }

    @Test func arrowsStepOverEmptyWedgesOnTheSecondPage() {
        let controller = WheelController()
        controller.model.show(items(8))
        controller.model.isPresented = true
        controller.model.showPage(1)
        controller.model.highlightedSlot = 1
        controller.step(.clockwise)
        #expect(controller.model.highlightedSlot == 6)
    }

    @Test func showingItemsStartsOnTheFirstPage() {
        let model = WheelModel()
        model.show(items(8))
        model.showPage(1)
        model.show(items(9))
        #expect(model.page == 0)
        #expect(model.slots.last == .more(toPage: 1, of: 2))
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
