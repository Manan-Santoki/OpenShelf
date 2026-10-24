// Adapted from David NeSmith’s d0b71b4 and 207bc44; see docs/FORK_REVIEW.md.
import AppKit
import CoreText
import Vision
import CoreML
import PDFKit
import SwiftUI
import Testing
@testable import OpenShelf

struct OCRTests {
    func workspace() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-ocr-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    static func picture(of text: String) throws -> CGImage {
        try ImageRenderingRegressionTests.picture(of: text)
    }

    @Test func ocrKeepsPagesThatAlreadyHaveText() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let pdf = folder.appendingPathComponent("Mixed.pdf")
        var box = CGRect(x: 0, y: 0, width: 612, height: 792)
        let context = try #require(CGContext(pdf as CFURL, mediaBox: &box, nil))
        context.beginPDFPage(nil)
        let font = CTFontCreateWithName("Helvetica" as CFString, 24, nil)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: "Vector page text", attributes: [.init(kCTFontAttributeName as String): font]))
        context.textPosition = CGPoint(x: 72, y: 700)
        CTLineDraw(line, context)
        context.endPDFPage()
        context.beginPDFPage(nil)
        context.draw(try Self.picture(of: "Scanned words here"), in: CGRect(x: 0, y: 300, width: 612, height: 153))
        context.endPDFPage()
        context.closePDF()

        // The pages outlive the code that opened the document, as they do in OCR.run.
        let pages = try autoreleasepool { try OCRPage.load(pdf) }
        let canvas = try #require(CGContext(
            data: nil, width: 612, height: 792, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        canvas.setFillColor(.white)
        canvas.fill(CGRect(x: 0, y: 0, width: 612, height: 792))
        pages[0].draw(in: canvas)
        let drawn = try #require(canvas.makeImage()?.dataProvider?.data as Data?)
        #expect(drawn.contains { $0 < 128 }, "the first page drew nothing")

        let output = try await OCR.run(pdf, output: .searchablePDF)
        let document = try #require(PDFDocument(url: output))
        #expect(document.pageCount == 2)
        #expect(document.page(at: 0)?.string?.contains("Vector page text") == true)
        #expect(document.page(at: 1)?.string?.contains("Scanned") == true)
    }

    @Test func picksSystemLanguagesVisionKnows() {
        let supported = ["en-US", "fr-FR", "de-DE", "zh-Hans"]
        #expect(OCR.recognitionLanguages(preferred: ["en-GB", "de-AT", "ja-JP"], supported: supported) == ["en-US", "de-DE"])
        #expect(OCR.recognitionLanguages(preferred: ["fr-FR", "fr-CA"], supported: supported) == ["fr-FR"])
    }

    @Test func readsTopToBottomThenLeftToRight() {
        let lines = [
            OCR.Line(text: "b", box: CGRect(x: 0.5, y: 0.8, width: 0.1, height: 0.05)),
            OCR.Line(text: "c", box: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.05)),
            OCR.Line(text: "a", box: CGRect(x: 0.1, y: 0.81, width: 0.1, height: 0.05)),
        ]
        #expect(OCR.readingOrder(lines).map(\.text) == ["a", "b", "c"])
    }

    @Test func recognisesTextInAnImageAndAScannedPDF() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let png = folder.appendingPathComponent("Scan.png")
        try EditableImage.write(try Self.picture(of: "Hello OpenShelf 2026"), to: png, type: .png)

        let text = try await OCR.run(png, output: .text)
        #expect(text.lastPathComponent == "Scan OCR.txt")
        #expect(try String(contentsOf: text, encoding: .utf8).contains("OpenShelf"))

        let pdf = try await OCR.run(png, output: .searchablePDF)
        #expect(pdf.lastPathComponent == "Scan OCR.pdf")
        let document = try #require(PDFDocument(url: pdf))
        #expect(document.string?.contains("OpenShelf") == true)
        // Found where it's drawn: the text sits across the middle of the page.
        let selection = try #require(document.findString("OpenShelf", withOptions: []).first)
        let bounds = selection.bounds(for: try #require(document.page(at: 0)))
        #expect(bounds.minY > 0 && bounds.maxY < 300 && bounds.width > 100)

        // That scan-only PDF, read again: its page now has text, so it's kept as it is.
        let again = try await OCR.run(pdf, output: .text)
        #expect(try String(contentsOf: again, encoding: .utf8).contains("OpenShelf"))
    }

}

