import AppKit
import Foundation
import Testing
@testable import OpenShelf

struct DelimitedTextTests {
    @Test func parsesQuotesEscapesAndEmbeddedNewlines() {
        let table = DelimitedText.parse("a,b,c\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\n1,,3\n", delimiter: ",")
        #expect(table == [["a", "b", "c"], ["x, y", "say \"hi\"", "two\nlines"], ["1", "", "3"]])
    }

    @Test func handlesCRLFAndMissingFinalNewline() {
        #expect(DelimitedText.parse("a,b\r\nc,d", delimiter: ",") == [["a", "b"], ["c", "d"]])
    }

    @Test func writeQuotesOnlyWhenNeededAndRoundTrips() {
        let table: Table = [["Name", "Note"], ["Smith, Jane", "Said \"hi\""], ["Tab\there", "multi\nline"]]
        let csv = DelimitedText.write(table, delimiter: ",")
        #expect(csv.hasPrefix("Name,Note\n\"Smith, Jane\",\"Said \"\"hi\"\"\""))
        #expect(DelimitedText.parse(csv, delimiter: ",") == table)
        #expect(DelimitedText.parse(DelimitedText.write(table, delimiter: "\t"), delimiter: "\t") == table)
    }
}

struct XlsxTests {
    @Test func numbersOnlyWhenTheyRoundTrip() {
        #expect(XlsxWriter.numericValue("1250.5") == "1250.5")
        #expect(XlsxWriter.numericValue("-3") == "-3")
        #expect(XlsxWriter.numericValue("007") == nil)
        #expect(XlsxWriter.numericValue("1e5") == nil)
        #expect(XlsxWriter.numericValue("12345678901234567890") == nil)
        #expect(XlsxWriter.numericValue("") == nil)
    }

    @Test func columnNamesAndReferences() {
        #expect(XlsxReader.columnName(0) == "A")
        #expect(XlsxReader.columnName(25) == "Z")
        #expect(XlsxReader.columnName(26) == "AA")
        #expect(XlsxReader.columnName(701) == "ZZ")
        #expect(XlsxReader.columnIndex(ofReference: "AB12") == 27)
        #expect(XlsxReader.columnIndex(ofReference: "C3") == 2)
    }

    @Test func writeThenReadRoundTrips() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("roundtrip-\(UUID().uuidString).xlsx")
        defer { try? FileManager.default.removeItem(at: url) }
        let table: Table = [["Name", "Amount", "Code"], ["Zürich & co <x>", "12.5", "007"], ["東京", "", "-3"]]
        try XlsxWriter.write(sheets: [(name: "Data", table: table)], to: url)
        #expect(try XlsxReader.firstSheet(of: url) == table)
    }

    @Test func readsSharedStringsAndSparseCells() {
        let sheet = Data(#"<worksheet><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1"><v>5</v></c></row><row r="3"><c r="B3" t="b"><v>1</v></c></row></sheetData></worksheet>"#.utf8)
        #expect(XlsxReader.cells(sheet, sharedStrings: ["Hello"]) == [["Hello", "", "5"], ["", "", ""], ["", "TRUE", ""]])
    }
}

struct MarkdownTests {
    @Test func richTextBecomesHeadingsListsAndInlineStyles() {
        let body = NSFont.systemFont(ofSize: 12)
        let text = NSMutableAttributedString()
        text.append(NSAttributedString(string: "Title\n", attributes: [.font: NSFont.boldSystemFont(ofSize: 24)]))
        text.append(NSAttributedString(string: "Plain ", attributes: [.font: body]))
        text.append(NSAttributedString(string: "bold", attributes: [.font: NSFont.boldSystemFont(ofSize: 12)]))
        text.append(NSAttributedString(string: " and ", attributes: [.font: body]))
        text.append(NSAttributedString(string: "site", attributes: [.font: body, .link: URL(string: "https://example.com")!]))
        text.append(NSAttributedString(string: "\n\t•\tFirst\n\t2\tSecond\n", attributes: [.font: body]))

        let markdown = DocumentStructure.markdown(DocumentStructure.blocks(from: text))
        #expect(markdown.contains("# Title"))
        #expect(markdown.contains("Plain **bold** and [site](https://example.com)"))
        #expect(markdown.contains("- First"))
        #expect(markdown.contains("1. Second"))
    }

