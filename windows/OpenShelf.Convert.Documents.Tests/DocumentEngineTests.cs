using System.Text;
using OpenShelf.Convert.Documents;

namespace OpenShelf.Convert.Documents.Tests;

public class DocumentEngineTests
{
    private static readonly Converter NoTools = Fixtures.ConverterWith(Fixtures.None);

    private static string MakeDocx(TempFolder folder, string name = "Report.docx")
    {
        var path = folder.Combine(name);
        WordWriter.Write(Fixtures.RichModel(), path);
        return path;
    }

    private static async Task<string> Run(Converter converter, string path, OutputFormat format) =>
        Assert.Single(await converter.ConvertAsync(new ConversionRequest(path, format)));

    [Fact]
    public async Task DocxToTxt()
    {
        using var folder = new TempFolder();
        var text = File.ReadAllText(await Run(NoTools, MakeDocx(folder), OutputFormat.Txt));
        Assert.StartsWith("Annual Report\n\nPlain, bold, italic and underlined. See the site.", text);
        Assert.Contains("• first bullet\n• second bullet", text);
        Assert.Contains("1. step one\n2. step two", text);
        Assert.Contains("Apples\t3", text);
    }

    [Fact]
    public async Task DocxToMarkdown()
    {
        using var folder = new TempFolder();
        var markdown = File.ReadAllText(await Run(NoTools, MakeDocx(folder), OutputFormat.Md));
        Assert.StartsWith("# Annual Report\n\n", markdown);
        Assert.Contains("**bold**", markdown);
        Assert.Contains("*italic*", markdown);
        Assert.Contains("[the site](https://example.com/a?b=1&c=2)", markdown);
        Assert.Contains("## Lists\n\n- first bullet\n- second bullet\n\n1. step one\n2. step two", markdown);
        Assert.Contains("| Name | Qty |\n| --- | --- |\n| Apples | 3 |", markdown);
    }

