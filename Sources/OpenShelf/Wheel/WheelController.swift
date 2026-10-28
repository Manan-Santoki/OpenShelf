import AppKit
import Carbon.HIToolbox
import Observation
import SwiftUI

/// State shared between the wheel's drop handling and its SwiftUI rendering.
@MainActor
@Observable
final class WheelModel {
    /// Every item on the wheel, across all its pages.
    private(set) var items: [WheelItem] = []
    /// The page showing, from 0.
    private(set) var page = 0
    /// What each wedge shows on the current page.
    private(set) var slots: [WheelSlot] = []
    /// Changes when a different set of items is shown, so the new wheel's
    /// petals start afresh instead of morphing out of the previous wheel's.
    private(set) var itemsID = 0
    /// Index in `slots` of the highlighted wedge.
    var highlightedSlot: Int?
    /// Index of the More or Back wedge filling up while the pointer rests on it.
    var dwellingSlot: Int?
    /// What the hub shows while no wedge is hovered, e.g. "1.2 MB" or "3 files".
    var dragSummary = ""
    /// A short instruction that replaces the label for a moment.
    var hint: String?
    /// Which wheel is showing, named in the label when opened from the keyboard.
    var wheelName: String?
    /// Whether ⌘ is held on a keyboard wheel, showing each wedge's number key.
    var showsBadges = false
    var isPresented = false

    var pageCount: Int { WheelPaging.pageCount(forItemCount: items.count) }

    var highlighted: WheelSlot? { highlightedSlot.flatMap { slots[safe: $0] } }

    /// Shows `items` from their first page.
    func show(_ items: [WheelItem]) {
        if items != self.items { itemsID += 1 }
        self.items = items
        showPage(0)
    }

    func showPage(_ page: Int) {
        self.page = page
        slots = WheelPaging.slots(for: items, page: page)
    }
}

/// Owns the overlay panel that shows a wheel under the pointer during a drag,
/// or, in keyboard mode, for files selected in Finder.
@MainActor
final class WheelController {
    /// Called when files are dropped on a wedge (or a wedge runs from the
    /// keyboard), with the modifier keys held for it: ⌥ asks for a
    /// conversion's options. Keys still down from the shortcut that opened a
    /// keyboard wheel don't count. The last value is the shelf these files
    /// were dragged or selected from, if any.
    var onDrop: ((WheelItem, [URL], NSEvent.ModifierFlags, Shelf.ID?) -> Void)?
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
    /// Resting on More or Back turns the page; the task checks back once the
    /// rest has lasted long enough.
    private var pageTurnDwell = PageTurnDwell()
    private var dwellTask: Task<Void, Never>?

    /// A wheel opened by shortcut for Finder's selection, driven by keys and clicks.
    private struct KeyboardSession {
        let urls: [URL]
        let itemsByKind: [WheelKind: [WheelItem]]
        let available: [WheelKind]
        var kind: WheelKind
        var taps: ModifierTapTracker
        /// The shelf `urls` came from, if any.
        var sourceShelfID: Shelf.ID?
    }

    var isInKeyboardMode: Bool { keyboardSession != nil }

    /// Builds the panel and draws it once, invisibly, so the first wheel opens
    /// as quickly as later ones.
    func prepare() {
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        panel.displayIfNeeded()
        panel.orderOut(nil)
        panel.alphaValue = 1
    }

