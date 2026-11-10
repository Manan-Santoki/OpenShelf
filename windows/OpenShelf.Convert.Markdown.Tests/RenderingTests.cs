using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ImageMagick;

namespace OpenShelf.Convert.Markdown.Tests;

/// <summary>Port of MarkdownRenderingTests plus golden checks of every renderer.</summary>
public class RenderingTests
{
    private readonly MarkdownDocument _document = MarkdownDocument.Parse(Samples.Pandoc);

    private static string Body(string markdown) => new MarkdownHtml().Body(MarkdownDocument.Parse(markdown));

    [Fact]
    public void HtmlIsWellFormedAndStructured()
    {
        var body = new MarkdownHtml().Body(_document);
        // Well-formed XHTML, as EPUB needs.
        XDocument.Parse("<body>" + body + "</body>");
        foreach (var tag in new[] { "<h1 id=\"an-h1-header\">", "<blockquote>", "<ol>", "<ul>", "<pre><code class=\"language-python\">", "<table>", "<caption>", "<dl>", "<hr/>", "<section class=\"footnotes\"" })
            Assert.True(body.Contains(tag), $"missing {tag}");
        Assert.DoesNotContain("**", body);
        Assert.DoesNotContain("===", body);
        Assert.Contains("<title>hello</title>", MarkdownHtml.Page(_document, "hello"));
    }

    [Fact]
    public void ScriptLinksAreDropped()
    {
        var body = Body("[click](javascript:alert(1)) <b onclick=\"x\">bold</b>");
        Assert.DoesNotContain("javascript", body);
        Assert.DoesNotContain("onclick", body);
        Assert.Contains("click", body);
    }

    [Fact]
    public void HtmlGoldenForEveryConstruct()
    {
        var document = MarkdownDocument.Parse(Samples.Everything);
        var body = new MarkdownHtml().Body(document);
        XDocument.Parse("<body>" + body + "</body>");
        var expected = new[]
        {
            "<h1 class=\"title\">Everything</h1>",
            "<h1 id=\"top\">Top</h1>",
            "<em>emphasis</em>, <strong>strong</strong>, <del>gone</del>, <code>code</code>",
            "<a href=\"https://example.com\" title=\"Tip\">link</a>",
            "“quotes” and 1–2 – 3—4…",
            "hard break<br/>after it.",
            "a soft break",
            "<span class=\"math\">x²</span>",
            "<sup><a href=\"#fn1\" id=\"fnref1\" class=\"footnote-ref\" role=\"doc-noteref\">1</a></sup>",
            "<sup><a href=\"#fn2\" id=\"fnref2\" class=\"footnote-ref\" role=\"doc-noteref\">2</a></sup>",
            "<blockquote>\n<p>quoted</p>\n</blockquote>",
            "<ul>\n<li>☐ open</li>\n<li>☑ done</li>\n</ul>",
            "<ol start=\"3\">\n<li>three</li>\n<li>four</li>\n</ol>",
            "<th style=\"text-align: left\">Left</th><th style=\"text-align: right\">Right</th>",
            "<td style=\"text-align: left\">a</td><td style=\"text-align: right\">1</td>",
            "<dl>\n<dt>Term</dt>\n<dd>Definition</dd>\n</dl>",
            "<div class=\"line-block\">line one<br/>\u00A0\u00A0line two</div>",
            "<p class=\"math display\">a = b/2</p>",
            "<pre><code class=\"language-python\">print(&quot;hi &lt;&amp;&gt;&quot;)</code></pre>",
            "<hr/>",
            "<section class=\"footnotes\" role=\"doc-endnotes\">",
            "<li id=\"fn1\"><p>The note. <a href=\"#fnref1\" class=\"footnote-back\" role=\"doc-backlink\">↩︎</a></p></li>",
            "<li id=\"fn2\"><p>an inline note <a href=\"#fnref2\" class=\"footnote-back\" role=\"doc-backlink\">↩︎</a></p></li>",
        };
        foreach (var snippet in expected) Assert.True(body.Contains(snippet), $"missing {snippet}\n---\n{body}");
        Assert.Equal("Everything", document.Title);
    }

