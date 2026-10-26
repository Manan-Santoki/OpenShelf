import AppKit
import Foundation
import PDFKit
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

/// Pandoc's own sample document, attached to issue #5: the conversion that
/// printed its Markdown source as plain text.
private let pandocSample = #"""
    An h1 header
    ============

    Paragraphs are separated by a blank line.

    2nd paragraph. *Italic*, **bold**, and `monospace`. Itemized lists
    look like:

      * this one
      * that one
      * the other one

    > Block quotes are
    > written like so.
    >
    > They can span multiple paragraphs,
    > if you like.

    Use 3 dashes for an em-dash. Use 2 dashes for ranges (ex., "it's all
    in chapters 12--14"). Three dots ... will be converted to an ellipsis.

    An h2 header
    ------------

     1. first item
     2. second item

    Code:

        # Let me re-iterate ...
        for i in 1 .. 10 { do-something(i) }

    ~~~python
    import time
    ~~~

    ### An h3 header ###

     1. First, get these ingredients:

          * carrots
          * celery

     2. Dump everything in the pot and follow
        this algorithm:

            find wooden spoon
            stir

        Do not bump wooden spoon or it will fall.

    Here's a link to [a website](http://foo.bar), to a [section heading in the current
    doc](#an-h2-header). Here's a footnote [^1].

    [^1]: Footnote text goes here.

    size  material      color
    ----  ------------  ------------
    9     leather       brown
    10    hemp canvas   natural

    Table: Shoes, their sizes, and what they're made of

    --------  -----------------------
    keyword   text
    --------  -----------------------
    red       Sunsets, apples, and
              other red or reddish
              things.

    green     Leaves, grass, frogs
    --------  -----------------------

    ***

    apples
      : Good for making applesauce.
    oranges
      : Citrus!

    | Line one
    |   Line too

    Inline math: $\omega = d\phi / dt$.

    $$I = \int \rho R^{2} dV$$

    Escapes: \`foo\`, \*bar\*.
    """#

struct MarkdownParserTests {
    private let document = MarkdownDocument(parsing: pandocSample)

    private func blocks(where predicate: (MarkdownBlock<MarkdownText>) -> Bool) -> [MarkdownBlock<MarkdownText>] {
        document.blocks.filter(predicate)
    }

    @Test func setextAndATXHeadingsGetPandocIdentifiers() {
        let headings = document.blocks.compactMap { block -> (Int, String, String)? in
            guard case .heading(let level, let text, let id) = block else { return nil }
            return (level, MarkdownInline.plainText(text), id)
        }
        #expect(headings.map(\.0) == [1, 2, 3])
        #expect(headings.map(\.1) == ["An h1 header", "An h2 header", "An h3 header"])
        #expect(headings.map(\.2) == ["an-h1-header", "an-h2-header", "an-h3-header"])
    }

    @Test func inlineStylesAndSmartPunctuation() {
        guard case .paragraph(let text) = document.blocks[2] else {
            Issue.record("Expected the second paragraph")
            return
        }
        #expect(text.contains(.emphasis([.text("Italic")])))
        #expect(text.contains(.strong([.text("bold")])))
        #expect(text.contains(.code("monospace")))
        let dashes = document.blocks.compactMap { block -> String? in
            if case .paragraph(let text) = block { return MarkdownInline.plainText(text) }
            return nil
        }.first { $0.hasPrefix("Use 3 dashes") }
        #expect(dashes?.contains("“it’s all in chapters 12–14”") == true)
        #expect(dashes?.contains("Three dots …") == true)
    }

    @Test func indentedBulletsQuotesAndNestedLists() throws {
        let lists = blocks { if case .list = $0 { true } else { false } }
        guard case .list(let bullets) = lists.first, case .list(let nested) = lists.last else {
            Issue.record("Expected lists")
            return
        }
        #expect(!bullets.ordered && bullets.tight && bullets.items.count == 3)
        #expect(nested.ordered && !nested.tight && nested.items.count == 2)
        guard case .list(let inner) = nested.items[0].last else {
            Issue.record("Expected the ingredients inside item 1")
            return
        }
        #expect(inner.items.count == 2)
        // Item 2 continues lazily, then holds indented code and a paragraph.
        #expect(nested.items[1].count == 3)
        #expect(nested.items[1][1] == .code("find wooden spoon\nstir", language: nil))

        let quotes = blocks { if case .quote = $0 { true } else { false } }
        guard case .quote(let quoted) = quotes.first else {
            Issue.record("Expected a block quote")
            return
        }
        #expect(quoted.count == 2)
    }

    @Test func codeBlocksKeepTheirTextAndLanguage() {
        #expect(document.blocks.contains(.code("# Let me re-iterate ...\nfor i in 1 .. 10 { do-something(i) }", language: nil)))
        #expect(document.blocks.contains(.code("import time", language: "python")))
        #expect(MarkdownParser.language(fromInfo: "{.swift .numberLines}") == "swift")
        #expect(MarkdownParser.language(fromInfo: "") == nil)
    }

    @Test func linksFootnotesAndEscapes() {
        let text = document.blocks.compactMap { block -> MarkdownText? in
            if case .paragraph(let text) = block, MarkdownInline.plainText(text).hasPrefix("Here’s a link") { return text }
            return nil
        }.first ?? []
        #expect(text.contains(.link(url: "http://foo.bar", title: nil, content: [.text("a website")])))
        #expect(text.contains { if case .link(let url, _, _) = $0 { url == "#an-h2-header" } else { false } })
        #expect(text.contains(.footnote(1)))
        #expect(document.footnotes == [[.paragraph([.text("Footnote text goes here.")])]])
        #expect(document.blocks.last == .paragraph([.text("Escapes: `foo`, *bar*.")]))
    }

    @Test func pandocSimpleAndMultilineTables() {
        let tables = document.blocks.compactMap { block -> MarkdownTable<MarkdownText>? in
            if case .table(let table) = block { return table }
            return nil
        }
        #expect(tables.count == 2)
        #expect(tables[0].header == [[.text("size")], [.text("material")], [.text("color")]])
        #expect(tables[0].rows.last == [[.text("10")], [.text("hemp canvas")], [.text("natural")]])
        #expect(tables[0].caption.map(MarkdownInline.plainText) == "Shoes, their sizes, and what they’re made of")
        #expect(tables[1].rows.count == 2)
        #expect(MarkdownInline.plainText(tables[1].rows[0][1]) == "Sunsets, apples, and other red or reddish things.")
        #expect(tables[1].widths?.count == 2)
    }

    /// Pandoc itself runs "oranges" into the definition above it.
    @Test func compactDefinitionListsKeepEachTerm() {
        guard let definitions = document.blocks.compactMap({ block -> [MarkdownDefinition<MarkdownText>]? in
            if case .definitions(let items) = block { return items }
            return nil
        }).first else {
            Issue.record("Expected a definition list")
            return
        }
        #expect(definitions.map { MarkdownInline.plainText($0.term) } == ["apples", "oranges"])
        #expect(definitions[1].definitions == [[.paragraph([.text("Citrus!")])]])
    }

    @Test func ruleLineBlockAndMath() {
        #expect(document.blocks.contains(.rule))
        #expect(document.blocks.contains(.lines([[.text("Line one")], [.text("\u{00A0}\u{00A0}Line too")]])))
        #expect(document.blocks.contains(.math(#"I = \int \rho R^{2} dV"#)))
        #expect(MarkdownMath.unicode(#"I = \int \rho R^{2} dV"#) == "I = ∫ ρ R² dV")
        #expect(MarkdownMath.unicode(#"\frac{a+b}{2}"#) == "(a+b)/2")
    }

    @Test func gitHubFlavour() {
        let document = MarkdownDocument(parsing: """
            | Name | Price |
            |:-----|------:|
            | `a|b` | 1 |
            | Tea \\| cake | 2 |

            - [ ] todo
            - [x] done

            ~~gone~~ and https://example.com/a_(b). Also <me@example.com>.
            """)
        guard case .table(let table) = document.blocks[0] else {
            Issue.record("Expected a pipe table")
            return
        }
        #expect(table.alignments == [.left, .right])
        #expect(table.rows == [[[.code("a|b")], [.text("1")]], [[.text("Tea | cake")], [.text("2")]]])
        guard case .list(let tasks) = document.blocks[1] else {
            Issue.record("Expected a task list")
            return
        }
        #expect(tasks.items.map { $0.first } == [.paragraph([.text("☐ todo")]), .paragraph([.text("☑ done")])])
        guard case .paragraph(let text) = document.blocks[2] else {
            Issue.record("Expected a paragraph")
            return
        }
        #expect(text.first == .strikethrough([.text("gone")]))
        #expect(text.contains(.link(url: "https://example.com/a_(b)", title: nil, content: [.text("https://example.com/a_(b)")])))
        #expect(text.contains(.link(url: "mailto:me@example.com", title: nil, content: [.text("me@example.com")])))
    }

    @Test func emphasisEdgeCases() {
        func inlines(_ source: String) -> MarkdownText {
            guard case .paragraph(let text) = MarkdownDocument(parsing: source).blocks.first else { return [] }
            return text
        }
        #expect(inlines("***both***") == [.emphasis([.strong([.text("both")])])])
        #expect(inlines("snake_case_name") == [.text("snake_case_name")])
        #expect(inlines("2 * 3 * 4") == [.text("2 * 3 * 4")])
        #expect(inlines("**bold *and italic***") == [.strong([.text("bold "), .emphasis([.text("and italic")])])])
        // Money isn't math.
        #expect(inlines("It costs $5 or $10.") == [.text("It costs $5 or $10.")])
    }

    @Test func referenceLinksImagesAndTitles() {
        let document = MarkdownDocument(parsing: """
            ---
            title: "Field notes"
            author: Someone
            ---

            See [the docs][docs] and [Docs] and ![a cat](cat.png "Cat"){width=50%}.

            [docs]: https://example.com/docs "Docs"
            """)
        #expect(document.title == "Field notes")
        guard case .paragraph(let text) = document.blocks.first else {
            Issue.record("Expected a paragraph")
            return
        }
        #expect(text.contains(.link(url: "https://example.com/docs", title: "Docs", content: [.text("the docs")])))
        #expect(text.contains(.link(url: "https://example.com/docs", title: "Docs", content: [.text("Docs")])))
        #expect(text.contains(.image(source: "cat.png", title: "Cat", alt: [.text("a cat")])))
        #expect(text.last == .text("."))
    }

    @Test func headingIDsAreUnique() {
        let document = MarkdownDocument(parsing: "# Notes\n\n# Notes\n\n## 2. Next steps {#next}\n")
        let ids = document.blocks.compactMap { block -> String? in
            if case .heading(_, _, let id) = block { return id }
            return nil
        }
        #expect(ids == ["notes", "notes-1", "next"])
    }
}

struct MarkdownRenderingTests {
    private let document = MarkdownDocument(parsing: pandocSample)

    private func temporaryFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-md-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    @Test func htmlIsWellFormedAndStructured() throws {
        var renderer = MarkdownHTML()
        let body = renderer.body(document)
        // Well-formed XHTML, as EPUB needs.
        _ = try XMLDocument(xmlString: "<body>\(body.replacingOccurrences(of: "&nbsp;", with: " "))</body>")
        for tag in ["<h1 id=\"an-h1-header\">", "<blockquote>", "<ol>", "<ul>", "<pre><code class=\"language-python\">", "<table>", "<caption>", "<dl>", "<hr/>", "<section class=\"footnotes\""] {
            #expect(body.contains(tag), "missing \(tag)")
        }
        #expect(!body.contains("**"))
        #expect(!body.contains("==="))
        #expect(MarkdownHTML.page(document, title: "hello").contains("<title>hello</title>"))
    }

    @Test func scriptLinksAreDropped() {
        var renderer = MarkdownHTML()
        let body = renderer.body(MarkdownDocument(parsing: "[click](javascript:alert(1)) <b onclick=\"x\">bold</b>"))
        #expect(!body.contains("javascript"))
        #expect(!body.contains("onclick"))
        #expect(body.contains("click"))
    }

    @Test func plainTextHasNoMarkup() {
        let text = MarkdownPlainText.text(document)
        #expect(text.hasPrefix("An h1 header\n============\n"))
        #expect(text.contains("2nd paragraph. Italic, bold, and monospace."))
        #expect(text.contains("• this one"))
        #expect(text.contains("a website (http://foo.bar)"))
        #expect(text.contains("10    hemp canvas  natural"))
        #expect(text.contains("[1] Footnote text goes here."))
        #expect(!text.contains("**"))
    }

    @Test func docxHasWordStylesListsTablesAndFootnotes() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("hello.docx")
        try MarkdownDocx.write(document, baseURL: folder, to: url)

        // unzip reads brackets as a pattern, hence "[[]".
        let parts = ["[[]Content_Types].xml", "word/document.xml", "word/styles.xml", "word/numbering.xml", "word/footnotes.xml", "word/settings.xml", "word/_rels/document.xml.rels"]
        for part in parts {
            let data = try #require(PackageZip.readEntry(part, from: url), "missing \(part)")
            _ = try XMLDocument(data: data)
        }
        let body = String(decoding: try #require(PackageZip.readEntry("word/document.xml", from: url)), as: UTF8.self)
        for snippet in [#"w:val="Heading1""#, #"w:val="Heading2""#, "<w:numPr>", "<w:tbl>", #"<w:footnoteReference w:id="1"/>"#, #"w:val="SourceCode""#, #"w:val="BlockText""#, #"w:anchor="an-h2-header""#, #"w:name="an-h2-header""#] {
            #expect(body.contains(snippet), "missing \(snippet)")
        }
        #expect(!body.contains("**bold**"))

        // macOS reads it back as the text, not the Markdown.
        let text = try NSAttributedString(url: url, options: [:], documentAttributes: nil).string
        #expect(text.contains("An h1 header"))
        #expect(!text.contains("============"))
        #expect(text.contains("hemp canvas"))
    }

    @Test func docxEmbedsLocalPicturesOnly() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let image = try #require(CGContext(data: nil, width: 40, height: 20, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)?.makeImage())
        try EditableImage.write(image, to: folder.appendingPathComponent("dot.png"), type: .png)
        let document = MarkdownDocument(parsing: "![A dot](dot.png)\n\n![Remote](https://example.com/x.png)\n")
        let url = folder.appendingPathComponent("pictures.docx")
        try MarkdownDocx.write(document, baseURL: folder, to: url)
        #expect(PackageZip.readEntry("word/media/image1.png", from: url) != nil)
        #expect(PackageZip.readEntry("word/media/image2.png", from: url) == nil)
        let body = String(decoding: try #require(PackageZip.readEntry("word/document.xml", from: url)), as: UTF8.self)
        #expect(body.contains("<w:drawing>"))
        #expect(body.contains("[Remote]"))
    }

    /// The conversion from the issue: Markdown to PDF used to print the source.
    @Test func markdownBecomesATypesetPDF() async throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("hello.md")
        try pandocSample.write(to: source, atomically: true, encoding: .utf8)

        let pdf = try await Converter.convert(source, to: .pdf, tools: ExternalTools()) { _ in }
        let document = try #require(PDFDocument(url: pdf[0]))
        let text = try #require(document.string)
        #expect(text.contains("An h1 header"))
        #expect(text.contains("hemp canvas"))
        #expect(text.contains("ω = dφ / dt"))
        for markup in ["============", "**bold**", "> Block", "~~~", "[^1]"] {
            #expect(!text.contains(markup), "\(markup) printed as text")
        }
        // The heading is set larger than the body text.
        let page = try #require(document.page(at: 0))
        let pageText = try #require(page.string) as NSString
        func height(of phrase: String) -> CGFloat {
            page.selection(for: pageText.range(of: phrase))?.bounds(for: page).height ?? 0
        }
        #expect(height(of: "An h1 header") > height(of: "Paragraphs are separated") * 1.5)
    }

    @Test func everyFormatComesOutForMarkdown() async throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("notes.md")
        try pandocSample.write(to: source, atomically: true, encoding: .utf8)
        for format: OutputFormat in [.docx, .txt, .epub, .html, .rtf] {
            let output = try await Converter.convert(source, to: format, tools: ExternalTools()) { _ in }
            #expect(output.map(\.lastPathComponent) == ["notes.\(format.fileExtension ?? "")"])
        }
        let rtf = try NSAttributedString(url: folder.appendingPathComponent("notes.rtf"), options: [:], documentAttributes: nil)
        #expect(rtf.string.contains("An h1 header"))
        #expect(!rtf.string.contains("**"))
        let chapter = try #require(PackageZip.readEntry("OEBPS/chapter1.xhtml", from: folder.appendingPathComponent("notes.epub")))
        _ = try XMLDocument(data: chapter)
    }
}

struct DocumentEngineTests {
    private let none = ExternalTools()
    private let pandoc = ExternalTools(pandoc: URL(fileURLWithPath: "/opt/homebrew/bin/pandoc"))
    private let pandocWithTeX = ExternalTools(pandoc: URL(fileURLWithPath: "/opt/homebrew/bin/pandoc"), pdfEngine: URL(fileURLWithPath: "/Library/TeX/texbin/xelatex"))
    private let office = ExternalTools(libreOffice: URL(fileURLWithPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice"))

    private func url(_ name: String) -> URL { URL(fileURLWithPath: "/tmp/\(name)") }

    @Test func builtInWithoutTools() {
        for format: OutputFormat in [.pdf, .docx, .html, .epub, .txt, .rtf] {
            #expect(DocumentEngine.choose(for: url("a.md"), to: format, tools: none) == .builtIn)
        }
        #expect(DocumentEngine.choose(for: url("a.docx"), to: .pdf, tools: none) == .builtIn)
    }

    @Test func pandocTakesMarkdownButPDFsNeedATypesetter() {
        #expect(DocumentEngine.choose(for: url("a.md"), to: .docx, tools: pandoc) == .pandoc)
        #expect(DocumentEngine.choose(for: url("a.md"), to: .html, tools: pandoc) == .pandoc)
        #expect(DocumentEngine.choose(for: url("a.md"), to: .pdf, tools: pandoc) == .builtIn)
        #expect(DocumentEngine.choose(for: url("a.md"), to: .pdf, tools: pandocWithTeX) == .pandoc)
        // Plain text reads better without Pandoc's wrapping.
        #expect(DocumentEngine.choose(for: url("a.md"), to: .txt, tools: pandoc) == .builtIn)
        #expect(DocumentEngine.choose(for: url("a.docx"), to: .md, tools: pandoc) == .pandoc)
        #expect(DocumentEngine.choose(for: url("a.txt"), to: .md, tools: pandoc) == .builtIn)
    }

    @Test func libreOfficeKeepsWordLayoutsInPDFs() {
        #expect(DocumentEngine.choose(for: url("a.docx"), to: .pdf, tools: office) == .libreOffice)
        #expect(DocumentEngine.choose(for: url("a.odt"), to: .pdf, tools: office) == .libreOffice)
        #expect(DocumentEngine.choose(for: url("a.docx"), to: .txt, tools: office) == .builtIn)
        var both = office
        both.pandoc = pandocWithTeX.pandoc
        both.pdfEngine = pandocWithTeX.pdfEngine
        #expect(DocumentEngine.choose(for: url("a.docx"), to: .pdf, tools: both) == .libreOffice)
        #expect(DocumentEngine.choose(for: url("a.html"), to: .pdf, tools: both) == .pandoc)
    }

    @Test func pandocArguments() throws {
        let source = URL(fileURLWithPath: "/Users/me/Notes/hello.md")
        let output = URL(fileURLWithPath: "/tmp/out.html")
        let html = try #require(PandocConversion.arguments(source: source, format: .html, output: output, pdfEngine: nil))
        #expect(html.prefix(5) == [source.path, "--from", "markdown", "--to", "html5"])
        #expect(html.contains("--standalone"))
        #expect(html.contains("pagetitle=hello"))
        #expect(html.contains("/Users/me/Notes"))

        let markdown = try #require(PandocConversion.arguments(source: URL(fileURLWithPath: "/a/b.docx"), format: .md, output: output, pdfEngine: nil))
        #expect(markdown.contains("gfm") && !markdown.contains("--standalone") && !markdown.contains("--metadata"))

        #expect(PandocConversion.arguments(source: source, format: .pdf, output: output, pdfEngine: nil) == nil)
        let pdf = try #require(PandocConversion.arguments(source: source, format: .pdf, output: output, pdfEngine: URL(fileURLWithPath: "/Library/TeX/texbin/xelatex")))
        #expect(pdf.contains("/Library/TeX/texbin/xelatex"))
        #expect(pdf.contains("geometry:margin=1in"))
        #expect(!pdf.contains("--to"))
    }

    private func script(_ body: String, in folder: URL) throws -> URL {
        let url = folder.appendingPathComponent("pandoc")
        try "#!/bin/sh\n\(body)\n".write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    @Test func pandocWritesBesideTheOriginal() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-pandoc-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        // Stands in for Pandoc: writes its arguments to --output.
        let fake = try script(#"while [ "$1" != "--output" ]; do shift; done; echo pandoc > "$2""#, in: folder)
        let source = folder.appendingPathComponent("notes.md")
        try "# Notes".write(to: source, atomically: true, encoding: .utf8)
        let output = try await DocumentConversion.convert(source, to: .docx, tools: ExternalTools(pandoc: fake))
        #expect(output.lastPathComponent == "notes.docx")
        #expect(try String(contentsOf: output, encoding: .utf8) == "pandoc\n")
    }

    @Test func aFailingPandocFallsBackToTheBuiltInConversion() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-pandoc-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let broken = try script("echo broken >&2; exit 3", in: folder)
        let source = folder.appendingPathComponent("notes.md")
        try "# Notes\n\nSome **bold** text.".write(to: source, atomically: true, encoding: .utf8)
        let output = try await DocumentConversion.convert(source, to: .html, tools: ExternalTools(pandoc: broken))
        #expect(output.lastPathComponent == "notes.html")
        #expect(try String(contentsOf: output, encoding: .utf8).contains("<strong>bold</strong>"))
    }

    @Test func detectsPandocAndATypesetter() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-detect-\(UUID().uuidString)", isDirectory: true)
        let tex = folder.appendingPathComponent("texbin", isDirectory: true)
        try FileManager.default.createDirectory(at: tex, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        for url in [folder.appendingPathComponent("pandoc"), tex.appendingPathComponent("pdflatex"), tex.appendingPathComponent("xelatex")] {
            try "#!/bin/sh\n".write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        }
        let locations = ToolLocations(commandDirectories: [folder], libreOfficeApps: [], texDirectories: [tex])
        let tools = ExternalTools.detect(in: locations, ignoring: [])
        #expect(tools.pandoc?.lastPathComponent == "pandoc")
        // XeLaTeX handles Unicode; pdfLaTeX is the last resort.
        #expect(tools.pdfEngine?.lastPathComponent == "xelatex")
        #expect(ExternalTools.detect(in: locations, ignoring: ["pandoc"]).pandoc == nil)
        let withoutPandoc = ToolLocations(commandDirectories: [], libreOfficeApps: [], texDirectories: [tex])
        #expect(ExternalTools.detect(in: withoutPandoc, ignoring: []).pdfEngine == nil)
    }

    @Test(.enabled(if: FileManager.default.isExecutableFile(atPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice")))
    func wordToPDFGoesThroughLibreOffice() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-word-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let docx = folder.appendingPathComponent("Report.docx")
        try MarkdownDocx.write(MarkdownDocument(parsing: "# Report\n\n| a | b |\n|---|---|\n| 1 | 2 |\n"), baseURL: folder, to: docx)
        let output = try await Converter.convert(docx, to: .pdf, tools: office) { _ in }
        #expect(output.map(\.lastPathComponent) == ["Report.pdf"])
        #expect(PDFDocument(url: output[0])?.string?.contains("Report") == true)
    }
}
