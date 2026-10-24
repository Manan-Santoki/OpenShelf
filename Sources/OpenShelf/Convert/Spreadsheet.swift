import CoreGraphics
import CoreText
import Foundation

/// A grid of cell texts, first row usually the header.
typealias Table = [[String]]

/// Comma- and tab-separated text (RFC 4180 quoting).
enum DelimitedText {
    /// The number of records in a CSV or TSV file, read in chunks without
    /// building the table: line breaks inside quoted fields don't count.
    static func recordCount(_ url: URL, delimiter: Character = ",") throws -> Int {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let separator: UInt16 = delimiter == "\t" ? 0x09 : 0x2C
        var count = 0
        var inQuotes = false
        var pendingQuote = false
        var fieldHasContent = false
        var hasFields = false
        var afterCR = false

        // Use the same quote and record rules as parse, including CR-only
        // records, escaped quotes, and a final record without a line break.
        func consume(_ unit: UInt16) {
            if afterCR {
                afterCR = false
                if unit == 0x0A { return }
            }
            if inQuotes {
                if unit == 0x22 {
                    if pendingQuote {
                        fieldHasContent = true
                        pendingQuote = false
                    } else {
                        pendingQuote = true
                    }
                    return
                }
                if pendingQuote {
                    inQuotes = false
                    pendingQuote = false
                } else {
                    fieldHasContent = true
                    return
                }
            }
            if unit == 0x22, !fieldHasContent {
                inQuotes = true
            } else if unit == separator {
                hasFields = true
                fieldHasContent = false
            } else if unit == 0x0A || unit == 0x0D {
                count += 1
                fieldHasContent = false
                hasFields = false
                afterCR = unit == 0x0D
            } else {
                fieldHasContent = true
            }
        }

        var first = true
        var utf16LittleEndian: Bool?
        var pendingByte: UInt8?
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            try Task.checkCancellation()
            var bytes = chunk[...]
            if first {
                first = false
                if bytes.starts(with: [0xFF, 0xFE]) {
                    utf16LittleEndian = true
                    bytes = bytes.dropFirst(2)
                } else if bytes.starts(with: [0xFE, 0xFF]) {
                    utf16LittleEndian = false
                    bytes = bytes.dropFirst(2)
                } else if bytes.starts(with: [0xEF, 0xBB, 0xBF]) {
                    bytes = bytes.dropFirst(3)
                }
            }
            for byte in bytes {
                if let littleEndian = utf16LittleEndian {
                    if let leading = pendingByte {
                        consume(littleEndian ? UInt16(leading) | UInt16(byte) << 8 : UInt16(leading) << 8 | UInt16(byte))
                        pendingByte = nil
                    } else {
                        pendingByte = byte
                    }
                } else {
                    consume(UInt16(byte))
                }
            }
        }
        return count + (fieldHasContent || hasFields ? 1 : 0)
    }

    static func parse(_ text: String, delimiter: Character) -> Table {
        var rows: Table = []
        var row: [String] = []
        var field = ""
        var inQuotes = false
        var iterator = text.makeIterator()
        var pending: Character?

        func endField() {
            row.append(field)
            field = ""
        }
        func endRow() {
            endField()
            rows.append(row)
            row = []
        }

        while let character = pending ?? iterator.next() {
            pending = nil
            if inQuotes {
                if character == "\"" {
                    // A doubled quote is a literal quote; a single one closes the field.
                    if let next = iterator.next() {
                        if next == "\"" { field.append("\"") } else { inQuotes = false; pending = next }
                    } else {
                        inQuotes = false
                    }
                } else {
                    field.append(character)
                }
            } else if character == "\"" && field.isEmpty {
                inQuotes = true
            } else if character == delimiter {
                endField()
            } else if character == "\n" || character == "\r\n" || character == "\r" {
                endRow()
            } else {
                field.append(character)
            }
        }
        if !field.isEmpty || !row.isEmpty { endRow() }
        return rows
    }

    static func write(_ table: Table, delimiter: Character) -> String {
        table.map { row in
            row.map { field in
                let needsQuotes = field.contains(delimiter) || field.contains("\"") || field.contains("\n") || field.contains("\r")
                return needsQuotes ? "\"" + field.replacingOccurrences(of: "\"", with: "\"\"") + "\"" : field
            }
            .joined(separator: String(delimiter))
        }
        .joined(separator: "\n") + "\n"
    }

    static func read(_ url: URL, delimiter: Character) throws -> Table {
        guard let data = try? Data(contentsOf: url), let text = TextDecoding.decode(data) else {
            throw ConversionError.unreadable(url)
        }
        return parse(text, delimiter: delimiter)
    }
}

