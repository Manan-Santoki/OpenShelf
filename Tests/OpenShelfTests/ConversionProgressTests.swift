import AppKit
import Foundation
import PDFKit
import Testing
@testable import OpenShelf

/// The Convert wheel's progress toast used to sit at 0% for the whole of a
/// PDF conversion, then jump to 100%: `PDFConversion` never reported any
/// progress. These cover real per-page progress out of `PDFConversion`,
/// per-file progress out of Combine, and rich documents still printing to
/// PDF now that they're read off the main thread.
struct PDFConversionProgressTests {
    private func makeDirectory(_ name: String) throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(name)-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// A PDF of drawn (but textless) pages: enough to render or re-page.
    private func writeDrawnPDF(pageCount: Int, in directory: URL, name: String = "drawn") throws -> URL {
        let url = directory.appendingPathComponent("\(name).pdf")
        var box = CGRect(x: 0, y: 0, width: 200, height: 260)
        let context = try #require(CGContext(url as CFURL, mediaBox: &box, nil))
        for _ in 0..<pageCount {
            context.beginPDFPage(nil)
            context.setFillColor(.white)
            context.fill(box)
            context.endPDFPage()
        }
        context.closePDF()
        return url
    }

    /// A PDF with real, extractable text spread over several pages.
    private func writeTextPDF(in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("text.pdf")
        let paragraph = String(repeating: "Hello world. ", count: 40) + "\n\n"
        try PlainTextPDF.write(String(repeating: paragraph, count: 30), to: url)
        return url
    }

    /// One update per page, never running backwards, ending exactly at 1.
    private func isHonestProgress(_ reported: [Double], pageCount: Int) -> Bool {
        reported.count == pageCount && reported == reported.sorted() && reported.last == 1
    }

    @Test func pageImagesReportOneUpdatePerPage() async throws {
        let directory = try makeDirectory("progress-jpg")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try writeDrawnPDF(pageCount: 5, in: directory)

        let progress = PageProgress()
        let output = try await Converter.convert(source, to: .jpg, tools: ExternalTools()) { progress.record($0) }
        #expect(output.count == 5)
        #expect(isHonestProgress(progress.values, pageCount: 5))
    }

    @Test func pageRenderingFormatsReportProgressThatEndsAtOne() async throws {
        let directory = try makeDirectory("progress-render")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try writeDrawnPDF(pageCount: 3, in: directory)

        for format: OutputFormat in [.epub, .pptx] {
            let progress = PageProgress()
            _ = try await Converter.convert(source, to: format, tools: ExternalTools()) { progress.record($0) }
            #expect(isHonestProgress(progress.values, pageCount: 3), "\(format)")
        }
    }

    @Test func textExtractionFormatsReportProgressThatEndsAtOne() async throws {
        let directory = try makeDirectory("progress-text")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = try writeTextPDF(in: directory)
        let pageCount = try #require(PDFDocument(url: source)?.pageCount)
        #expect(pageCount > 1, "fixture should span several pages")

        for format: OutputFormat in [.docx, .md, .xlsx] {
            let progress = PageProgress()
            _ = try await Converter.convert(source, to: format, tools: ExternalTools()) { progress.record($0) }
            #expect(isHonestProgress(progress.values, pageCount: pageCount), "\(format)")
        }
    }

    @Test func combiningReportsEachFileAndLeavesRoomForTheWrite() async throws {
        let directory = try makeDirectory("progress-combine")
        defer { try? FileManager.default.removeItem(at: directory) }
        let sources = try (1...3).map { try writeDrawnPDF(pageCount: 2, in: directory, name: "part\($0)") }

        let progress = PageProgress()
        let combined = try await PDFTools.combine(sources) { progress.record($0) }
        #expect(PDFDocument(url: combined)?.pageCount == 6)
        // A quarter per file; the last quarter is the write, which the runner
        // fills in when the job returns.
        #expect(progress.values == [0.25, 0.5, 0.75])
    }

    @Test func richTextStillPrintsToPDF() async throws {
        let directory = try makeDirectory("progress-rtf")
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("notes.rtf")
        let text = NSAttributedString(string: String(repeating: "Rich text, read off the main thread. ", count: 400))
        try text.data(from: NSRange(location: 0, length: text.length), documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf])
            .write(to: source)

