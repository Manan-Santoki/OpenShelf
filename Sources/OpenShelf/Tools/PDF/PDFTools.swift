import AppKit
import ImageIO
import PDFKit
import Quartz

enum PDFToolError: LocalizedError {
    case unreadable(URL)
    case locked(URL)
    case unwritable(URL)
    case nothingToSave
    /// Compressing didn't make the files smaller, so nothing was saved.
    case notSmaller(count: Int)

    var errorDescription: String? {
        switch self {
        case .unreadable(let url): "“\(url.lastPathComponent)” couldn’t be opened as a PDF."
        case .locked(let url): "“\(url.lastPathComponent)” is password-protected."
        case .unwritable(let url): "“\(url.lastPathComponent)” couldn’t be saved."
        case .nothingToSave: "There are no pages to save."
        case .notSmaller(let count):
            count == 1
                ? "Already as small as it gets — nothing saved."
                : "These \(count) PDFs are already as small as they get — nothing saved."
        }
    }
}

/// One page in the Pages editor: where it came from and how much to turn it.
struct PDFPageEdit: Identifiable, Hashable, Sendable {
    let id = UUID()
    /// Index in the original document.
    let sourceIndex: Int
    /// Extra clockwise rotation in degrees (a multiple of 90).
    var rotation = 0
}

/// How hard to squeeze a PDF.
enum PDFCompressionLevel: CaseIterable, Hashable, Sendable {
    case light
    case balanced
    case strong

    var title: String {
        switch self {
        case .light: "Light"
        case .balanced: "Balanced"
        case .strong: "Strong"
        }
    }

    var summary: String {
        switch self {
        case .light: "Re-encodes images at high quality. Best for print."
        case .balanced: "Scales images to screen resolution. Good for sharing."
        case .strong: "Smallest file; images get noticeably softer."
        }
    }

    /// JPEG quality, target resolution (dpi) and longest image edge (px).
    fileprivate var imageSettings: (quality: Double, resolution: Int, maxSize: Int) {
        switch self {
        case .light: (0.85, 220, 4000)
        case .balanced: (0.7, 144, 2400)
        case .strong: (0.45, 96, 1600)
        }
    }
}

/// Document-level details shown and edited by the PDF Metadata tool.
struct PDFMetadataFields: Equatable, Sendable {
    var title = ""
    var author = ""
    var subject = ""
    var keywords = ""
    var creator = ""
    var producer = ""

    var isEmpty: Bool {
        [title, author, subject, keywords, creator, producer].allSatisfy { $0.trimmingCharacters(in: .whitespaces).isEmpty }
    }
}

struct PDFInfo: Sendable {
    var fields: PDFMetadataFields
    var pageCount: Int
    var fileSize: Int64
    var created: Date?
    var modified: Date?
    var isEncrypted: Bool
    var isLocked: Bool

    /// Whether the file says who made it or what it's about. The stamps macOS
    /// puts on every PDF it saves (itself as Producer, and the save time) don't
    /// count: older versions add them even to a cleaned copy, so they can't be
    /// removed, and they describe the save rather than the author.
    var hasMetadata: Bool {
        var identifying = fields
        if Self.isSystemProducer(identifying.producer) {
            identifying.producer = ""
        }
        return !identifying.isEmpty
    }

    /// Whether `producer` is the stamp macOS's PDF writer adds, e.g.
    /// "macOS Version 15.1 (Build 24B83) Quartz PDFContext".
    static func isSystemProducer(_ producer: String) -> Bool {
        producer.hasSuffix("Quartz PDFContext")
    }
}

/// PDF operations used by the PDF tools. Everything here is synchronous and
/// meant to be called off the main thread (except where noted).
enum PDFTools {
    // MARK: Combine

    /// Combines PDFs and images, in order, into "Combined.pdf" next to the first file.
    static func combine(_ urls: [URL]) async throws -> URL {
        try await Task.detached(priority: .userInitiated) { try combineNow(urls) }.value
    }

    static func combineNow(_ urls: [URL]) throws -> URL {
        guard let first = urls.first else { throw PDFToolError.nothingToSave }
        let output = PDFDocument()
        for url in urls {
            try Task.checkCancellation()
            if FileKind(url: url) == .pdf {
                let document = try open(url)
                for index in 0..<document.pageCount {
                    if let page = document.page(at: index)?.copy() as? PDFPage {
                        output.insert(page, at: output.pageCount)
                    }
                }
            } else {
                output.insert(try imagePage(url), at: output.pageCount)
            }
        }
        guard output.pageCount > 0 else { throw PDFToolError.nothingToSave }
        let destination = OutputNaming.uniqueURL(in: first.deletingLastPathComponent(), baseName: "Combined", pathExtension: "pdf")
        try write(output, to: destination)
        return destination
    }

