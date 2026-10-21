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
}