    /// Shows `items` around `location`, or swaps them in place if the wheel is
    /// already open (e.g. when ⌥ is added to switch from Convert to Tools).
    /// Either way the wheel starts on its first page.
    func present(_ items: [WheelItem], at location: NSPoint) {
        dismissTask?.cancel()
        let isSwitching = panel.isVisible && model.isPresented
        model.show(items)
        model.highlightedSlot = nil
        model.dragSummary = ""
        model.hint = nil
        model.wheelName = nil
        summarizedURLs = []
        hasHoverReading = false
        endAbsorbingDrop()
        cancelDwell()
        updateDropTargets()
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
        cancelDwell()
        guard panel.isVisible else { return }
        dismissTask?.cancel()
        dismissTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard let self, !Task.isCancelled else { return }
            model.isPresented = false
            model.highlightedSlot = nil
            endAbsorbingDrop()
            try? await Task.sleep(for: .milliseconds(160))
            guard !Task.isCancelled else { return }
            panel.orderOut(nil)
        }
    }

    /// The drag that summoned the wheel is over (the button came up, or Esc).
    /// Hides the wheel, unless the files were dropped on More or Back, which
    /// leaves it open to finish with a click or the keys.
    func dragDidEnd() {
        guard !isInKeyboardMode else { return }
        dismiss()
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

    /// Moves the highlight one wedge with the keyboard, over More or Back too
    /// but not empty wedges.
    func step(_ direction: WheelStepDirection) {
        guard model.isPresented, !isAbsorbingDrop else { return }
        guard let next = WheelKeyboardNavigator.step(from: model.highlightedSlot, in: model.slots, direction: direction) else { return }
        setHighlight(next)
    }

    /// Runs the highlighted wedge on `urls`, as if they were dropped on it, and
    /// hides the wheel. On More or Back it turns the page instead, keeping the
    /// highlight there so Return again turns back. Returns whether a wedge ran.
    func confirmHighlighted(with urls: [URL]) -> Bool {
        guard model.isPresented, !isAbsorbingDrop, !urls.isEmpty else { return false }
        guard let slot = model.highlightedSlot, let highlighted = model.highlighted else {
            showHint("Choose a wedge with the arrow keys")
            return false
        }
        guard let item = highlighted.item else {
            turnPage(from: slot)
            return false
        }
        absorbNextDrop()
        onDrop?(item, urls, NSEvent.modifierFlags, Shelf.activeDragSource?.id)
        return true
    }

    // MARK: Pages

    /// Turns to the page that the More or Back wedge at `slot` leads to. The
    /// highlight stays on that wedge, which now shows Back or More.
    private func turnPage(from slot: Int) {
        guard let page = model.slots[safe: slot]?.targetPage else { return }
        // More and Back share a wedge, so a pointer resting there is now on the
        // other one; it has to leave and come back to turn again.
        let isPointerOnSlot = hasHoverReading && lastHoverWedge == slot
        pageTurnDwell.pageDidTurn(isOnPageTurn: isPointerOnSlot, at: ContinuousClock.now)
        dwellTask?.cancel()
        dwellTask = nil
        if model.dwellingSlot != nil { model.dwellingSlot = nil }
        model.showPage(page)
        updateDropTargets()
        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
    }

    /// Follows the pointer for the dwell on More and Back: resting there fills
    /// the wedge's ring, and the page turns once it's full.
    private func updateDwell(pointerSlot slot: Int?) {
        let isOnPageTurn = slot.flatMap { model.slots[safe: $0]?.targetPage } != nil
        let wasPending = pageTurnDwell.isPending
        if pageTurnDwell.update(isOnPageTurn: isOnPageTurn, at: ContinuousClock.now), let slot {
            turnPage(from: slot)
            return
        }
        guard pageTurnDwell.isPending, let slot else {
            cancelDwell(resettingState: false)
            return
        }
        // `@Observable` notifies observers on every assignment, even to an
        // equal value, so this would otherwise re-render every petal on each
        // of the dozens of hover readings a second while resting on the wedge.
        if model.dwellingSlot != slot { model.dwellingSlot = slot }
        guard !wasPending else { return }
        // Pointer readings stop while it rests, so check back on a timer.
        dwellTask = Task { [weak self] in
            try? await Task.sleep(for: PageTurnDwell.delay)
            while let self, !Task.isCancelled, self.pageTurnDwell.isPending {
                self.updateDwell(pointerSlot: slot)
                try? await Task.sleep(for: .milliseconds(10))
            }
        }
    }

    private func cancelDwell(resettingState: Bool = true) {
        if resettingState { pageTurnDwell = PageTurnDwell() }
        dwellTask?.cancel()
        dwellTask = nil
        // Called on every hover reading where the pointer isn't on More or
        // Back, i.e. most mouse moves over the wheel, so skip the write (and
        // the observation notification it carries) once it's already nil.
        if model.dwellingSlot != nil { model.dwellingSlot = nil }
    }

    /// Tells the drop view where the wedges are and which ones take files.
    private func updateDropTargets() {
        dropView.geometry = WheelGeometry(wedgeCount: model.slots.count)
        dropView.emptySlots = Set(model.slots.indices.filter { model.slots[$0].isEmpty })
    }

    // MARK: Keyboard mode

    /// Opens a wheel for `urls` (e.g. Finder's selection) that is driven by the
    /// keyboard or clicks instead of a drag: arrows choose, Return runs, Tab or
    /// a trigger tap switches wheels, Esc or a click elsewhere closes. Returns
    /// false when no wheel has anything for these files.
    @discardableResult
    func presentForKeyboard(urls: [URL], at location: NSPoint, sourceShelfID: Shelf.ID? = nil) -> Bool {
        guard let itemsProvider, !urls.isEmpty else { return false }
        var itemsByKind: [WheelKind: [WheelItem]] = [:]
        for kind in WheelCycle.order {
            itemsByKind[kind] = itemsProvider(kind, urls)
        }
        let available = WheelCycle.available { !(itemsByKind[$0] ?? []).isEmpty }
        guard let first = available.first else { return false }

        endKeyboardMode(hidingWheel: true)
        // Always the first wheel in Tab order. The shortcut's own modifiers are
        // usually still down; letting go of them isn't a tap that switches wheels.
        keyboardSession = KeyboardSession(
            urls: urls,
            itemsByKind: itemsByKind,
            available: available,
            kind: first,
            taps: ModifierTapTracker(alreadyHeld: NSEvent.modifierFlags),
            sourceShelfID: sourceShelfID
        )
        showKeyboardWheel(first, at: location)
        takeKeyboard(hint: KeyboardWheelHint.opening(canSwitch: available.count > 1, hasOptions: hasOptions(first)))
        return true
    }

    /// After files were dropped on More or Back, keeps the wheel open on its
    /// new page as a keyboard wheel for those files, so a click or Return can
    /// finish what the drag started.
    private func holdOpenAfterDrop(of urls: [URL]) {
        // A keyboard wheel that had files dropped on it already has its own.
        guard !isInKeyboardMode else { return }
        var itemsByKind: [WheelKind: [WheelItem]] = [:]
        for kind in WheelCycle.order {
            itemsByKind[kind] = itemsProvider?(kind, urls) ?? []
        }
        let available = WheelCycle.available { !(itemsByKind[$0] ?? []).isEmpty }
        // The wheel showing is the one with exactly these items; no two wheels share any.
        guard let kind = available.first(where: { itemsByKind[$0] == model.items }) else {
            dismiss(after: .zero)
            return
        }
        // The drag's end may already have asked for the wheel to go.
        dismissTask?.cancel()
        keyboardSession = KeyboardSession(
            urls: urls,
            itemsByKind: itemsByKind,
            available: available,
            kind: kind,
            taps: ModifierTapTracker(alreadyHeld: NSEvent.modifierFlags),
            // The drag that dropped on More/Back is still in progress here.
            sourceShelfID: Shelf.activeDragSource?.id
        )
        updateSummary(for: urls)
        model.wheelName = kind.shortTitle
        takeKeyboard(hint: "Click to run · Esc closes")
    }

    /// Lets the open wheel take keys and clicks for the keyboard session.
    private func takeKeyboard(hint: String) {
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
        model.showsBadges = NSEvent.modifierFlags.contains(.command)
        showHint(hint)
    }

    /// Leaves keyboard mode. A drag-triggered wheel calls this with
    /// `hidingWheel` so it starts fresh under the pointer.
    func endKeyboardMode(hidingWheel: Bool = false) {
        guard keyboardSession != nil else { return }
        keyboardSession = nil
        model.showsBadges = false
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
            cancelDwell()
            model.isPresented = false
            model.highlightedSlot = nil
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
        setHighlight(items.isEmpty ? nil : 0)
        // On switching to it with Tab or a tap; the hint the wheel opened
        // with already mentions ⌥Return.
        if location == nil, hasOptions(kind) {
            showHint(KeyboardWheelHint.options)
        }
    }

    /// Whether `kind`'s wheel in this session has a format with options,
    /// which ⌥Return opens.
    private func hasOptions(_ kind: WheelKind) -> Bool {
        guard kind == .convert, let session = keyboardSession, let items = session.itemsByKind[kind] else { return false }
        return ConversionCatalog.formats(in: items).contains { !OutputOptions(format: $0, urls: session.urls).isEmpty }
    }

    /// The keyboard wheel's keys (internal, not private, for tests).
    func handleKey(_ event: NSEvent) -> Bool {
        guard var session = keyboardSession else { return false }
        if event.type == .flagsChanged {
            model.showsBadges = event.modifierFlags.contains(.command)
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
        if event.modifierFlags.contains(.command) {
            return runBadge(event.charactersIgnoringModifiers ?? "", modifiers: event.modifierFlags)
        }
        switch Int(event.keyCode) {
        case kVK_RightArrow, kVK_DownArrow:
            step(.clockwise)
        case kVK_LeftArrow, kVK_UpArrow:
            step(.counterClockwise)
        case kVK_Return, kVK_ANSI_KeypadEnter:
            runHighlighted(modifiers: event.modifierFlags)
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

    /// ⌘ and a wedge's number: runs it as Return would, or turns the page for 0.
    private func runBadge(_ key: String, modifiers: NSEvent.ModifierFlags) -> Bool {
        guard let slot = WheelBadges.slot(forKey: key, in: model.slots) else { return false }
        setHighlight(slot)
        runHighlighted(modifiers: modifiers)
        return true
    }

    /// Runs the highlighted wedge, or turns the page when it's More or Back.
    private func runHighlighted(modifiers: NSEvent.ModifierFlags? = nil) {
        guard let session = keyboardSession, let slot = model.highlightedSlot, let highlighted = model.highlighted else { return }
        guard let item = highlighted.item else {
            turnPage(from: slot)
            return
        }
        let modifiers = session.taps.chosen(modifiers ?? NSEvent.modifierFlags)
        dismiss(after: .zero)
        onDrop?(item, session.urls, modifiers, session.sourceShelfID)
    }

    /// Hides the wheel but keeps its panel, invisible, centred under the pointer
    /// so the user's mouse-up drops the files here, where nothing happens,
    /// instead of on whatever is underneath. Ends when the drop arrives or the
    /// drag ends (`dismiss`).
    private func absorbNextDrop() {
        dismissTask?.cancel()
        isAbsorbingDrop = true
        cancelDwell()
        model.isPresented = false
        model.highlightedSlot = nil
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

    /// Highlights the wedge at `slot`; an empty wedge or nil clears the highlight.
    private func setHighlight(_ slot: Int?) {
        let slot = slot.flatMap { model.slots[safe: $0]?.isEmpty == false ? $0 : nil }
        // Two different wedges can both resolve to the same highlight (e.g.
        // two empty placeholders both map to nil), and `@Observable` notifies
        // observers even on an equal assignment, so guard here rather than
        // just on the raw wedge index the callers already compare against.
        guard slot != model.highlightedSlot else { return }
        if slot != nil {
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
        }
        model.highlightedSlot = slot
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
            updateDwell(pointerSlot: wedge)
            guard !hasHoverReading || wedge != lastHoverWedge else { return }
            hasHoverReading = true
            lastHoverWedge = wedge
            setHighlight(wedge)
        }
        dropView.onDrop = { [weak self] wedge, urls in
            guard let self, let slot = model.slots[safe: wedge] else { return }
            switch slot {
            case .item(let item):
                dismiss(after: .zero)
                onDrop?(item, urls, NSEvent.modifierFlags, Shelf.activeDragSource?.id)
            case .more, .back:
                // Nothing runs; the wheel stays open on the other page.
                turnPage(from: wedge)
                setHighlight(wedge)
                holdOpenAfterDrop(of: urls)
            case .empty:
                break
            }
        }
        dropView.onAbsorbedDrop = { [weak self] in
            self?.dismiss(after: .zero)
        }
        dropView.onPointerMoved = { [weak self] wedge in
            guard let self, isInKeyboardMode else { return }
            updateDwell(pointerSlot: wedge)
            guard !hasHoverReading || wedge != lastHoverWedge else { return }
            hasHoverReading = true
            lastHoverWedge = wedge
            if let wedge, model.slots[safe: wedge]?.isEmpty == false { setHighlight(wedge) }
        }
        dropView.onClick = { [weak self] wedge in
            guard let self, isInKeyboardMode else { return }
            guard let wedge, let slot = model.slots[safe: wedge] else {
                dismiss(after: .zero)
                return
            }
            // An empty wedge belongs to the wheel but does nothing.
            guard !slot.isEmpty else { return }
            setHighlight(wedge)
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

    /// ⌘ with a number reaches the wheel before any menu shortcut does.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if acceptsKeyboard, event.modifierFlags.contains(.command), keyHandler?(event) == true {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

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
    /// Wedges that are only placeholders on a paged wheel; files can't be dropped there.
    var emptySlots: Set<Int> = []
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
    /// The dragged files, read once when the drag enters and reused for every
    /// hover update, so a file drag moving over the wheel doesn't hit the
    /// pasteboard on every mouse move (AppKit calls `draggingUpdated` on each
    /// one, and the dragged files don't change while it's hovering).
    private var hoveredFileURLs: [URL] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        hoveredFileURLs = isAbsorbingDrop ? [] : fileURLs(in: sender)
        return draggingUpdated(sender)
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        guard !isAbsorbingDrop else { return .copy }
        let wedge = wedge(for: sender)
        onHover?(wedge, hoveredFileURLs)
        return wedge == nil ? [] : .copy
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        guard !isAbsorbingDrop else { return }
        hoveredFileURLs = []
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

    /// The wedge under a drag, if it takes files.
    private func wedge(for info: any NSDraggingInfo) -> Int? {
        wedge(atWindowPoint: info.draggingLocation).flatMap { emptySlots.contains($0) ? nil : $0 }
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
