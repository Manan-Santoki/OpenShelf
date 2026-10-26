import AppKit
import Observation
import QuickLookThumbnailing
import SwiftUI

/// The tour's window. Opens by itself the first time OpenShelf runs, and from
/// the menu bar and Settings → General after that.
@MainActor
final class TourWindowController: NSWindowController, NSWindowDelegate {
    private static var current: TourWindowController?
    private static let shownKey = "hasShownTour"
    /// Set by the welcome guide the tour replaced: people who saw it are
    /// offered the tour once too, as something new rather than a welcome.
    private static let welcomeKey = "hasShownWelcome"
    static let size = CGSize(width: 800, height: 560)

    private let model = TourModel()

    /// Shows the tour unless it has been shown before.
    static func presentIfNeeded() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: shownKey) else { return }
        present()
        current?.model.isReturning = defaults.bool(forKey: welcomeKey)
    }

    /// Shows the tour, from the start when `startingOver`.
    static func present(startingOver: Bool = false) {
        UserDefaults.standard.set(true, forKey: shownKey)
        if startingOver { TourProgress.shared.reset() }
        let controller = current ?? TourWindowController()
        current = controller
        controller.model.start()
        TourProgress.shared.isActive = true
        controller.window?.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    private init() {
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: Self.size),
            styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "OpenShelf Tour"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isReleasedWhenClosed = false
        window.setFrameAutosaveName("OpenShelfTour")
        super.init(window: window)
        window.delegate = self
        window.contentView = NSHostingView(rootView: TourView(model: model, progress: .shared) { [weak window] in
            window?.performClose(nil)
        })
        if !window.setFrameUsingName("OpenShelfTour") {
            window.center()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func windowWillClose(_ notification: Notification) {
        TourProgress.shared.isActive = false
        model.stop()
        Self.current = nil
    }
}

/// The practice folder as the tour shows it: whether its files are ready, and
/// what the person has made from them.
@MainActor
@Observable
final class TourModel {
    private(set) var isReady = false
    private(set) var failure: String?
    /// Files made from the practice files, newest first.
    private(set) var results: [URL] = []
    private(set) var thumbnails: [URL: NSImage] = [:]
    /// Whether the person used OpenShelf before the tour existed.
    var isReturning = false

    @ObservationIgnored private var watcher: Task<Void, Never>?
    @ObservationIgnored let folder: URL

    init(folder: URL = PracticeFiles.folder) {
        self.folder = folder
    }

    func url(of file: PracticeFile) -> URL {
        PracticeFiles.url(of: file, in: folder)
    }

    /// Makes any missing practice files, then follows the folder while the tour is open.
    func start() {
        guard watcher == nil else { return }
        watcher = Task { [weak self] in
            guard let self else { return }
            do {
                try await PracticeFiles.prepare(in: folder)
                isReady = true
                failure = nil
            } catch {
                failure = "The practice files couldn’t be made: \(error.localizedDescription)"
            }
            for file in PracticeFile.allCases {
                await loadThumbnail(url(of: file))
            }
            while !Task.isCancelled {
                refresh()
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    func stop() {
        watcher?.cancel()
        watcher = nil
    }

    /// Makes the practice files again after they were removed.
    func restore() {
        stop()
        isReady = false
        start()
    }

    func removePracticeFiles() {
        try? PracticeFiles.remove(folder)
        isReady = false
        results = []
    }

    private func refresh() {
        let found = PracticeFiles.results(in: folder)
        guard found != results else { return }
        results = found
        for url in found where thumbnails[url] == nil {
            Task { await loadThumbnail(url) }
        }
    }

    private func loadThumbnail(_ url: URL) async {
        let request = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: 96, height: 96), scale: 2, representationTypes: .thumbnail)
        if let representation = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request) {
            thumbnails[url] = representation.nsImage
        } else {
            thumbnails[url] = NSWorkspace.shared.icon(forFile: url.path)
        }
    }
}

/// Makes the view it covers a drag source for one file, as a Finder icon is,
/// so dragging it out lets the wheels open just as they do from Finder.
struct FileDragSource: NSViewRepresentable {
    let url: URL
    var image: NSImage?

    func makeNSView(context: Context) -> FileDragSourceView {
        let view = FileDragSourceView()
        view.url = url
        view.image = image
        return view
    }

    func updateNSView(_ view: FileDragSourceView, context: Context) {
        view.url = url
        view.image = image
    }
}

final class FileDragSourceView: NSView, NSDraggingSource {
    var url: URL?
    var image: NSImage?
    private var mouseDownEvent: NSEvent?

    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .openHand)
    }

    override func mouseDown(with event: NSEvent) {
        mouseDownEvent = event
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = mouseDownEvent, let url, FileManager.default.fileExists(atPath: url.path) else { return }
        let distance = hypot(event.locationInWindow.x - start.locationInWindow.x, event.locationInWindow.y - start.locationInWindow.y)
        guard distance > 3 else { return }
        mouseDownEvent = nil
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        let side: CGFloat = 64
        let point = convert(start.locationInWindow, from: nil)
        item.setDraggingFrame(
            NSRect(x: point.x - side / 2, y: point.y - side / 2, width: side, height: side),
            contents: image ?? NSWorkspace.shared.icon(forFile: url.path)
        )
        let session = beginDraggingSession(with: [item], event: start, source: self)
        session.animatesToStartingPositionsOnCancelOrFail = true
    }

    override func mouseUp(with event: NSEvent) {
        if event.clickCount == 2, let url {
            NSWorkspace.shared.open(url)
        }
        mouseDownEvent = nil
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        // Practice files are copied when dropped elsewhere, never moved away.
        .copy
    }
}
