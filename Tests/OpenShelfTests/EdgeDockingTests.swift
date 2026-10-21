import CoreGraphics
import Testing
@testable import OpenShelf

struct EdgeDockingTests {
    let docking = EdgeDocking(threshold: 48)
    let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)

    @Test func docksToLeftEdgeWhenClose() {
        let result = docking.settle(CGRect(x: 30, y: 400, width: 140, height: 160), in: screen)
        #expect(result.edge == .left)
        #expect(result.frame.minX == 0)
    }

    @Test func docksToRightEdgeWhenClose() {
        let result = docking.settle(CGRect(x: 1270, y: 400, width: 140, height: 160), in: screen)
        #expect(result.edge == .right)
        #expect(result.frame.maxX == 1440)
    }

    @Test func staysFloatingInTheMiddle() {
        let frame = CGRect(x: 600, y: 400, width: 140, height: 160)
        let result = docking.settle(frame, in: screen)
        #expect(result.edge == nil)
        #expect(result.frame == frame)
    }

    @Test func keepsShelfOnScreenVertically() {
        let result = docking.settle(CGRect(x: 600, y: 850, width: 140, height: 160), in: screen)
        #expect(result.frame.maxY == 900)
    }

    @Test(arguments: [CGFloat(40), 180, 395, 610, 740])
    func dockingKeepsTheHeightTheShelfWasDroppedAt(y: CGFloat) {
        for x in [CGFloat(-20), 30, 1270, 1320] {
            let frame = CGRect(x: x, y: y, width: 140, height: 160)
            let result = docking.settle(frame, in: screen)
            #expect(result.edge != nil)
            #expect(result.frame.minY == y, "docked at x \(x) moved from y \(y) to \(result.frame.minY)")
            #expect(result.frame.size == frame.size)
        }
    }

    @Test func dockingNearTheTopOrBottomOnlyMovesItBackOnScreen() {
        let low = docking.settle(CGRect(x: 10, y: -60, width: 140, height: 160), in: screen)
        #expect(low.edge == .left)
        #expect(low.frame.minY == 0)
        let high = docking.settle(CGRect(x: 1290, y: 800, width: 140, height: 160), in: screen)
        #expect(high.edge == .right)
        #expect(high.frame.maxY == 900)
    }

    @Test func dockingOnASecondDisplayKeepsItsHeight() {
        // A display above and to the left of the main one, below its menu bar.
        let visible = CGRect(x: -1920, y: 900, width: 1920, height: 1055)
        let result = docking.settle(CGRect(x: -1900, y: 1500, width: 140, height: 160), in: visible)
        #expect(result.edge == .left)
        #expect(result.frame.minX == -1920)
        #expect(result.frame.minY == 1500)
    }
}
