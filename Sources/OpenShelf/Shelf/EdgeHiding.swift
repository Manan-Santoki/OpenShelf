import CoreGraphics

/// Geometry for docked shelves that tuck away behind the screen edge, leaving
/// a slim tab to bring them back.
enum EdgeHiding {
    static let tabSize = CGSize(width: 14, height: 72)
    /// How long the pointer must stay away before a docked shelf hides.
    static let hideDelay: Double = 1.5
    /// Slack around the shelf so brushing past its edge doesn't count as leaving.
    static let hoverMargin: CGFloat = 8

    /// Where the shelf sits while hidden: just past the edge it's docked to.
    static func hiddenFrame(for shown: CGRect, edge: ShelfEdge, in visibleFrame: CGRect) -> CGRect {
        var frame = shown
        switch edge {
        case .left: frame.origin.x = visibleFrame.minX - shown.width
        case .right: frame.origin.x = visibleFrame.maxX
        }
        return frame
    }

    /// The tab's frame: against the docked edge, centred on the shelf, on screen.
    static func tabFrame(for shown: CGRect, edge: ShelfEdge, in visibleFrame: CGRect) -> CGRect {
        let x = edge == .left ? visibleFrame.minX : visibleFrame.maxX - tabSize.width
        let centeredY = shown.midY - tabSize.height / 2
        let y = min(max(centeredY, visibleFrame.minY), visibleFrame.maxY - tabSize.height)
        return CGRect(x: x, y: y, width: tabSize.width, height: tabSize.height)
    }

    /// Whether the pointer counts as being over the shelf.
    static func isPointerNear(_ point: CGPoint, shelf frame: CGRect) -> Bool {
        frame.insetBy(dx: -hoverMargin, dy: -hoverMargin).contains(point)
    }
}