    [Fact]
    public void HtmlPageHasItsStylesheetAndTitle()
    {
        var page = MarkdownHtml.Page(MarkdownDocument.Parse("# Hi"), "fallback");
        Assert.StartsWith("<!DOCTYPE html>", page);
        Assert.Contains("<title>fallback</title>", page);
        Assert.Contains("prefers-color-scheme: dark", page);
        XDocument.Parse(page.Replace("<!DOCTYPE html>", ""));
    }

    [Fact]
    public void PrintPageHasPaperRulesAndNoNetwork()
    {
        var page = MarkdownHtml.PrintPage(_document, "t", PaperSize.A4, _ => null);
        Assert.Contains("@page { size: A4;", page);
        Assert.Contains("break-after: avoid", page);
        Assert.Contains("Content-Security-Policy", page);
        Assert.Contains("@page { size: Letter;", MarkdownHtml.PrintPage(_document, "t", PaperSize.Letter, _ => null));
    }

    [Fact]
    public void PlainTextHasNoMarkup()
    {
        var text = MarkdownPlainText.Text(_document);
        Assert.StartsWith("An h1 header\n============\n", text);
        Assert.Contains("2nd paragraph. Italic, bold, and monospace.", text);
        Assert.Contains("• this one", text);
        Assert.Contains("a website (http://foo.bar)", text);
        Assert.Contains("10    hemp canvas  natural", text);
        Assert.Contains("[1] Footnote text goes here.", text);
        Assert.DoesNotContain("**", text);
    }

    [Fact]
    public void DocxHasWordStylesListsTablesAndFootnotes()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("hello.docx");
        MarkdownDocx.Write(_document, folder.Path, path, paper: PaperSize.Letter);

        foreach (var part in new[] { "[Content_Types].xml", "word/document.xml", "word/styles.xml", "word/numbering.xml", "word/footnotes.xml", "word/settings.xml", "word/_rels/document.xml.rels", "word/_rels/footnotes.xml.rels", "docProps/core.xml", "_rels/.rels" })
            Assert.NotNull(Zips.Xml(path, part));
        var body = Zips.Text(path, "word/document.xml");
        foreach (var snippet in new[] { "w:val=\"Heading1\"", "w:val=\"Heading2\"", "<w:numPr>", "<w:tbl>", "<w:footnoteReference w:id=\"1\"/>", "w:val=\"SourceCode\"", "w:val=\"BlockText\"", "w:anchor=\"an-h2-header\"", "w:name=\"an-h2-header\"" })
            Assert.True(body.Contains(snippet), $"missing {snippet}");
        Assert.DoesNotContain("**bold**", body);
        Assert.Contains("<w:pgSz w:w=\"12240\" w:h=\"15840\"/>", body);

        var styles = Zips.Text(path, "word/styles.xml");
        foreach (var id in new[] { "Normal", "Title", "Heading1", "Heading6", "Compact", "BlockText", "SourceCode", "VerbatimChar", "Hyperlink", "FootnoteText", "FootnoteReference", "TableCaption", "Table" })
            Assert.True(styles.Contains($"w:styleId=\"{id}\""), $"missing style {id}");

