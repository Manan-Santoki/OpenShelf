import AppKit
import Observation
import SwiftUI

/// State shared between the wheel's drop handling and its SwiftUI rendering.
@MainActor
@Observable
final class WheelModel {
    var actions: [WheelAction] = []
    var highlighted: WheelAction?
    /// What the hub shows while no wedge is hovered, e.g. "1.2 MB" or "3 files".
    var dragSummary = ""
    var isPresented = false
}

/// Owns the overlay panel that shows a wheel under the pointer during a drag.
@MainActor
final class WheelController {
    /// Called when files are dropped on a wedge.
    var onDrop: ((WheelAction, [URL]) -> Void)?

    let model = WheelModel()
    private let dropView = WheelDropView()
    private lazy var panel = makePanel()
    private var dismissTask: Task<Void, Never>?
    private var summarizedURLs: [URL] = []

    func present(_ kind: WheelKind, actions: [WheelAction], at location: NSPoint) {
        dismissTask?.cancel()
        model.actions = actions
        model.highlighted = nil
        model.dragSummary = ""
        summarizedURLs = []
        dropView.geometry = WheelGeometry(wedgeCount: actions.count)

        let side = WheelGeometry.panelSide
        var frame = NSRect(x: location.x - side / 2, y: location.y - side / 2, width: side, height: side)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(location) }) {
            frame = frame.clamped(to: screen.frame)
        }
        panel.setFrame(frame, display: false)
        panel.orderFrontRegardless()
        model.isPresented = true
    }

    /// Hides the wheel. The short default delay lets a drop that coincides with
    /// the mouse-up reach the panel before it disappears.
    func dismiss(after delay: Duration = .milliseconds(200)) {
        guard panel.isVisible else { return }
        dismissTask?.cancel()
        dismissTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard let self, !Task.isCancelled else { return }
            model.isPresented = false
            model.highlighted = nil
            try? await Task.sleep(for: .milliseconds(160))
            guard !Task.isCancelled else { return }
            panel.orderOut(nil)
        }
    }

    /// Summarises the dragged files for the hub: the size of a single file (or of
    /// several), falling back to a count when folders are involved.
    private func updateSummary(for urls: [URL]) {
        guard !urls.isEmpty, urls != summarizedURLs else { return }
        summarizedURLs = urls
        let count = urls.count == 1 ? "1 item" : "\(urls.count) items"
        guard let bytes = FileActions.totalFileSize(of: urls) else {
            model.dragSummary = count
            return
        }
        let size = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
        model.dragSummary = urls.count == 1 ? size : "\(urls.count) · \(size)"
    }

    private func makePanel() -> NSPanel {
        let side = WheelGeometry.panelSide
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: side, height: side),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false

        dropView.onHover = { [weak self] wedge, urls in
            guard let self else { return }
            updateSummary(for: urls)
            let highlighted = wedge.flatMap { self.model.actions[safe: $0] }
            if highlighted != model.highlighted, highlighted != nil {
                NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
            }
            model.highlighted = highlighted
        }
        dropView.onDrop = { [weak self] wedge, urls in
            guard let self, let action = model.actions[safe: wedge] else { return }
            dismiss(after: .zero)
            onDrop?(action, urls)
        }

        // Size the container first: autoresizing can't grow a subview from a zero-sized superview.
        dropView.frame = NSRect(x: 0, y: 0, width: side, height: side)
        let hostingView = NSHostingView(rootView: WheelView(model: model))
        hostingView.sizingOptions = []
        hostingView.frame = dropView.bounds
        hostingView.autoresizingMask = [.width, .height]
        dropView.addSubview(hostingView)
        panel.contentView = dropView
        return panel
    }
}

/// Container view that receives the file drag and hit-tests it against the wedges.
final class WheelDropView: NSView {
    var geometry = WheelGeometry(wedgeCount: 0)
    var onHover: ((Int?, [URL]) -> Void)?
    var onDrop: ((Int, [URL]) -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        draggingUpdated(sender)
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        let wedge = wedge(for: sender)
        onHover?(wedge, fileURLs(in: sender))
        return wedge == nil ? [] : .copy
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        onHover?(nil, [])
    }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        let urls = fileURLs(in: sender)
        guard let wedge = wedge(for: sender), !urls.isEmpty else { return false }
        onDrop?(wedge, urls)
        return true
    }

    private func wedge(for info: any NSDraggingInfo) -> Int? {
        let point = convert(info.draggingLocation, from: nil)
        return geometry.wedge(at: point, center: CGPoint(x: bounds.midX, y: bounds.midY))
    }

    private func fileURLs(in info: any NSDraggingInfo) -> [URL] {
        let objects = info.draggingPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        )
        return objects as? [URL] ?? []
    }
}

extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}

extension NSRect {
    /// This rect moved (not resized) so that it lies inside `bounds` where possible.
    func clamped(to bounds: NSRect) -> NSRect {
        var rect = self
        rect.origin.x = Swift.min(Swift.max(rect.minX, bounds.minX), bounds.maxX - rect.width)
        rect.origin.y = Swift.min(Swift.max(rect.minY, bounds.minY), bounds.maxY - rect.height)
        return rect
    }
}
