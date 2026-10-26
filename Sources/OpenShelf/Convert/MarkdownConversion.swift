import AppKit
import ImageIO
import UniformTypeIdentifiers

/// Markdown files, rendered: headings, lists, tables, code, footnotes and
/// pictures become the real thing in each format, instead of the Markdown
/// source shown as plain text.
enum MarkdownConversion {
    static func isMarkdown(_ url: URL) -> Bool {
        ["md", "markdown", "mdown", "mkd", "mkdn"].contains(url.pathExtension.lowercased())
    }

    static func convert(_ source: URL, to format: OutputFormat, destination: URL) async throws {
        guard let data = try? Data(contentsOf: source), let text = TextDecoding.decode(data) else {
            throw ConversionError.unreadable(source)
        }
        let document = MarkdownDocument(parsing: text)
        let folder = source.deletingLastPathComponent()
        let name = source.deletingPathExtension().lastPathComponent

        switch format {
        case .html:
            try MarkdownHTML.page(document, title: name).write(to: destination, atomically: true, encoding: .utf8)
        case .txt:
            try MarkdownPlainText.text(document).write(to: destination, atomically: true, encoding: .utf8)
        case .docx:
            try MarkdownDocx.write(document, baseURL: folder, to: destination)
        case .epub:
            try writeEpub(document, title: document.title ?? name, baseURL: folder, to: destination)
        case .rtf:
            let text = MarkdownRichText.attributedString(document, baseURL: folder, width: DocumentConversion.printableWidth)
            let range = NSRange(location: 0, length: text.length)
            try text.data(from: range, documentAttributes: [.documentType: NSAttributedString.DocumentType.rtf]).write(to: destination)
        case .pdf:
            try await MainActor.run {
                let text = MarkdownRichText.attributedString(document, baseURL: folder, width: DocumentConversion.printableWidth)
                try DocumentConversion.printPDF(text, to: destination)
            }
        default:
            throw ConversionError.unsupported("Markdown to \(format.title)")
        }
    }

    /// One chapter, with the document's own pictures packed in.
    private static func writeEpub(_ document: MarkdownDocument, title: String, baseURL: URL, to destination: URL) throws {
        var images: [String: Data] = [:]
        var names: [String: String] = [:]
        for source in document.imageSources {
            guard names[source] == nil,
                  let url = MarkdownImages.localURL(for: source, relativeTo: baseURL),
                  let jpeg = jpegData(url)
            else { continue }
            let name = "image\(images.count + 1).jpg"
            images[name] = jpeg
            names[source] = "images/\(name)"
        }
        var renderer = MarkdownHTML(imageSource: { names[$0] ?? $0 })
        let body = renderer.body(document)
        try EpubWriter.write(title: title, chapters: [EpubChapter(title: title, bodyXHTML: body)], images: images, to: destination)
    }

    /// A JPEG of the picture on white, since EPUB pictures are packed as JPEG.
    private static func jpegData(_ url: URL) -> Data? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
              let context = CGContext(
                data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
              )
        else { return nil }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(rect)
        context.draw(image, in: rect)
        guard let flattened = context.makeImage() else { return nil }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, flattened, [kCGImageDestinationLossyCompressionQuality: 0.88] as CFDictionary)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }
}

extension MarkdownDocument {
    /// Every picture the document shows, in order.
    var imageSources: [String] {
        var sources: [String] = []
        func visit(_ text: MarkdownText) {
            for inline in text {
                switch inline {
                case .image(let source, _, let alt):
                    sources.append(source)
                    visit(alt)
                case .emphasis(let content), .strong(let content), .strikethrough(let content), .link(_, _, let content):
                    visit(content)
                default:
                    break
                }
            }
        }
        for block in blocks + footnotes.flatMap({ $0 }) {
            _ = block.map { text -> MarkdownText in
                visit(text)
                return text
            }
        }
        return sources
    }
}

/// Markdown as plain text that reads well on its own: no asterisks or
/// pound signs, tables lined up in columns, link addresses after their text.
enum MarkdownPlainText {
    static func text(_ document: MarkdownDocument) -> String {
        var lines: [String] = []
        if let title = document.title {
            lines += [title, String(repeating: "=", count: title.count), ""]
        }
        lines += blocks(document.blocks)
        if !document.footnotes.isEmpty {
            lines += ["", "---", ""]
            for (index, footnote) in document.footnotes.enumerated() {
                lines += prefixed(blocks(footnote), first: "[\(index + 1)] ", rest: "    ")
            }
        }
        return lines.joined(separator: "\n").trimmingCharacters(in: .newlines) + "\n"
    }

