import AppKit
import PDFKit

enum PDFConversion {
    /// Page images are rendered at print resolution.
    private static let renderDPI = 300.0

    static func convert(_ source: URL, to format: OutputFormat) throws -> [URL] {
        guard let document = PDFDocument(url: source) else { throw ConversionError.unreadable(source) }

        switch format {
        case .txt:
            let destination = OutputNaming.destination(for: source, pathExtension: "txt")
            try (document.string ?? "").write(to: destination, atomically: true, encoding: .utf8)
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
/// Runs on the main actor because AppKit's HTML import and printing require it.
@MainActor
enum DocumentConversion {
    static func convert(_ source: URL, to format: OutputFormat) throws -> URL {
        guard let pathExtension = format.fileExtension else {
            throw ConversionError.unsupported("document to \(format.title)")
        }
        let text = try read(source)
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
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
        case .pdf:
            try printPDF(text, to: destination)
        default:
            throw ConversionError.unsupported("document to \(format.title)")
        }
        return destination
    }

    private static func read(_ url: URL) throws -> NSAttributedString {
        let ext = url.pathExtension.lowercased()
        if ["md", "markdown", "txt", "text"].contains(ext) {
            let string = try String(contentsOf: url, encoding: .utf8)
            return NSAttributedString(string: string, attributes: [.font: bodyFont])
        }
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

    /// Lays the text out on US Letter pages and prints it to a PDF file.
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
