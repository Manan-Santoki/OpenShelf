import Foundation
import PDFKit
import Testing
@testable import OpenShelf

/// A PDF of `pageCount` pages, each a point wider than the one before so the
/// order can be read back.
private func writePDF(pageCount: Int, named name: String, in directory: URL) throws -> URL {
    let document = PDFDocument()
    for index in 0..<pageCount {
        let page = PDFPage()
        page.setBounds(CGRect(x: 0, y: 0, width: 100 + index, height: 200), for: .mediaBox)
        document.insert(page, at: index)
    }
    let url = directory.appendingPathComponent(name)
    guard document.write(to: url) else { throw PDFToolError.unwritable(url) }
    return url
}

private func pageWidths(of url: URL) -> [Int] {
    guard let document = PDFDocument(url: url) else { return [] }
    return (0..<document.pageCount).compactMap { document.page(at: $0).map { Int($0.bounds(for: .mediaBox).width) } }
}

private func makeDirectory(_ name: String) throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(name)-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

/// Pages and Split on several PDFs at once.
struct BatchPDFTests {
    @Test func oneRuleSplitsEachPDFAsFarAsItsPagesGo() throws {
        let rule = PDFSplitRule(mode: .chunks, chunkSize: 3)
        #expect(try rule.groups(pageCount: 7, rangeLimit: 7).get() == [[0, 1, 2], [3, 4, 5], [6]])
        // Three pages or fewer make one copy of the whole PDF, so it isn't split.
        #expect(try rule.groups(pageCount: 3, rangeLimit: 7).get() == [[0, 1, 2]])
        #expect(!PDFTools.splits([[0, 1, 2]], pageCount: 3))
        #expect(!PDFTools.splits(try rule.groups(pageCount: 2, rangeLimit: 7).get(), pageCount: 2))

        let everyPage = PDFSplitRule()
        #expect(!PDFTools.splits(try everyPage.groups(pageCount: 1).get(), pageCount: 1))
        #expect(PDFTools.splits(try everyPage.groups(pageCount: 2).get(), pageCount: 2))
    }

    @Test func rangesAreReadAgainstTheLongestPDFAndCutToEach() throws {
        let rule = PDFSplitRule(mode: .ranges, rangesText: "1-2, 4-9, 8-")
        #expect(try rule.groups(pageCount: 9, rangeLimit: 9).get() == [[0, 1], [3, 4, 5, 6, 7, 8], [7, 8]])
        #expect(try rule.groups(pageCount: 6, rangeLimit: 9).get() == [[0, 1], [3, 4, 5]])
        // Only the first range reaches a 3-page PDF: pages 1-2 are still a new PDF.
        #expect(try rule.groups(pageCount: 3, rangeLimit: 9).get() == [[0, 1]])
        #expect(PDFTools.splits([[0, 1]], pageCount: 3))
        // On a 2-page PDF it's the whole PDF, so that one is left alone.
        #expect(!PDFTools.splits(try rule.groups(pageCount: 2, rangeLimit: 9).get(), pageCount: 2))

        let past = PDFSplitRule(mode: .ranges, rangesText: "5-6")
        #expect(try past.groups(pageCount: 3, rangeLimit: 9).get().isEmpty)
        // Pages past the longest PDF are still a mistake.
        #expect(throws: PDFTools.RangeError.self) { try PDFSplitRule(mode: .ranges, rangesText: "4-12").groups(pageCount: 6, rangeLimit: 9).get() }
    }

    @Test func oneRuleOnOnePDFIsAsStrictAsBefore() {
        let rule = PDFSplitRule(mode: .ranges, rangesText: "1-5")
        #expect(rule.groups(pageCount: 3) == PDFTools.ranges("1-5", pageCount: 3))
        #expect(PDFSplitRule(mode: .ranges, rangesText: "2-").groups(pageCount: 4) == .success([[1, 2, 3]]))
    }