        let output = try await Converter.convert(source, to: .pdf, tools: ExternalTools()) { _ in }
        let pdf = try #require(output.first.flatMap { PDFDocument(url: $0) })
        #expect(pdf.pageCount >= 1)
        #expect(pdf.string?.contains("Rich text") == true)
    }

    /// Markdown's rich text, pictures loaded from disk included, is now built
    /// off the main thread; the picture must still make it into the PDF.
    @Test func markdownPicturesStillPrintToPDF() async throws {
        let directory = try makeDirectory("progress-pictures")
        defer { try? FileManager.default.removeItem(at: directory) }
        try writeRedSquare(to: directory.appendingPathComponent("red.png"))
        let source = directory.appendingPathComponent("notes.md")
        try "# Pictures\n\n![Red](red.png)\n\nAfter the picture.\n".write(to: source, atomically: true, encoding: .utf8)

        let output = try await Converter.convert(source, to: .pdf, tools: ExternalTools()) { _ in }
        let pdf = try #require(output.first.flatMap { PDFDocument(url: $0) })
        let page = try #require(pdf.page(at: 0))
        #expect(pdf.string?.contains("After the picture") == true)
        #expect(redPixels(on: page) > 100)
    }

    private func writeRedSquare(to url: URL) throws {
        let context = try #require(CGContext(data: nil, width: 120, height: 120, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(red: 1, green: 0, blue: 0, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 120, height: 120))
        let image = try #require(context.makeImage())
        let data = try #require(NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]))
        try data.write(to: url)
    }

    private func redPixels(on page: PDFPage) -> Int {
        let bounds = page.bounds(for: .mediaBox)
        guard let cgImage = page.thumbnail(of: bounds.size, for: .mediaBox).cgImage(forProposedRect: nil, context: nil, hints: nil) else { return 0 }
        let bitmap = NSBitmapImageRep(cgImage: cgImage)
        var count = 0
        for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else { continue }
                // Printing shifts pure red towards orange, so allow some slack.
                if color.redComponent > 0.7, color.greenComponent < 0.45, color.blueComponent < 0.4 { count += 1 }
            }
        }
        return count
    }
}

/// `StepProgress` backs the Convert wheel's progress toast: a real fraction
/// while a conversion reports one, a busy bar once it's gone quiet.
@MainActor
struct StepProgressTests {
    /// Long enough for the switch to the busy bar to have happened.
    private let pastTheDelay = StepProgress.indeterminateDelay + .milliseconds(150)

    @Test func reportingBeforeTheDelayNeverShowsTheBusyBar() async throws {
        let toast = ToastLog()
        let step = StepProgress(index: 0, total: 2, update: toast.record)
        step.report(0.5)
        #expect(toast.progress == .determinate(0.25))

        try await Task.sleep(for: pastTheDelay)
        // Real progress already arrived, so the late timer has nothing to do.
        #expect(toast.progress == .determinate(0.25))
        withExtendedLifetime(step) {}
    }

    @Test func laterFilesReportTheirShareOfTheBatch() {
        let toast = ToastLog()
        let step = StepProgress(index: 2, total: 4, update: toast.record)
        step.report(0.5)
        #expect(toast.progress == .determinate(0.625))
        // Out-of-range fractions stay within this file's share.
        step.report(3)
        #expect(toast.progress == .determinate(0.75))
        step.cancel()
    }

    @Test func goingQuietForTooLongShowsABusyBar() async throws {
        let toast = ToastLog()
        let step = StepProgress(index: 0, total: 1, update: toast.record)
        #expect(toast.progress == nil)

        // Waits for the switch itself rather than a fixed time: with the whole
        // suite running, other tests can hold the main actor past the delay.
        await step.pending?.value
        #expect(toast.progress == .indeterminate)
        // A late report still brings the real fraction back.
        step.report(0.4)
        #expect(toast.progress == .determinate(0.4))
        step.cancel()
    }

    @Test func finishingCancelsAnyPendingSwitchToTheBusyBar() async throws {
        let toast = ToastLog()
        let step = StepProgress(index: 0, total: 1, update: toast.record)
        step.finish(subtitle: "1 of 1 saved")
        #expect(toast.progress == .determinate(1))
        #expect(toast.subtitle == "1 of 1 saved")

        try await Task.sleep(for: pastTheDelay)
        // Done already; it must not flip to busy afterwards.
        #expect(toast.progress == .determinate(1))
        withExtendedLifetime(step) {}
    }

    @Test func cancellingStopsTheLaterSwitchToTheBusyBarToo() async throws {
        let toast = ToastLog()
        let step = StepProgress(index: 0, total: 1, update: toast.record)
        step.cancel()

        try await Task.sleep(for: pastTheDelay)
        #expect(toast.progress == nil)
        withExtendedLifetime(step) {}
    }
}

/// What `StepProgress` last told the toast.
@MainActor
private final class ToastLog {
    private(set) var progress: ToastProgress?
    private(set) var subtitle: String?

    func record(_ progress: ToastProgress, subtitle: String?) {
        self.progress = progress
        if let subtitle { self.subtitle = subtitle }
    }
}

/// Progress reports, gathered across threads.
private final class PageProgress: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [Double] = []

    func record(_ value: Double) {
        lock.withLock { recorded.append(value) }
    }

    var values: [Double] { lock.withLock { recorded } }
}
