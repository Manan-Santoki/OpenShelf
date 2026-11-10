using System.IO.Compression;
using System.Text;
using OpenShelf.Convert.Documents;

namespace OpenShelf.Convert.Documents.Tests;

public class StructureTests
{
    [Theory]
    [InlineData(24, 12, false, 1)]
    [InlineData(18, 12, false, 2)]
    [InlineData(14, 12, false, 3)]
    [InlineData(12.7, 12, true, 3)]
    [InlineData(12.7, 12, false, null)]
    [InlineData(12, 12, true, null)]
    public void HeadingLevelFollowsTheRatioToBodyText(double size, double body, bool bold, int? expected) =>
        Assert.Equal(expected, DocumentStructure.HeadingLevel(size, body, bold, 20));

    [Fact]
    public void LongLinesAreNeverHeadings() => Assert.Null(DocumentStructure.HeadingLevel(30, 12, false, 201));

    [Theory]
    [InlineData("• apples", false)]
    [InlineData("1. first", true)]
    [InlineData("2) second", true)]
    [InlineData("\t1\tItem", true)]
    [InlineData("\t•\tItem", false)]
    [InlineData("Plain words", null)]
    [InlineData("2024 was a good year", null)]
    public void ListMarkersAreRecognised(string text, bool? ordered) => Assert.Equal(ordered, DocumentStructure.ListMarkerKind(text));

    [Fact]
    public void NormalizeFindsHeadingsAndTypedLists()
    {
        var blocks = DocumentStructure.Normalize(
        [
            new ParagraphBlock([new TextRun("Big title", Size: 24)]),
            new ParagraphBlock([new TextRun("Body text that is long enough to be the body. " + new string('x', 80), Size: 12)]),
            new ParagraphBlock([new TextRun("• apples", Size: 12)]),
            new ParagraphBlock([new TextRun("1. one", Size: 12)]),
            new ParagraphBlock([new TextRun("   ", Size: 12)]),
        ]);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(blocks[0]).Level);
        Assert.IsType<ParagraphBlock>(blocks[1]);
        Assert.False(Assert.IsType<ListItemBlock>(blocks[2]).Ordered);
        Assert.Equal("apples", Texts.Of(((ListItemBlock)blocks[2]).Runs));
        Assert.True(Assert.IsType<ListItemBlock>(blocks[3]).Ordered);
        Assert.Equal(4, blocks.Count);   // the blank paragraph is gone
    }

    [Fact]
    public void MarkdownMatchesTheMac()
    {
        var markdown = DocumentStructure.Markdown(
        [
            new HeadingBlock(2, [new TextRun("Title")]),
            new ParagraphBlock([new TextRun("a "), new TextRun("bold ", Bold: true), new TextRun("and "), new TextRun("both", Bold: true, Italic: true), new TextRun(" snake_case "), new TextRun("link", Link: "https://x.test")]),
            new ListItemBlock(false, 0, [new TextRun("one")]),
            new ListItemBlock(false, 0, [new TextRun("two")]),
            new ListItemBlock(true, 0, [new TextRun("first")]),
            new ListItemBlock(true, 0, [new TextRun("second")]),
        ]);
        Assert.Equal("## Title\n\na **bold** and ***both*** snake\\_case [link](https://x.test)\n\n- one\n- two\n\n1. first\n2. second\n", markdown);
    }

    [Fact]
    public void MarkdownKeepsEmphasisMarkersHuggingTheText()
    {
        var text = DocumentStructure.Markdown([new TextRun("say "), new TextRun(" hi ", Bold: true), new TextRun("now")]);
        Assert.Equal("say  **hi** now", text);
    }

    [Fact]
    public void MarkdownTablesAndNestedLists()
    {
        var markdown = DocumentStructure.Markdown(
        [
            new TableBlock([[[new TextRun("A")], [new TextRun("B|C")]], [[new TextRun("1")], [new TextRun("2")]]], true),
            new ListItemBlock(false, 0, [new TextRun("outer")]),
            new ListItemBlock(false, 1, [new TextRun("inner")]),
        ]);
        Assert.Equal("| A | B\\|C |\n| --- | --- |\n| 1 | 2 |\n\n- outer\n   - inner\n", markdown);
    }

    [Fact]
    public void XhtmlWrapsRunsListsAndBreaks()
    {
        var html = DocumentStructure.Xhtml(
        [
            new HeadingBlock(1, [new TextRun("T & U")]),
            new ParagraphBlock([new TextRun("a\nb "), new TextRun("x", Italic: true, Bold: true, Link: "https://x.test/?a=1&b=2")]),
            new ListItemBlock(false, 0, [new TextRun("one")]),
            new ListItemBlock(false, 1, [new TextRun("nested")]),
            new ListItemBlock(true, 0, [new TextRun("num")]),
        ]);
        Assert.Contains("<h1>T &amp; U</h1>", html);
        Assert.Contains("<p>a<br/>b <a href=\"https://x.test/?a=1&amp;b=2\"><strong><em>x</em></strong></a></p>", html);
        Assert.Contains("<ul><li>one<ul><li>nested</li></ul></li></ul><ol><li>num</li></ol>", html.Replace("\n", ""));
    }

    [Fact]
    public void PlainTextNumbersListsAndSeparatesBlocks()
    {
        var text = DocumentStructure.PlainText(
        [
            new HeadingBlock(1, [new TextRun("Head")]),
            new ListItemBlock(true, 0, [new TextRun("a")]),
            new ListItemBlock(true, 0, [new TextRun("b")]),
            new ParagraphBlock([new TextRun("end")]),
        ]);
        Assert.Equal("Head\n\n1. a\n2. b\n\nend\n", text);
    }

    [Fact]
    public void ParagraphsSplitAtBlankLines() =>
        Assert.Equal(["one\ntwo", "three"], DocumentStructure.Paragraphs("one\r\ntwo\r\n \r\n\r\nthree\r\n"));
}

