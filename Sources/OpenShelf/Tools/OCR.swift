// Adapted from David NeSmith’s d0b71b4 and 207bc44; see docs/FORK_REVIEW.md.
import CoreGraphics
import CoreML
import CoreText
import Dispatch
import Foundation
import ImageIO
import PDFKit
import Vision

/// Reads the text in images and scanned PDF pages with Vision, entirely on
/// this Mac, and saves it as text or as a PDF with an invisible text layer
/// that can be searched and selected.
enum OCR {
    enum Output: Hashable, Sendable {
        case searchablePDF
        case text
    }

    /// A line of recognised text and where it sits, in Vision's normalised
    /// coordinates (0–1, origin at the bottom left).
    struct Line: Equatable, Sendable {
        var text: String
        var box: CGRect
    }

    /// Pages are rendered at this resolution for recognition.
    static let renderDPI: Double = 300

    // MARK: Recognition

    static func recognize(
        _ image: CGImage,
        languages: [String] = Locale.preferredLanguages,
        checkCancellation: () throws -> Void = { try Task.checkCancellation() },
        perform: (VNImageRequestHandler, VNRecognizeTextRequest) throws -> Void = { try $0.perform([$1]) }
    ) throws -> [Line] {
        try checkCancellation()
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = true
        let supported = (try? request.supportedRecognitionLanguages()) ?? []
        let chosen = recognitionLanguages(preferred: languages, supported: supported)
        if !chosen.isEmpty { request.recognitionLanguages = chosen }
        do {
            try perform(VNImageRequestHandler(cgImage: image), request)
        } catch {
            try checkCancellation()
            // Accelerator execution can fail under load. Retry once on the
            // supported CPU devices, keeping the same recognition settings.
            guard let stages = try? request.supportedComputeStageDevices else { throw error }
            var hasCPU = false
            for (stage, devices) in stages {
                if let cpu = devices.first(where: { if case .cpu = $0 { return true }; return false }) {
                    request.setComputeDevice(cpu, for: stage)
                    hasCPU = true
                }
            }
            guard hasCPU else { throw error }
            try perform(VNImageRequestHandler(cgImage: image), request)
        }
        let lines = (request.results ?? []).compactMap { observation in
            observation.topCandidates(1).first.map { Line(text: $0.string, box: observation.boundingBox) }
        }
        return readingOrder(lines)
    }

    /// The system's preferred languages that Vision reads, matched exactly
    /// ("en-US") or by language ("de-AT" → "de-DE").
    static func recognitionLanguages(preferred: [String], supported: [String]) -> [String] {
        var chosen: [String] = []
        for language in preferred {
            let code = language.split(separator: "-").first.map(String.init) ?? language
            let match = supported.first { $0 == language } ?? supported.first { $0.split(separator: "-").first.map(String.init) == code }
            if let match, !chosen.contains(match) { chosen.append(match) }
        }
        return chosen
    }

    /// Top to bottom, then left to right within a row.
    static func readingOrder(_ lines: [Line]) -> [Line] {
        lines.sorted { a, b in
            abs(a.box.midY - b.box.midY) > min(a.box.height, b.box.height) / 2 ? a.box.midY > b.box.midY : a.box.minX < b.box.minX
        }
    }

    // MARK: Saving

