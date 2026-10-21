import AppKit
import Carbon.HIToolbox
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
    /// A short instruction that replaces the label for a moment.
    var hint: String?
    /// Which wheel is showing, named in the label when opened from the keyboard.
    var wheelName: String?
    var isPresented = false
}

/// Owns the overlay panel that shows a wheel under the pointer during a drag,
/// or, in keyboard mode, for files selected in Finder.
@MainActor
final class WheelController {
    /// Called when files are dropped on a wedge (or a wedge runs from the keyboard).
    var onDrop: ((WheelItem, [URL]) -> Void)?
    /// Supplies each wheel's wedges for the files of a keyboard session.
    var itemsProvider: ((WheelKind, [URL]) -> [WheelItem])?

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
    private var hintTask: Task<Void, Never>?
    private var keyboardSession: KeyboardSession?
    private var resignKeyObserver: (any NSObjectProtocol)?

    /// A wheel opened by shortcut for Finder's selection, driven by keys and clicks.
    private struct KeyboardSession {
        let urls: [URL]
        let itemsByKind: [WheelKind: [WheelItem]]
        let available: [WheelKind]
        var kind: WheelKind
        var taps = ModifierTapTracker()
    }

    var isInKeyboardMode: Bool { keyboardSession != nil }

    /// Shows `items` around `location`, or swaps them in place if the wheel is
    /// already open (e.g. when ⌥ is added to switch from Convert to Tools).
    func present(_ items: [WheelItem], at location: NSPoint) {
        dismissTask?.cancel()
        let isSwitching = panel.isVisible && model.isPresented
        model.items = items
        model.highlighted = nil
        model.dragSummary = ""
        model.hint = nil
        model.wheelName = nil
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
        endKeyboardMode()
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

    /// Shows a short instruction in the wheel's label for a moment.
    func showHint(_ text: String) {
        guard model.isPresented, model.hint != text else { return }
        model.hint = text
        hintTask?.cancel()
        hintTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            self?.model.hint = nil
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

    // MARK: Keyboard mode

    /// Opens a wheel for `urls` (e.g. Finder's selection) that is driven by the
    /// keyboard or clicks instead of a drag: arrows choose, Return runs, Tab or
    /// a trigger tap switches wheels, Esc or a click elsewhere closes. Returns
    /// false when no wheel has anything for these files.
    @discardableResult
    func presentForKeyboard(urls: [URL], at location: NSPoint) -> Bool {
        guard let itemsProvider, !urls.isEmpty else { return false }
        var itemsByKind: [WheelKind: [WheelItem]] = [:]
        for kind in WheelCycle.order {
            itemsByKind[kind] = itemsProvider(kind, urls)
        }
        let available = WheelCycle.available { !(itemsByKind[$0] ?? []).isEmpty }
        guard let first = available.first else { return false }

        endKeyboardMode(hidingWheel: true)
        keyboardSession = KeyboardSession(urls: urls, itemsByKind: itemsByKind, available: available, kind: first)
        showKeyboardWheel(first, at: location)

        dropView.isKeyboardMode = true
        panel.acceptsKeyboard = true
        panel.keyHandler = { [weak self] event in self?.handleKey(event) ?? false }
        panel.makeKeyAndOrderFront(nil)
        if !panel.isKeyWindow {
            NSApp.activate()
            panel.makeKey()
        }
        resignKeyObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.didResignKeyNotification, object: panel, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                // Clicking anywhere else closes the wheel.
                guard let self, self.isInKeyboardMode else { return }
                self.dismiss(after: .zero)
            }
        }
        let hint = available.count > 1 ? "Arrows choose · Return runs · Tab switches wheel" : "Arrows choose · Return runs"
        showHint(hint)
        return true
    }

    /// Leaves keyboard mode. A drag-triggered wheel calls this with
    /// `hidingWheel` so it starts fresh under the pointer.
    func endKeyboardMode(hidingWheel: Bool = false) {
        guard keyboardSession != nil else { return }
        keyboardSession = nil
        dropView.isKeyboardMode = false
        panel.acceptsKeyboard = false
        panel.keyHandler = nil
        model.wheelName = nil
        if let resignKeyObserver {
            NotificationCenter.default.removeObserver(resignKeyObserver)
        }
        resignKeyObserver = nil
        if panel.isKeyWindow {
            panel.resignKey()
        }
        if hidingWheel {
            dismissTask?.cancel()
            model.isPresented = false
            model.highlighted = nil
            panel.orderOut(nil)
        }
    }