    /// A page the size of the image at its stated resolution, drawn losslessly.
    private static func imagePage(_ url: URL) throws -> PDFPage {
        let image = try EditableImage.load(url)
        let dpi = image.properties[kCGImagePropertyDPIWidth] as? Double ?? 72
        let scale = 72 / max(dpi, 1)
        var mediaBox = CGRect(x: 0, y: 0, width: Double(image.cgImage.width) * scale, height: Double(image.cgImage.height) * scale)
        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData),
              let context = CGContext(consumer: consumer, mediaBox: &mediaBox, nil)
        else { throw PDFToolError.unreadable(url) }
        context.beginPDFPage(nil)
        context.draw(image.cgImage, in: mediaBox)
        context.endPDFPage()
        context.closePDF()
        guard let page = PDFDocument(data: data as Data)?.page(at: 0) else { throw PDFToolError.unreadable(url) }
        return page
    }

    // MARK: Pages

    /// Saves the pages in the given order and rotation as "<name> Edited.pdf".
    static func saveEditedPages(of url: URL, pages: [PDFPageEdit]) throws -> URL {
        guard !pages.isEmpty else { throw PDFToolError.nothingToSave }
        let document = try open(url)
        let output = PDFDocument()
        for edit in pages {
            guard let page = document.page(at: edit.sourceIndex)?.copy() as? PDFPage else { continue }
            page.rotation = ((page.rotation + edit.rotation) % 360 + 360) % 360
            output.insert(page, at: output.pageCount)
        }
        let destination = OutputNaming.destination(for: url, suffix: "Edited", pathExtension: "pdf")
        try write(output, to: destination)
        return destination
    }

    // MARK: Split

    /// Page groups (0-based) for one file per page.
    static func everyPage(pageCount: Int) -> [[Int]] {
        (0..<max(pageCount, 0)).map { [$0] }
    }

    /// Page groups (0-based) of `size` pages each; the last may be shorter.
    static func chunks(pageCount: Int, size: Int) -> [[Int]] {
        guard pageCount > 0, size > 0 else { return [] }
        return stride(from: 0, to: pageCount, by: size).map { Array($0..<min($0 + size, pageCount)) }
    }

    /// Parses "1-3, 4, 7-" into 0-based page groups, one per comma-separated part.
    /// Returns a message describing the first problem instead when the text is invalid.
    static func ranges(_ text: String, pageCount: Int) -> Result<[[Int]], RangeError> {
        let parts = text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard !parts.isEmpty else { return .failure(RangeError(message: "Enter page ranges, like 1-3, 4, 5-\(max(pageCount, 5)).")) }
        var groups: [[Int]] = []
        for part in parts {
            let bounds = part.split(separator: "-", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces) }
            let start: Int?
            let end: Int?
            switch bounds.count {
            case 1:
                start = Int(bounds[0])
                end = start
            case 2:
                start = bounds[0].isEmpty ? 1 : Int(bounds[0])
                end = bounds[1].isEmpty ? pageCount : Int(bounds[1])
            default:
                start = nil
                end = nil
            }
            guard let start, let end else { return .failure(RangeError(message: "“\(part)” isn’t a page or range.")) }
            guard start >= 1, end <= pageCount else { return .failure(RangeError(message: "“\(part)” is outside pages 1–\(pageCount).")) }
            guard start <= end else { return .failure(RangeError(message: "“\(part)” runs backwards.")) }
            groups.append(Array((start - 1)...(end - 1)))
        }
        return .success(groups)
    }

    struct RangeError: Error, Equatable {
        let message: String
    }

    /// Writes one PDF per group next to the original.
    static func split(_ url: URL, into groups: [[Int]]) throws -> [URL] {
        let document = try open(url)
        return try groups.map { group in
            try Task.checkCancellation()
            let output = PDFDocument()
            for index in group {
                if let page = document.page(at: index)?.copy() as? PDFPage {
                    output.insert(page, at: output.pageCount)
                }
            }
            guard let first = group.first, let last = group.last else { throw PDFToolError.nothingToSave }
            let suffix = first == last ? "Page \(first + 1)" : "Pages \(first + 1)-\(last + 1)"
            let destination = OutputNaming.destination(for: url, suffix: suffix, pathExtension: "pdf")
            try write(output, to: destination)
            return destination
        }
    }

    // MARK: Compress

    /// The compressed file's bytes, without writing anything.
    ///
    /// Uses a Quartz filter (the mechanism behind Preview's "Reduce File Size")
    /// that re-encodes and downsamples images while keeping text and vectors.
    static func compressedData(_ url: URL, level: PDFCompressionLevel) throws -> Data {
        let document = try open(url)
        let settings = level.imageSettings
        let properties: [String: Any] = [
            "Domains": ["Applications": true, "Printing": true],
            "FilterType": 1,
            "Name": "OpenShelf \(level.title)",
            "FilterData": [
                "ColorSettings": [
                    "ImageSettings": [
                        "Compression Quality": settings.quality,
                        "ImageCompression": "ImageJPEGCompress",
                        "ImageScaleSettings": [
                            "ImageResolution": settings.resolution,
                            "ImageScaleInterpolate": true,
                            "ImageSizeMax": settings.maxSize,
                            "ImageSizeMin": 0,
                        ],
                    ],
                ],
            ],
        ]
        guard let filter = QuartzFilter(properties: properties) else { throw PDFToolError.unwritable(url) }

        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".pdf")
        defer { try? FileManager.default.removeItem(at: temporary) }
        guard document.write(to: temporary, withOptions: [PDFDocumentWriteOption(rawValue: "QuartzFilter"): filter]) else {
            throw PDFToolError.unwritable(url)
        }
        return try Data(contentsOf: temporary)
    }

    /// Saves "<name> Compressed.pdf", or returns nil without writing anything
    /// when the result wouldn't be smaller than the original.
    static func compress(_ url: URL, level: PDFCompressionLevel) throws -> URL? {
        let data = try compressedData(url, level: level)
        guard ImageCompressor.isWorthSaving(data.count, originalSize: fileSize(url)) else { return nil }
        let destination = OutputNaming.destination(for: url, suffix: "Compressed", pathExtension: "pdf")
        try data.writeNew(to: destination)
        return destination
    }

    /// Compresses each PDF; files that wouldn't get smaller are left alone and counted.
    static func compress(_ urls: [URL], level: PDFCompressionLevel) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "compressed", unchangedLabel: "can’t get smaller")
        for url in urls {
            if let destination = try compress(url, level: level) {
                outcome.saved.append(destination)
            } else {
                outcome.unchanged += 1
            }
        }
        return outcome
    }

    // MARK: Metadata

    static func info(for url: URL) -> PDFInfo? {
        guard let document = PDFDocument(url: url) else { return nil }
        let attributes = document.documentAttributes ?? [:]
        func string(_ key: PDFDocumentAttribute) -> String {
            attributes[key] as? String ?? ""
        }
        let keywords: String = if let list = attributes[PDFDocumentAttribute.keywordsAttribute] as? [String] {
            list.joined(separator: ", ")
        } else {
            string(.keywordsAttribute)
        }
        return PDFInfo(
            fields: PDFMetadataFields(
                title: string(.titleAttribute),
                author: string(.authorAttribute),
                subject: string(.subjectAttribute),
                keywords: keywords,
                creator: string(.creatorAttribute),
                producer: string(.producerAttribute)
            ),
            pageCount: document.pageCount,
            fileSize: Int64((try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0),
            created: attributes[PDFDocumentAttribute.creationDateAttribute] as? Date,
            modified: attributes[PDFDocumentAttribute.modificationDateAttribute] as? Date,
            isEncrypted: document.isEncrypted,
            isLocked: document.isLocked
        )
    }

    /// Writes a copy with `fields` as the document info ("<name> Metadata.pdf"),
    /// or with all info removed when `fields` is nil ("<name> Clean.pdf", as the
    /// image Metadata tool names its copies).
    static func saveMetadata(_ url: URL, fields: PDFMetadataFields?) throws -> URL {
        let document = try open(url)
        if let fields, !fields.isEmpty {
            var attributes = document.documentAttributes ?? [:]
            func set(_ key: PDFDocumentAttribute, _ value: String) {
                let trimmed = value.trimmingCharacters(in: .whitespaces)
                if trimmed.isEmpty {
                    attributes.removeValue(forKey: key)
                } else {
                    attributes[key] = trimmed
                }
            }
            set(.titleAttribute, fields.title)
            set(.authorAttribute, fields.author)
            set(.subjectAttribute, fields.subject)
            set(.creatorAttribute, fields.creator)
            set(.producerAttribute, fields.producer)
            let keywords = fields.keywords.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            if keywords.isEmpty {
                attributes.removeValue(forKey: PDFDocumentAttribute.keywordsAttribute)
            } else {
                attributes[PDFDocumentAttribute.keywordsAttribute] = keywords
            }
            attributes[PDFDocumentAttribute.modificationDateAttribute] = Date()
            document.documentAttributes = attributes
            let destination = OutputNaming.destination(for: url, suffix: MetadataSuffix.edited, pathExtension: "pdf")
            try write(document, to: destination)
            return destination
        }

        document.documentAttributes = [:]
        let destination = OutputNaming.destination(for: url, suffix: MetadataSuffix.removed, pathExtension: "pdf")
        try write(document, to: destination)
        return destination
    }

    /// Writes clean copies of the PDFs that have document info; the rest are
    /// left alone and counted, since a copy would be identical.
    static func removeMetadata(_ urls: [URL]) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "cleaned", unchangedLabel: "already clean")
        for url in urls {
            // An unreadable file goes through, so its error is reported.
            guard info(for: url)?.hasMetadata ?? true else {
                outcome.unchanged += 1
                continue
            }
            outcome.saved.append(try saveMetadata(url, fields: nil))
        }
        return outcome
    }

    // MARK: Helpers

    /// Writes `document`, explaining why if it can't be saved.
    private static func write(_ document: PDFDocument, to destination: URL) throws {
        try SaveError.checkWritable(destination)
        guard document.write(to: destination) else {
            try? FileManager.default.removeItem(at: destination)
            throw SaveError.diagnose(destination)
        }
    }

    static func open(_ url: URL) throws -> PDFDocument {
        guard let document = PDFDocument(url: url) else { throw PDFToolError.unreadable(url) }
        guard !document.isLocked else { throw PDFToolError.locked(url) }
        return document
    }

    static func fileSize(_ url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
    }

    static func formatted(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
