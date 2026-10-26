import AppKit
import CoreText
import PDFKit

enum PDFConversion {
    /// Page images are rendered at print resolution.
    private static let renderDPI = 300.0

    /// - Parameters:
    ///   - original: Where results are saved and what they're named after, when
    ///     `source` is an intermediate PDF (e.g. made from slides).
    ///   - pageLabel: How numbered image files are labelled ("Page 2", "Slide 2").
    ///   - imageQuality: JPEG quality for page images; nil for the usual 0.92.
    static func convert(
        _ source: URL,
        to format: OutputFormat,
        savingNextTo original: URL? = nil,
        pageLabel: String = "Page",
        imageQuality: Double? = nil
    ) throws -> [URL] {
        guard let document = PDFDocument(url: source) else { throw ConversionError.unreadable(source) }
        let original = original ?? source
        // A locked PDF opens, but every page renders blank and has no text.
        guard !document.isLocked else {
            throw ConversionError.failed("“\(source.lastPathComponent)” is password-protected. Remove the password in Preview first.")
        }

        switch format {
        case .txt:
            let text = document.string ?? ""
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw ConversionError.failed("“\(source.lastPathComponent)” has no text to extract (it looks like a scan).")
            }
            let destination = OutputNaming.destination(for: original, pathExtension: "txt")
            try text.write(to: destination, atomically: true, encoding: .utf8)
            return [destination]

        case .docx:
            let destination = OutputNaming.destination(for: original, pathExtension: "docx")
            let text = NSMutableAttributedString()
            for index in 0..<document.pageCount {
                if let page = document.page(at: index)?.attributedString {
                    text.append(page)
                    text.append(NSAttributedString(string: "\n\n"))
                }
            }
            if text.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                // Scanned PDFs have no text layer; keep the pages as pictures instead.
                try DocxWriter.write(images: renderPages(of: document, dpi: 150), to: destination)
            } else {
                let data = try text.data(
                    from: NSRange(location: 0, length: text.length),
                    documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML]
                )
                try data.write(to: destination)
            }
            return [destination]

        case .jpg, .png:
            let type = format.imageIOType!
            // Rendered and written a page at a time: 500 pages at 300 dpi held
            // together came to about 16 GB.
            return try (0..<document.pageCount).compactMap { index in
                try autoreleasepool {
                    guard let page = document.page(at: index), let image = renderPage(page, dpi: renderDPI) else { return nil }
                    let suffix = document.pageCount == 1 ? nil : "\(pageLabel) \(index + 1)"
                    let destination = OutputNaming.destination(for: original, suffix: suffix, pathExtension: format.rawValue)
                    try EditableImage.write(image, to: destination, type: type, quality: imageQuality ?? 0.92)
                    return destination
                }
            }

        case .md:
            let pages = try pageTexts(of: document, source: source)
            let markdown = pages.map { page in
                DocumentStructure.markdown(PDFText.paragraphs(fromPageText: page).map { .paragraph([TextRun(text: $0)]) })
            }
            .joined(separator: "\n---\n\n")
            let destination = OutputNaming.destination(for: original, pathExtension: "md")
            try markdown.write(to: destination, atomically: true, encoding: .utf8)
            return [destination]

        case .epub:
            // A chapter per page: text where the page has some, the page image where it doesn't.
            var chapters: [EpubChapter] = []
            var images: [String: Data] = [:]
            for index in 0..<document.pageCount {
                guard let page = document.page(at: index) else { continue }
                let text = page.string?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                let body: String
                if text.isEmpty, let image = renderPage(page, dpi: 150), let jpeg = jpegData(image) {
                    let name = "page\(index + 1).jpg"
                    images[name] = jpeg
                    body = #"<p><img src="images/\#(name)" alt="Page \#(index + 1)"/></p>"#
                } else {
                    body = DocumentStructure.xhtml(PDFText.paragraphs(fromPageText: text).map { .paragraph([TextRun(text: $0)]) })
                }
                chapters.append(EpubChapter(title: "Page \(index + 1)", bodyXHTML: body))
            }
            let destination = OutputNaming.destination(for: original, pathExtension: "epub")
            try EpubWriter.write(title: original.deletingPathExtension().lastPathComponent, chapters: chapters, images: images, to: destination)
            return [destination]

        case .pptx:
            let first = document.page(at: 0).map(displaySize) ?? CGSize(width: 720, height: 540)
            let destination = OutputNaming.destination(for: original, pathExtension: "pptx")
            try PptxWriter.write(slides: renderPages(of: document, dpi: 160), slideSize: first, to: destination)
            return [destination]

        case .xlsx:
            let pages = try pageTexts(of: document, source: source)
            let sheets = pages.enumerated().map { index, text in
                (name: "Page \(index + 1)", table: PDFText.rows(fromPageText: text))
            }
            let destination = OutputNaming.destination(for: original, pathExtension: "xlsx")
            try XlsxWriter.write(sheets: sheets, to: destination)
            return [destination]

        default:
            throw ConversionError.unsupported("PDF to \(format.title)")
        }
    }

    /// Each page's text, refusing PDFs that have none (scans).
    private static func pageTexts(of document: PDFDocument, source: URL) throws -> [String] {
        let pages = (0..<document.pageCount).map { document.page(at: $0)?.string ?? "" }
        guard pages.contains(where: { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
            throw ConversionError.failed("“\(source.lastPathComponent)” has no text to extract (it looks like a scan).")
        }
        return pages
    }

    private static func displaySize(of page: PDFPage) -> CGSize {
        let bounds = page.bounds(for: .mediaBox)
        return page.rotation % 180 == 0 ? bounds.size : CGSize(width: bounds.height, height: bounds.width)
    }

    private static func jpegData(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, "public.jpeg" as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: 0.85] as CFDictionary)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    private static func renderPage(_ page: PDFPage, dpi: Double) -> CGImage? {
        let size = displaySize(of: page)
        let pixels = CGSize(width: size.width * dpi / 72, height: size.height * dpi / 72)
        var rect = CGRect(origin: .zero, size: pixels)
        return page.thumbnail(of: pixels, for: .mediaBox).cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }

    private static func renderPages(of document: PDFDocument, dpi: Double) -> [CGImage] {
        (0..<document.pageCount).compactMap { index in
            document.page(at: index).flatMap { renderPage($0, dpi: dpi) }
        }
    }
}

