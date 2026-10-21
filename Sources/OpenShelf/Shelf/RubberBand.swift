import AppKit
import SwiftUI

/// Selection by dragging a rectangle over items, following Finder's rules:
/// a plain drag selects what the rectangle touches, ⇧ adds to the existing
/// selection, and ⌘ toggles the touched items.
enum RubberBand {
    static func rect(from start: CGPoint, to end: CGPoint) -> CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(end.x - start.x), height: abs(end.y - start.y))
    }

    static func selection<ID: Hashable>(
        itemFrames: [ID: CGRect],
        rect: CGRect,
        initial: Set<ID>,
        modifiers: NSEvent.ModifierFlags
    ) -> Set<ID> {
        let touched = Set(itemFrames.filter { $0.value.intersects(rect) }.map(\.key))
        if modifiers.contains(.command) {
            return initial.symmetricDifference(touched)
        }
        if modifiers.contains(.shift) {
            return initial.union(touched)
        }
        return touched
    }
}

/// Reports each item's frame in the shelf content's coordinate space.
struct ItemFramesKey: PreferenceKey {
    static let defaultValue: [ShelfItem.ID: CGRect] = [:]

    static func reduce(value: inout [ShelfItem.ID: CGRect], nextValue: () -> [ShelfItem.ID: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}

/// Transparent AppKit layer behind the items that turns drags on empty space
/// into a selection rectangle instead of moving the window, and clicks on empty
/// space into clearing the selection.
struct RubberBandArea: NSViewRepresentable {
    /// Called with the rectangle (in this view's top-left coordinates) and the
    /// modifiers held when the drag began; nil when the drag ends.
    var onChange: (CGRect?, NSEvent.ModifierFlags) -> Void
    var onClick: () -> Void

    func makeNSView(context: Context) -> RubberBandView {
        let view = RubberBandView()
        view.configuration = self
        return view
    }

    func updateNSView(_ view: RubberBandView, context: Context) {
        view.configuration = self
    }
}

final class RubberBandView: NSView {
    var configuration: RubberBandArea?

    private var start: CGPoint?
    private var modifiers: NSEvent.ModifierFlags = []
    private var didDrag = false

    override var isFlipped: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        start = convert(event.locationInWindow, from: nil)
        modifiers = event.modifierFlags.intersection(TriggerKey.relevantFlags)
        didDrag = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start else { return }
        let point = convert(event.locationInWindow, from: nil)
        guard didDrag || hypot(point.x - start.x, point.y - start.y) > 3 else { return }
        didDrag = true
        configuration?.onChange(RubberBand.rect(from: start, to: point).intersection(bounds), modifiers)
    }

    override func mouseUp(with event: NSEvent) {
        if didDrag {
            configuration?.onChange(nil, modifiers)
        } else if modifiers.isDisjoint(with: [.command, .shift]) {
            configuration?.onClick()
        }
        start = nil
        didDrag = false
    }
}
