import CoreGraphics

/// Layout of a wheel: a hub surrounded by equal wedges, wedge 0 centred at
/// 12 o'clock and the rest following clockwise.
struct WheelGeometry {
    static let outerRadius: CGFloat = 132
    static let innerRadius: CGFloat = 50
    /// Room around the wheel for its rim, shadow and the label above it.
    static let margin: CGFloat = 76
    static var panelSide: CGFloat { (outerRadius + margin) * 2 }

    var wedgeCount: Int
    var innerRadius: CGFloat = Self.innerRadius
    var outerRadius: CGFloat = Self.outerRadius

    /// Angular width of one wedge, in radians.
    var sweep: CGFloat { wedgeCount > 0 ? 2 * .pi / CGFloat(wedgeCount) : 0 }

    /// Angle of a wedge's centre, in radians clockwise from 12 o'clock.
    func centerAngle(ofWedge index: Int) -> CGFloat {
        sweep * CGFloat(index)
    }

    /// The wedge under `point`, or nil over the hub or outside the wheel.
    /// Both points use a y-up coordinate space (AppKit's default).
    func wedge(at point: CGPoint, center: CGPoint) -> Int? {
        guard wedgeCount > 0 else { return nil }
        let dx = point.x - center.x
        let dy = point.y - center.y
        let distance = (dx * dx + dy * dy).squareRoot()
        guard distance >= innerRadius, distance <= outerRadius else { return nil }

        var angle = atan2(dx, dy)  // clockwise from 12 o'clock in y-up space
        if angle < 0 { angle += 2 * .pi }
        return Int((angle + sweep / 2) / sweep) % wedgeCount
    }
}