/// Rich and plain text documents (TXT, RTF, DOCX, HTML, Markdown, ODT).
///
/// Plain text is read and laid out off the main thread (big files would
/// otherwise freeze the app). Only HTML import and printing rich documents to
/// PDF need AppKit on the main thread.
enum DocumentConversion {
    /// Pandoc or LibreOffice do the conversion when they're installed and do
    /// it better (see `DocumentEngine`); if they fail, the built-in way runs.
    static func convert(_ source: URL, to format: OutputFormat, tools: ExternalTools = ExternalTools()) async throws -> URL {
        do {
            switch DocumentEngine.choose(for: source, to: format, tools: tools) {
            case .pandoc:
                return try await PandocConversion.convert(source, to: format, tools: tools)
            case .libreOffice:
                if let converted = try await OfficeConversion.convert(source, to: format, tools: tools).first { return converted }
            case .builtIn:
                break
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            // Fall through to the built-in conversion.
        }
        return try await convertBuiltIn(source, to: format)
    }

    static func convertBuiltIn(_ source: URL, to format: OutputFormat) async throws -> URL {
        guard let pathExtension = format.fileExtension else {
            throw ConversionError.unsupported("document to \(format.title)")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)

        if MarkdownConversion.isMarkdown(source), format != .md {
            try await MarkdownConversion.convert(source, to: format, destination: destination)
            return destination
        }

        if isPlainText(source) {
            let string = try readPlainText(source)
            switch format {
            case .pdf:
                try PlainTextPDF.write(string, to: destination)
            case .md:
                // Plain text is already valid Markdown.
                try string.write(to: destination, atomically: true, encoding: .utf8)
            case .epub:
                try writeEpub(DocumentStructure.blocks(fromPlainText: string), source: source, to: destination)
            default:
                try write(NSAttributedString(string: string, attributes: [.font: bodyFont]), as: format, source: source, to: destination)
            }
            return destination
        }

        let isHTML = ["html", "htm"].contains(source.pathExtension.lowercased())
        if format == .pdf || isHTML {
            try await MainActor.run {
                let text = try readRich(source)
                if format == .pdf {
                    try printPDF(text, to: destination)
                } else {
                    try write(text, as: format, source: source, to: destination)
                }
            }
        } else {
            try write(readRich(source), as: format, source: source, to: destination)
        }
        return destination
    }

    private static func writeEpub(_ blocks: [TextBlock], source: URL, to destination: URL) throws {
        let title = source.deletingPathExtension().lastPathComponent
        try EpubWriter.write(title: title, chapters: [EpubChapter(title: title, bodyXHTML: DocumentStructure.xhtml(blocks))], to: destination)
    }

    static func isPlainText(_ url: URL) -> Bool {
        ["md", "markdown", "txt", "text"].contains(url.pathExtension.lowercased())
    }

    /// Writes everything except PDF.
    private static func write(_ text: NSAttributedString, as format: OutputFormat, source: URL, to destination: URL) throws {
        let range = NSRange(location: 0, length: text.length)
        switch format {
        case .md:
            try DocumentStructure.markdown(DocumentStructure.blocks(from: text)).write(to: destination, atomically: true, encoding: .utf8)
        case .epub:
            try writeEpub(DocumentStructure.blocks(from: text), source: source, to: destination)
        case .txt:
            try text.string.write(to: destination, atomically: true, encoding: .utf8)
        case .docx:
            try text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML]).write(to: destination)
        case .rtf:
            try text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf]).write(to: destination)
        case .html:
            try text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.html]).write(to: destination)
        default:
            throw ConversionError.unsupported("document to \(format.title)")
        }
    }

    private static func readPlainText(_ url: URL) throws -> String {
        guard let data = try? Data(contentsOf: url), let string = TextDecoding.decode(data) else {
            throw ConversionError.unreadable(url)
        }
        return string
    }

    private static func readRich(_ url: URL) throws -> NSAttributedString {
        do {
            return try NSAttributedString(url: url, options: [:], documentAttributes: nil)
        } catch {
            throw ConversionError.unreadable(url)
        }
    }

    /// The system UI font can't be embedded in PDFs (it falls back to Times), so
    /// plain text uses Helvetica Neue.
    private static var bodyFont: NSFont {
        NSFont(name: "HelveticaNeue", size: 12) ?? .userFont(ofSize: 12) ?? .systemFont(ofSize: 12)
    }

    /// The width text is laid out in on a US Letter page with 0.75 in margins.
    static let printableWidth: CGFloat = 612 - 54 * 2

    /// Lays rich text out on US Letter pages and prints it to a PDF file. AppKit
    /// printing keeps tables and images that Core Text would drop.
    @MainActor
    static func printPDF(_ text: NSAttributedString, to url: URL) throws {
        let printInfo = NSPrintInfo()
        printInfo.paperSize = NSSize(width: 612, height: 792)
        printInfo.topMargin = 54
        printInfo.bottomMargin = 54
        printInfo.leftMargin = 54
        printInfo.rightMargin = 54
        printInfo.horizontalPagination = .fit
        printInfo.verticalPagination = .automatic
        printInfo.isVerticallyCentered = false
        printInfo.jobDisposition = .save
        printInfo.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = url

        let width = printInfo.paperSize.width - printInfo.leftMargin - printInfo.rightMargin
        // TextKit 1: TextKit 2 lays out neither tables nor boxed text blocks.
        let textView = NSTextView(usingTextLayoutManager: false)
        textView.frame = NSRect(x: 0, y: 0, width: width, height: 10)
        textView.isVerticallyResizable = true
        textView.textContainer?.widthTracksTextView = true
        textView.textStorage?.setAttributedString(text)
        if let layoutManager = textView.layoutManager, let container = textView.textContainer {
            layoutManager.ensureLayout(for: container)
            textView.frame.size.height = max(layoutManager.usedRect(for: container).height, 10)
        }

        let operation = NSPrintOperation(view: textView, printInfo: printInfo)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        guard operation.run() else { throw EditableImageError.unwritable(url) }
    }
}

