import Foundation
import Testing
@testable import OpenShelf

struct ExcelLimitTests {
    /// A fresh, empty folder.
    func workspace() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-tools-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    // MARK: Excel's row limit

    @Test func countsRecordsNotLines() throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let csv = folder.appendingPathComponent("Quoted.csv")
        // Header, a record whose quoted field holds a line break, and a last record with no closing newline.
        try "a,b\n1,\"two\nlines\"\n2,z".write(to: csv, atomically: true, encoding: .utf8)
        #expect(try DelimitedText.recordCount(csv) == 3)
        try "a,b\n1,x\n".write(to: csv, atomically: true, encoding: .utf8)
        #expect(try DelimitedText.recordCount(csv) == 2)
    }

    @Test(arguments: ["", "a,b\r1,x\r", "a,b\r\n1,x\r\n", "a,b\n1,\"two\"\"\nlines\"\n2,z", "a,b\n1,an\"unquoted\n2,z", "a,b\n1,\"two\nlines\""])
    func counterMatchesParser(text: String) throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let csv = folder.appendingPathComponent("Rows.csv")
        for encoding: String.Encoding in [.utf8, .utf16, .utf16BigEndian, .utf16LittleEndian] {
            var data = try #require(text.data(using: encoding))
            if encoding == .utf16BigEndian { data = Data([0xFE, 0xFF]) + data }
            if encoding == .utf16LittleEndian { data = Data([0xFF, 0xFE]) + data }
            try data.write(to: csv)
            #expect(try DelimitedText.recordCount(csv) == DelimitedText.read(csv, delimiter: ",").count)
        }
    }

    @Test func quotedFieldsAcrossChunks() throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let tsv = folder.appendingPathComponent("Rows.tsv")
        let text = "a\tb\n1\t\"" + String(repeating: "x", count: (1 << 20) - 8) + "\"\"\nmore\"\n2\tz"
        try text.write(to: tsv, atomically: true, encoding: .utf8)
        #expect(try DelimitedText.recordCount(tsv, delimiter: "\t") == 3)
    }

    @Test func excelLimitMessage() {
        #expect(throws: Never.self) { try ExcelLimit.check(rows: 1_048_576) }
        do {
            try ExcelLimit.check(rows: 1_048_577)
            Issue.record("one row too many was accepted")
        } catch {
            #expect(error.localizedDescription == "Excel holds up to 1,048,576 rows; this has 1,048,577. Save as CSV instead.")
        }
    }

    func expectRefusal(_ folder: URL, _ body: () async throws -> Void) async {
        let before = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        do {
            try await body()
            Issue.record("a table over the limit was written")
        } catch {
            #expect(error.localizedDescription.hasPrefix("Excel holds up to"), "\(error.localizedDescription)")
        }
        #expect(((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []) == before, "something was saved")
    }

    @Test func csvOverTheLimitIsRefused() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let csv = folder.appendingPathComponent("Rows.csv")
        try "name\nAda\nGrace\nKatherine\n".write(to: csv, atomically: true, encoding: .utf8)
        // Four rows with the header: fine at four, refused at three.
        let fits = try await SpreadsheetConversion.convert(csv, to: .xlsx, tools: ExternalTools(ffmpeg: nil, cwebp: nil), rowLimit: 4)
        #expect(fits.map(\.lastPathComponent) == ["Rows.xlsx"])
        try FileManager.default.removeItem(at: fits[0])
        await expectRefusal(folder) { _ = try await SpreadsheetConversion.convert(csv, to: .xlsx, tools: ExternalTools(ffmpeg: nil, cwebp: nil), rowLimit: 3) }
    }

}
