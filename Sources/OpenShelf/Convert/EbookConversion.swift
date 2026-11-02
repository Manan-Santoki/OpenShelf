import Foundation

/// Books through calibre's `ebook-convert` (https://calibre-ebook.com), when
/// calibre is installed: `brew install --cask calibre`. EPUB, MOBI and AZW3
/// convert to each other and to PDF; PDFs become MOBI and AZW3. Pandoc, when
/// installed, adds Word, Markdown, HTML, ODT and text for EPUB.
enum EbookConversion {
    /// What calibre makes of a book, in wheel order.
    static let formats: [OutputFormat] = [.epub, .mobi, .azw3, .pdf]
    /// What calibre makes of a PDF that nothing else does.
    static let formatsForPDF: [OutputFormat] = [.mobi, .azw3]
    /// Books calibre reads (AZW is the older name of AZW3's container).
    static let bookExtensions: Set<String> = ["epub", "mobi", "azw3", "azw"]

    /// Everything a book can become with what's installed: calibre's formats,
    /// then Pandoc's. Empty when neither can read it.
    static func formats(forBook url: URL, tools: ExternalTools) -> [OutputFormat] {
        let calibre = tools.ebookConvert != nil && bookExtensions.contains(url.pathExtension.lowercased()) ? formats : []
        let pandoc = PandocConversion.formats(forBook: url, tools: tools).filter { !calibre.contains($0) }
        return calibre + pandoc
    }

    /// Whether calibre does this conversion (rather than Pandoc). calibre
    /// keeps a book's own layout in PDFs, and needs no typesetter for them.
    static func handles(_ source: URL, to format: OutputFormat, tools: ExternalTools) -> Bool {
        guard tools.ebookConvert != nil else { return false }
        switch FileKind(url: source) {
        case .ebook: return bookExtensions.contains(source.pathExtension.lowercased()) && formats.contains(format)
        case .pdf: return formatsForPDF.contains(format)
        default: return false
        }
    }

    /// `ebook-convert` takes the input and output files and picks the output
    /// format from the latter's extension. Both are absolute paths, so a name
    /// starting with "-" can't be read as an option.
    static func arguments(source: URL, output: URL) -> [String] {
        [source.path, output.path]
    }

    /// calibre makes PDFs with Qt WebEngine (Chromium), which crashed in its
    /// Metal GPU thread when started from an app (calibre 9.16, Qt 6.10.1;
    /// see djnesmith/OpenShelf). A PDF needs no GPU.
    static let environment = ["QTWEBENGINE_CHROMIUM_FLAGS": "--disable-gpu"]

    /// calibre 9.17 also crashes now and then on Chromium's main thread while
    /// rendering a PDF (about 1 in 10 with several running at once, whatever
    /// the flags); the same book converts on the next try. So a crash, never
    /// an ordinary failure, gets one more go.
    static let attemptsAfterCrash = 2

    /// ebook-convert prints "34% Running transforms on e-book..." as it goes.
    static func fraction(fromProgressLine line: String) -> Double? {
        let digits = line.prefix { $0.isNumber }
        guard !digits.isEmpty, line.dropFirst(digits.count).hasPrefix("%"), let percent = Double(digits) else { return nil }
        return min(percent / 100, 1)
    }

    static func convert(
        _ source: URL,
        to format: OutputFormat,
        tools: ExternalTools,
        runner: ToolRunner = ProcessRunner.live,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        guard let ebookConvert = tools.ebookConvert else { throw MissingToolError(name: "calibre") }
        guard let pathExtension = format.fileExtension, handles(source, to: format, tools: tools) else {
            throw ConversionError.unsupported("\(source.pathExtension.uppercased()) to \(format.title)")
        }
        // calibre writes its temporary files beside the output; all of it
        // stays in a folder of its own until the book is complete.
        let workspace = try PackageZip.makeWorkspace("ebook")
        defer { try? FileManager.default.removeItem(at: workspace) }
        let output = workspace.appendingPathComponent("converted.\(pathExtension)")

        let gate = ProgressGate(progress)
        defer { gate.close() }
        let invocation = ToolInvocation(
            executable: ebookConvert,
            arguments: arguments(source: source, output: output),
            currentDirectory: workspace,
            extraEnvironment: environment
        )
        for attempt in 1...attemptsAfterCrash {
            do {
                try await runner(invocation) { line in
                    if let fraction = fraction(fromProgressLine: line) { gate.report(fraction) }
                }
                break
            } catch let failure as ProcessFailure where failure.crashed && attempt < attemptsAfterCrash {
                try Task.checkCancellation()
                try? FileManager.default.removeItem(at: output)
            }
        }
        try Task.checkCancellation()

        guard FileManager.default.fileExists(atPath: output.path) else {
            throw ConversionError.failed("calibre couldn’t convert “\(source.lastPathComponent)”.")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        try FileManager.default.moveItem(at: output, to: destination)
        gate.report(1)
        return destination
    }
}
