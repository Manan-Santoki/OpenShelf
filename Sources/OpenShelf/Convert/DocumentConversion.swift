import AppKit
import CoreText
import PDFKit

enum PDFConversion {
    /// Page images are rendered at print resolution.
    private static let renderDPI = 300.0

    static func convert(_ source: URL, to format: OutputFormat) throws -> [URL] {
        guard let document = PDFDocument(url: source) else { throw ConversionError.unreadable(source) }
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
            let destination = OutputNaming.destination(for: source, pathExtension: "txt")
            try text.write(to: destination, atomically: true, encoding: .utf8)
            return [destination]

        case .docx:
            let destination = OutputNaming.destination(for: source, pathExtension: "docx")
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
            let pages = renderPages(of: document, dpi: renderDPI)
            return try pages.enumerated().map { index, image in
                let suffix = pages.count == 1 ? nil : "Page \(index + 1)"
                let destination = OutputNaming.destination(for: source, suffix: suffix, pathExtension: format.rawValue)
                try EditableImage.write(image, to: destination, type: type, quality: 0.92)
                return destination
            }

        default:
            throw ConversionError.unsupported("PDF to \(format.title)")
        }
    }

    private static func renderPages(of document: PDFDocument, dpi: Double) -> [CGImage] {
        (0..<document.pageCount).compactMap { index in
            guard let page = document.page(at: index) else { return nil }
            let bounds = page.bounds(for: .mediaBox)
            let rotated = page.rotation % 180 != 0
            let size = CGSize(
                width: (rotated ? bounds.height : bounds.width) * dpi / 72,
                height: (rotated ? bounds.width : bounds.height) * dpi / 72
            )
            var rect = CGRect(origin: .zero, size: size)
            return page.thumbnail(of: size, for: .mediaBox).cgImage(forProposedRect: &rect, context: nil, hints: nil)
        }
    }
}

/// Rich and plain text documents (TXT, RTF, DOCX, HTML, Markdown, ODT).
///
/// Plain text is read and laid out off the main thread (big files would
/// otherwise freeze the app). Only HTML import and printing rich documents to
/// PDF need AppKit on the main thread.
enum DocumentConversion {
    static func convert(_ source: URL, to format: OutputFormat) async throws -> URL {
        guard let pathExtension = format.fileExtension else {
            throw ConversionError.unsupported("document to \(format.title)")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)

        if isPlainText(source) {
            let string = try readPlainText(source)
            if format == .pdf {
                try PlainTextPDF.write(string, to: destination)
            } else {
                try write(NSAttributedString(string: string, attributes: [.font: bodyFont]), as: format, to: destination)
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
                    try write(text, as: format, to: destination)
                }
            }
        } else {
            try write(readRich(source), as: format, to: destination)
        }
        return destination
    }

    static func isPlainText(_ url: URL) -> Bool {
        ["md", "markdown", "txt", "text"].contains(url.pathExtension.lowercased())
    }

    /// Writes everything except PDF.
    private static func write(_ text: NSAttributedString, as format: OutputFormat, to destination: URL) throws {
        let range = NSRange(location: 0, length: text.length)
        switch format {
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

    /// Lays rich text out on US Letter pages and prints it to a PDF file. AppKit
    /// printing keeps tables and images that Core Text would drop.
    @MainActor
    private static func printPDF(_ text: NSAttributedString, to url: URL) throws {
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
        let textView = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 10))
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