/// Reads text files whatever their encoding.
enum TextDecoding {
    static func decode(_ data: Data) -> String? {
        if data.isEmpty { return "" }
        // A byte-order mark settles it.
        if data.starts(with: [0xEF, 0xBB, 0xBF]) {
            return String(data: data.dropFirst(3), encoding: .utf8)
        }
        if data.starts(with: [0xFF, 0xFE]) || data.starts(with: [0xFE, 0xFF]) {
            return String(data: data, encoding: .utf16)
        }
        if let string = String(data: data, encoding: .utf8) {
            return string
        }
        // Let Foundation guess among likely encodings without lossy conversion.
        var converted: NSString?
        var lossy = ObjCBool(false)
        let detected = NSString.stringEncoding(
            for: data,
            encodingOptions: [
                .suggestedEncodingsKey: [String.Encoding.utf16LittleEndian.rawValue, String.Encoding.utf16BigEndian.rawValue, String.Encoding.windowsCP1252.rawValue],
                .allowLossyKey: false,
            ],
            convertedString: &converted,
            usedLossyConversion: &lossy
        )
        if detected != 0, let converted {
            return converted as String
        }
        // Windows-1252 maps nearly every byte, so it's a safe last resort.
        return String(data: data, encoding: .windowsCP1252) ?? String(data: data, encoding: .isoLatin1)
    }
}

