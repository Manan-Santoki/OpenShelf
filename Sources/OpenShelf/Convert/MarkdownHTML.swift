import Foundation

/// Writes a Markdown document as HTML. The markup is well-formed XHTML, so the
/// same body serves a standalone page and an EPUB chapter.
struct MarkdownHTML {
    /// Rewrites an image's address, e.g. to the copy packed into an EPUB.
    var imageSource: (String) -> String = { $0 }
    private var referencedFootnotes: Set<Int> = []

    init(imageSource: @escaping (String) -> String = { $0 }) {
        self.imageSource = imageSource
    }

    /// A complete page with its own stylesheet, light and dark.
    static func page(_ document: MarkdownDocument, title: String) -> String {
        var renderer = MarkdownHTML()
        let body = renderer.body(document)
        return """
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <title>\((document.title ?? title).xmlEscaped)</title>
            <style>
            \(stylesheet)
            </style>
            </head>
            <body>
            \(body)
            </body>
            </html>

            """
    }

    /// The `<body>` contents: the title, the blocks, then the footnotes.
    mutating func body(_ document: MarkdownDocument) -> String {
        var html: [String] = []
        if let title = document.title {
            html.append(#"<h1 class="title">\#(title.xmlEscaped)</h1>"#)
        }
        html.append(blocks(document.blocks, tight: false))
        if !document.footnotes.isEmpty {
            var notes = [#"<section class="footnotes" role="doc-endnotes">"#, "<hr/>", "<ol>"]
            for (index, footnote) in document.footnotes.enumerated() {
                let number = index + 1
                let back = ##" <a href="#fnref\##(number)" class="footnote-back" role="doc-backlink">↩︎</a>"##
                notes.append(#"<li id="fn\#(number)">\#(appending(back, toLastParagraphOf: blocks(footnote, tight: false)))</li>"#)
            }
            notes += ["</ol>", "</section>"]
            html.append(notes.joined(separator: "\n"))
        }
        return html.filter { !$0.isEmpty }.joined(separator: "\n")
    }

    /// Puts the back link inside the note's last paragraph, where it reads naturally.
    private func appending(_ link: String, toLastParagraphOf html: String) -> String {
        guard html.hasSuffix("</p>") else { return html + link }
        return String(html.dropLast(4)) + link + "</p>"
    }

    // MARK: Blocks

    private mutating func blocks(_ blocks: [MarkdownBlock<MarkdownText>], tight: Bool) -> String {
        blocks.map { block($0, tight: tight) }.joined(separator: "\n")
    }

    private mutating func block(_ block: MarkdownBlock<MarkdownText>, tight: Bool) -> String {
        switch block {
        case .heading(let level, let text, let id):
            return #"<h\#(level) id="\#(id.xmlEscaped)">\#(inlines(text))</h\#(level)>"#
        case .paragraph(let text):
            if let figure = MarkdownInline.figureImage(text) {
                let alt = MarkdownInline.plainText(figure.alt)
                let caption = alt.isEmpty ? "" : "<figcaption>\(inlines(figure.alt))</figcaption>"
                return "<figure>\(image(figure.source, title: figure.title, alt: alt))\(caption)</figure>"
            }
            return tight ? inlines(text) : "<p>\(inlines(text))</p>"
        case .code(let code, let language):
            let attribute = language.map { #" class="language-\#($0.xmlEscaped)""# } ?? ""
            return "<pre><code\(attribute)>\(code.xmlEscaped)</code></pre>"
        case .math(let tex):
            return #"<p class="math display">\#(MarkdownMath.unicode(tex).xmlEscaped)</p>"#
        case .quote(let content):
            return "<blockquote>\n\(blocks(content, tight: false))\n</blockquote>"
        case .list(let list):
            let tag = list.ordered ? "ol" : "ul"
            let start = list.ordered && list.start != 1 ? #" start="\#(list.start)""# : ""
            let items = list.items.map { item in
                "<li>\(blocks(item, tight: list.tight))</li>"
            }
            return "<\(tag)\(start)>\n\(items.joined(separator: "\n"))\n</\(tag)>"
        case .table(let table):
            return self.table(table)
        case .definitions(let items):
            var html = ["<dl>"]
            for item in items {
                html.append("<dt>\(inlines(item.term))</dt>")
                for definition in item.definitions {
                    html.append("<dd>\(blocks(definition, tight: definition.count == 1))</dd>")
                }
            }
            html.append("</dl>")
            return html.joined(separator: "\n")
        case .lines(let lines):
            return #"<div class="line-block">\#(lines.map { inlines($0) }.joined(separator: "<br/>"))</div>"#
        case .rule:
            return "<hr/>"
        }
    }

    private mutating func table(_ table: MarkdownTable<MarkdownText>) -> String {
        func style(_ column: Int) -> String {
            switch table.alignments[column] {
            case .none: ""
            case .left: #" style="text-align: left""#
            case .center: #" style="text-align: center""#
            case .right: #" style="text-align: right""#
            }
        }
        var html = ["<table>"]
        if let caption = table.caption {
            html.append("<caption>\(inlines(caption))</caption>")
        }
        if let widths = table.widths {
            html.append("<colgroup>" + widths.map { #"<col style="width: \#(Int(($0 * 100).rounded()))%"/>"# }.joined() + "</colgroup>")
        }
        if let header = table.header, header.contains(where: { !$0.isEmpty }) {
            let cells = header.enumerated().map { "<th\(style($0.offset))>\(inlines($0.element))</th>" }
            html.append("<thead>\n<tr>\(cells.joined())</tr>\n</thead>")
        }
        html.append("<tbody>")
        for row in table.rows {
            let cells = row.enumerated().map { "<td\(style($0.offset))>\(inlines($0.element))</td>" }
            html.append("<tr>\(cells.joined())</tr>")
        }
        html += ["</tbody>", "</table>"]
        return html.joined(separator: "\n")
    }

    // MARK: Inline content

    private mutating func inlines(_ text: MarkdownText) -> String {
        text.map { inline($0) }.joined()
    }

    private mutating func inline(_ inline: MarkdownInline) -> String {
        switch inline {
        case .text(let string):
            return string.xmlEscaped
        case .emphasis(let content):
            return "<em>\(inlines(content))</em>"
        case .strong(let content):
            return "<strong>\(inlines(content))</strong>"
        case .strikethrough(let content):
            return "<del>\(inlines(content))</del>"
        case .code(let code):
            return "<code>\(code.xmlEscaped)</code>"
        case .math(let tex):
            return #"<span class="math">\#(MarkdownMath.unicode(tex).xmlEscaped)</span>"#
        case .link(let url, let title, let content):
            guard Self.isSafe(url) else { return inlines(content) }
            let titleAttribute = title.map { #" title="\#($0.xmlEscaped)""# } ?? ""
            return #"<a href="\#(url.xmlEscaped)"\#(titleAttribute)>\#(inlines(content))</a>"#
        case .image(let source, let title, let alt):
            return image(source, title: title, alt: MarkdownInline.plainText(alt))
        case .footnote(let number):
            // Only the first reference can be the note's way back.
            let id = referencedFootnotes.insert(number).inserted ? #" id="fnref\#(number)""# : ""
            return ##"<sup><a href="#fn\##(number)"\##(id) class="footnote-ref" role="doc-noteref">\##(number)</a></sup>"##
        case .lineBreak:
            return "<br/>"
        case .softBreak:
            return "\n"
        }
    }

    private func image(_ source: String, title: String?, alt: String) -> String {
        guard Self.isSafe(source) else { return alt.xmlEscaped }
        let titleAttribute = title.map { #" title="\#($0.xmlEscaped)""# } ?? ""
        return #"<img src="\#(imageSource(source).xmlEscaped)" alt="\#(alt.xmlEscaped)"\#(titleAttribute)/>"#
    }

    /// Script links are dropped; the document is opened by people who didn't write it.
    static func isSafe(_ url: String) -> Bool {
        let scheme = url.prefix { $0 != ":" && $0 != "/" && $0 != "#" && $0 != "?" }.lowercased()
        guard url.dropFirst(scheme.count).hasPrefix(":") else { return true }
        return !["javascript", "vbscript", "data", "file"].contains(scheme.trimmingCharacters(in: .whitespaces))
    }

    // MARK: Style

    static let stylesheet = """
        :root { color-scheme: light dark; --text: #1d1d1f; --muted: #6e6e73; --rule: #d2d2d7; --code: #f5f5f7; --link: #0066cc; }
        @media (prefers-color-scheme: dark) { :root { --text: #f5f5f7; --muted: #a1a1a6; --rule: #424245; --code: #2c2c2e; --link: #2997ff; } }
        body { font: 16px/1.6 -apple-system, BlinkMacSystemFont, "Helvetica Neue", Helvetica, Arial, sans-serif; color: var(--text); max-width: 46em; margin: 0 auto; padding: 2em 1.25em 4em; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.6em 0 0.6em; }
        h1 { font-size: 2em; } h2 { font-size: 1.5em; } h3 { font-size: 1.25em; } h4 { font-size: 1.05em; }
        h1.title { margin-top: 0; text-align: center; }
        a { color: var(--link); }
        p, ul, ol, dl, pre, blockquote, table, figure { margin: 0 0 1em; }
        code, pre { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 0.875em; }
        code { background: var(--code); border-radius: 4px; padding: 0.1em 0.35em; }
        pre { background: var(--code); border-radius: 8px; padding: 0.85em 1em; overflow-x: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 1em; }
        blockquote { border-left: 4px solid var(--rule); color: var(--muted); margin-left: 0; padding: 0 1em; }
        table { border-collapse: collapse; margin-left: auto; margin-right: auto; }
        caption { font-style: italic; color: var(--muted); padding-bottom: 0.4em; }
        th, td { border-bottom: 1px solid var(--rule); padding: 0.35em 0.8em; vertical-align: top; text-align: left; }
        thead th { border-bottom: 2px solid var(--text); }
        hr { border: none; border-top: 1px solid var(--rule); margin: 2em 0; }
        img { max-width: 100%; height: auto; }
        figure { text-align: center; } figcaption { font-style: italic; color: var(--muted); margin-top: 0.4em; }
        dt { font-weight: 600; } dd { margin: 0 0 0.6em 1.5em; }
        .line-block { margin: 0 0 1em; }
        .math { font-family: "Times New Roman", Times, serif; font-style: italic; }
        .math.display { text-align: center; font-size: 1.15em; }
        .footnotes { font-size: 0.9em; color: var(--muted); margin-top: 3em; }
        .footnote-ref { text-decoration: none; }
        """
}