    @Test func splittingSeveralPDFsCountsTheOnesLeftWhole() throws {
        let directory = try makeDirectory("batch-split")
        defer { try? FileManager.default.removeItem(at: directory) }
        let short = try writePDF(pageCount: 1, named: "Short.pdf", in: directory)
        let four = try writePDF(pageCount: 4, named: "Four.pdf", in: directory)
        let five = try writePDF(pageCount: 5, named: "Five.pdf", in: directory)

        let rule = PDFSplitRule(mode: .chunks, chunkSize: 2)
        let jobs = try [(short, 1), (four, 4), (five, 5)].map { url, pageCount in
            (url: url, groups: try rule.groups(pageCount: pageCount, rangeLimit: 5).get())
        }
        let outcome = try PDFTools.split(jobs)
        #expect(outcome.saved.map(\.lastPathComponent) == [
            "Four Pages 1-2.pdf", "Four Pages 3-4.pdf",
            "Five Pages 1-2.pdf", "Five Pages 3-4.pdf", "Five Page 5.pdf",
        ])
        #expect(outcome.saved.map { pageWidths(of: $0) } == [[100, 101], [102, 103], [100, 101], [102, 103], [104]])
        #expect(outcome.unchanged == 1)
        #expect(outcome.title == "5 saved · 1 not split")
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("Short Page 1.pdf").path))
    }

    @Test func splittingNothingSavesNothing() throws {
        let directory = try makeDirectory("batch-split-none")
        defer { try? FileManager.default.removeItem(at: directory) }
        let a = try writePDF(pageCount: 1, named: "A.pdf", in: directory)
        let b = try writePDF(pageCount: 2, named: "B.pdf", in: directory)

        let outcome = try PDFTools.split([(url: a, groups: [[0]]), (url: b, groups: [])])
        #expect(!outcome.savedAnything)
        #expect(outcome.unchanged == 2)
        #expect(outcome.subtitle == "All 2 not split")
    }

    @Test func savingPagesWritesOnlyTheChangedPDFs() throws {
        let directory = try makeDirectory("batch-pages")
        defer { try? FileManager.default.removeItem(at: directory) }
        let report = try writePDF(pageCount: 3, named: "Report.pdf", in: directory)
        let slides = try writePDF(pageCount: 4, named: "Slides.pdf", in: directory)
        let notes = try writePDF(pageCount: 2, named: "Notes.pdf", in: directory)
        let scan = try writePDF(pageCount: 2, named: "Scan.pdf", in: directory)

        // Report: page 2 deleted and page 3 turned. Slides: reversed.
        // Notes: never shown. Scan: turned all the way round, so as it was.
        let jobs: [(url: URL, pages: [PDFPageEdit]?)] = [
            (report, [PDFPageEdit(sourceIndex: 0), PDFPageEdit(sourceIndex: 2, rotation: 90)]),
            (slides, (0..<4).reversed().map { PDFPageEdit(sourceIndex: $0) }),
            (notes, nil),
            (scan, [PDFPageEdit(sourceIndex: 0, rotation: 360), PDFPageEdit(sourceIndex: 1)]),
        ]
        let outcome = try PDFTools.saveEditedPages(jobs)
        #expect(outcome.saved.map(\.lastPathComponent) == ["Report Edited.pdf", "Slides Edited.pdf"])
        #expect(outcome.unchanged == 2)
        #expect(outcome.title == "2 edited · 2 unchanged")

        #expect(pageWidths(of: outcome.saved[0]) == [100, 102])
        #expect(PDFDocument(url: outcome.saved[0])?.page(at: 1)?.rotation == 90)
        #expect(pageWidths(of: outcome.saved[1]) == [103, 102, 101, 100])
    }

    @Test func pagesLeftAsTheyWereKeepTheDocument() {
        let pages = (0..<3).map { PDFPageEdit(sourceIndex: $0) }
        #expect(PDFTools.keepsDocument(pages, pageCount: 3))
        #expect(!PDFTools.keepsDocument(pages, pageCount: 4))
        #expect(!PDFTools.keepsDocument(Array(pages.dropLast()), pageCount: 3))
        #expect(!PDFTools.keepsDocument(pages.reversed(), pageCount: 3))
        #expect(!PDFTools.keepsDocument([pages[0], pages[1], PDFPageEdit(sourceIndex: 2, rotation: 270)], pageCount: 3))
    }

    @Test func severalPDFsOfferPagesAndSplit() {
        let urls = ["a.pdf", "b.pdf", "c.pdf"].map { URL(fileURLWithPath: "/tmp/\($0)") }
        let tools = FileTool.available(for: urls)
        #expect(tools.contains(.organizePages))
        #expect(tools.contains(.splitPDF))
        #expect(WheelOrdering.tools(tools) == [.combinePDF, .organizePages, .splitPDF, .compressPDF, .pdfMetadata, .ocr])
    }
}
