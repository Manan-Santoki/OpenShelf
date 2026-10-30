import AppKit
import WebKit

/// Word documents to PDF the way Quick Look shows them, pictures included.
/// AppKit's own Word reader drops every picture, so without LibreOffice this
/// is the closest the built-in conversion gets to the document's own layout.
///
/// `qlmanage` writes Quick Look's preview out as HTML with the pictures beside
/// it, and WebKit prints that onto pages the size of the document's.
enum QuickLookConversion {
    /// Quick Look's Office previewer reads these; it doesn't preview ODT, and
    /// hands RTF back unchanged.
    static let extensions: Set<String> = ["docx", "doc"]

    /// How long each step (the preview, the printing) gets.
    static let timeout: Duration = .seconds(60)

    private static let qlmanage = URL(fileURLWithPath: "/usr/bin/qlmanage")

    static func canConvert(_ source: URL) -> Bool {
        extensions.contains(source.pathExtension.lowercased())
    }

    static func pdf(from source: URL, to destination: URL) async throws {
        let workspace = try PackageZip.makeWorkspace("quicklook")
        defer { try? FileManager.default.removeItem(at: workspace) }

        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask { try await ProcessRunner.run(qlmanage, ["-p", "-o", workspace.path, source.path]) }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw ConversionError.failed("Quick Look took too long to read “\(source.lastPathComponent)”.")
            }
            defer { group.cancelAll() }
            try await group.next()
        }

        // qlmanage succeeds even when it couldn't preview the file.
        let preview = workspace
            .appendingPathComponent(source.lastPathComponent + ".qlpreview", isDirectory: true)
            .appendingPathComponent("Preview.html")
        guard FileManager.default.fileExists(atPath: preview.path) else {
            throw ConversionError.failed("Quick Look couldn’t read “\(source.lastPathComponent)”.")
        }
        try await QuickLookPrinter.print(preview, to: destination)
    }

    /// Quick Look gives each page the document's width but not its height
    /// (every page is 844 tall), so the height comes from the paper sizes
    /// Word offers: A4, US Letter, A5, B5. Anything else gets A4's proportions.
    static func paperSize(width: Double) -> NSSize {
        let sizes: [(width: Double, height: Double)] = [(595, 842), (612, 792), (420, 595), (499, 709)]
        let height = sizes.first { abs($0.width - width) <= 3 }?.height ?? (width * 2.0.squareRoot()).rounded()
        return NSSize(width: width, height: height)
    }
}

/// Prints Quick Look's HTML preview to a PDF in an off-screen web view.
@MainActor
private final class QuickLookPrinter: NSObject, WKNavigationDelegate {
    /// Reads the first page's width and bottom margin, then takes the page
    /// padding away: each page in the preview is one long box with the
    /// margins as padding, and printing puts the margins on every page instead.
    ///
    /// WebKit lays printed pages out 1.25 times as wide as the paper and
    /// shrinks them to fit, so the zoom keeps the document at its own size.
    private static let preparePages = """
        (() => {
          const page = document.body.firstElementChild;
          const box = page ? [page.offsetWidth, parseFloat(getComputedStyle(page).paddingBottom) || 0] : [];
          const style = document.createElement('style');
          style.textContent = 'body { margin: 0 !important; zoom: 1.25 } body > div { padding-top: 0 !important; padding-bottom: 0 !important; min-height: 0 !important }';
          document.head.appendChild(style);
          return box;
        })()
        """

    private let destination: URL
    private let window: NSWindow
    private let webView: WKWebView
    private var continuation: CheckedContinuation<Void, Error>?

    static func print(_ preview: URL, to destination: URL) async throws {
        let printer = QuickLookPrinter(destination: destination)
        try await printer.run(preview)
    }

    private init(destination: URL) {
        self.destination = destination
        let frame = NSRect(x: 0, y: 0, width: 612, height: 792)
        // Printing a web view needs a window to run in; this one is never shown.
        window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        webView = WKWebView(frame: frame, configuration: configuration)
        super.init()
        window.contentView = webView
        webView.navigationDelegate = self
    }

    private func run(_ preview: URL) async throws {
        let timer = Task { [weak self] in
            try await Task.sleep(for: QuickLookConversion.timeout)
            self?.finish(.failure(ConversionError.failed("Quick Look’s preview took too long to print.")))
        }
        defer {
            timer.cancel()
            window.close()
        }
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            webView.loadFileURL(preview, allowingReadAccessTo: preview.deletingLastPathComponent())
        }
    }

    private func finish(_ result: Result<Void, Error>) {
        guard let continuation else { return }
        self.continuation = nil
        continuation.resume(with: result)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task {
            do {
                let box = try await webView.evaluateJavaScript(Self.preparePages) as? [Double] ?? []
                let width = box.first ?? 612
                let margin = box.count > 1 && box[1] > 0 ? box[1] : 72
                printPages(size: QuickLookConversion.paperSize(width: width), margin: margin)
            } catch {
                finish(.failure(error))
            }
        }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        finish(.failure(error))
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        finish(.failure(error))
    }

    private func printPages(size: NSSize, margin: CGFloat) {
        // As wide as the page, so printing doesn't shrink it to fit.
        window.setContentSize(NSSize(width: size.width, height: size.height))

        let printInfo = NSPrintInfo()
        printInfo.paperSize = size
        // The page box keeps its own left and right margins.
        printInfo.leftMargin = 0
        printInfo.rightMargin = 0
        // Quick Look gives the bottom margin; the top padding is the header's distance.
        printInfo.topMargin = margin
        printInfo.bottomMargin = margin
        printInfo.horizontalPagination = .fit
        printInfo.verticalPagination = .automatic
        printInfo.isHorizontallyCentered = false
        printInfo.isVerticallyCentered = false
        printInfo.jobDisposition = .save
        printInfo.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = destination

        let operation = webView.printOperation(with: printInfo)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        operation.view?.frame = webView.bounds
        // A web view prints blank pages, or never returns, with `run()`.
        operation.runModal(for: window, delegate: self, didRun: #selector(printOperationDidRun(_:success:contextInfo:)), contextInfo: nil)
    }

    /// AppKit calls this on the print operation's own thread.
    @objc nonisolated private func printOperationDidRun(_ operation: NSPrintOperation, success: Bool, contextInfo: UnsafeMutableRawPointer?) {
        Task { @MainActor in
            if success, FileManager.default.fileExists(atPath: destination.path) {
                finish(.success(()))
            } else {
                finish(.failure(EditableImageError.unwritable(destination)))
            }
        }
    }
}
