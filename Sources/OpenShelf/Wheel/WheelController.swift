import AppKit
import Observation
import SwiftUI

/// State shared between the wheel's drop handling and its SwiftUI rendering.
@MainActor
@Observable
final class WheelModel {
    var items: [WheelItem] = []
    var highlighted: WheelItem?
    /// What the hub shows while no wedge is hovered, e.g. "1.2 MB" or "3 files".
    var dragSummary = ""
    var isPresented = false
}

/// Owns the overlay panel that shows a wheel under the pointer during a drag.
@MainActor
final class WheelController {
    /// Called when files are dropped on a wedge.
    var onDrop: ((WheelItem, [URL]) -> Void)?

    let model = WheelModel()
    private let dropView = WheelDropView()
    private lazy var panel = makePanel()
    private var dismissTask: Task<Void, Never>?
    private var summarizedURLs: [URL] = []
    /// The wedge under the pointer at the last hover update. AppKit repeats
    /// hover updates while the pointer rests, so only a change of wedge moves the
    /// highlight; that keeps a keyboard choice until the pointer actually moves.
    private var lastHoverWedge: Int?
    private var hasHoverReading = false
    /// After Return runs a wedge, the invisible panel stays under the pointer to
    /// catch the drop at mouse-up so it can't land somewhere else.
    private var isAbsorbingDrop = false
    private var followTask: Task<Void, Never>?

    /// Shows `items` around `location`, or swaps them in place if the wheel is
    /// already open (e.g. when ⌥ is added to switch from Convert to Tools).
    func present(_ items: [WheelItem], at location: NSPoint) {
        dismissTask?.cancel()
        let isSwitching = panel.isVisible && model.isPresented
        model.items = items
        model.highlighted = nil
        model.dragSummary = ""
        summarizedURLs = []
        hasHoverReading = false
        endAbsorbingDrop()
        dropView.geometry = WheelGeometry(wedgeCount: items.count)
        guard !isSwitching else { return }

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
            endAbsorbingDrop()
            try? await Task.sleep(for: .milliseconds(160))
            guard !Task.isCancelled else { return }
            panel.orderOut(nil)
        }
    }

    /// Moves the highlight one wedge with the keyboard.
    func step(_ direction: WheelStepDirection) {
        guard model.isPresented, !isAbsorbingDrop else { return }
        let current = model.highlighted.flatMap { model.items.firstIndex(of: $0) }
        guard let next = WheelKeyboardNavigator.step(from: current, count: model.items.count, direction: direction) else { return }
        setHighlight(model.items[next])
    }

    /// Runs the highlighted wedge on `urls`, as if they were dropped on it, and
    /// hides the wheel. Returns false when nothing is highlighted.
    func confirmHighlighted(with urls: [URL]) -> Bool {
        guard model.isPresented, !isAbsorbingDrop, let item = model.highlighted, !urls.isEmpty else { return false }
        absorbNextDrop()
        onDrop?(item, urls)
        return true
    }

    /// Hides the wheel but keeps its panel, invisible, centred under the pointer
    /// so the user's mouse-up drops the files here, where nothing happens,
    /// instead of on whatever is underneath. Ends when the drop arrives or the
    /// drag ends (`dismiss`).
    private func absorbNextDrop() {
        dismissTask?.cancel()
        isAbsorbingDrop = true
        model.isPresented = false
        model.highlighted = nil
        dropView.isAbsorbingDrop = true
        // Windows let drags pass through fully transparent pixels, so keep a
        // nearly invisible fill over the whole panel.
        dropView.wantsLayer = true
        dropView.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.01).cgColor
        followTask?.cancel()
        followTask = Task { [weak self] in
            while let self, self.isAbsorbingDrop, !Task.isCancelled {
                let location = NSEvent.mouseLocation
                let side = self.panel.frame.width
                self.panel.setFrameOrigin(NSPoint(x: location.x - side / 2, y: location.y - side / 2))
                try? await Task.sleep(for: .milliseconds(25))
            }
        }
    }

    private func endAbsorbingDrop() {
        guard isAbsorbingDrop else { return }
        isAbsorbingDrop = false
        followTask?.cancel()
        followTask = nil
        dropView.isAbsorbingDrop = false
        dropView.layer?.backgroundColor = nil
    }

    private func setHighlight(_ item: WheelItem?) {
        if item != model.highlighted, item != nil {
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
        }
        model.highlighted = item
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
            guard !hasHoverReading || wedge != lastHoverWedge else { return }
            hasHoverReading = true
            lastHoverWedge = wedge
            setHighlight(wedge.flatMap { self.model.items[safe: $0] })
        }
        dropView.onDrop = { [weak self] wedge, urls in
            guard let self, let item = model.items[safe: wedge] else { return }
            dismiss(after: .zero)
            onDrop?(item, urls)
        }
        dropView.onAbsorbedDrop = { [weak self] in
            self?.dismiss(after: .zero)
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
    /// While set, any drop is accepted and discarded (see `absorbNextDrop`).
    var isAbsorbingDrop = false
    var onAbsorbedDrop: (() -> Void)?

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
        guard !isAbsorbingDrop else { return .copy }
        let wedge = wedge(for: sender)
        onHover?(wedge, fileURLs(in: sender))
        return wedge == nil ? [] : .copy
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        guard !isAbsorbingDrop else { return }
        onHover?(nil, [])
    }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        if isAbsorbingDrop {
            // The wedge already ran from the keyboard; swallow the drop.
            onAbsorbedDrop?()
            return true
        }
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
