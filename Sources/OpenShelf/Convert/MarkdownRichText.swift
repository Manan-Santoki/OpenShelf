import AppKit

/// Lays a Markdown document out as rich text: headings, lists, boxed code,
/// quotes, tables and pictures, ready to print as a PDF or save as RTF.
///
/// Colours are fixed rather than dynamic, so a PDF printed in Dark Mode still
/// has black text on white paper.
struct MarkdownRichText {
    /// The Markdown file's folder, where relative image paths start.
    var baseURL: URL?
    /// The width text is laid out in, which pictures are scaled to fit.
    var width: CGFloat
    private let output = NSMutableAttributedString()

    static func attributedString(_ document: MarkdownDocument, baseURL: URL?, width: CGFloat) -> NSAttributedString {
        var renderer = MarkdownRichText(baseURL: baseURL, width: width)
        return renderer.render(document)
    }

    init(baseURL: URL?, width: CGFloat) {
        self.baseURL = baseURL
        self.width = width
    }

    /// What surrounds the block being written: its indentation, the boxes
    /// (quotes, code, table cells) and lists it sits in, and its type size.
    private struct Context {
        /// The width of the column the block sits in, before its indentation.
        var width: CGFloat
        var indent: CGFloat = 0
        var textBlocks: [NSTextBlock] = []
        var lists: [NSTextList] = []
        var size: CGFloat = Style.bodySize
        var color: NSColor = Style.text
        var spacingAfter: CGFloat = 8
        var alignment: NSTextAlignment = .natural
        var tight = false
    }