/// Writes a minimal Excel workbook with inline strings and numbers.
/// Excel opens at most 1,048,576 rows a sheet, the header row included, so
/// larger tables are refused before the (slow, memory-hungry) conversion.
enum ExcelLimit {
    static let rows = 1_048_576

    static func check(rows count: Int, limit: Int = rows) throws {
        guard count > limit else { return }
        throw ConversionError.failed("Excel holds up to \(limit.formatted()) rows; this has \(count.formatted()). Save as CSV instead.")
    }
}

enum XlsxWriter {
    static func write(sheets: [(name: String, table: Table)], to url: URL, rowLimit: Int = ExcelLimit.rows) throws {
        for sheet in sheets {
            try ExcelLimit.check(rows: sheet.table.count, limit: rowLimit)
        }
        let root = try PackageZip.makeWorkspace("xlsx")
        defer { try? FileManager.default.removeItem(at: root) }
        let header = #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"# + "\n"
        let relationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        let sheets = sheets.isEmpty ? [(name: "Sheet1", table: Table())] : sheets

        var sheetEntries: [String] = []
        var sheetRelationships: [String] = []
        var overrides: [String] = []
        var usedNames: Set<String> = []
        for (index, sheet) in sheets.enumerated() {
            let number = index + 1
            let name = uniqueSheetName(sheet.name, used: &usedNames)
            try PackageZip.write(header + worksheet(sheet.table), to: "xl/worksheets/sheet\(number).xml", in: root)
            sheetEntries.append(#"<sheet name="\#(name.xmlEscaped)" sheetId="\#(number)" r:id="rId\#(number)"/>"#)
            sheetRelationships.append(#"<Relationship Id="rId\#(number)" Type="\#(relationshipType)/worksheet" Target="worksheets/sheet\#(number).xml"/>"#)
            overrides.append(#"<Override PartName="/xl/worksheets/sheet\#(number).xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"#)
        }
        let stylesID = sheets.count + 1
        sheetRelationships.append(#"<Relationship Id="rId\#(stylesID)" Type="\#(relationshipType)/styles" Target="styles.xml"/>"#)

        try PackageZip.write(header + #"<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>\#(overrides.joined())</Types>"#, to: "[Content_Types].xml", in: root)
        try PackageZip.write(header + #"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="\#(relationshipType)/officeDocument" Target="xl/workbook.xml"/></Relationships>"#, to: "_rels/.rels", in: root)
        try PackageZip.write(header + #"<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="\#(relationshipType)"><sheets>\#(sheetEntries.joined())</sheets></workbook>"#, to: "xl/workbook.xml", in: root)
        try PackageZip.write(header + #"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">\#(sheetRelationships.joined())</Relationships>"#, to: "xl/_rels/workbook.xml.rels", in: root)
        try PackageZip.write(header + styles, to: "xl/styles.xml", in: root)
        try PackageZip.zip(root, to: url)
    }

    private static func worksheet(_ table: Table) -> String {
        var rows = ""
        for (rowIndex, row) in table.enumerated() {
            var cells = ""
            for (columnIndex, value) in row.enumerated() where !value.isEmpty {
                let reference = XlsxReader.columnName(columnIndex) + String(rowIndex + 1)
                if let number = numericValue(value) {
                    cells += #"<c r="\#(reference)"><v>\#(number)</v></c>"#
                } else {
                    cells += #"<c r="\#(reference)" t="inlineStr"><is><t xml:space="preserve">\#(value.xmlEscaped)</t></is></c>"#
                }
            }
            rows += #"<row r="\#(rowIndex + 1)">\#(cells)</row>"#
        }
        return #"<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>\#(rows)</sheetData></worksheet>"#
    }

    /// The cell's text as a number when it reads as one and would survive the
    /// round trip unchanged (so "007" and "1e5" stay text).
    static func numericValue(_ text: String) -> String? {
        guard text.wholeMatch(of: /-?(0|[1-9]\d{0,14})(\.\d{1,10})?/) != nil, Double(text) != nil else { return nil }
        return text
    }

    /// Excel sheet names: at most 31 characters, none of []:*?/\, unique.
    private static func uniqueSheetName(_ name: String, used: inout Set<String>) -> String {
        var cleaned = String(name.filter { !"[]:*?/\\".contains($0) }.prefix(31))
        if cleaned.isEmpty { cleaned = "Sheet" }
        var candidate = cleaned
        var counter = 2
        while used.contains(candidate.lowercased()) {
            let suffix = " \(counter)"
            candidate = String(cleaned.prefix(31 - suffix.count)) + suffix
            counter += 1
        }
        used.insert(candidate.lowercased())
        return candidate
    }

    private static let styles = #"<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>"#
}

/// Reads the first worksheet of an Excel (.xlsx) workbook as text.
enum XlsxReader {
    static func firstSheet(of url: URL) throws -> Table {
        guard let workbook = PackageZip.readEntry("xl/workbook.xml", from: url) else { throw ConversionError.unreadable(url) }
        let path = firstSheetPath(workbook: workbook, relationships: PackageZip.readEntry("xl/_rels/workbook.xml.rels", from: url)) ?? "xl/worksheets/sheet1.xml"
        guard let sheet = PackageZip.readEntry(path, from: url) else { throw ConversionError.unreadable(url) }
        let shared = PackageZip.readEntry("xl/sharedStrings.xml", from: url).map(sharedStrings) ?? []
        return cells(sheet, sharedStrings: shared)
    }

    /// "A", "B", … "Z", "AA", … for a zero-based column.
    static func columnName(_ index: Int) -> String {
        var index = index + 1
        var name = ""
        while index > 0 {
            let remainder = (index - 1) % 26
            name = String(UnicodeScalar(UInt8(65 + remainder))) + name
            index = (index - 1) / 26
        }
        return name
    }

    /// Zero-based column of a cell reference like "AB12".
    static func columnIndex(ofReference reference: String) -> Int {
        var index = 0
        for scalar in reference.unicodeScalars {
            guard scalar.value >= 65 && scalar.value <= 90 else { break }
            index = index * 26 + Int(scalar.value - 64)
        }
        return max(index - 1, 0)
    }

    private static func firstSheetPath(workbook: Data, relationships: Data?) -> String? {
        let sheets = XMLCollector(elements: ["sheet"])
        sheets.parse(workbook)
        guard let id = sheets.found.first?["r:id"], let relationships else { return nil }
        let rels = XMLCollector(elements: ["Relationship"])
        rels.parse(relationships)
        guard let target = rels.found.first(where: { $0["Id"] == id })?["Target"] else { return nil }
        return target.hasPrefix("/") ? String(target.dropFirst()) : "xl/" + target
    }

    private static func sharedStrings(_ data: Data) -> [String] {
        let parser = SharedStringsParser()
        parser.parse(data)
        return parser.strings
    }

    static func cells(_ sheet: Data, sharedStrings: [String]) -> Table {
        let parser = SheetParser(sharedStrings: sharedStrings)
        parser.parse(sheet)
        let width = parser.rows.values.map { ($0.keys.max() ?? -1) + 1 }.max() ?? 0
        guard let lastRow = parser.rows.keys.max() else { return [] }
        return (0...lastRow).map { row in
            (0..<width).map { parser.rows[row]?[$0] ?? "" }
        }
    }
}

// MARK: XML parsing

/// Collects the attributes of every element with one of the given names.
private final class XMLCollector: NSObject, XMLParserDelegate {
    let elements: Set<String>
    var found: [[String: String]] = []

    init(elements: Set<String>) { self.elements = elements }

    func parse(_ data: Data) {
        let parser = XMLParser(data: data)
        parser.delegate = self
        parser.parse()
    }

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
        if elements.contains(name) { found.append(attributes) }
    }
}

/// `<si>` items of sharedStrings.xml, joining rich-text runs.
private final class SharedStringsParser: NSObject, XMLParserDelegate {
    var strings: [String] = []
    private var current: String?
    private var inText = false

    func parse(_ data: Data) {
        let parser = XMLParser(data: data)
        parser.delegate = self
        parser.parse()
    }

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
        if name == "si" { current = "" }
        if name == "t" { inText = true }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if inText { current? += string }
    }

    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        if name == "t" { inText = false }
        if name == "si" {
            strings.append(current ?? "")
            current = nil
        }
    }
}

/// Cell values of a worksheet, by zero-based row and column.
private final class SheetParser: NSObject, XMLParserDelegate {
    let sharedStrings: [String]
    var rows: [Int: [Int: String]] = [:]
    private var rowIndex = 0
    private var column = 0
    private var cellType = ""
    private var value = ""
    private var collecting = false

    init(sharedStrings: [String]) { self.sharedStrings = sharedStrings }

    func parse(_ data: Data) {
        let parser = XMLParser(data: data)
        parser.delegate = self
        parser.parse()
    }

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
        switch name {
        case "row":
            rowIndex = (attributes["r"].flatMap(Int.init) ?? rowIndex + 1) - 1
            column = 0
        case "c":
            if let reference = attributes["r"] { column = XlsxReader.columnIndex(ofReference: reference) }
            cellType = attributes["t"] ?? "n"
            value = ""
        case "v", "t":
            collecting = true
        default:
            break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if collecting { value += string }
    }

    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        switch name {
        case "v", "t":
            collecting = false
        case "c":
            let text: String
            switch cellType {
            case "s": text = Int(value).flatMap { sharedStrings.indices.contains($0) ? sharedStrings[$0] : nil } ?? ""
            case "b": text = value == "1" ? "TRUE" : "FALSE"
            default: text = value
            }
            if !text.isEmpty { rows[rowIndex, default: [:]][column] = text }
            column += 1
        default:
            break
        }
    }
}

// MARK: Table PDF

/// Renders a table onto landscape US Letter pages, repeating the header row.
/// Uses Core Text, so it's safe off the main thread.
enum TablePDF {
    static func write(_ table: Table, to url: URL) throws {
        var page = CGRect(x: 0, y: 0, width: 792, height: 612)
        guard let context = CGContext(url as CFURL, mediaBox: &page, nil) else { throw EditableImageError.unwritable(url) }
        let margin: CGFloat = 36
        let usable = page.insetBy(dx: margin, dy: margin)
        let columns = table.map(\.count).max() ?? 0
        let rows = table.isEmpty ? [[" "]] : table

        // Size columns to their content, then shrink everything to fit the width.
        var fontSize: CGFloat = 9
        var widths = (0..<max(columns, 1)).map { column -> CGFloat in
            let longest = rows.prefix(500).map { $0.indices.contains(column) ? $0[column] : "" }.map(\.count).max() ?? 1
            return min(max(CGFloat(longest) * 5.4 + 10, 36), 220)
        }
        let total = widths.reduce(0, +)
        if total > usable.width {
            let scale = usable.width / total
            widths = widths.map { $0 * scale }
            fontSize = max(6, 9 * max(scale, 0.66))
        }
        let rowHeight = fontSize * 1.7
        let regular = CTFontCreateWithName("HelveticaNeue" as CFString, fontSize, nil)
        let bold = CTFontCreateWithName("HelveticaNeue-Bold" as CFString, fontSize, nil)
        let rowsPerPage = max(Int((usable.height - rowHeight) / rowHeight), 1)
        let header = rows[0]
        let body = Array(rows.dropFirst())

        var start = 0
        repeat {
            try Task.checkCancellation()
            context.beginPDFPage(nil)
            var y = usable.maxY - rowHeight
            context.setFillColor(CGColor(gray: 0.9, alpha: 1))
            context.fill(CGRect(x: usable.minX, y: y, width: widths.reduce(0, +), height: rowHeight))
            draw(header, at: y, widths: widths, x: usable.minX, rowHeight: rowHeight, font: bold, in: context)
            for (offset, row) in body[start..<min(start + rowsPerPage, body.count)].enumerated() {
                y -= rowHeight
                if offset.isMultiple(of: 2) == false {
                    context.setFillColor(CGColor(gray: 0.97, alpha: 1))
                    context.fill(CGRect(x: usable.minX, y: y, width: widths.reduce(0, +), height: rowHeight))
                }
                draw(row, at: y, widths: widths, x: usable.minX, rowHeight: rowHeight, font: regular, in: context)
            }
            context.endPDFPage()
            start += rowsPerPage
        } while start < body.count
        context.closePDF()
    }

    private static func draw(_ row: [String], at y: CGFloat, widths: [CGFloat], x: CGFloat, rowHeight: CGFloat, font: CTFont, in context: CGContext) {
        var x = x
        for (column, width) in widths.enumerated() {
            let text = row.indices.contains(column) ? row[column].replacingOccurrences(of: "\n", with: " ") : ""
            let attributed = NSAttributedString(string: text, attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 0.1, alpha: 1),
            ])
            let line = CTLineCreateWithAttributedString(attributed)
            let ellipsis = CTLineCreateWithAttributedString(NSAttributedString(string: "…", attributes: [NSAttributedString.Key(kCTFontAttributeName as String): font]))
            let fitted = CTLineCreateTruncatedLine(line, Double(width - 8), .end, ellipsis) ?? line
            context.textPosition = CGPoint(x: x + 4, y: y + rowHeight * 0.32)
            CTLineDraw(fitted, context)
            x += width
        }
    }
}

// MARK: Conversions

/// CSV, TSV and Excel files. ODS and XLS need LibreOffice (see OfficeConversion).
enum SpreadsheetConversion {
    static func convert(_ source: URL, to format: OutputFormat, tools: ExternalTools, rowLimit: Int = ExcelLimit.rows) async throws -> [URL] {
        let ext = source.pathExtension.lowercased()
        if format == .xlsx, ["csv", "tsv"].contains(ext) {
            try ExcelLimit.check(rows: try DelimitedText.recordCount(source, delimiter: ext == "tsv" ? "\t" : ","), limit: rowLimit)
        }
        // Formats only LibreOffice reads or writes.
        if format == .ods || !["csv", "tsv", "xlsx"].contains(ext) {
            return try await OfficeConversion.convert(source, to: format, tools: tools)
        }
        guard let pathExtension = format.fileExtension else { throw ConversionError.unsupported("spreadsheet to \(format.title)") }
        let table = try read(source)
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        switch format {
        case .csv:
            try DelimitedText.write(table, delimiter: ",").write(to: destination, atomically: true, encoding: .utf8)
        case .tsv:
            try DelimitedText.write(table, delimiter: "\t").write(to: destination, atomically: true, encoding: .utf8)
        case .xlsx:
            try XlsxWriter.write(sheets: [(name: source.deletingPathExtension().lastPathComponent, table: table)], to: destination, rowLimit: rowLimit)
        case .pdf:
            try TablePDF.write(table, to: destination)
        default:
            throw ConversionError.unsupported("spreadsheet to \(format.title)")
        }
        return [destination]
    }

    static func read(_ url: URL) throws -> Table {
        switch url.pathExtension.lowercased() {
        case "csv": return try DelimitedText.read(url, delimiter: ",")
        case "tsv": return try DelimitedText.read(url, delimiter: "\t")
        case "xlsx": return try XlsxReader.firstSheet(of: url)
        default: throw ConversionError.unsupported("reading \(url.pathExtension.uppercased())")
        }
    }
}
