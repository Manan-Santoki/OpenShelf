import AppKit
import Foundation
import PDFKit
import Testing
@testable import OpenShelf

/// Issue #17: Word to PDF without LibreOffice dropped the pictures.
struct QuickLookConversionTests {
    private func temporaryFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-quicklook-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// A US Letter Word document with 1 in margins, a picture and enough text for two pages.
    private func wordDocument(in folder: URL) throws -> URL {
        let image = try #require(CGContext(data: nil, width: 200, height: 100, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        image.setFillColor(CGColor(red: 0.8, green: 0.2, blue: 0.2, alpha: 1))
        image.fill(CGRect(x: 0, y: 0, width: 200, height: 100))
        try EditableImage.write(try #require(image.makeImage()), to: folder.appendingPathComponent("banner.png"), type: .png)
        let paragraphs = (1...40).map { "Paragraph \($0). " + String(repeating: "Some **bold** words and some plain ones. ", count: 6) }
        let markdown = "![Banner](banner.png)\n\n# Speaker\n\n" + paragraphs.joined(separator: "\n\n")
        let url = folder.appendingPathComponent("Speaker.docx")
        try MarkdownDocx.write(MarkdownDocument(parsing: markdown), baseURL: folder, to: url)
        return url
    }

    @Test func wordToPDFKeepsPicturesAndPageLayout() async throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = try wordDocument(in: folder)

        let output = try await Converter.convert(source, to: .pdf, tools: ExternalTools()) { _ in }
        #expect(output.map(\.lastPathComponent) == ["Speaker.pdf"])
        let document = try #require(PDFDocument(url: output[0]))
        #expect(document.pageCount >= 2)
        let page = try #require(document.page(at: 0))
        #expect(page.bounds(for: .mediaBox).size == CGSize(width: 612, height: 792))
        #expect(document.string?.contains("Speaker") == true)
        #expect(document.string?.contains("Paragraph 40.") == true)

        // The picture made it into the PDF.
        let data = try Data(contentsOf: output[0])
        #expect(data.range(of: Data("/Subtype /Image".utf8)) != nil)

        // Text starts at the document's 1 in margins on every page, not shrunk
        // (WebKit prints pages at 80% unless told otherwise).
        for index in 0..<2 {
            let page = try #require(document.page(at: index))
            let text = try #require(page.string as NSString?)
            let paragraph = try #require(page.selection(for: text.range(of: "Paragraph")))
            #expect(abs(paragraph.bounds(for: page).minX - 72) < 2)
        }
    }

    @Test func paperSizesFollowTheDocumentsWidth() {
        #expect(QuickLookConversion.paperSize(width: 595) == NSSize(width: 595, height: 842))
        #expect(QuickLookConversion.paperSize(width: 612) == NSSize(width: 612, height: 792))
        #expect(QuickLookConversion.paperSize(width: 420) == NSSize(width: 420, height: 595))
        #expect(QuickLookConversion.paperSize(width: 700) == NSSize(width: 700, height: 990))
    }

    @Test func onlyWordDocumentsGoThroughQuickLook() {
        #expect(QuickLookConversion.canConvert(URL(fileURLWithPath: "/tmp/a.DOCX")))
        #expect(QuickLookConversion.canConvert(URL(fileURLWithPath: "/tmp/a.doc")))
        #expect(!QuickLookConversion.canConvert(URL(fileURLWithPath: "/tmp/a.odt")))
        #expect(!QuickLookConversion.canConvert(URL(fileURLWithPath: "/tmp/a.rtf")))
    }
}
