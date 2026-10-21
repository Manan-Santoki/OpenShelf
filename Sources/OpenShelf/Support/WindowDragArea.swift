import AppKit
import SwiftUI

/// Lets the user move a borderless window by dragging this area. Used behind
/// title bars, since SwiftUI content inside a container view doesn't count as
/// draggable window background.
///
/// The window follows the pointer exactly rather than being handed to the
/// window server's drag (`performDrag`), whose edge tiling re-centred a shelf
/// vertically when it was dropped against the side of the screen.
struct WindowDragArea: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class DragView: NSView {
        /// The pointer and the window's origin when the drag started, in screen coordinates.
        private var start: (pointer: NSPoint, origin: NSPoint)?

        override var mouseDownCanMoveWindow: Bool { false }

        override func mouseDown(with event: NSEvent) {
            guard let window else { return }
            start = (NSEvent.mouseLocation, window.frame.origin)
        }

        override func mouseDragged(with event: NSEvent) {
            guard let window, let start else { return }
            let pointer = NSEvent.mouseLocation
            window.setFrameOrigin(NSPoint(
                x: start.origin.x + pointer.x - start.pointer.x,
                y: start.origin.y + pointer.y - start.pointer.y
            ))
        }

        override func mouseUp(with event: NSEvent) {
            start = nil
        }
    }
}
