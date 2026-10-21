import AppKit
import Observation
import SwiftUI

@MainActor
@Observable
final class EdgeTabModel {
    var edge = ShelfEdge.right
    var count = 0
    var isPresented = false
}

/// The slim tab left at the screen edge while a docked shelf is hidden.
/// Hovering it, clicking it, or dragging files onto it brings the shelf back.
@MainActor
final class EdgeTab {
    var onReveal: (() -> Void)?

    private let model = EdgeTabModel()
    private lazy var panel = makePanel()

    func show(at frame: CGRect, edge: ShelfEdge, count: Int) {
        model.edge = edge
        model.count = count
        panel.setFrame(frame, display: false)
        panel.orderFrontRegardless()
        model.isPresented = true
    }

    func update(count: Int) {
        model.count = count
    }

    func hide() {
        model.isPresented = false
        panel.orderOut(nil)
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: EdgeHiding.tabSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false

        let trigger = EdgeTabTriggerView(frame: NSRect(origin: .zero, size: EdgeHiding.tabSize))
        trigger.onReveal = { [weak self] in self?.onReveal?() }
        let hostingView = NSHostingView(rootView: EdgeTabView(model: model))
        hostingView.sizingOptions = []
        hostingView.frame = trigger.bounds
        hostingView.autoresizingMask = [.width, .height]
        trigger.addSubview(hostingView)
        panel.contentView = trigger
        return panel
    }
}

/// Container that turns hovers, clicks and file drags on the tab into a reveal.
private final class EdgeTabTriggerView: NSView {
    var onReveal: (() -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel("Show shelf")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        // Keep events on this view rather than the SwiftUI drawing inside it.
        frame.contains(point) ? self : nil
    }

    override func mouseEntered(with event: NSEvent) { onReveal?() }
    override func mouseDown(with event: NSEvent) { onReveal?() }
    override func accessibilityPerformPress() -> Bool {
        onReveal?()
        return true
    }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        onReveal?()
        // The shelf slides out under the pointer and takes the drop from there.
        return .copy
    }
}

private struct EdgeTabView: View {
    let model: EdgeTabModel

    var body: some View {
        let radius: CGFloat = 7
        let shape = UnevenRoundedRectangle(
            cornerRadii: model.edge == .left
                ? RectangleCornerRadii(topLeading: 0, bottomLeading: 0, bottomTrailing: radius, topTrailing: radius)
                : RectangleCornerRadii(topLeading: radius, bottomLeading: radius, bottomTrailing: 0, topTrailing: 0),
            style: .continuous
        )
        VStack(spacing: 6) {
            Capsule()
                .fill(.secondary)
                .frame(width: 3, height: 18)
            Text("\(model.count)")
                .font(.system(size: 9, weight: .bold).monospacedDigit())
                .foregroundStyle(.secondary)
                .minimumScaleFactor(0.6)
                .lineLimit(1)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background {
            shape.fill(.regularMaterial)
                .overlay { shape.strokeBorder(Color.primary.opacity(0.12), lineWidth: 1) }
        }
        .opacity(model.isPresented ? 1 : 0)
        .animation(.easeOut(duration: 0.2), value: model.isPresented)
    }
}