        var numbering = Zips.Xml(path, "word/numbering.xml");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        // Two numbered lists in the sample, each with an instance that restarts the count.
        Assert.True(numbering.Descendants(w + "num").Count() >= 3);
        var footnotes = Zips.Xml(path, "word/footnotes.xml");
        Assert.Contains(footnotes.Descendants(w + "footnote"), f => (string?)f.Attribute(w + "id") == "1");
        Assert.Contains("Footnote text goes here.", footnotes.ToString());
        Assert.Contains("[Content_Types].xml", Zips.Names(path));
        Assert.Contains("hemp canvas", body);
        Assert.Contains("An h1 header", body);
    }

    [Fact]
    public void DocxUsesA4PaperWhenAsked()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("a4.docx");
        MarkdownDocx.Write(MarkdownDocument.Parse("hello"), folder.Path, path, paper: PaperSize.A4);
        Assert.Contains("<w:pgSz w:w=\"11906\" w:h=\"16838\"/>", Zips.Text(path, "word/document.xml"));
    }

    [Fact]
    public void DocxWithoutFootnotesHasNoFootnotePart()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("plain.docx");
        MarkdownDocx.Write(MarkdownDocument.Parse("# Title\n\nJust text."), folder.Path, path);
        Assert.DoesNotContain("word/footnotes.xml", Zips.Names(path));
        Assert.DoesNotContain("footnotes", Zips.Text(path, "[Content_Types].xml"));
    }

    [Fact]
    public void DocxEmbedsLocalPicturesOnly()
    {
        using var folder = new TempFolder();
        using (var dot = new MagickImage(MagickColors.Red, 40, 20)) dot.Write(folder.Combine("dot.png"), MagickFormat.Png);
        var document = MarkdownDocument.Parse("![A dot](dot.png)\n\n![Remote](https://example.com/x.png)\n");
        var path = folder.Combine("pictures.docx");
        MarkdownDocx.Write(document, folder.Path, path);
        Assert.NotNull(Zips.Entry(path, "word/media/image1.png"));
        Assert.Null(Zips.Entry(path, "word/media/image2.png"));
        var body = Zips.Text(path, "word/document.xml");
        Assert.Contains("<w:drawing>", body);
        Assert.Contains("[Remote]", body);
        Assert.Contains("media/image1.png", Zips.Text(path, "word/_rels/document.xml.rels"));
        Zips.Xml(path, "word/document.xml");
    }

    [Fact]
    public void RtfIsBalancedAndHasTheContent()
    {
        var rtf = MarkdownRtf.Render(_document, null, PaperSize.Letter);
        Assert.StartsWith("{\\rtf1", rtf);
        Assert.True(Balanced(rtf), "braces are not balanced");
        foreach (var snippet in new[] { "An h1 header", "hemp canvas", "\\trowd", "\\cell", "\\row", "HYPERLINK \"http://foo.bar\"", "\\footnote", "Footnote text goes here.", "\\b ", "\\i ", "ω", "Let me re-iterate" })
        {
            var needle = snippet == "ω" ? "\\u969?" : snippet;
            Assert.True(rtf.Contains(needle), $"missing {snippet}");
        }
        Assert.DoesNotContain("**", rtf);
        Assert.All(rtf, c => Assert.True(c < 128, "RTF must be 7-bit"));
    }

    [Fact]
    public void RtfEmbedsPictures()
    {
        using var folder = new TempFolder();
        using (var dot = new MagickImage(MagickColors.Blue, 30, 30)) dot.Write(folder.Combine("blue.png"), MagickFormat.Png);
        var rtf = MarkdownRtf.Render(MarkdownDocument.Parse("![Blue](blue.png)"), folder.Path, PaperSize.A4);
        Assert.Contains("\\pngblip", rtf);
        Assert.Contains("\\picw30", rtf);
        Assert.True(Balanced(rtf));
    }

    [Fact]
    public void EpubHasTheRequiredStructure()
    {
        using var folder = new TempFolder();
        using (var dot = new MagickImage(MagickColors.Green, 30, 20)) dot.Write(folder.Combine("dot.png"), MagickFormat.Png);
        var source = folder.File("book.md", "# Book\n\nHello ![dot](dot.png) world.\n\n[^1]: note\n");
        var result = Convert(source, OutputFormat.Epub);
        var epub = result[0];

        // The mimetype entry comes first and is stored.
        using (var zip = ZipFile.OpenRead(epub))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        }
        Assert.Equal("application/epub+zip", Zips.Text(epub, "mimetype"));
        Assert.Contains("OEBPS/content.opf", Zips.Text(epub, "META-INF/container.xml"));
        var opf = Zips.Text(epub, "OEBPS/content.opf");
        Zips.Xml(epub, "OEBPS/content.opf");
        Assert.Contains("properties=\"nav\"", opf);
        Assert.Contains("<dc:title>book</dc:title>", opf);
        Assert.Contains("images/image1.jpg", opf);
        Assert.Contains("<itemref idref=\"c1\"/>", opf);
        Zips.Xml(epub, "OEBPS/nav.xhtml");
        var chapter = Zips.Text(epub, "OEBPS/chapter1.xhtml");
        Zips.Xml(epub, "OEBPS/chapter1.xhtml");
        Assert.Contains("<img src=\"images/image1.jpg\"", chapter);
        var jpeg = Zips.Entry(epub, "OEBPS/images/image1.jpg")!;
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    [Fact]
    public void EveryBuiltInFormatComesOutForMarkdown()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", Samples.Pandoc);
        foreach (var format in new[] { OutputFormat.Docx, OutputFormat.Txt, OutputFormat.Epub, OutputFormat.Html, OutputFormat.Rtf })
        {
            var output = Convert(source, format);
            Assert.Equal([$"notes.{format.FileExtension()}"], output.Select(Path.GetFileName));
        }
        Assert.Contains("An h1 header", File.ReadAllText(folder.Combine("notes.rtf")));
        Zips.Xml(folder.Combine("notes.epub"), "OEBPS/chapter1.xhtml");
        Assert.StartsWith("An h1 header\n====", File.ReadAllText(folder.Combine("notes.txt")));
        Assert.Contains("<strong>bold</strong>", File.ReadAllText(folder.Combine("notes.html")));
        Assert.Equal(new[] { "notes.docx", "notes.epub", "notes.html", "notes.md", "notes.rtf", "notes.txt" }, folder.Names());
    }

    [Fact]
    public void NamesAreNeverOverwritten()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes");
        folder.File("notes.html", "mine");
        var output = Convert(source, OutputFormat.Html);
        Assert.Equal("notes 2.html", Path.GetFileName(output[0]));
        Assert.Equal("mine", File.ReadAllText(folder.Combine("notes.html")));
    }

    [Fact]
    public void WindowsEncodedFilesAreRead()
    {
        using var folder = new TempFolder();
        var source = folder.Combine("legacy.md");
        File.WriteAllBytes(source, Encoding.GetEncoding(1252).GetBytes("# Caf\u00E9\n\nna\u00EFve"));
        var output = Convert(source, OutputFormat.Txt);
        Assert.Contains("Café", File.ReadAllText(output[0]));
    }

    [Fact]
    public void UnsupportedFormatsAreRefused()
    {
        var engine = new MarkdownEngine();
        Assert.False(engine.CanConvert(new ConversionRequest(@"C:\x\notes.md", OutputFormat.Png)));
        Assert.False(engine.CanConvert(new ConversionRequest(@"C:\x\notes.txt", OutputFormat.Docx)));
        Assert.True(engine.CanConvert(new ConversionRequest(@"C:\x\notes.MARKDOWN", OutputFormat.Pdf)));
    }

    private static IReadOnlyList<string> Convert(string source, OutputFormat format)
    {
        var registry = new EngineRegistry();
        MarkdownEngines.Register(registry);
        var converter = new Converter(registry, new ExternalToolsProvider(() => ExternalTools.None));
        return converter.ConvertAsync(new ConversionRequest(source, format)).GetAwaiter().GetResult();
    }

    private static bool Balanced(string rtf)
    {
        var depth = 0;
        for (var i = 0; i < rtf.Length; i++)
        {
            switch (rtf[i])
            {
                case '\\': i++; break;
                case '{': depth++; break;
                case '}': depth--; if (depth < 0) return false; break;
            }
        }
        return depth == 0;
    }
}