    private static func blocks(_ blocks: [MarkdownBlock<MarkdownText>], tight: Bool = false) -> [String] {
        var lines: [String] = []
        for block in blocks {
            if !lines.isEmpty && !tight { lines.append("") }
            lines += self.block(block)
        }
        return lines
    }

    private static func block(_ block: MarkdownBlock<MarkdownText>) -> [String] {
        switch block {
        case .heading(let level, let text, _):
            let title = inline(text)
            switch level {
            case 1: return [title, String(repeating: "=", count: title.count)]
            case 2: return [title, String(repeating: "-", count: title.count)]
            default: return [title]
            }
        case .paragraph(let text):
            return inline(text).components(separatedBy: "\n")
        case .code(let code, _):
            return code.components(separatedBy: "\n").map { $0.isEmpty ? "" : "    " + $0 }
        case .math(let tex):
            return ["    " + MarkdownMath.unicode(tex)]
        case .quote(let content):
            return blocks(content).map { $0.isEmpty ? ">" : "> " + $0 }
        case .list(let list):
            var lines: [String] = []
            let width = list.ordered ? "\(list.start + list.items.count - 1). ".count : 2
            for (index, item) in list.items.enumerated() {
                if !list.tight && index > 0 { lines.append("") }
                let marker = list.ordered ? "\(list.start + index). " : "• "
                lines += prefixed(blocks(item, tight: list.tight), first: marker.padding(toLength: width, withPad: " ", startingAt: 0), rest: String(repeating: " ", count: width))
            }
            return lines
        case .table(let table):
            return self.table(table)
        case .definitions(let items):
            var lines: [String] = []
            for (index, item) in items.enumerated() {
                if index > 0 { lines.append("") }
                lines.append(inline(item.term))
                for definition in item.definitions {
                    lines += blocks(definition).map { $0.isEmpty ? "" : "    " + $0 }
                }
            }
            return lines
        case .lines(let lines):
            return lines.map { inline($0).replacingOccurrences(of: "\u{00A0}", with: " ") }
        case .rule:
            return ["* * *"]
        }
    }

    private static func table(_ table: MarkdownTable<MarkdownText>) -> [String] {
        let header = table.header.map { $0.map(cell) }
        let rows = table.rows.map { $0.map(cell) }
        var widths = Array(repeating: 0, count: table.columnCount)
        for row in [header ?? []] + rows {
            for (column, text) in row.enumerated() where column < widths.count {
                widths[column] = max(widths[column], text.count)
            }
        }
        func line(_ cells: [String]) -> String {
            cells.enumerated().map { column, text in
                let padding = String(repeating: " ", count: max(widths[column] - text.count, 0))
                return table.alignments[column] == .right ? padding + text : text + padding
            }
            .joined(separator: "  ")
            .trimmingCharacters(in: .whitespaces)
        }
        var lines: [String] = []
        if let caption = table.caption { lines += [inline(caption), ""] }
        if let header, header.contains(where: { !$0.isEmpty }) {
            lines.append(line(header))
            lines.append(widths.map { String(repeating: "-", count: max($0, 1)) }.joined(separator: "  "))
        }
        lines += rows.map(line)
        return lines
    }

    private static func cell(_ text: MarkdownText) -> String {
        inline(text).replacingOccurrences(of: "\n", with: " ")
    }

    private static func prefixed(_ lines: [String], first: String, rest: String) -> [String] {
        lines.enumerated().map { index, line in
            if index == 0 { return first + line }
            return line.isEmpty ? "" : rest + line
        }
    }

    private static func inline(_ text: MarkdownText) -> String {
        text.map { inline in
            switch inline {
            case .text(let string), .code(let string): string
            case .math(let tex): MarkdownMath.unicode(tex)
            case .emphasis(let content), .strong(let content), .strikethrough(let content): self.inline(content)
            case .link(let url, _, let content):
                linked(self.inline(content), url: url)
            case .image(_, _, let alt):
                "[\(self.inline(alt).isEmpty ? "image" : self.inline(alt))]"
            case .footnote(let number): "[\(number)]"
            case .lineBreak: "\n"
            case .softBreak: " "
            }
        }
        .joined()
    }

    /// "a website (http://foo.bar)", unless the text already is the address.
    private static func linked(_ text: String, url: String) -> String {
        let bare = url.replacingOccurrences(of: "mailto:", with: "")
        if url.hasPrefix("#") || text == url || text == bare { return text }
        return "\(text) (\(bare))"
    }
}