    @Test func listItemsSitOnConsecutiveLines() {
        let blocks: [TextBlock] = [
            .listItem(ordered: true, runs: [TextRun(text: "a")]),
            .listItem(ordered: true, runs: [TextRun(text: "b")]),
            .paragraph([TextRun(text: "after")]),
        ]
        #expect(DocumentStructure.markdown(blocks) == "1. a\n2. b\n\nafter\n")
    }

    @Test func markdownSourceBecomesXHTML() {
        let html = DocumentStructure.xhtml(DocumentStructure.blocks(fromMarkdown: "# Hi & bye\n\nSome **bold** text.\n\n- one\n- two\n"))
        #expect(html.contains("<h1>Hi &amp; bye</h1>"))
        #expect(html.contains("<strong>bold</strong>"))
        #expect(html.contains("<ul>\n<li>one</li>\n<li>two</li>\n</ul>"))
    }

    @Test func emphasisMarkersHugTheirText() {
        #expect(DocumentStructure.markdown([TextRun(text: "word ", bold: true), TextRun(text: "next")]) == "**word** next")
    }
}

struct PDFTextTests {
    @Test func joinsWrappedLinesOfProse() {
        let long = String(repeating: "word ", count: 16).trimmingCharacters(in: .whitespaces)
        let text = "\(long)\n\(long)\nend of para.\n\(long)\nlast"
        #expect(PDFText.paragraphs(fromPageText: text) == ["\(long) \(long) end of para.", "\(long) last"])
    }

    @Test func keepsShortLinesApart() {
        #expect(PDFText.paragraphs(fromPageText: "Milk\nEggs\n\nBread") == ["Milk", "Eggs", "Bread"])
    }

    @Test func rowsSplitOnTabsAndWideGaps() {
        #expect(PDFText.rows(fromPageText: "Name  Amount\tCode\nBob   12\t7\n\n") == [["Name", "Amount", "Code"], ["Bob", "12", "7"]])
    }
}

struct FormatCatalogTests {
    let none = ExternalTools(ffmpeg: nil, cwebp: nil)
    let all = ExternalTools(
        ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
        cwebp: URL(fileURLWithPath: "/usr/bin/true"),
        ffprobe: nil,
        libreOffice: URL(fileURLWithPath: "/usr/bin/true")
    )
    private func url(_ name: String) -> URL { URL(fileURLWithPath: "/tmp/\(name)") }

    @Test(arguments: [
        ("a.mkv", FileKind.video), ("a.webm", .video), ("a.avi", .video), ("a.wmv", .video),
        ("a.ogg", .audio), ("a.opus", .audio), ("a.wma", .audio),
        ("a.csv", .spreadsheet), ("a.xlsx", .spreadsheet), ("a.ods", .spreadsheet),
        ("a.pptx", .presentation), ("a.key", .presentation), ("a.rtfd", .document),
    ])
    func classifiesNewKinds(name: String, kind: FileKind) {
        #expect(FileKind(url: url(name)) == kind)
    }

    @Test func ffmpegOnlyFormatsNeedFFmpeg() {
        #expect(ConversionCatalog.formats(for: [url("a.ogg")], tools: none) == [.zip])
        #expect(ConversionCatalog.formats(for: [url("a.mkv")], tools: none) == [.zip])
        #expect(ConversionCatalog.formats(for: [url("a.mkv")], tools: all).contains(.webm))
        #expect(ConversionCatalog.formats(for: [url("a.aiff")], tools: all).contains(.opus))
        #expect(!ConversionCatalog.formats(for: [url("a.aiff")], tools: none).contains(.ogg))
    }

