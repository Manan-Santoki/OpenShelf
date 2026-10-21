import CoreGraphics
import Testing
@testable import OpenShelf

struct WheelPagingTests {
    /// `count` distinct items, as on the Convert wheel.
    private func items(_ count: Int) -> [WheelItem] {
        Array(OutputFormat.allCases.prefix(count)).map(WheelItem.convert)
    }

    private func itemSlots(_ items: ArraySlice<WheelItem>) -> [WheelSlot] {
        items.map(WheelSlot.item)
    }

    @Test func sevenItemsFitOnOnePage() {
        let items = items(7)
        #expect(WheelPaging.pageCount(forItemCount: 7) == 1)
        #expect(WheelPaging.slots(for: items, page: 0) == itemSlots(items[...]))
    }

    @Test func eightItemsMakeTwoPagesWithBackWhereMoreWas() {
        let items = items(8)
        #expect(WheelPaging.pageCount(forItemCount: 8) == 2)
        #expect(WheelPaging.slots(for: items, page: 0) == itemSlots(items[0..<6]) + [.more(toPage: 1, of: 2)])
        #expect(WheelPaging.slots(for: items, page: 1) == itemSlots(items[6..<8]) + Array(repeating: .empty, count: 4) + [.back(toPage: 0, of: 2)])
    }

    @Test func elevenItemsLeaveOneEmptyWedge() {
        let items = items(11)
        #expect(WheelPaging.slots(for: items, page: 0) == itemSlots(items[0..<6]) + [.more(toPage: 1, of: 2)])
        #expect(WheelPaging.slots(for: items, page: 1) == itemSlots(items[6..<11]) + [.empty, .back(toPage: 0, of: 2)])
    }

    @Test func twelveItemsFillBothPages() {
        let items = items(12)
        #expect(WheelPaging.slots(for: items, page: 0) == itemSlots(items[0..<6]) + [.more(toPage: 1, of: 2)])
        #expect(WheelPaging.slots(for: items, page: 1) == itemSlots(items[6..<12]) + [.back(toPage: 0, of: 2)])
    }

    @Test func longerWheelsHaveMoreOnEveryPageButTheLast() {
        let items = items(14)
        #expect(WheelPaging.pageCount(forItemCount: 14) == 3)
        #expect(WheelPaging.slots(for: items, page: 0).last == .more(toPage: 1, of: 3))
        #expect(WheelPaging.slots(for: items, page: 1).last == .more(toPage: 2, of: 3))
        #expect(WheelPaging.slots(for: items, page: 2) == itemSlots(items[12..<14]) + Array(repeating: .empty, count: 4) + [.back(toPage: 0, of: 3)])
        #expect(WheelPaging.slots(for: items, page: 2).count == WheelPaging.slotCount)
    }

    @Test func emptyWheelHasNoSlots() {
        #expect(WheelPaging.pageCount(forItemCount: 0) == 1)
        #expect(WheelPaging.slots(for: [], page: 0).isEmpty)
    }

    @Test func pageTurnSlotsDescribeTheirPage() {
        #expect(WheelSlot.more(toPage: 1, of: 2).label == "More · 2 of 2")
        #expect(WheelSlot.back(toPage: 0, of: 2).label == "Back · 1 of 2")
        #expect(WheelSlot.more(toPage: 1, of: 2).accessibilityLabel == "More options, page 2 of 2")
        #expect(WheelSlot.back(toPage: 0, of: 2).accessibilityLabel == "Back to page 1 of 2")
        #expect(WheelSlot.empty.label == nil)
        #expect(WheelSlot.item(.tool(.crop)).targetPage == nil)
    }

    @Test func moreSitsInTheLastWedgeOfSeven() {
        // More's wedge is centred six sevenths of the way round, at about 10 o'clock.
        let geometry = WheelGeometry(wedgeCount: WheelPaging.slotCount, innerRadius: 50, outerRadius: 130)
        let center = CGPoint(x: 200, y: 200)
        let angle: CGFloat = geometry.centerAngle(ofWedge: WheelPaging.pageTurnSlot)
        let point = CGPoint(x: center.x + 90 * sin(angle), y: center.y + 90 * cos(angle))
        #expect(geometry.wedge(at: point, center: center) == WheelPaging.pageTurnSlot)
    }
}