public class PdfTextRuleTests
{
    [Fact]
    public void WrappedLinesJoinIntoParagraphs()
    {
        var long1 = new string('a', 70) + " first";
        var text = $"{long1}\n{long1}\nshort end\n\nnext paragraph";
        var paragraphs = PdfText.Paragraphs(text);
        Assert.Equal(2, paragraphs.Count);
        Assert.StartsWith("aaaa", paragraphs[0]);
        Assert.EndsWith("short end", paragraphs[0]);
        Assert.Equal("next paragraph", paragraphs[1]);
    }

    [Fact]
    public void HyphenatedWordsAreRejoined()
    {
        var text = new string('b', 70) + " inter-\nnational word " + new string('c', 70);
        Assert.Contains("international word", PdfText.Paragraphs(text)[0]);
    }

    [Fact]
    public void PagesOfShortLinesKeepEveryLineApart() =>
        Assert.Equal(["one", "two", "three"], PdfText.Paragraphs("one\ntwo\n\nthree"));

    [Fact]
    public void RowsSplitAtTabsAndRunsOfSpaces()
    {
        var rows = PdfText.Rows("Name  Qty\tPrice\n\nApples  3  0.50\nsingle words here");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Name", "Qty", "Price"], rows[0]);
        Assert.Equal(["Apples", "3", "0.50"], rows[1]);
        Assert.Equal(["single words here"], rows[2]);
    }
}

public class TextDecodingTests
{
    [Fact]
    public void ByteOrderMarksSettleTheEncoding()
    {
        Assert.Equal("héllo", TextDecoding.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("héllo")]));
        Assert.Equal("héllo", TextDecoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("héllo")]));
        Assert.Equal("héllo", TextDecoding.Decode([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("héllo")]));
    }

    [Fact]
    public void InvalidUtf8FallsBackToWindows1252() =>
        Assert.Equal("café “quoted”", TextDecoding.Decode([0x63, 0x61, 0x66, 0xE9, 0x20, 0x93, 0x71, 0x75, 0x6F, 0x74, 0x65, 0x64, 0x94]));

    [Fact]
    public void EmptyIsEmpty() => Assert.Equal("", TextDecoding.Decode([]));
}

public class ReaderWriterTests
{
    private static string Plain(IEnumerable<Block> blocks) => DocumentStructure.PlainText(blocks.ToList());

    [Fact]
    public void DocxRoundTripKeepsStructure()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("r.docx");
        WordWriter.Write(Fixtures.RichModel(), path);
        var read = DocxReader.Read(path);