    private enum Style {
        static let bodySize: CGFloat = 11
        static let codeSize: CGFloat = 9
        static let headingSizes: [CGFloat] = [22, 17, 14, 12, 11, 11]
        static let text = NSColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1)
        static let muted = NSColor(srgbRed: 0.40, green: 0.40, blue: 0.43, alpha: 1)
        static let rule = NSColor(srgbRed: 0.80, green: 0.80, blue: 0.82, alpha: 1)
        static let codeBackground = NSColor(srgbRed: 0.95, green: 0.95, blue: 0.96, alpha: 1)
        static let headerBackground = NSColor(srgbRed: 0.97, green: 0.97, blue: 0.98, alpha: 1)
        static let link = NSColor(srgbRed: 0.0, green: 0.36, blue: 0.75, alpha: 1)
        static let bullets = ["•", "◦", "▪"]
    }

    private mutating func render(_ document: MarkdownDocument) -> NSAttributedString {
        let context = Context(width: width)
        if let title = document.title {
            var titleContext = context
            titleContext.alignment = .center
            appendParagraph(
                NSAttributedString(string: title, attributes: attributes(size: 24, bold: true, color: Style.text)),
                style: paragraphStyle(titleContext, after: 18)
            )
        }
        blocks(document.blocks, context)
        if !document.footnotes.isEmpty {
            rule(context, short: true)
            var notes = context
            notes.size = 9
            notes.color = Style.muted
            list(MarkdownList(ordered: true, items: document.footnotes), notes)
        }
        // The last paragraph's newline would add an empty line at the end.
        if output.string.hasSuffix("\n") {
            output.deleteCharacters(in: NSRange(location: output.length - 1, length: 1))
        }
        return output
    }

    // MARK: Blocks

    private mutating func blocks(_ blocks: [MarkdownBlock<MarkdownText>], _ context: Context) {
        for block in blocks {
            self.block(block, context)
        }
    }

    private mutating func block(_ block: MarkdownBlock<MarkdownText>, _ context: Context) {
        switch block {
        case .heading(let level, let text, _):
            let size = Style.headingSizes[min(max(level, 1), 6) - 1] * context.size / Style.bodySize
            var heading = context
            heading.size = size
            appendParagraph(
                inlines(text, heading, bold: true, italic: level == 6),
                style: paragraphStyle(context, before: level <= 2 ? 14 : 10, after: 6)
            )
        case .paragraph(let text):
            if let figure = MarkdownInline.figureImage(text) {
                var centered = context
                centered.alignment = .center
                let picture = NSMutableAttributedString(attributedString: image(figure.source, alt: figure.alt, centered))
                let caption = MarkdownInline.plainText(figure.alt)
                if !caption.isEmpty, picture.containsAttachments {
                    var captionContext = centered
                    captionContext.size = context.size * 0.9
                    captionContext.color = Style.muted
                    appendParagraph(picture, style: paragraphStyle(centered, after: 4))
                    appendParagraph(inlines(figure.alt, captionContext, italic: true), style: paragraphStyle(centered))
                } else {
                    appendParagraph(picture, style: paragraphStyle(centered))
                }
            } else {
                appendParagraph(inlines(text, context), style: paragraphStyle(context, after: context.tight ? 2 : nil))
            }
        case .code(let code, _):
            let box = NSTextBlock()
            box.backgroundColor = Style.codeBackground
            box.setWidth(8, type: .absoluteValueType, for: .padding)
            box.setWidth(context.indent, type: .absoluteValueType, for: .margin, edge: .minX)
            box.setWidth(2, type: .absoluteValueType, for: .margin, edge: .minY)
            box.setWidth(context.tight ? 4 : 10, type: .absoluteValueType, for: .margin, edge: .maxY)
            var inside = context
            inside.width = fit(box, in: context)
            inside.indent = 0
            inside.textBlocks.append(box)
            let text = code.replacingOccurrences(of: "\t", with: "    ")
            let style = NSMutableParagraphStyle()
            style.setParagraphStyle(paragraphStyle(inside, after: 0))
            style.lineHeightMultiple = 1.05
            appendParagraph(
                NSAttributedString(string: text.isEmpty ? " " : text, attributes: attributes(size: Style.codeSize * context.size / Style.bodySize, monospaced: true, color: Style.text)),
                style: style
            )
        case .math(let tex):
            var centered = context
            centered.alignment = .center
            appendParagraph(mathText(tex, size: context.size * 1.15, color: context.color), style: paragraphStyle(centered, before: 2, after: 10))
        case .quote(let content):
            let box = NSTextBlock()
            box.setWidth(3, type: .absoluteValueType, for: .border, edge: .minX)
            box.setBorderColor(Style.rule, for: .minX)
            box.setWidth(12, type: .absoluteValueType, for: .padding, edge: .minX)
            box.setWidth(context.indent, type: .absoluteValueType, for: .margin, edge: .minX)
            box.setWidth(context.tight ? 2 : 8, type: .absoluteValueType, for: .margin, edge: .maxY)
            var inside = context
            inside.width = fit(box, in: context)
            inside.indent = 0
            inside.textBlocks.append(box)
            inside.color = Style.muted
            inside.tight = false
            blocks(content, inside)
            setSpacingAfterLastParagraph(0)
        case .list(let list):
            self.list(list, context)
        case .table(let table):
            self.table(table, context)
        case .definitions(let items):
            for item in items {
                appendParagraph(inlines(item.term, context, bold: true), style: paragraphStyle(context, after: 2))
                for definition in item.definitions {
                    var inside = context
                    inside.indent += 24
                    inside.tight = definition.count == 1
                    blocks(definition, inside)
                }
                setSpacingAfterLastParagraph(6)
            }
            setSpacingAfterLastParagraph(context.spacingAfter)
        case .lines(let lines):
            let text = NSMutableAttributedString()
            for (index, line) in lines.enumerated() {
                if index > 0 { text.append(NSAttributedString(string: "\u{2028}", attributes: attributes(size: context.size, color: context.color))) }
                text.append(inlines(line, context))
            }
            appendParagraph(text, style: paragraphStyle(context))
        case .rule:
            rule(context, short: false)
        }
    }

    private mutating func list(_ list: MarkdownList<MarkdownText>, _ context: Context) {
        let depth = context.lists.count
        let format: NSTextList.MarkerFormat = list.ordered ? .decimal : [.disc, .circle, .square][depth % 3]
        let textList = NSTextList(markerFormat: format, options: 0)
        textList.startingItemNumber = list.start
        let widestNumber = "\(list.start + max(list.items.count - 1, 0))."
        let markerWidth: CGFloat = list.ordered ? max(16, CGFloat(widestNumber.count) * context.size * 0.6) : 12
        let textIndent = context.indent + markerWidth + 8

        var inside = context
        inside.indent = textIndent
        inside.lists.append(textList)
        inside.tight = list.tight
        inside.spacingAfter = list.tight ? 2 : 6

        for (index, item) in list.items.enumerated() {
            let marker = list.ordered ? "\(list.start + index)." : Style.bullets[depth % Style.bullets.count]
            let style = NSMutableParagraphStyle()
            style.setParagraphStyle(paragraphStyle(inside, after: inside.spacingAfter))
            style.firstLineHeadIndent = context.indent
            style.tabStops = [
                NSTextTab(textAlignment: .right, location: context.indent + markerWidth),
                NSTextTab(textAlignment: .left, location: textIndent),
            ]
            // The marker is part of the text, between tabs, as Cocoa writes lists.
            let line = NSMutableAttributedString(string: "\t\(marker)\t", attributes: attributes(size: context.size, color: context.color))
            var rest = item[...]
            if case .paragraph(let text) = item.first, MarkdownInline.figureImage(text) == nil {
                line.append(inlines(text, inside))
                rest = item.dropFirst()
            }
            appendParagraph(line, style: style)
            blocks(Array(rest), inside)
        }
        setSpacingAfterLastParagraph(context.tight ? 2 : context.spacingAfter)
    }

    private mutating func table(_ table: MarkdownTable<MarkdownText>, _ context: Context) {
        let size = context.size * 0.95
        if let caption = table.caption {
            var centered = context
            centered.alignment = .center
            centered.size = size
            centered.color = Style.muted
            appendParagraph(inlines(caption, centered, italic: true), style: paragraphStyle(centered, before: 4, after: 4))
        }
        let textTable = NSTextTable()
        textTable.numberOfColumns = table.columnCount
        textTable.layoutAlgorithm = .automaticLayoutAlgorithm
        textTable.collapsesBorders = true
        textTable.hidesEmptyCells = false
        textTable.setWidth(context.indent, type: .absoluteValueType, for: .margin, edge: .minX)
        if table.widths != nil {
            textTable.setContentWidth(100, type: .percentageValueType)
        }

        var rows: [(cells: [MarkdownText], isHeader: Bool)] = []
        if let header = table.header, header.contains(where: { !$0.isEmpty }) {
            rows.append((header, true))
        }
        rows += table.rows.map { ($0, false) }

        for (rowIndex, row) in rows.enumerated() {
            for column in 0..<table.columnCount {
                let cell = NSTextTableBlock(table: textTable, startingRow: rowIndex, rowSpan: 1, startingColumn: column, columnSpan: 1)
                cell.setWidth(4, type: .absoluteValueType, for: .padding, edge: .minY)
                cell.setWidth(4, type: .absoluteValueType, for: .padding, edge: .maxY)
                cell.setWidth(7, type: .absoluteValueType, for: .padding, edge: .minX)
                cell.setWidth(7, type: .absoluteValueType, for: .padding, edge: .maxX)
                cell.setWidth(row.isHeader ? 1 : 0.5, type: .absoluteValueType, for: .border, edge: .maxY)
                cell.setBorderColor(row.isHeader ? Style.text : Style.rule, for: .maxY)
                if row.isHeader {
                    cell.backgroundColor = Style.headerBackground
                }
                if let widths = table.widths, column < widths.count {
                    cell.setValue(widths[column] * 100, type: .percentageValueType, for: .width)
                }
                var inside = context
                inside.indent = 0
                inside.size = size
                inside.textBlocks.append(cell)
                inside.alignment = switch table.alignments[column] {
                case .none, .left: .natural
                case .center: .center
                case .right: .right
                }
                let content = column < row.cells.count ? row.cells[column] : []
                let text = NSMutableAttributedString(attributedString: inlines(content, inside, bold: row.isHeader))
                if text.length == 0 {
                    text.append(NSAttributedString(string: " ", attributes: attributes(size: size, color: context.color)))
                }
                appendParagraph(text, style: paragraphStyle(inside, after: 0))
            }
        }
        // Room below the table.
        appendParagraph(NSAttributedString(string: "", attributes: attributes(size: 4, color: context.color)), style: paragraphStyle(context, after: context.tight ? 2 : 6))
    }

    private mutating func rule(_ context: Context, short: Bool) {
        let box = NSTextBlock()
        box.setWidth(0.75, type: .absoluteValueType, for: .border, edge: .maxY)
        box.setBorderColor(Style.rule, for: .maxY)
        box.setWidth(context.indent, type: .absoluteValueType, for: .margin, edge: .minX)
        box.setWidth(6, type: .absoluteValueType, for: .margin, edge: .minY)
        box.setWidth(12, type: .absoluteValueType, for: .margin, edge: .maxY)
        var inside = context
        inside.width = fit(box, in: context, share: short ? 0.3 : 1)
        inside.indent = 0
        inside.textBlocks.append(box)
        appendParagraph(NSAttributedString(string: "\u{00A0}", attributes: attributes(size: 2, color: context.color)), style: paragraphStyle(inside, after: 0))
    }

    /// Sets the box's width to what's left of the column after its margins,
    /// border and padding, and returns it. A box without a width collapses to
    /// a single character when printed.
    private func fit(_ box: NSTextBlock, in context: Context, share: CGFloat = 1) -> CGFloat {
        var extra: CGFloat = 0
        for layer in [NSTextBlock.Layer.margin, .border, .padding] {
            extra += box.width(for: layer, edge: .minX) + box.width(for: layer, edge: .maxX)
        }
        let width = max((context.width - extra) * share, 20)
        box.setContentWidth(width, type: .absoluteValueType)
        return width
    }

    // MARK: Paragraphs

    private func paragraphStyle(_ context: Context, before: CGFloat = 0, after: CGFloat? = nil) -> NSParagraphStyle {
        let style = NSMutableParagraphStyle()
        style.textBlocks = context.textBlocks
        style.textLists = context.lists
        style.firstLineHeadIndent = context.indent
        style.headIndent = context.indent
        style.paragraphSpacingBefore = before
        style.paragraphSpacing = after ?? context.spacingAfter
        style.lineHeightMultiple = 1.15
        style.alignment = context.alignment
        return style
    }

    private func appendParagraph(_ content: NSAttributedString, style: NSParagraphStyle) {
        let paragraph = NSMutableAttributedString(attributedString: content)
        // The newline takes the last character's font, so the line keeps its height.
        let trailing = paragraph.length > 0 ? paragraph.attributes(at: paragraph.length - 1, effectiveRange: nil) : [:]
        paragraph.append(NSAttributedString(string: "\n", attributes: trailing.filter { $0.key == .font || $0.key == .foregroundColor }))
        paragraph.addAttribute(.paragraphStyle, value: style, range: NSRange(location: 0, length: paragraph.length))
        output.append(paragraph)
    }

    /// Changes the space after the last paragraph written, e.g. to close a tight list.
    private func setSpacingAfterLastParagraph(_ spacing: CGFloat) {
        guard output.length > 0 else { return }
        let string = output.string as NSString
        let range = string.paragraphRange(for: NSRange(location: output.length - 1, length: 0))
        guard let current = output.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle else { return }
        let style = NSMutableParagraphStyle()
        style.setParagraphStyle(current)
        style.paragraphSpacing = spacing
        output.addAttribute(.paragraphStyle, value: style, range: range)
    }

    // MARK: Inline content

    private struct InlineStyle {
        var bold = false
        var italic = false
        var code = false
        var strike = false
        var link: URL?
    }

    private func inlines(_ text: MarkdownText, _ context: Context, bold: Bool = false, italic: Bool = false) -> NSAttributedString {
        let result = NSMutableAttributedString()
        append(text, to: result, context, InlineStyle(bold: bold, italic: italic))
        return result
    }

    private func append(_ text: MarkdownText, to result: NSMutableAttributedString, _ context: Context, _ style: InlineStyle) {
        for inline in text {
            switch inline {
            case .text(let string):
                result.append(NSAttributedString(string: string, attributes: attributes(context, style)))
            case .emphasis(let content):
                var inner = style
                inner.italic = true
                append(content, to: result, context, inner)
            case .strong(let content):
                var inner = style
                inner.bold = true
                append(content, to: result, context, inner)
            case .strikethrough(let content):
                var inner = style
                inner.strike = true
                append(content, to: result, context, inner)
            case .code(let code):
                var inner = style
                inner.code = true
                var codeAttributes = attributes(context, inner)
                codeAttributes[.backgroundColor] = Style.codeBackground
                result.append(NSAttributedString(string: code, attributes: codeAttributes))
            case .math(let tex):
                result.append(mathText(tex, size: context.size, color: context.color))
            case .link(let url, _, let content):
                var inner = style
                // Links within the document, or to files beside it, have nowhere to go in a PDF.
                if MarkdownHTML.isSafe(url), let destination = URL(string: url), destination.scheme != nil {
                    inner.link = destination
                }
                append(content, to: result, context, inner)
            case .image(let source, _, let alt):
                result.append(image(source, alt: alt, context))
            case .footnote(let number):
                var footnoteAttributes = attributes(size: context.size * 0.7, color: context.color)
                footnoteAttributes[.baselineOffset] = context.size * 0.35
                result.append(NSAttributedString(string: "\(number)", attributes: footnoteAttributes))
            case .lineBreak:
                result.append(NSAttributedString(string: "\u{2028}", attributes: attributes(context, style)))
            case .softBreak:
                result.append(NSAttributedString(string: " ", attributes: attributes(context, style)))
            }
        }
    }

    private func attributes(_ context: Context, _ style: InlineStyle) -> [NSAttributedString.Key: Any] {
        var result = attributes(
            size: style.code ? context.size * 0.9 : context.size,
            bold: style.bold,
            italic: style.italic,
            monospaced: style.code,
            color: style.link == nil ? context.color : Style.link
        )
        if style.strike {
            result[.strikethroughStyle] = NSUnderlineStyle.single.rawValue
        }
        if let link = style.link {
            result[.link] = link
        }
        return result
    }

    private func attributes(size: CGFloat, bold: Bool = false, italic: Bool = false, monospaced: Bool = false, color: NSColor) -> [NSAttributedString.Key: Any] {
        [.font: Self.font(size: size, bold: bold, italic: italic, monospaced: monospaced), .foregroundColor: color]
    }

    private func mathText(_ tex: String, size: CGFloat, color: NSColor) -> NSAttributedString {
        let font = NSFont(name: "TimesNewRomanPS-ItalicMT", size: size * 1.1) ?? Self.font(size: size, italic: true)
        return NSAttributedString(string: MarkdownMath.unicode(tex), attributes: [.font: font, .foregroundColor: color])
    }

    /// The picture, scaled to fit, or its description when it can't be shown:
    /// pictures on the web are never downloaded.
    private func image(_ source: String, alt: MarkdownText, _ context: Context) -> NSAttributedString {
        if let url = MarkdownImages.localURL(for: source, relativeTo: baseURL),
           let image = NSImage(contentsOf: url), image.size.width > 0, image.size.height > 0 {
            let available = max(context.width - context.indent - 16, 40)
            // Half a page at most, so a picture doesn't push its caption over.
            let scale = min(1, available / image.size.width, 380 / image.size.height)
            let attachment = NSTextAttachment()
            attachment.image = image
            attachment.bounds = CGRect(x: 0, y: 0, width: image.size.width * scale, height: image.size.height * scale)
            return NSAttributedString(attachment: attachment)
        }
        let description = MarkdownInline.plainText(alt)
        var muted = context
        muted.color = Style.muted
        return NSAttributedString(string: "[\(description.isEmpty ? "image" : description)]", attributes: attributes(muted, InlineStyle(italic: true)))
    }

    /// Helvetica Neue for text and Menlo for code: both embed in PDFs, which
    /// the system UI font doesn't.
    static func font(size: CGFloat, bold: Bool = false, italic: Bool = false, monospaced: Bool = false) -> NSFont {
        let base = monospaced
            ? NSFont(name: "Menlo-Regular", size: size) ?? .monospacedSystemFont(ofSize: size, weight: .regular)
            : NSFont(name: "HelveticaNeue", size: size) ?? .userFont(ofSize: size) ?? .systemFont(ofSize: size)
        var traits: NSFontDescriptor.SymbolicTraits = []
        if bold { traits.insert(.bold) }
        if italic { traits.insert(.italic) }
        guard !traits.isEmpty else { return base }
        return NSFont(descriptor: base.fontDescriptor.withSymbolicTraits(traits), size: size) ?? base
    }
}

/// Finds the pictures a Markdown file refers to.
enum MarkdownImages {
    /// The file `source` names, relative to the Markdown file's folder, if it's
    /// on this Mac. Web addresses give nil: OpenShelf never downloads them.
    static func localURL(for source: String, relativeTo base: URL?) -> URL? {
        let path = source.removingPercentEncoding ?? source
        if let scheme = URL(string: source)?.scheme?.lowercased(), scheme.count > 1 {
            guard scheme == "file", let url = URL(string: source) else { return nil }
            return FileManager.default.fileExists(atPath: url.path) ? url : nil
        }
        let url: URL
        if path.hasPrefix("/") {
            url = URL(fileURLWithPath: path)
        } else if path.hasPrefix("~/") {
            url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(String(path.dropFirst(2)))
        } else if let base {
            url = base.appendingPathComponent(path)
        } else {
            return nil
        }
        return FileManager.default.fileExists(atPath: url.path) ? url.standardizedFileURL : nil
    }
}