    private func showKeyboardWheel(_ kind: WheelKind, at location: NSPoint? = nil) {
        guard var session = keyboardSession, let items = session.itemsByKind[kind] else { return }
        session.kind = kind
        keyboardSession = session
        present(items, at: location ?? NSPoint(x: panel.frame.midX, y: panel.frame.midY))
        updateSummary(for: session.urls)
        model.wheelName = kind.shortTitle
        // Start on the first wedge so Return does something straight away.
        setHighlight(items.first)
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        guard var session = keyboardSession else { return false }
        if event.type == .flagsChanged {
            let tapped = session.taps.flagsChanged(event.modifierFlags)
            keyboardSession = session
            if let tapped,
               let kind = TriggerMatcher(triggers: Preferences.shared.triggers).wheel(for: tapped),
               session.available.contains(kind), kind != session.kind {
                showKeyboardWheel(kind)
            }
            return true
        }
        session.taps.keyPressed()
        keyboardSession = session
        switch Int(event.keyCode) {
        case kVK_RightArrow, kVK_DownArrow:
            step(.clockwise)
        case kVK_LeftArrow, kVK_UpArrow:
            step(.counterClockwise)
        case kVK_Return, kVK_ANSI_KeypadEnter:
            runHighlighted()
        case kVK_Escape:
            dismiss(after: .zero)
        case kVK_Tab:
            if let next = WheelCycle.next(after: session.kind, in: session.available, backwards: event.modifierFlags.contains(.shift)) {
                showKeyboardWheel(next)
            }
        default:
            return false
        }
        return true
    }

    private func runHighlighted() {
        guard let session = keyboardSession, let item = model.highlighted else { return }
        dismiss(after: .zero)
        onDrop?(item, session.urls)
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

    private func makePanel() -> WheelPanel {
        let side = WheelGeometry.panelSide
        let panel = WheelPanel(
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
        panel.acceptsMouseMovedEvents = true

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
        dropView.onPointerMoved = { [weak self] wedge in
            guard let self, isInKeyboardMode else { return }
            guard !hasHoverReading || wedge != lastHoverWedge else { return }
            hasHoverReading = true
            lastHoverWedge = wedge
            if let wedge { setHighlight(model.items[safe: wedge]) }
        }
        dropView.onClick = { [weak self] wedge in
            guard let self, isInKeyboardMode else { return }
            guard let wedge, let item = model.items[safe: wedge] else {
                dismiss(after: .zero)
                return
            }
            setHighlight(item)
            runHighlighted()
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

/// The wheel's window. It only takes keyboard focus in keyboard mode, so a
/// wheel shown mid-drag never takes focus from the app being dragged from.
final class WheelPanel: NSPanel {
    var acceptsKeyboard = false
    /// Return true when the key or modifier event was handled.
    var keyHandler: ((NSEvent) -> Bool)?

    override var canBecomeKey: Bool { acceptsKeyboard }

    override func keyDown(with event: NSEvent) {
        if keyHandler?(event) != true {
            super.keyDown(with: event)
        }
    }

    override func flagsChanged(with event: NSEvent) {
        if keyHandler?(event) != true {
            super.flagsChanged(with: event)
        }
    }
}

/// Container view that receives the file drag and hit-tests it against the
/// wedges; in keyboard mode it also handles hover and clicks.
final class WheelDropView: NSView {
    var geometry = WheelGeometry(wedgeCount: 0)
    var onHover: ((Int?, [URL]) -> Void)?
    var onDrop: ((Int, [URL]) -> Void)?
    /// While set, any drop is accepted and discarded (see `absorbNextDrop`).
    var isAbsorbingDrop = false
    var onAbsorbedDrop: (() -> Void)?
    /// Set while the wheel was opened from the keyboard rather than a drag.
    var isKeyboardMode = false
    var onPointerMoved: ((Int?) -> Void)?
    /// Called with the clicked wedge, or nil for a click outside the wedges.
    var onClick: ((Int?) -> Void)?

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
        wedge(atWindowPoint: info.draggingLocation)
    }

    private func wedge(atWindowPoint windowPoint: NSPoint) -> Int? {
        let point = convert(windowPoint, from: nil)
        return geometry.wedge(at: point, center: CGPoint(x: bounds.midX, y: bounds.midY))
    }

    // MARK: Keyboard mode pointer handling

    override func hitTest(_ point: NSPoint) -> NSView? {
        // Take clicks before the SwiftUI content; drags are unaffected.
        guard isKeyboardMode else { return super.hitTest(point) }
        return frame.contains(point) ? self : nil
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas {
            removeTrackingArea(area)
        }
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseMoved(with event: NSEvent) {
        guard isKeyboardMode else { return }
        onPointerMoved?(wedge(atWindowPoint: event.locationInWindow))
    }

    override func mouseDown(with event: NSEvent) {
        guard isKeyboardMode else {
            super.mouseDown(with: event)
            return
        }
        onClick?(wedge(atWindowPoint: event.locationInWindow))
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
