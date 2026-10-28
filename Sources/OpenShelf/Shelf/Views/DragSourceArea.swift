import AppKit
import SwiftUI

/// A transparent AppKit layer that turns clicks into selection changes and drags
/// into a multi-file drag session. SwiftUI's `onDrag` can only carry one item,
/// so shelf items use this instead.
struct DragSourceArea: NSViewRepresentable {
    var isSelected: () -> Bool
    var dragItems: () -> [ShelfItem]
    var onSelect: (NSEvent.ModifierFlags) -> Void
    var onDoubleClick: () -> Void
    var menu: (NSView) -> NSMenu?
    var onDragEnded: ([ShelfItem], NSDragOperation) -> Void
    /// Called right as a drag of this shelf's items begins. No-op by default.
    var onDragBegan: () -> Void = {}

    func makeNSView(context: Context) -> DragSourceView {
        let view = DragSourceView()
        view.configuration = self
        return view
    }

    func updateNSView(_ view: DragSourceView, context: Context) {
        view.configuration = self
    }
}

final class DragSourceView: NSView, NSDraggingSource {
    var configuration: DragSourceArea?

    private var mouseDownEvent: NSEvent?
    /// Clicking an already-selected item keeps the selection until mouse-up, so the
    /// whole selection can still be dragged.
    private var selectsOnMouseUp = false
    private var draggedItems: [ShelfItem] = []

    override var mouseDownCanMoveWindow: Bool { false }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        guard let configuration else { return }
        window?.makeKey()
        if event.clickCount == 2 {
            mouseDownEvent = nil
            configuration.onDoubleClick()
            return
        }
        mouseDownEvent = event
        let modifiers = event.modifierFlags.intersection(TriggerKey.relevantFlags)
        selectsOnMouseUp = configuration.isSelected() && modifiers.isDisjoint(with: [.command, .shift])
        if !selectsOnMouseUp {
            configuration.onSelect(modifiers)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = mouseDownEvent else { return }
        let distance = hypot(event.locationInWindow.x - start.locationInWindow.x, event.locationInWindow.y - start.locationInWindow.y)
        guard distance > 4 else { return }
        mouseDownEvent = nil
        selectsOnMouseUp = false
        beginDrag(with: start)
    }

    override func mouseUp(with event: NSEvent) {
        if selectsOnMouseUp {
            configuration?.onSelect([])
        }
        selectsOnMouseUp = false
        mouseDownEvent = nil
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        guard let configuration else { return nil }
        if !configuration.isSelected() {
            configuration.onSelect([])
        }
        return configuration.menu(self)
    }

    private func beginDrag(with event: NSEvent) {
        guard let items = configuration?.dragItems(), !items.isEmpty else { return }
        draggedItems = items
        configuration?.onDragBegan()

        let side = min(bounds.width, bounds.height, 64)
        let origin = NSPoint(x: bounds.midX - side / 2, y: bounds.midY - side / 2)
        let draggingItems = items.enumerated().map { index, item in
            let draggingItem = NSDraggingItem(pasteboardWriter: item.url as NSURL)
            let offset = CGFloat(min(index, 4)) * 3
            draggingItem.setDraggingFrame(
                NSRect(x: origin.x + offset, y: origin.y - offset, width: side, height: side),
                contents: item.thumbnail
            )
            return draggingItem
        }
        let session = beginDraggingSession(with: draggingItems, event: event, source: self)
        session.animatesToStartingPositionsOnCancelOrFail = true
        session.draggingFormation = items.count > 1 ? .pile : .none
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        // Outside the app, let the destination apply its usual rules (Finder moves
        // within a volume and copies across volumes); between shelves, just copy.
        context == .outsideApplication ? [.copy, .move, .link, .generic] : .copy
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        let items = draggedItems
        draggedItems = []
        Shelf.activeDragSource = nil
        configuration?.onDragEnded(items, operation)
    }
}