    [Fact]
    public async Task DocxToEpubKeepsHeadingsListsTablesAndPictures()
    {
        using var folder = new TempFolder();
        var epub = await Run(NoTools, MakeDocx(folder), OutputFormat.Epub);
        var chapter = Fixtures.ZipText(epub, "OEBPS/chapter1.xhtml");
        Assert.Contains("<h1>Annual Report</h1>", chapter);
        Assert.Contains("<li>step two</li>", chapter);
        Assert.Contains("<th>Name</th>", chapter);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\">the site</a>", chapter);
        Assert.Contains("images/image1.png", chapter);
        Assert.Contains("OEBPS/images/image1.png", Fixtures.ZipNames(epub));
        System.Xml.Linq.XDocument.Parse(chapter);   // well-formed XHTML
    }

    [Fact]
    public async Task DocxToHtmlIsSelfContained()
    {
        using var folder = new TempFolder();
        var html = File.ReadAllText(await Run(NoTools, MakeDocx(folder), OutputFormat.Html));
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Report</title>", html);
        Assert.Contains("<h1>Annual Report</h1>", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        var back = HtmlReader.Parse(html, "x");
        Assert.Single(back.Blocks.OfType<ImageBlock>());
    }

    [Fact]
    public async Task DocxToRtfReadsBack()
    {
        using var folder = new TempFolder();
        var rtf = await Run(NoTools, MakeDocx(folder), OutputFormat.Rtf);
        var model = RtfReader.Read(rtf);
        Assert.Contains("Annual Report", DocumentStructure.PlainText(model.Blocks));
        Assert.StartsWith("{\\rtf1", File.ReadAllText(rtf));
    }

    [Fact]
    public async Task HtmlToDocxAndBack()
    {
        using var folder = new TempFolder();
        var html = folder.File("Page.html", "<html><head><title>T</title></head><body><h2>Sub</h2><p>Hello <b>world</b></p><ol><li>a</li><li>b</li></ol></body></html>");
        var docx = await Run(NoTools, html, OutputFormat.Docx);
        var model = DocxReader.Read(docx);
        Assert.Equal(2, Assert.IsType<HeadingBlock>(model.Blocks[0]).Level);
        Assert.Contains(((ParagraphBlock)model.Blocks[1]).Runs, r => r.Text == "world" && r.Bold);
        Assert.Equal([true, true], model.Blocks.OfType<ListItemBlock>().Select(i => i.Ordered));
    }

    [Fact]
    public async Task PlainTextToEverythingItCan()
    {
        using var folder = new TempFolder();
        var txt = folder.File("Notes.txt", "First paragraph\nstill first\n\nSecond paragraph\n");
        var docx = DocxReader.Read(await Run(NoTools, txt, OutputFormat.Docx));
        Assert.Equal(2, docx.Blocks.Count);
        Assert.Contains("\n", Texts.Of(((ParagraphBlock)docx.Blocks[0]).Runs));
        Assert.Equal("First paragraph\nstill first\n\nSecond paragraph\n", File.ReadAllText(await Run(NoTools, txt, OutputFormat.Md)));
        Assert.Contains("<p>First paragraph<br/>still first</p>", File.ReadAllText(await Run(NoTools, txt, OutputFormat.Html)).Replace("\r", ""));
        Assert.Contains("Second paragraph", Fixtures.ZipText(await Run(NoTools, txt, OutputFormat.Epub), "OEBPS/chapter1.xhtml"));
        Assert.Contains("Second paragraph", File.ReadAllText(await Run(NoTools, txt, OutputFormat.Rtf)));
    }

    [Fact]
    public async Task PlainTextInWindows1252AndUtf16IsRead()
    {
        using var folder = new TempFolder();
        var latin = folder.Bytes("Latin.txt", [0x63, 0x61, 0x66, 0xE9]);
        Assert.Contains("café", File.ReadAllText(await Run(NoTools, latin, OutputFormat.Html)));
        var utf16 = folder.Bytes("Wide.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("naïve text")]);
        Assert.Contains("naïve text", File.ReadAllText(await Run(NoTools, utf16, OutputFormat.Html)));
    }

    [Fact]
    public async Task RtfToMarkdownKeepsHeadingsBySize()
    {
        using var folder = new TempFolder();
        var rtf = folder.File("Doc.rtf", @"{\rtf1\ansi\pard\fs48 Title Here\par\pard\fs24 Body text of the document goes on for a while here.\par\pard\fs24 More body text, as a second paragraph.\par}");
        var markdown = File.ReadAllText(await Run(NoTools, rtf, OutputFormat.Md));
        Assert.Equal("# Title Here\n\nBody text of the document goes on for a while here.\n\nMore body text, as a second paragraph.\n", markdown);
    }

    [Fact]
    public async Task OdtToTextAndOdtFallbackWriter()
    {
        using var folder = new TempFolder();
        var odt = folder.Combine("Doc.odt");
        OdtWriter.Write(Fixtures.RichModel(), odt);
        var text = File.ReadAllText(await Run(NoTools, odt, OutputFormat.Txt));
        Assert.StartsWith("Annual Report", text);
        Assert.Contains("1. step one", text);
    }

    [Fact]
    public async Task OldWordFilesNeedLibreOffice()
    {
        using var folder = new TempFolder();
        var doc = folder.File("Old.doc", "binary junk");
        var error = await Assert.ThrowsAsync<ConversionException>(() => Run(NoTools, doc, OutputFormat.Txt));
        Assert.Contains("needs LibreOffice".Replace("needs", "reading it needs"), error.Message);
        Assert.Equal(["Old.doc"], folder.Names());
    }

    [Fact]
    public async Task ABrokenDocxIsUnreadableAndLeavesNothing()
    {
        using var folder = new TempFolder();
        var docx = folder.File("Broken.docx", "nope");
        var error = await Assert.ThrowsAsync<ConversionException>(() => Run(NoTools, docx, OutputFormat.Md));
        Assert.Equal("“Broken.docx” couldn’t be read.", error.Message);
        Assert.Equal(["Broken.docx"], folder.Names());
    }

    [Fact]
    public async Task RtfdPackagesAreRead()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("Note.rtfd"));
        folder.File("Note.rtfd/TXT.rtf", @"{\rtf1\ansi Packaged text\par}");
        Assert.Contains("Packaged text", File.ReadAllText(await Run(NoTools, folder.Combine("Note.rtfd"), OutputFormat.Txt)));
    }

    [Fact]
    public async Task DocumentsToPdfNeedEdgeAndSayWhenItIsMissing()
    {
        using var folder = new TempFolder();
        var converter = Fixtures.ConverterWith(Fixtures.None, null, new EdgePrinter(new FakeTool().Runner, () => null));
        var error = await Assert.ThrowsAsync<ConversionException>(() => Run(converter, MakeDocx(folder), OutputFormat.Pdf));
        Assert.Contains("Microsoft Edge isn’t installed", error.Message);
        Assert.Equal(["Report.docx"], folder.Names());
    }

    // ----- With a real Edge -----

    [Fact]
    public async Task DocxToPdfKeepsTextTablesAndPictures()
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var pdf = await Run(NoTools, MakeDocx(folder), OutputFormat.Pdf);
        Assert.Equal("Report.pdf", Path.GetFileName(pdf));
        var text = string.Join("\n", Fixtures.PageTexts(pdf));
        Assert.Contains("Annual Report", text);
        Assert.Contains("step two", text);
        Assert.Contains("Pears", text);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(["Report.docx", "Report.pdf"], folder.Names());
    }

    [Fact]
    public async Task PlainTextToPdfPaginatesLongText()
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var txt = folder.File("Long.txt", string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Line number {i} of the long text")));
        var pdf = await Run(NoTools, txt, OutputFormat.Pdf);
        var pages = Fixtures.PageTexts(pdf);
        Assert.InRange(pages.Length, 3, 6);
        Assert.Contains("Line number 1 ", pages[0]);
        Assert.Contains("Line number 200 ", pages[^1]);
    }

    [Fact]
    public async Task HtmlAndRtfToPdf()
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var html = folder.File("Web.html", "<h1>Web heading</h1><p>Web body</p>");
        Assert.Contains("Web heading", Fixtures.PageTexts(await Run(NoTools, html, OutputFormat.Pdf))[0]);
        var rtf = folder.File("Doc.rtf", @"{\rtf1\ansi Rich text body\par}");
        Assert.Contains("Rich text body", Fixtures.PageTexts(await Run(NoTools, rtf, OutputFormat.Pdf))[0]);
    }

    [Fact]
    public async Task SpreadsheetToPdfIsALandscapeTableWithARepeatedHeader()
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var csv = folder.File("Data.csv", "Name,Amount,Note\n" + string.Join("\n", Enumerable.Range(1, 120).Select(i => $"Item {i},{i * 3},note {i}")));
        var pdf = await Run(NoTools, csv, OutputFormat.Pdf);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.InRange(document.NumberOfPages, 2, 4);
        var first = document.GetPage(1);
        Assert.Equal((792, 612), ((int)Math.Round(first.Width), (int)Math.Round(first.Height)));
        foreach (var page in document.GetPages())
            Assert.Contains("Name", page.Text);   // the header repeats on every page
        Assert.Contains("Item 120", document.GetPage(document.NumberOfPages).Text);
        Assert.DoesNotContain("Item 120", first.Text);
    }

    [Fact]
    public async Task XlsxToPdfWorksToo()
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var xlsx = folder.Combine("Book.xlsx");
        XlsxWriter.Write([("Sheet", new List<IReadOnlyList<string>> { new[] { "A", "B" }, new[] { "one", "two" } })], xlsx);
        Assert.Contains("two", Fixtures.PageTexts(await Run(NoTools, xlsx, OutputFormat.Pdf))[0]);
    }

    [Theory]
    [InlineData(OutputFormat.Docx)]
    [InlineData(OutputFormat.Jpg)]
    [InlineData(OutputFormat.Png)]
    [InlineData(OutputFormat.Txt)]
    [InlineData(OutputFormat.Md)]
    [InlineData(OutputFormat.Epub)]
    [InlineData(OutputFormat.Pptx)]
    [InlineData(OutputFormat.Xlsx)]
    public async Task APdfPrintedByEdgeConvertsToEveryOutput(OutputFormat format)
    {
        if (!Fixtures.EdgeAvailable) return;
        using var folder = new TempFolder();
        var html = folder.File("Source.html", "<h1>Annual Report</h1><p>Sales rose in every region over the year, led by the north.</p><ul><li>Hire more</li><li>Ship more</li></ul>" +
            "<h1>Second Chapter</h1><p>Closing words.</p>");
        var pdf = folder.Combine("Source.pdf");
        await EdgePrinter.Default.PrintHtmlAsync(File.ReadAllText(html), pdf);
        Assert.True(Fixtures.PageCount(pdf) >= 1);

        var files = await NoTools.ConvertAsync(new ConversionRequest(pdf, format));
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 0));
        switch (format)
        {
            case OutputFormat.Txt: Assert.Contains("Sales rose in every region", File.ReadAllText(files[0])); break;
            case OutputFormat.Md: Assert.Contains("# Annual Report", File.ReadAllText(files[0])); break;
            case OutputFormat.Docx: Assert.Contains("Closing words", DocumentStructure.PlainText(DocxReader.Read(files[0]).Blocks)); break;
            case OutputFormat.Epub: Assert.Contains("Annual Report", Fixtures.ZipText(files[0], "OEBPS/chapter1.xhtml")); break;
            case OutputFormat.Xlsx: Assert.Contains("Annual", Fixtures.ZipText(files[0], "xl/worksheets/sheet1.xml")); break;
            case OutputFormat.Pptx: Assert.Equal(Fixtures.PageCount(pdf), Fixtures.ZipNames(files[0]).Count(n => n.StartsWith("ppt/media/"))); break;
            case OutputFormat.Jpg or OutputFormat.Png: Assert.Equal(Fixtures.PageCount(pdf), files.Count); break;
        }
    }
}