extension OCRTests {
    @Test func concurrentJobsCompleteWithoutBlockingTheSwiftExecutor() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let image = try Self.picture(of: "Concurrent OpenShelf")
        let inputs = try (0..<16).map { index in
            let url = folder.appendingPathComponent("Scan \(index).png")
            try EditableImage.write(image, to: url, type: .png)
            return url
        }
        let outputs = try await withThrowingTaskGroup(of: URL.self) { group in
            for input in inputs {
                group.addTask { try await OCR.run(input, output: .text) }
            }
            var results: [URL] = []
            for try await output in group { results.append(output) }
            return results
        }
        #expect(Set(outputs).count == inputs.count)
        for output in outputs {
            #expect(try String(contentsOf: output, encoding: .utf8).contains("OpenShelf"))
        }
    }

    @Test func retriesFailedAcceleratorRecognitionOnCPU() throws {
        enum SimulatedFailure: Error { case accelerator }
        var attempts = 0
        let lines = try OCR.recognize(Self.picture(of: "CPU OpenShelf"), languages: ["en-US"], perform: { handler, request in
            attempts += 1
            if attempts == 1 { throw SimulatedFailure.accelerator }
            let stages = try request.supportedComputeStageDevices
            #expect(stages.keys.contains { stage in
                if case .cpu? = request.computeDevice(for: stage) { return true }
                return false
            })
            try handler.perform([request])
        })
        #expect(attempts == 2)
        #expect(lines.map(\.text).joined(separator: " ").contains("OpenShelf"))
    }

    @Test func rejectsLockedPDFs() throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let pdf = PDFDocument()
        pdf.insert(try #require(PDFPage(image: NSImage(cgImage: try Self.picture(of: "Secret text"), size: .zero))), at: 0)
        let url = folder.appendingPathComponent("Locked.pdf")
        #expect(pdf.write(to: url, withOptions: [.userPasswordOption: "secret", .ownerPasswordOption: "owner"]))
        #expect(throws: (any Error).self) { try OCRPage.load(url) }
    }

    @MainActor @Test func cancelledRecognitionWritesNothing() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let image = folder.appendingPathComponent("Scan.png")
        try EditableImage.write(try Self.picture(of: "Cancel this recognition"), to: image, type: .png)
        let box = OCRCancellation()
        let task = Task {
            try await OCR.run(image, output: .searchablePDF) { _ in box.cancel() }
        }
        box.set(task)
        do {
            _ = try await task.value
            Issue.record("Cancelled OCR unexpectedly saved a file")
        } catch is CancellationError { }
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["Scan.png"])
    }

    @Test func rotatedPDFKeepsItsDisplayedSizeAndSearchableText() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let png = folder.appendingPathComponent("Scan.png")
        try EditableImage.write(try Self.picture(of: "Rotate OpenShelf"), to: png, type: .png)
        let searchable = try await OCR.run(png, output: .searchablePDF)
        let document = try #require(PDFDocument(url: searchable))
        let page = try #require(document.page(at: 0))
        page.rotation = 90
        let originalSize = page.bounds(for: .mediaBox).size
        let rotated = folder.appendingPathComponent("Rotated.pdf")
        #expect(document.write(to: rotated))
        let output = try await OCR.run(rotated, output: .searchablePDF)
        let result = try #require(PDFDocument(url: output))
        let resultPage = try #require(result.page(at: 0))
        #expect(resultPage.bounds(for: .mediaBox).size == CGSize(width: originalSize.height, height: originalSize.width))
        #expect(result.string?.contains("OpenShelf") == true)
    }

    @MainActor @Test func editorFitsItsWindow() {
        let actions = EditorActions(close: {}, finished: { _ in }, failed: { _ in }, focus: {})
        let editor = OCREditor(urls: [URL(fileURLWithPath: "/tmp/Scan.png")], actions: actions)
        let size = NSHostingController(rootView: editor).sizeThatFits(in: OCREditor.windowSize)
        #expect(size.width <= OCREditor.windowSize.width)
        #expect(size.height <= OCREditor.windowSize.height)
    }
}

private final class OCRCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var task: Task<URL, Error>?
    func set(_ task: Task<URL, Error>) { lock.withLock { self.task = task } }
    func cancel() { lock.withLock { task?.cancel() } }
}
