import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Writes a Markdown document as a Word document with Word's own styles:
/// headings that appear in the navigation pane, real numbered and bulleted
/// lists, tables, footnotes, links and pictures. The system's `.officeOpenXML`
/// exporter keeps none of that, so the package is assembled here.
struct MarkdownDocx {
    /// The Markdown file's folder, where relative image paths start.
    var baseURL: URL?

    private var documentRelationships: [String] = []
    private var footnoteRelationships: [String] = []
    private var nextRelationship = 10
    private var numberingInstances: [String] = []
    private var nextNumberingID = 2
    private var footnoteParts: [String] = []
    private var media: [(name: String, data: Data)] = []
    private var nextDrawingID = 1
    private var nextBookmarkID = 0
    private var inFootnote = false
    /// Runs put at the start of the next paragraph: a footnote's own number.
    private var pendingPrefix: String?

    /// Usable width of a US Letter page with 1 in margins: 6.5 in.
    private static let textWidthTwips = 9360
    private static let textWidthEMU = 5_943_600
    private static let bulletNumberingID = 1

    static func write(_ document: MarkdownDocument, baseURL: URL?, to url: URL) throws {
        var writer = MarkdownDocx(baseURL: baseURL)
        try writer.write(document, to: url)
    }

    private init(baseURL: URL?) {
        self.baseURL = baseURL
    }

    private struct Context {
        /// Left indentation, in twentieths of a point.
        var indent = 0
        /// The paragraph style for body text here: nil for Normal.
        var style: String?
        var listLevel = -1
    }