/// Paginates plain text into a US Letter PDF with Core Text, which (unlike
/// AppKit printing) is safe to run off the main thread.
enum PlainTextPDF {
    static func write(_ string: String, to url: URL) throws {
        var page = CGRect(x: 0, y: 0, width: 612, height: 792)
        guard let context = CGContext(url as CFURL, mediaBox: &page, nil) else {
            throw EditableImageError.unwritable(url)
        }
        let font = CTFontCreateWithName("HelveticaNeue" as CFString, 12, nil)
        let text = NSAttributedString(
            string: string.isEmpty ? " " : string,
            attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 0, alpha: 1),
            ]
        )
        let framesetter = CTFramesetterCreateWithAttributedString(text)
        let path = CGPath(rect: page.insetBy(dx: 54, dy: 54), transform: nil)
        var location = 0
        repeat {
            try Task.checkCancellation()
            context.beginPDFPage(nil)
            let frame = CTFramesetterCreateFrame(framesetter, CFRange(location: location, length: 0), path, nil)
            CTFrameDraw(frame, context)
            context.endPDFPage()
            let visible = CTFrameGetVisibleStringRange(frame)
            guard visible.length > 0 else { break }
            location += visible.length
        } while location < text.length
        context.closePDF()
    }
}

/// Turning a PDF page's extracted text into paragraphs and table rows.
enum PDFText {
    /// Joins wrapped lines into paragraphs. A line much shorter than the
    /// page's longest line usually ends a paragraph; blank lines always do.
    /// Pages of short lines (lists, records, verse) keep every line apart.
    static func paragraphs(fromPageText text: String) -> [String] {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
        let longest = lines.map(\.count).max() ?? 0
        guard longest >= 60 else { return lines.filter { !$0.isEmpty } }
        var paragraphs: [String] = []
        var current = ""
        for line in lines {
            if line.isEmpty {
                if !current.isEmpty { paragraphs.append(current) }
                current = ""
                continue
            }
            if current.isEmpty {
                current = line
            } else if current.hasSuffix("-") {
                // Re-join a word hyphenated across lines.
                current = String(current.dropLast()) + line
            } else {
                current += " " + line
            }
            if Double(line.count) < Double(longest) * 0.6 {
                paragraphs.append(current)
                current = ""
            }
        }
        if !current.isEmpty { paragraphs.append(current) }
        return paragraphs
    }

    /// One row per line, cells split at tabs or runs of two or more spaces.
    static func rows(fromPageText text: String) -> Table {
        text.replacingOccurrences(of: "\r\n", with: "\n")
            .components(separatedBy: "\n")
            .filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { line in
                line.trimmingCharacters(in: .whitespaces)
                    .split(separator: /\t| {2,}/)
                    .map { $0.trimmingCharacters(in: .whitespaces) }
            }
    }
}