        Assert.Equal("r", read.Title);
        var heading = Assert.IsType<HeadingBlock>(read.Blocks[0]);
        Assert.Equal(1, heading.Level);
        Assert.Equal("Annual Report", Texts.Of(heading.Runs));
        var paragraph = Assert.IsType<ParagraphBlock>(read.Blocks[1]);
        Assert.Contains(paragraph.Runs, r => r.Text == "bold" && r.Bold && !r.Italic);
        Assert.Contains(paragraph.Runs, r => r.Text == "italic" && r.Italic);
        Assert.Contains(paragraph.Runs, r => r.Text == "underlined" && r.Underline);
        Assert.Contains(paragraph.Runs, r => r.Text == "the site" && r.Link == "https://example.com/a?b=1&c=2");
        var items = read.Blocks.OfType<ListItemBlock>().ToList();
        Assert.Equal([false, false, true, true], items.Select(i => i.Ordered));
        Assert.Equal("step two", Texts.Of(items[3].Runs));
        var table = read.Blocks.OfType<TableBlock>().Single();
        Assert.True(table.HasHeader);
        Assert.Equal("Pears", Texts.Of(table.Rows[2][0]));
        Assert.Equal("12", Texts.Of(table.Rows[2][1]));
        var image = read.Blocks.OfType<ImageBlock>().Single();
        Assert.Equal("png", image.Extension);
        Assert.Equal(40, image.Width);
        Assert.Equal("blue box", image.Alt);
        Assert.Equal("The end.", Texts.Of(((ParagraphBlock)read.Blocks[^1]).Runs));
    }

    [Fact]
    public void DocxPartsAreWellFormed()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("r.docx");
        WordWriter.Write(Fixtures.RichModel(), path);
        foreach (var name in Fixtures.ZipNames(path).Where(n => n.EndsWith(".xml") || n.EndsWith(".rels")))
            System.Xml.Linq.XDocument.Parse(Fixtures.ZipText(path, name));
        Assert.Contains("word/media/image1.png", Fixtures.ZipNames(path));
        Assert.Contains("w:hyperlink", Fixtures.ZipText(path, "word/document.xml"));
    }

    [Fact]
    public void DocxReaderReadsWordStyleNamesNumberingAndFieldLinks()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("word.docx");
        const string w = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        using (var file = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Zips.Add(zip, "word/document.xml", $"<w:document {w}><w:body>" +
                "<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr><w:r><w:t>Section</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:pStyle w:val=\"ListBullet\"/></w:pPr><w:r><w:t>from style numbering</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t xml:space=\"preserve\">Go to </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> HYPERLINK \"https://a.test/\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>a site</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:t xml:space=\"preserve\"> now</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>before</w:t></w:r><w:r><w:br w:type=\"page\"/></w:r><w:r><w:t>after</w:t></w:r></w:p>" +
                "</w:body></w:document>");
            Zips.Add(zip, "word/styles.xml", $"<w:styles {w}><w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"ListBullet\"><w:name w:val=\"List Bullet\"/><w:pPr><w:numPr><w:numId w:val=\"3\"/></w:numPr></w:pPr></w:style></w:styles>");
            Zips.Add(zip, "word/numbering.xml", $"<w:numbering {w}><w:abstractNum w:abstractNumId=\"7\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum><w:num w:numId=\"3\"><w:abstractNumId w:val=\"7\"/></w:num></w:numbering>");
        }
        var model = DocxReader.Read(path);
        Assert.Equal(2, Assert.IsType<HeadingBlock>(model.Blocks[0]).Level);
        var item = Assert.IsType<ListItemBlock>(model.Blocks[1]);
        Assert.False(item.Ordered);
        var link = Assert.IsType<ParagraphBlock>(model.Blocks[2]);
        Assert.Contains(link.Runs, r => r.Text == "a site" && r.Link == "https://a.test/");
        Assert.DoesNotContain(link.Runs, r => r.Text.Contains("HYPERLINK"));
        Assert.Contains(model.Blocks, b => b is PageBreakBlock);
        Assert.Equal("after", Texts.Of(((ParagraphBlock)model.Blocks[^1]).Runs));
    }

    [Fact]
    public void DocxThatIsNotAZipIsUnreadable()
    {
        using var folder = new TempFolder();
        var path = folder.File("bad.docx", "not a zip");
        var error = Assert.Throws<ConversionException>(() => DocxReader.Read(path));
        Assert.Equal(ConversionErrorKind.Unreadable, error.Kind);
    }

    [Fact]
    public void HtmlReaderReadsStructure()
    {
        var png = System.Convert.ToBase64String(Fixtures.Png(10, 5));
        var model = HtmlReader.Parse(
            "<html><head><title>My Page</title><style>p{color:red}</style></head><body>" +
            "<h1>Heading <em>one</em></h1><p>Some <b>bold</b> and <i>italic</i> and <a href=\"https://e.test/x\">a link</a>.<br>Second line</p>" +
            "<ul><li>apple</li><li>pear<ul><li>nested</li></ul></li></ul><ol><li>one</li><li>two</li></ol>" +
            "<table><tr><th>Name</th><th>Qty</th></tr><tr><td>Fig</td><td>4</td></tr></table>" +
            $"<img src=\"data:image/png;base64,{png}\" alt=\"dot\" width=\"20\"><script>var x = 1;</script></body></html>", "fallback");
        Assert.Equal("My Page", model.Title);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(model.Blocks[0]).Level);
        var paragraph = Assert.IsType<ParagraphBlock>(model.Blocks[1]);
        Assert.Contains(paragraph.Runs, r => r.Text == "bold" && r.Bold);
        Assert.Contains(paragraph.Runs, r => r.Text == "a link" && r.Link == "https://e.test/x");
        Assert.Contains("\nSecond line", Texts.Of(paragraph.Runs));
        var items = model.Blocks.OfType<ListItemBlock>().ToList();
        Assert.Equal(["apple", "pear", "nested", "one", "two"], items.Select(i => Texts.Of(i.Runs)));
        Assert.Equal([0, 0, 1, 0, 0], items.Select(i => i.Level));
        Assert.Equal([false, false, false, true, true], items.Select(i => i.Ordered));
        var table = model.Blocks.OfType<TableBlock>().Single();
        Assert.True(table.HasHeader);
        Assert.Equal("Fig", Texts.Of(table.Rows[1][0]));
        var image = model.Blocks.OfType<ImageBlock>().Single();
        Assert.Equal((20, 10), (image.Width, image.Height));
        Assert.DoesNotContain("var x", Plain(model.Blocks));
    }

    [Fact]
    public void HtmlReaderFindsPicturesBesideTheFile()
    {
        using var folder = new TempFolder();
        folder.Bytes("pics/dot.png", Fixtures.Png(8, 8));
        var path = folder.File("page.html", "<p>hi</p><img src=\"pics/dot.png\">");
        var model = HtmlReader.Read(path);
        Assert.Single(model.Blocks.OfType<ImageBlock>());
    }

    [Fact]
    public void RtfReaderReadsTextStylesEscapesAndParagraphs()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}" +
            @"\pard\fs48 Big Title\par\pard\fs24 Body with {\b bold} and {\i italic} caf\'e9 \" + @"u8364? and \'93quoted\'94.\par" +
            @"{\listtext\f1\'b7\tab}Bullet item\par" +
            @"{\field{\*\fldinst{HYPERLINK ""https://r.test/""}}{\fldrslt{\ul a link}}} follows\par}";
        var blocks = RtfReader.Parse(rtf);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(blocks[0]).Level);
        var body = Assert.IsType<ParagraphBlock>(blocks[1]);
        Assert.Contains(body.Runs, r => r.Text == "bold" && r.Bold);
        Assert.Contains(body.Runs, r => r.Text == "italic" && r.Italic);
        var text = Texts.Of(body.Runs);
        Assert.Contains("café € and “quoted”.", text);
        var link = blocks.OfType<ParagraphBlock>().Last();
        Assert.Contains(link.Runs, r => r.Text == "a link" && r.Link == "https://r.test/");
        Assert.DoesNotContain("HYPERLINK", Plain(blocks));
        Assert.DoesNotContain("Arial", Plain(blocks));
    }

    [Fact]
    public void RtfReaderReadsTables()
    {
        var rtf = @"{\rtf1\ansi\trowd\cellx3000\cellx6000\intbl A1\cell B1\cell\row\trowd\cellx3000\cellx6000\intbl A2\cell B2\cell\row\pard After\par}";
        var blocks = RtfReader.Parse(rtf);
        var table = Assert.IsType<TableBlock>(blocks[0]);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("B2", Texts.Of(table.Rows[1][1]));
        Assert.Equal("After", Texts.Of(Assert.IsType<ParagraphBlock>(blocks[1]).Runs));
    }

    [Fact]
    public void RtfRoundTrips()
    {
        var rtf = RtfWriter.Build(Fixtures.RichModel());
        Assert.StartsWith("{\\rtf1", rtf);
        var blocks = RtfReader.Parse(Encoding.Latin1.GetBytes(rtf), null);
        var text = Plain(blocks);
        Assert.Contains("Annual Report", text);
        Assert.Contains("the site", text);
        Assert.Contains("step two", text);
        Assert.Contains("Pears", text);
        Assert.Contains(blocks.OfType<ParagraphBlock>().SelectMany(p => p.Runs), r => r.Link == "https://example.com/a?b=1&c=2");
        Assert.Single(blocks.OfType<ImageBlock>());
        Assert.IsType<HeadingBlock>(blocks[0]);
    }

    [Fact]
    public void RtfWriterEscapesUnicode()
    {
        var rtf = RtfWriter.Build(new DocumentModel("t", [new ParagraphBlock([new TextRun("naïve {x} \\ €")])]));
        Assert.Contains("na\\u239?ve \\{x\\} \\\\ \\u8364?", rtf);
        Assert.Equal("naïve {x} \\ €", Texts.Of(Assert.IsType<ParagraphBlock>(RtfReader.Parse(rtf)[0]).Runs));
    }

    [Fact]
    public void OdtRoundTrips()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("r.odt");
        OdtWriter.Write(Fixtures.RichModel(), path);
        var model = OdtReader.Read(path);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(model.Blocks[0]).Level);
        var paragraph = Assert.IsType<ParagraphBlock>(model.Blocks[1]);
        Assert.Contains(paragraph.Runs, r => r.Text == "bold" && r.Bold);
        Assert.Contains(paragraph.Runs, r => r.Text == "italic" && r.Italic);
        Assert.Contains(paragraph.Runs, r => r.Text == "underlined" && r.Underline);
        Assert.Contains(paragraph.Runs, r => r.Link == "https://example.com/a?b=1&c=2");
        Assert.Equal([false, false, true, true], model.Blocks.OfType<ListItemBlock>().Select(i => i.Ordered));
        Assert.Equal("Pears", Texts.Of(model.Blocks.OfType<TableBlock>().Single().Rows[2][0]));
        Assert.Equal("odt", Fixtures.ZipNames(path)[0] == "mimetype" ? "odt" : "no mimetype first");
        System.Xml.Linq.XDocument.Parse(Fixtures.ZipText(path, "content.xml"));
    }

    [Fact]
    public void EpubIsAValidPackage()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("b.epub");
        var (body, images) = EpubWriter.BodyFor(Fixtures.RichModel().Blocks);
        EpubWriter.Write("A & B", [new EpubChapter("One", body), new EpubChapter("Two", "<p>2</p>")], path, images);

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);   // stored
        }
        Assert.Equal("application/epub+zip", Fixtures.ZipText(path, "mimetype"));
        foreach (var name in Fixtures.ZipNames(path).Where(n => n.EndsWith(".xml") || n.EndsWith(".xhtml") || n.EndsWith(".opf")))
            System.Xml.Linq.XDocument.Parse(Fixtures.ZipText(path, name));
        var opf = Fixtures.ZipText(path, "OEBPS/content.opf");
        Assert.Contains("A &amp; B", opf);
        Assert.Contains("chapter2.xhtml", opf);
        Assert.Contains("image/png", opf);
        Assert.Contains("images/image1.png", Fixtures.ZipText(path, "OEBPS/chapter1.xhtml"));
        Assert.Contains("OEBPS/images/image1.png", Fixtures.ZipNames(path));
        Assert.Contains("chapter1.xhtml", Fixtures.ZipText(path, "OEBPS/nav.xhtml"));
    }

    [Fact]
    public void PptxHasOneSlidePerPicture()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("deck.pptx");
        var jpeg = Pictures.EncodeJpeg(new SkiaSharp.SKBitmap(40, 30), 0.8);
        PptxWriter.Write([new SlidePicture(jpeg, 40, 30), new SlidePicture(jpeg, 40, 30), new SlidePicture(jpeg, 40, 30)], 720, 540, path);
        var names = Fixtures.ZipNames(path);
        Assert.Equal(3, names.Count(n => n.StartsWith("ppt/slides/slide") && n.EndsWith(".xml")));
        Assert.Equal(3, names.Count(n => n.StartsWith("ppt/media/")));
        foreach (var name in names.Where(n => n.EndsWith(".xml") || n.EndsWith(".rels"))) System.Xml.Linq.XDocument.Parse(Fixtures.ZipText(path, name));
        var presentation = Fixtures.ZipText(path, "ppt/presentation.xml");
        Assert.Contains($"cx=\"{720 * 12700}\" cy=\"{540 * 12700}\"", presentation);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(presentation, "<p:sldId ").Count);
        Assert.Contains("slide3.xml", Fixtures.ZipText(path, "ppt/_rels/presentation.xml.rels"));
    }
}
