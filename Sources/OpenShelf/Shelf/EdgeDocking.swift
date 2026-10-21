import CoreGraphics

/// Decides where a shelf settles after the user moves it: flush against the left
/// or right screen edge when released close to it, otherwise wherever it was
/// dropped. Either way it is kept fully inside the visible screen area, and its
/// height on screen is the user's: docking only ever moves it sideways.
struct EdgeDocking {
    var threshold: CGFloat = 48

    func settle(_ frame: CGRect, in visibleFrame: CGRect) -> (frame: CGRect, edge: ShelfEdge?) {
        var frame = frame
        frame.origin.y = min(max(frame.minY, visibleFrame.minY), visibleFrame.maxY - frame.height)

        if frame.minX - visibleFrame.minX <= threshold {
            frame.origin.x = visibleFrame.minX
            return (frame, .left)
        }
        if visibleFrame.maxX - frame.maxX <= threshold {
            frame.origin.x = visibleFrame.maxX - frame.width
            return (frame, .right)
        }
        frame.origin.x = min(max(frame.minX, visibleFrame.minX), visibleFrame.maxX - frame.width)
        return (frame, nil)
    }
}