    /// Recognises `url` (an image or PDF) and saves "<name> OCR.pdf" or "<name> OCR.txt".
    /// PDF pages that already have text are kept as they are.
    static func run(_ url: URL, output: Output, progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws -> URL {
        try Task.checkCancellation()
        let job = OCRJob()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                workerQueue.async {
                    do {
                        let result = try autoreleasepool {
                            try process(url, output: output, progress: progress, job: job)
                        }
                        continuation.resume(returning: result)
                    } catch {
                        continuation.resume(throwing: error)
                    }
                }
            }
        } onCancel: {
            job.cancel()
        }
    }

    // Vision performs blocking work and may schedule its own Swift tasks.
    // Keep it off Swift's cooperative executor, and limit concurrent model work.
    private static let workerQueue = DispatchQueue(label: "OpenShelf.OCR", qos: .userInitiated)

    private static func process(
        _ url: URL,
        output: Output,
        progress: @escaping @Sendable (Double) -> Void,
        job: OCRJob
    ) throws -> URL {
        try job.checkCancellation()
        let pages = try OCRPage.load(url)
        var recognised: [(page: OCRPage, lines: [Line])] = []
        for (index, page) in pages.enumerated() {
            try job.checkCancellation()
            // Each rendered page is let go before the next, not at the end.
            let lines = try autoreleasepool {
                try page.existingText == nil ? page.image().map { try recognize($0, checkCancellation: job.checkCancellation) } ?? [] : []
            }
            try job.checkCancellation()
            recognised.append((page, lines))
            progress(Double(index + 1) / Double(pages.count))
        }
        try job.checkCancellation()
        switch output {
        case .text:
            let destination = OutputNaming.destination(for: url, suffix: "OCR", pathExtension: "txt")
            try SaveError.checkWritable(destination)
            let text = recognised.map { $0.page.existingText ?? $0.lines.map(\.text).joined(separator: "\n") }
                .joined(separator: "\n\n")
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw ConversionError.failed("No text was found in “\(url.lastPathComponent)”.")
            }
            try job.checkCancellation()
            try Data(text.utf8).writeNew(to: destination)
            return destination
        case .searchablePDF:
            let destination = OutputNaming.destination(for: url, suffix: "OCR", pathExtension: "pdf")
            try SaveError.checkWritable(destination)
            let partial = destination.deletingLastPathComponent().appendingPathComponent(".openshelf-ocr-\(UUID().uuidString).pdf")
            defer { try? FileManager.default.removeItem(at: partial) }
            try writePDF(recognised, to: partial, checkCancellation: job.checkCancellation)
            try job.checkCancellation()
            try FileManager.default.moveItem(at: partial, to: destination)
            return destination
        }
    }

    private static func writePDF(_ pages: [(page: OCRPage, lines: [Line])], to url: URL, checkCancellation: () throws -> Void) throws {
        guard let context = CGContext(url as CFURL, mediaBox: nil, nil) else { throw EditableImageError.unwritable(url) }
        defer { context.closePDF() }
        for (page, lines) in pages {
            try checkCancellation()
            autoreleasepool {
                var box = CGRect(origin: .zero, size: page.size)
                context.beginPage(mediaBox: &box)
                page.draw(in: context)
                drawInvisibleText(lines, in: box, context: context)
                context.endPage()
            }
        }
    }

    /// Each line as invisible text stretched over where it appears, so
    /// selecting and searching find it in the right place.
    static func drawInvisibleText(_ lines: [Line], in page: CGRect, context: CGContext) {
        context.saveGState()
        context.setTextDrawingMode(.invisible)
        for line in lines {
            let rect = CGRect(
                x: line.box.minX * page.width, y: line.box.minY * page.height,
                width: line.box.width * page.width, height: line.box.height * page.height
            )
            guard rect.width > 0, rect.height > 0 else { continue }
            let font = CTFontCreateWithName("Helvetica" as CFString, rect.height * 0.8, nil)
            let attributed = NSAttributedString(string: line.text, attributes: [.init(kCTFontAttributeName as String): font])
            let ctLine = CTLineCreateWithAttributedString(attributed)
            let width = CTLineGetTypographicBounds(ctLine, nil, nil, nil)
            guard width > 0 else { continue }
            context.saveGState()
            context.textMatrix = .identity
            context.translateBy(x: rect.minX, y: rect.minY + rect.height * 0.2)
            context.scaleBy(x: rect.width / width, y: 1)
            context.textPosition = .zero
            CTLineDraw(ctLine, context)
            context.restoreGState()
        }
        context.restoreGState()
    }
}

/// One page to read: an image file, or a page of a PDF.
struct OCRPage: @unchecked Sendable {
    /// In points.
    let size: CGSize
    /// The page's own text, for PDF pages that have some.
    let existingText: String?
    private let picture: CGImage?
    private let pdfPage: PDFPage?
    /// A PDFPage draws nothing once its document is gone, so each page keeps it.
    private let document: PDFDocument?

    static func load(_ url: URL) throws -> [OCRPage] {
        if FileKind(url: url) == .pdf {
            guard let document = PDFDocument(url: url) else { throw ConversionError.unreadable(url) }
            guard !document.isLocked else {
                throw ConversionError.failed("“\(url.lastPathComponent)” is password-protected. Remove the password in Preview first.")
            }
            return (0..<document.pageCount).compactMap { document.page(at: $0) }.map { page in
                let bounds = page.bounds(for: .mediaBox)
                let size = page.rotation % 180 == 0 ? bounds.size : CGSize(width: bounds.height, height: bounds.width)
                let text = page.string?.trimmingCharacters(in: .whitespacesAndNewlines)
                return OCRPage(size: size, existingText: text?.isEmpty == false ? text : nil, picture: nil, pdfPage: page, document: document)
            }
        }
        let image = try EditableImage.load(url)
        let dpi = image.properties[kCGImagePropertyDPIWidth] as? Double ?? 72
        let scale = 72 / max(dpi, 1)
        let size = CGSize(width: Double(image.cgImage.width) * scale, height: Double(image.cgImage.height) * scale)
        return [OCRPage(size: size, existingText: nil, picture: image.cgImage, pdfPage: nil, document: nil)]
    }

    /// The picture to recognise: the image itself, or the page rendered at `OCR.renderDPI`.
    func image() -> CGImage? {
        if let picture { return picture }
        guard let pdfPage else { return nil }
        let pixels = CGSize(width: size.width * OCR.renderDPI / 72, height: size.height * OCR.renderDPI / 72)
        var rect = CGRect(origin: .zero, size: pixels)
        return pdfPage.thumbnail(of: pixels, for: .mediaBox).cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }

    func draw(in context: CGContext) {
        if let picture {
            context.draw(picture, in: CGRect(origin: .zero, size: size))
        } else if let page = pdfPage?.pageRef {
            context.saveGState()
            context.concatenate(page.getDrawingTransform(.mediaBox, rect: CGRect(origin: .zero, size: size), rotate: 0, preserveAspectRatio: true))
            context.drawPDFPage(page)
            context.restoreGState()
        }
    }
}

/// Cooperative cancellation for synchronous work running outside a Swift task.
private final class OCRJob: @unchecked Sendable {
    private let lock = NSLock()
    private var isCancelled = false

    func cancel() { lock.withLock { isCancelled = true } }

    func checkCancellation() throws {
        if lock.withLock({ isCancelled }) { throw CancellationError() }
    }
}
