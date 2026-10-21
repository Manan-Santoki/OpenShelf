import CoreGraphics
import Testing
@testable import OpenShelf

struct WheelGeometryTests {
    let geometry = WheelGeometry(wedgeCount: 4, innerRadius: 50, outerRadius: 130)
    let center = CGPoint(x: 200, y: 200)

    @Test func wedgesFollowClockwiseFromTwelveOClock() {
        #expect(geometry.wedge(at: CGPoint(x: 200, y: 300), center: center) == 0)  // above (y-up)
        #expect(geometry.wedge(at: CGPoint(x: 300, y: 200), center: center) == 1)  // right
        #expect(geometry.wedge(at: CGPoint(x: 200, y: 100), center: center) == 2)  // below
        #expect(geometry.wedge(at: CGPoint(x: 100, y: 200), center: center) == 3)  // left
    }

    @Test func justLeftOfTwelveOClockWrapsToFirstWedge() {
        #expect(geometry.wedge(at: CGPoint(x: 190, y: 300), center: center) == 0)
    }

    @Test func hubAndOutsideHaveNoWedge() {
        #expect(geometry.wedge(at: CGPoint(x: 210, y: 210), center: center) == nil)
        #expect(geometry.wedge(at: CGPoint(x: 200, y: 340), center: center) == nil)
    }

    @Test func emptyWheelHasNoWedges() {
        let empty = WheelGeometry(wedgeCount: 0)
        #expect(empty.wedge(at: CGPoint(x: 200, y: 300), center: center) == nil)
    }
}