    @Test func officeFormatsNeedLibreOffice() {
        #expect(ConversionCatalog.formats(for: [url("a.pptx")], tools: none) == [.zip])
        #expect(ConversionCatalog.formats(for: [url("a.pptx")], tools: all) == [.pdf, .jpg, .png, .zip])
        #expect(ConversionCatalog.formats(for: [url("a.key")], tools: all) == [.zip])
        #expect(ConversionCatalog.formats(for: [url("a.xls")], tools: none) == [.zip])
        #expect(ConversionCatalog.formats(for: [url("a.csv")], tools: none) == [.xlsx, .tsv, .pdf, .zip])
        #expect(ConversionCatalog.formats(for: [url("a.csv")], tools: all) == [.xlsx, .tsv, .pdf, .ods, .zip])
    }

    @Test func documentsAndPDFsOfferNewFormats() {
        #expect(ConversionCatalog.formats(for: [url("a.pdf")], tools: none) == [.docx, .jpg, .png, .txt, .md, .epub, .pptx, .xlsx, .zip])
        #expect(ConversionCatalog.formats(for: [url("a.md")], tools: none) == [.pdf, .docx, .txt, .epub, .html, .rtf, .zip])
    }

    @Test func foldersOfferTarArchives() {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("folder-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(ConversionCatalog.formats(for: [folder], tools: none) == [.zip, .tar, .tgz])
        #expect(OutputFormat.tgz.fileExtension == "tar.gz")
        #expect(OutputFormat.tgz.title == "TAR.GZ")
    }

    @Test func wheelsStayWithinTwelveWedges() {
        for name in ["a.jpg", "a.pdf", "a.docx", "a.mkv", "a.mov", "a.ogg", "a.aiff", "a.csv", "a.xls", "a.pptx"] {
            #expect(ConversionCatalog.formats(for: [url(name)], tools: all).count <= ConversionCatalog.maxWedges)
        }
    }
}

struct ExternalToolArgumentTests {
    @Test func oggFallsBackWhenLibvorbisIsMissing() {
        #expect(FFmpegArguments.arguments(for: .ogg, encoders: ["libvorbis", "libopus"]).contains("libvorbis"))
        #expect(FFmpegArguments.arguments(for: .ogg, encoders: ["libopus"]).contains("libopus"))
        #expect(FFmpegArguments.arguments(for: .ogg, encoders: []).contains("experimental"))
    }

    @Test func videoContainersUseTheirCodecs() {
        #expect(FFmpegArguments.arguments(for: .webm, encoders: []).contains("libvpx-vp9"))
        #expect(FFmpegArguments.arguments(for: .wmv, encoders: []).contains("wmv2"))
        #expect(FFmpegArguments.arguments(for: .avi, encoders: []).contains("mpeg4"))
        #expect(FFmpegArguments.arguments(for: .mp3, encoders: []).first == "-vn")
    }

    @Test func libreOfficeCommandUsesAPrivateProfile() {
        let arguments = OfficeConversion.arguments(
            source: URL(fileURLWithPath: "/tmp/My Deck.pptx"),
            filter: "pdf",
            profile: URL(fileURLWithPath: "/tmp/work/profile", isDirectory: true),
            output: URL(fileURLWithPath: "/tmp/work/out")
        )
        #expect(arguments.first == "-env:UserInstallation=file:///tmp/work/profile/")
        #expect(arguments.contains("--headless"))
        #expect(Array(arguments.suffix(4)) == ["pdf", "--outdir", "/tmp/work/out", "/tmp/My Deck.pptx"])
        #expect(OfficeConversion.filter(for: .csv)?.hasPrefix("csv:") == true)
        #expect(OfficeConversion.filter(for: .mp3) == nil)
    }
}
