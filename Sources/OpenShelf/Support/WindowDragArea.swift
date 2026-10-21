import AppKit
import SwiftUI

/// Lets the user move a borderless window by dragging this area. Used behind
/// title bars, since SwiftUI content inside a container view doesn't count as
/// draggable window background.
struct WindowDragArea: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class DragView: NSView {
        override func mouseDown(with event: NSEvent) {
            window?.performDrag(with: event)
        }
    }
}