    private mutating func write(_ document: MarkdownDocument, to url: URL) throws {
        var body: [String] = []
        if let title = document.title {
            body.append(#"<w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr>\#(run(title, RunFormat()))</w:p>"#)
        }
        body += blocks(document.blocks, Context())

        inFootnote = true
        for (index, footnote) in document.footnotes.enumerated() {
            let number = index + 1
            pendingPrefix = #"<w:r><w:rPr><w:rStyle w:val="FootnoteReference"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space="preserve"> </w:t></w:r>"#
            var paragraphs = blocks(footnote, Context(style: "FootnoteText"))
            if let prefix = pendingPrefix {
                paragraphs.insert(#"<w:p><w:pPr><w:pStyle w:val="FootnoteText"/></w:pPr>\#(prefix)</w:p>"#, at: 0)
                pendingPrefix = nil
            }
            footnoteParts.append(#"<w:footnote w:id="\#(number)">\#(paragraphs.joined())</w:footnote>"#)
        }
        inFootnote = false

        let root = try PackageZip.makeWorkspace("docx")
        defer { try? FileManager.default.removeItem(at: root) }
        let hasFootnotes = !footnoteParts.isEmpty
        try PackageZip.write(contentTypes(hasFootnotes: hasFootnotes), to: "[Content_Types].xml", in: root)
        try PackageZip.write(Self.packageRelationships, to: "_rels/.rels", in: root)
        try PackageZip.write(Self.core(title: document.title ?? url.deletingPathExtension().lastPathComponent), to: "docProps/core.xml", in: root)
        try PackageZip.write(Self.relationships(documentPartRelationships(hasFootnotes: hasFootnotes)), to: "word/_rels/document.xml.rels", in: root)
        try PackageZip.write(Self.document(body.joined()), to: "word/document.xml", in: root)
        try PackageZip.write(Self.styles, to: "word/styles.xml", in: root)
        try PackageZip.write(Self.numbering(instances: numberingInstances), to: "word/numbering.xml", in: root)
        try PackageZip.write(Self.settings(hasFootnotes: hasFootnotes), to: "word/settings.xml", in: root)
        if hasFootnotes {
            try PackageZip.write(Self.footnotes(footnoteParts), to: "word/footnotes.xml", in: root)
            try PackageZip.write(Self.relationships(footnoteRelationships), to: "word/_rels/footnotes.xml.rels", in: root)
        }
        for item in media {
            try PackageZip.write(item.data, to: "word/media/\(item.name)", in: root)
        }
        try PackageZip.zip(root, to: url)
    }

    // MARK: Blocks

    private mutating func blocks(_ blocks: [MarkdownBlock<MarkdownText>], _ context: Context) -> [String] {
        blocks.flatMap { block($0, context) }
    }

    private mutating func block(_ block: MarkdownBlock<MarkdownText>, _ context: Context) -> [String] {
        switch block {
        case .heading(let level, let text, let id):
            let bookmark = nextBookmarkID
            nextBookmarkID += 1
            let name = String(id.prefix(40)).xmlEscaped
            let content = #"<w:bookmarkStart w:id="\#(bookmark)" w:name="\#(name)"/>\#(runs(text, RunFormat()))<w:bookmarkEnd w:id="\#(bookmark)"/>"#
            return [paragraph(content, context, style: "Heading\(min(max(level, 1), 6))")]
        case .paragraph(let text):
            if let figure = MarkdownInline.figureImage(text) {
                let picture = image(figure.source, alt: figure.alt)
                var paragraphs = [paragraph(picture, context, style: "Figure")]
                if !figure.alt.isEmpty {
                    paragraphs.append(paragraph(runs(figure.alt, RunFormat()), context, style: "ImageCaption"))
                }
                return paragraphs
            }
            return [paragraph(runs(text, RunFormat()), context)]
        case .code(let code, _):
            let content = run(code.replacingOccurrences(of: "\t", with: "    "), RunFormat(style: "VerbatimChar"))
            return [paragraph(content, context, style: "SourceCode")]
        case .math(let tex):
            return [paragraph(run(MarkdownMath.unicode(tex), RunFormat(italic: true, font: "Cambria Math")), context, justification: "center")]
        case .quote(let content):
            var inside = context
            inside.indent += 360
            inside.style = "BlockText"
            return blocks(content, inside)
        case .list(let list):
            return self.list(list, context)
        case .table(let table):
            return self.table(table, context)
        case .definitions(let items):
            var result: [String] = []
            for item in items {
                result.append(paragraph(runs(item.term, RunFormat()), context, style: "DefinitionTerm"))
                for definition in item.definitions {
                    var inside = context
                    inside.indent += 720
                    inside.style = definition.count == 1 ? "Compact" : context.style
                    result += blocks(definition, inside)
                }
            }
            return result
        case .lines(let lines):
            let content = lines.map { runs($0, RunFormat()) }.joined(separator: "<w:r><w:br/></w:r>")
            return [paragraph(content, context)]
        case .rule:
            return [paragraph("", context, style: "HorizontalRule")]
        }
    }

    private mutating func list(_ list: MarkdownList<MarkdownText>, _ context: Context) -> [String] {
        let level = min(context.listLevel + 1, 8)
        let numberingID: Int
        if list.ordered {
            // Each numbered list counts from its own start.
            numberingID = nextNumberingID
            nextNumberingID += 1
            numberingInstances.append(
                #"<w:num w:numId="\#(numberingID)"><w:abstractNumId w:val="2"/><w:lvlOverride w:ilvl="\#(level)"><w:startOverride w:val="\#(list.start)"/></w:lvlOverride></w:num>"#
            )
        } else {
            numberingID = Self.bulletNumberingID
        }
        var inside = context
        inside.listLevel = level
        inside.indent = context.indent + 360
        if list.tight { inside.style = "Compact" } else if inside.style == "Compact" { inside.style = nil }

        var result: [String] = []
        for item in list.items {
            var rest = item[...]
            if case .paragraph(let text) = item.first, MarkdownInline.figureImage(text) == nil {
                result.append(paragraph(runs(text, RunFormat()), inside, numbering: (numberingID, level)))
                rest = item.dropFirst()
            } else {
                result.append(paragraph("", inside, numbering: (numberingID, level)))
            }
            result += blocks(Array(rest), inside)
        }
        return result
    }

    private mutating func table(_ table: MarkdownTable<MarkdownText>, _ context: Context) -> [String] {
        var result: [String] = []
        if let caption = table.caption {
            result.append(paragraph(runs(caption, RunFormat()), context, style: "TableCaption"))
        }
        let columns = max(table.columnCount, 1)
        let available = Self.textWidthTwips - context.indent
        let widths: [Int] = (table.widths ?? Array(repeating: 1 / Double(columns), count: columns)).map { Int(Double(available) * $0) }
        let width = table.widths == nil ? #"<w:tblW w:w="0" w:type="auto"/><w:jc w:val="center"/>"# : #"<w:tblW w:w="\#(available)" w:type="dxa"/>"#
        let indent = context.indent > 0 ? #"<w:tblInd w:w="\#(context.indent)" w:type="dxa"/>"# : ""
        var xml = #"<w:tbl><w:tblPr><w:tblStyle w:val="Table"/>\#(width)\#(indent)<w:tblLook w:val="0020" w:firstRow="1" w:lastRow="0" w:firstColumn="0" w:lastColumn="0" w:noHBand="0" w:noVBand="0"/></w:tblPr><w:tblGrid>"#
        xml += widths.map { #"<w:gridCol w:w="\#($0)"/>"# }.joined() + "</w:tblGrid>"

        var rows: [(cells: [MarkdownText], isHeader: Bool)] = []
        if let header = table.header, header.contains(where: { !$0.isEmpty }) {
            rows.append((header, true))
        }
        rows += table.rows.map { ($0, false) }
        var cellContext = Context(style: "Compact")
        cellContext.listLevel = context.listLevel
        for row in rows {
            xml += row.isHeader ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>"
            for column in 0..<table.columnCount {
                let border = row.isHeader ? #"<w:tcBorders><w:bottom w:val="single" w:sz="6" w:space="0" w:color="auto"/></w:tcBorders>"# : ""
                let cellWidth = table.widths == nil ? #"<w:tcW w:w="0" w:type="auto"/>"# : #"<w:tcW w:w="\#(widths[column])" w:type="dxa"/>"#
                let content = column < row.cells.count ? runs(row.cells[column], RunFormat(bold: row.isHeader)) : ""
                let alignment: String? = switch table.alignments[column] {
                case .none, .left: nil
                case .center: "center"
                case .right: "right"
                }
                xml += "<w:tc><w:tcPr>\(cellWidth)\(border)</w:tcPr>\(paragraph(content, cellContext, justification: alignment))</w:tc>"
            }
            xml += "</w:tr>"
        }
        xml += "</w:tbl>"
        result.append(xml)
        // Word has no space below a table; a small empty paragraph makes some.
        result.append(#"<w:p><w:pPr><w:spacing w:before="0" w:after="80"/><w:rPr><w:sz w:val="12"/></w:rPr></w:pPr></w:p>"#)
        return result
    }

    private mutating func paragraph(
        _ content: String,
        _ context: Context,
        style: String? = nil,
        numbering: (id: Int, level: Int)? = nil,
        justification: String? = nil
    ) -> String {
        var properties = ""
        if let style = style ?? context.style {
            properties += #"<w:pStyle w:val="\#(style)"/>"#
        }
        if let numbering {
            properties += #"<w:numPr><w:ilvl w:val="\#(numbering.level)"/><w:numId w:val="\#(numbering.id)"/></w:numPr>"#
        }
        if numbering != nil {
            properties += #"<w:ind w:left="\#(context.indent)" w:hanging="360"/>"#
        } else if context.indent > 0 {
            properties += #"<w:ind w:left="\#(context.indent)"/>"#
        }
        if let justification {
            properties += #"<w:jc w:val="\#(justification)"/>"#
        }
        var content = content
        if let prefix = pendingPrefix {
            content = prefix + content
            pendingPrefix = nil
        }
        return "<w:p>\(properties.isEmpty ? "" : "<w:pPr>\(properties)</w:pPr>")\(content)</w:p>"
    }

    // MARK: Inline content

    private struct RunFormat {
        var bold = false
        var italic = false
        var strike = false
        var style: String?
        var font: String?
    }

    private mutating func runs(_ text: MarkdownText, _ format: RunFormat) -> String {
        text.map { inline($0, format) }.joined()
    }

    private mutating func inline(_ inline: MarkdownInline, _ format: RunFormat) -> String {
        switch inline {
        case .text(let string):
            return run(string, format)
        case .emphasis(let content):
            var inner = format
            inner.italic = true
            return runs(content, inner)
        case .strong(let content):
            var inner = format
            inner.bold = true
            return runs(content, inner)
        case .strikethrough(let content):
            var inner = format
            inner.strike = true
            return runs(content, inner)
        case .code(let code):
            var inner = format
            inner.style = "VerbatimChar"
            return run(code, inner)
        case .math(let tex):
            var inner = format
            inner.italic = true
            inner.font = "Cambria Math"
            return run(MarkdownMath.unicode(tex), inner)
        case .link(let url, _, let content):
            var inner = format
            if format.style == nil { inner.style = "Hyperlink" }
            let text = runs(content, inner)
            if url.hasPrefix("#") {
                return #"<w:hyperlink w:anchor="\#(String(url.dropFirst().prefix(40)).xmlEscaped)">\#(text)</w:hyperlink>"#
            }
            guard MarkdownHTML.isSafe(url) else { return runs(content, format) }
            let id = relationship(type: "hyperlink", target: url, external: true)
            return #"<w:hyperlink r:id="\#(id)" w:history="1">\#(text)</w:hyperlink>"#
        case .image(let source, _, let alt):
            return image(source, alt: alt)
        case .footnote(let number):
            return #"<w:r><w:rPr><w:rStyle w:val="FootnoteReference"/></w:rPr><w:footnoteReference w:id="\#(number)"/></w:r>"#
        case .lineBreak:
            return "<w:r><w:br/></w:r>"
        case .softBreak:
            return run(" ", format)
        }
    }

    private func run(_ text: String, _ format: RunFormat) -> String {
        var properties = ""
        if let style = format.style { properties += #"<w:rStyle w:val="\#(style)"/>"# }
        if let font = format.font { properties += #"<w:rFonts w:ascii="\#(font)" w:hAnsi="\#(font)" w:cs="\#(font)"/>"# }
        if format.bold { properties += "<w:b/><w:bCs/>" }
        if format.italic { properties += "<w:i/><w:iCs/>" }
        if format.strike { properties += "<w:strike/>" }
        let lines = text.components(separatedBy: "\n").map { line in
            line.components(separatedBy: "\t")
                .map { #"<w:t xml:space="preserve">\#($0.xmlEscaped)</w:t>"# }
                .joined(separator: "<w:tab/>")
        }
        let content = lines.joined(separator: "<w:br/>")
        return "<w:r>\(properties.isEmpty ? "" : "<w:rPr>\(properties)</w:rPr>")\(content)</w:r>"
    }

    /// The picture embedded in the document, or its description when it isn't
    /// on this Mac (pictures on the web are never downloaded).
    private mutating func image(_ source: String, alt: MarkdownText) -> String {
        let description = MarkdownInline.plainText(alt)
        guard !inFootnote, let url = MarkdownImages.localURL(for: source, relativeTo: baseURL),
              let picture = Self.embeddable(url)
        else {
            return run("[\(description.isEmpty ? "image" : description)]", RunFormat(italic: true))
        }
        let number = media.count + 1
        let name = "image\(number).\(picture.fileExtension)"
        media.append((name, picture.data))
        let id = relationship(type: "image", target: "media/\(name)", external: false)
        let scale = min(1, Double(Self.textWidthEMU) / picture.width, 7_315_200 / picture.height)
        let width = Int(picture.width * scale)
        let height = Int(picture.height * scale)
        let drawing = nextDrawingID
        nextDrawingID += 1
        let name1 = description.xmlEscaped
        return #"<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="\#(width)" cy="\#(height)"/><wp:docPr id="\#(drawing)" name="Picture \#(drawing)" descr="\#(name1)"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="\#(drawing)" name="\#(name)"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="\#(id)"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="\#(width)" cy="\#(height)"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"#
    }

    /// PNG, JPEG and GIF go in as they are; anything else ImageIO can read becomes PNG.
    /// Sizes are in EMU, at the picture's own resolution.
    private static func embeddable(_ url: URL) -> (data: Data, fileExtension: String, width: Double, height: Double)? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let pixelWidth = properties[kCGImagePropertyPixelWidth] as? Double,
              let pixelHeight = properties[kCGImagePropertyPixelHeight] as? Double,
              pixelWidth > 0, pixelHeight > 0
        else { return nil }
        let dpi = (properties[kCGImagePropertyDPIWidth] as? Double).flatMap { $0 > 0 ? $0 : nil } ?? 96
        let emuPerPixel = 914_400 / dpi
        let type = CGImageSourceGetType(source).map { $0 as String }
        let width = pixelWidth * emuPerPixel
        let height = pixelHeight * emuPerPixel
        switch type {
        case UTType.png.identifier?, UTType.jpeg.identifier?, UTType.gif.identifier?:
            guard let data = try? Data(contentsOf: url) else { return nil }
            let fileExtension = type == UTType.png.identifier ? "png" : type == UTType.jpeg.identifier ? "jpeg" : "gif"
            return (data, fileExtension, width, height)
        default:
            guard let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
            let data = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { return nil }
            return (data as Data, "png", width, height)
        }
    }

    /// Adds a relationship to the part being written (the document or its footnotes).
    private mutating func relationship(type: String, target: String, external: Bool) -> String {
        let id = "rId\(nextRelationship)"
        nextRelationship += 1
        let mode = external ? #" TargetMode="External""# : ""
        let xml = #"<Relationship Id="\#(id)" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/\#(type)" Target="\#(target.xmlEscaped)"\#(mode)/>"#
        if inFootnote {
            footnoteRelationships.append(xml)
        } else {
            documentRelationships.append(xml)
        }
        return id
    }

    // MARK: Package parts

    private func contentTypes(hasFootnotes: Bool) -> String {
        let footnotes = hasFootnotes
            ? #"<Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/>"#
            : ""
        return #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Default Extension="jpeg" ContentType="image/jpeg"/><Default Extension="gif" ContentType="image/gif"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/><Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>\#(footnotes)<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>"#
    }

    private func documentPartRelationships(hasFootnotes: Bool) -> [String] {
        let base = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/"
        var fixed = [
            #"<Relationship Id="rId1" Type="\#(base)styles" Target="styles.xml"/>"#,
            #"<Relationship Id="rId2" Type="\#(base)numbering" Target="numbering.xml"/>"#,
            #"<Relationship Id="rId3" Type="\#(base)settings" Target="settings.xml"/>"#,
        ]
        if hasFootnotes {
            fixed.append(#"<Relationship Id="rId4" Type="\#(base)footnotes" Target="footnotes.xml"/>"#)
        }
        return fixed + documentRelationships
    }

    private static func relationships(_ entries: [String]) -> String {
        #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">\#(entries.joined())</Relationships>"#
    }

    private static let packageRelationships = #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>"#

    private static func core(title: String) -> String {
        #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>\#(title.xmlEscaped)</dc:title></cp:coreProperties>"#
    }

    private static func document(_ body: String) -> String {
        #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>\#(body)<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr></w:body></w:document>"#
    }

    private static func footnotes(_ notes: [String]) -> String {
        let separators = #"<w:footnote w:type="separator" w:id="-1"><w:p><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>"#
        return #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">\#(separators)\#(notes.joined())</w:footnotes>"#
    }

    private static func settings(hasFootnotes: Bool) -> String {
        let footnotes = hasFootnotes ? #"<w:footnotePr><w:footnote w:id="-1"/><w:footnote w:id="0"/></w:footnotePr>"# : ""
        return #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:defaultTabStop w:val="720"/><w:characterSpacingControl w:val="doNotCompress"/>\#(footnotes)<w:compat><w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="15"/></w:compat></w:settings>"#
    }

    /// Bullets (abstract list 1, instance 1) and numbers (abstract list 2, one
    /// instance per list so each starts afresh), nine levels deep.
    private static func numbering(instances: [String]) -> String {
        let bullets = ["•", "◦", "▪"]
        func level(_ index: Int, format: String, text: String) -> String {
            #"<w:lvl w:ilvl="\#(index)"><w:start w:val="1"/><w:numFmt w:val="\#(format)"/><w:lvlText w:val="\#(text)"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="\#(360 * (index + 1))" w:hanging="360"/></w:pPr></w:lvl>"#
        }
        let bulletLevels = (0..<9).map { level($0, format: "bullet", text: bullets[$0 % 3]) }.joined()
        let numberLevels = (0..<9).map { level($0, format: "decimal", text: "%\($0 + 1).") }.joined()
        return #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="hybridMultilevel"/>\#(bulletLevels)</w:abstractNum><w:abstractNum w:abstractNumId="2"><w:multiLevelType w:val="hybridMultilevel"/>\#(numberLevels)</w:abstractNum><w:num w:numId="\#(bulletNumberingID)"><w:abstractNumId w:val="1"/></w:num>\#(instances.joined())</w:numbering>"#
    }

    private static let styles: String = {
        func paragraphStyle(_ id: String, name: String, basedOn: String? = "Normal", next: String? = nil, pPr: String = "", rPr: String = "", primary: Bool = true) -> String {
            let based = basedOn.map { #"<w:basedOn w:val="\#($0)"/>"# } ?? ""
            let nextStyle = next.map { #"<w:next w:val="\#($0)"/>"# } ?? ""
            let quick = primary ? "<w:qFormat/>" : ""
            let paragraph = pPr.isEmpty ? "" : "<w:pPr>\(pPr)</w:pPr>"
            let run = rPr.isEmpty ? "" : "<w:rPr>\(rPr)</w:rPr>"
            return #"<w:style w:type="paragraph" w:styleId="\#(id)"><w:name w:val="\#(name)"/>\#(based)\#(nextStyle)\#(quick)\#(paragraph)\#(run)</w:style>"#
        }
        let headingSizes = [36, 30, 26, 24, 22, 22]
        let headingSpaceBefore = [360, 300, 240, 200, 200, 200]
        let headings = (1...6).map { level in
            paragraphStyle(
                "Heading\(level)",
                name: "heading \(level)",
                next: "Normal",
                pPr: #"<w:keepNext/><w:keepLines/><w:spacing w:before="\#(headingSpaceBefore[level - 1])" w:after="120"/><w:outlineLvl w:val="\#(level - 1)"/>"#,
                rPr: #"<w:b/><w:bCs/>\#(level == 6 ? "<w:i/><w:iCs/>" : "")<w:color w:val="1D1D1F"/><w:sz w:val="\#(headingSizes[level - 1])"/><w:szCs w:val="\#(headingSizes[level - 1])"/>"#
            )
        }.joined()
        // Courier New comes with both macOS and Windows.
        let code = #"<w:rFonts w:ascii="Courier New" w:hAnsi="Courier New" w:cs="Courier New"/>"#
        let styles = [
            #"<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>"#,
            #"<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>"#,
            #"<w:style w:type="character" w:default="1" w:styleId="DefaultParagraphFont"><w:name w:val="Default Paragraph Font"/><w:uiPriority w:val="1"/><w:semiHidden/><w:unhideWhenUsed/></w:style>"#,
            #"<w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:uiPriority w:val="99"/><w:semiHidden/><w:unhideWhenUsed/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>"#,
            paragraphStyle("Title", name: "Title", next: "Normal", pPr: #"<w:spacing w:after="300"/><w:jc w:val="center"/>"#, rPr: #"<w:b/><w:bCs/><w:sz w:val="48"/><w:szCs w:val="48"/>"#),
            headings,
            paragraphStyle("Compact", name: "Compact", pPr: #"<w:spacing w:before="36" w:after="36"/>"#),
            paragraphStyle("BlockText", name: "Block Text", pPr: #"<w:pBdr><w:left w:val="single" w:sz="18" w:space="8" w:color="D2D2D7"/></w:pBdr><w:spacing w:before="60" w:after="120"/>"#, rPr: #"<w:color w:val="595959"/>"#),
            paragraphStyle("SourceCode", name: "Source Code", pPr: #"<w:shd w:val="clear" w:color="auto" w:fill="F4F4F5"/><w:spacing w:before="60" w:after="160" w:line="240" w:lineRule="auto"/>"#, rPr: "\(code)<w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/>"),
            #"<w:style w:type="character" w:styleId="VerbatimChar"><w:name w:val="Verbatim Char"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr>\#(code)<w:sz w:val="20"/><w:szCs w:val="20"/><w:shd w:val="clear" w:color="auto" w:fill="F4F4F5"/></w:rPr></w:style>"#,
            #"<w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:basedOn w:val="DefaultParagraphFont"/><w:uiPriority w:val="99"/><w:unhideWhenUsed/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>"#,
            paragraphStyle("FootnoteText", name: "footnote text", pPr: #"<w:spacing w:after="60" w:line="240" w:lineRule="auto"/>"#, rPr: #"<w:sz w:val="18"/><w:szCs w:val="18"/>"#, primary: false),
            #"<w:style w:type="character" w:styleId="FootnoteReference"><w:name w:val="footnote reference"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr><w:vertAlign w:val="superscript"/></w:rPr></w:style>"#,
            paragraphStyle("TableCaption", name: "Table Caption", pPr: #"<w:keepNext/><w:spacing w:before="120" w:after="80"/><w:jc w:val="center"/>"#, rPr: #"<w:i/><w:iCs/><w:color w:val="595959"/>"#),
            paragraphStyle("Figure", name: "Figure", pPr: #"<w:keepNext/><w:spacing w:after="80"/><w:jc w:val="center"/>"#),
            paragraphStyle("ImageCaption", name: "Image Caption", pPr: #"<w:jc w:val="center"/>"#, rPr: #"<w:i/><w:iCs/><w:color w:val="595959"/>"#),
            paragraphStyle("DefinitionTerm", name: "Definition Term", next: "Normal", pPr: #"<w:keepNext/><w:spacing w:after="40"/>"#, rPr: "<w:b/><w:bCs/>"),
            paragraphStyle("HorizontalRule", name: "Horizontal Rule", pPr: #"<w:pBdr><w:bottom w:val="single" w:sz="6" w:space="1" w:color="BFBFBF"/></w:pBdr><w:spacing w:before="120" w:after="240"/>"#, rPr: #"<w:sz w:val="8"/><w:szCs w:val="8"/>"#),
            #"<w:style w:type="table" w:styleId="Table"><w:name w:val="Table"/><w:basedOn w:val="TableNormal"/><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="8" w:space="0" w:color="auto"/><w:bottom w:val="single" w:sz="8" w:space="0" w:color="auto"/></w:tblBorders><w:tblCellMar><w:top w:w="40" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="40" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>"#,
        ]
        return #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">\#(styles.joined())</w:styles>"#
    }()
}