struct SevenSlotGeometryTests {
    let geometry = WheelGeometry(wedgeCount: 7, innerRadius: 50, outerRadius: 130)
    let center = CGPoint(x: 200, y: 200)

    /// A point `distance` from the centre at `degrees` clockwise from 12 o'clock (y-up).
    private func point(degrees: CGFloat, distance: CGFloat = 90) -> CGPoint {
        let radians: CGFloat = degrees * .pi / 180
        return CGPoint(x: center.x + distance * sin(radians), y: center.y + distance * cos(radians))
    }

    @Test func eachSlotCentreHitsItsSlot() {
        for slot in 0..<7 {
            let degrees: CGFloat = CGFloat(slot) * 360 / 7
            #expect(geometry.wedge(at: point(degrees: degrees), center: center) == slot)
        }
    }

    @Test func boundariesFallHalfASlotEitherSide() {
        let half: CGFloat = 180 / 7
        #expect(geometry.wedge(at: point(degrees: half - 1), center: center) == 0)
        #expect(geometry.wedge(at: point(degrees: half + 1), center: center) == 1)
        // Just left of 12 o'clock is still the first slot; a little further is the last.
        #expect(geometry.wedge(at: point(degrees: 360 - half + 1), center: center) == 0)
        #expect(geometry.wedge(at: point(degrees: 360 - half - 1), center: center) == 6)
    }

    @Test func hubAndOutsideHaveNoSlot() {
        #expect(geometry.wedge(at: point(degrees: 300, distance: 30), center: center) == nil)
        #expect(geometry.wedge(at: point(degrees: 300, distance: 140), center: center) == nil)
    }
}

struct PageTurnDwellTests {
    let start = ContinuousClock.now

    private func turns(_ dwell: inout PageTurnDwell, at milliseconds: Int, onPageTurn: Bool = true) -> Bool {
        dwell.update(isOnPageTurn: onPageTurn, at: start + .milliseconds(milliseconds))
    }

    @Test func turnsAfterResting350Milliseconds() {
        var dwell = PageTurnDwell()
        #expect(!turns(&dwell, at: 0))
        #expect(dwell.isPending)
        #expect(!turns(&dwell, at: 340))
        #expect(turns(&dwell, at: 350))
        #expect(!dwell.isPending)
    }

    @Test func restingOnBackDoesNotTurnAgainUntilThePointerLeaves() {
        var dwell = PageTurnDwell()
        #expect(!turns(&dwell, at: 0))
        #expect(turns(&dwell, at: 350))
        #expect(!turns(&dwell, at: 800))
        #expect(!turns(&dwell, at: 2000))
        #expect(!turns(&dwell, at: 2100, onPageTurn: false))
        #expect(!turns(&dwell, at: 2200))
        #expect(!turns(&dwell, at: 2540))
        #expect(turns(&dwell, at: 2550))
    }

    @Test func leavingCancels() {
        var dwell = PageTurnDwell()
        #expect(!turns(&dwell, at: 0))
        #expect(!turns(&dwell, at: 200, onPageTurn: false))
        #expect(!dwell.isPending)
        // Coming back starts the wait over.
        #expect(!turns(&dwell, at: 300))
        #expect(!turns(&dwell, at: 400))
        #expect(!turns(&dwell, at: 640))
        #expect(turns(&dwell, at: 650))
    }

    @Test func turningByKeyWhileRestingWaitsForTheNextVisit() {
        var dwell = PageTurnDwell()
        #expect(!turns(&dwell, at: 0))
        dwell.pageDidTurn(isOnPageTurn: true, at: start + .milliseconds(100))
        #expect(!dwell.isPending)
        #expect(!turns(&dwell, at: 600))
        #expect(!turns(&dwell, at: 700, onPageTurn: false))
        #expect(!turns(&dwell, at: 800))
        #expect(turns(&dwell, at: 1150))
    }

    @Test func turningByKeyElsewhereLeavesTheDwellReady() {
        var dwell = PageTurnDwell()
        dwell.pageDidTurn(isOnPageTurn: false, at: start)
        #expect(!turns(&dwell, at: 100))
        #expect(turns(&dwell, at: 450))
    }
}
