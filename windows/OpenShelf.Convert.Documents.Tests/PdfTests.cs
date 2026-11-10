using System.IO.Compression;
using OpenShelf.Convert.Documents;
using SkiaSharp;

namespace OpenShelf.Convert.Documents.Tests;

public class PdfSourceTests
{
    private static readonly Converter Converter = Fixtures.ConverterWith(Fixtures.None);

    private static (string Heading, string Body) Sample => ("Quarterly Plan", "Revenue grew steadily across all regions this quarter.");

    /// <summary>Two pages: a heading, wrapped body text and a bullet on the first, a table-like row set on the second.</summary>
    private static byte[] TwoPagePdf() => Fixtures.Pdf(
        [
            ("Quarterly Plan", 26, 72, 720),
            ("Revenue grew steadily across all regions this quarter.", 11, 72, 690),
            ("Costs stayed flat while hiring slowed.", 11, 72, 674),
            ("\u2022 Launch the new product", 11, 72, 640),
        ],
        [
            ("Name", 11, 72, 720), ("Qty", 11, 250, 720), ("Price", 11, 400, 720),
            ("Apples", 11, 72, 704), ("3", 11, 250, 704), ("0.50", 11, 400, 704),
            ("Pears", 11, 72, 688), ("12", 11, 250, 688), ("1.25", 11, 400, 688),
        ]);

    private static async Task<IReadOnlyList<string>> Run(string path, OutputFormat format) =>
        await Converter.ConvertAsync(new ConversionRequest(path, format));

    [Fact]
    public async Task TextOutputHasThePagesText()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        var output = Assert.Single(await Run(pdf, OutputFormat.Txt));
        Assert.Equal("Plan.txt", Path.GetFileName(output));
        var text = File.ReadAllText(output);
        Assert.Contains("Quarterly Plan", text);
        Assert.Contains("Revenue grew steadily", text);
        Assert.Contains("Pears", text);
    }

    [Fact]
    public async Task MarkdownFindsHeadingsListsAndParagraphsPerPage()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        var markdown = File.ReadAllText(Assert.Single(await Run(pdf, OutputFormat.Md)));
        Assert.Contains("# Quarterly Plan", markdown);
        Assert.Contains("- Launch the new product", markdown);
        Assert.Contains("\n---\n", markdown);
        Assert.DoesNotContain("# Revenue", markdown);
        Assert.Contains("Revenue grew steadily across all regions this quarter.", markdown);
    }

    [Fact]
    public async Task DocxOfATextPdfHasStructure()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        var docx = Assert.Single(await Run(pdf, OutputFormat.Docx));
        var model = DocxReader.Read(docx);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(model.Blocks[0]).Level);
        Assert.Contains(model.Blocks, b => b is ListItemBlock);
        Assert.DoesNotContain(Fixtures.ZipNames(docx), n => n.StartsWith("word/media/"));
    }

    [Fact]
    public async Task ScannedPdfBecomesAnImageDocx()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Scan.pdf", Fixtures.BlankPdf(2));
        var docx = Assert.Single(await Run(pdf, OutputFormat.Docx));
        var media = Fixtures.ZipNames(docx).Where(n => n.StartsWith("word/media/")).ToList();
        Assert.Equal(2, media.Count);
        // 150 dpi: a Letter page is 1275 x 1650 pixels.
        using var zip = ZipFile.OpenRead(docx);
        using var bitmap = SKBitmap.Decode(Zips.ReadEntry(zip, media[0])!);
        Assert.Equal((1275, 1650), (bitmap.Width, bitmap.Height));
        Assert.Contains("<w:br w:type=\"page\"/>", Fixtures.ZipText(docx, "word/document.xml"));
    }

    [Theory]
    [InlineData(OutputFormat.Txt)]
    [InlineData(OutputFormat.Md)]
    [InlineData(OutputFormat.Xlsx)]
    public async Task TextOutputsRefuseScansWithTheMacsMessage(OutputFormat format)
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Scan.pdf", Fixtures.BlankPdf(1));
        var error = await Assert.ThrowsAsync<ConversionException>(() => Run(pdf, format));
        Assert.Equal("“Scan.pdf” has no text to extract (it looks like a scan).", error.Message);
        Assert.Equal(["Scan.pdf"], folder.Names());
    }

    [Fact]
    public async Task PagesRenderAt300DpiAsNumberedJpegs()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        var files = await Run(pdf, OutputFormat.Jpg);
        Assert.Equal(["Plan Page 1.jpg", "Plan Page 2.jpg"], files.Select(Path.GetFileName));
        using var bitmap = SKBitmap.Decode(files[0]);
        Assert.Equal((2550, 3300), (bitmap.Width, bitmap.Height));
        // The heading is dark ink on white: the page is not blank and not black.
        var dark = 0;
        for (var y = 0; y < bitmap.Height; y += 8)
            for (var x = 0; x < bitmap.Width; x += 8)
                if (bitmap.GetPixel(x, y).Red < 100) dark++;
        Assert.InRange(dark, 20, 100_000);
        Assert.Equal(0xFF, File.ReadAllBytes(files[0])[0]);
    }

    [Fact]
    public async Task SinglePagePngKeepsThePlainName()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("One.pdf", Fixtures.Pdf(Fixtures.Lines("hello")));
        var file = Assert.Single(await Run(pdf, OutputFormat.Png));
        Assert.Equal("One.png", Path.GetFileName(file));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(file).Take(4));
    }

    [Fact]
    public async Task ImageQualitySettingChangesTheJpegSize()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("One.pdf", Fixtures.Pdf(Fixtures.Lines("quality test line", "another line of text")));
        var low = new FileInfo(Assert.Single(await Converter.ConvertAsync(new ConversionRequest(pdf, OutputFormat.Jpg, new ConversionOptions { ImageQuality = 0.1 })))).Length;
        var high = new FileInfo(Assert.Single(await Converter.ConvertAsync(new ConversionRequest(pdf, OutputFormat.Jpg, new ConversionOptions { ImageQuality = 1.0 })))).Length;
        Assert.True(low < high, $"{low} should be smaller than {high}");
    }

    [Fact]
    public async Task EpubHasAChapterPerPageAndImagesForScans()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Mixed.pdf", Fixtures.Pdf(Fixtures.Lines("First page text"), []));
        var epub = Assert.Single(await Run(pdf, OutputFormat.Epub));
        var names = Fixtures.ZipNames(epub);
        Assert.Contains("OEBPS/chapter1.xhtml", names);
        Assert.Contains("OEBPS/chapter2.xhtml", names);
        Assert.Contains("First page text", Fixtures.ZipText(epub, "OEBPS/chapter1.xhtml"));
        Assert.Contains("images/page2.jpg", Fixtures.ZipText(epub, "OEBPS/chapter2.xhtml"));
        Assert.Contains("OEBPS/images/page2.jpg", names);
        Assert.Contains("<dc:title>Mixed</dc:title>", Fixtures.ZipText(epub, "OEBPS/content.opf"));
    }

    [Fact]
    public async Task PptxHasOnePictureSlidePerPageAtThePageSize()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Deck.pdf", TwoPagePdf());
        var pptx = Assert.Single(await Run(pdf, OutputFormat.Pptx));
        var names = Fixtures.ZipNames(pptx);
        Assert.Equal(2, names.Count(n => n.StartsWith("ppt/slides/slide") && n.EndsWith(".xml")));
        Assert.Equal(2, names.Count(n => n.StartsWith("ppt/media/")));
        Assert.Contains($"cx=\"{612 * 12700}\" cy=\"{792 * 12700}\"", Fixtures.ZipText(pptx, "ppt/presentation.xml"));
        using var zip = ZipFile.OpenRead(pptx);
        using var picture = SKBitmap.Decode(Zips.ReadEntry(zip, "ppt/media/image1.jpeg")!);
        Assert.Equal((1360, 1760), (picture.Width, picture.Height));   // 160 dpi
    }

    [Fact]
    public async Task XlsxHasASheetPerPageWithCellsSplitAtWideGaps()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Sheet.pdf", TwoPagePdf());
        var xlsx = Assert.Single(await Run(pdf, OutputFormat.Xlsx));
        var workbook = Fixtures.ZipText(xlsx, "xl/workbook.xml");
        Assert.Contains("name=\"Page 1\"", workbook);
        Assert.Contains("name=\"Page 2\"", workbook);
        var sheet2 = Fixtures.ZipText(xlsx, "xl/worksheets/sheet2.xml");
        Assert.Contains("Apples", sheet2);
        Assert.Contains("1.25", sheet2);
        // Name, Qty and Price are three cells in one row, not one long string.
        Assert.DoesNotContain("Name Qty", sheet2);
        Assert.Contains(">Qty<", sheet2);
    }

    [Fact]
    public async Task UnreadableOrMissingPdfsFailCleanly()
    {
        using var folder = new TempFolder();
        var junk = folder.File("Junk.pdf", "this is not a pdf");
        var error = await Assert.ThrowsAsync<ConversionException>(() => Run(junk, OutputFormat.Jpg));
        Assert.Contains("Junk.pdf", error.Message);
        Assert.Equal(["Junk.pdf"], folder.Names());
    }

    [Fact]
    public async Task CancellingStopsAndLeavesNothing()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter.ConvertAsync(new ConversionRequest(pdf, OutputFormat.Png), null, cancel.Token));
        Assert.Equal(["Plan.pdf"], folder.Names());
    }

    [Fact]
    public async Task ProgressReachesOne()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        double last = 0;
        await Converter.ConvertAsync(new ConversionRequest(pdf, OutputFormat.Png), new Progress<double>(v => last = v));
        await Task.Delay(100);
        Assert.Equal(1, last);
    }

    [Fact]
    public async Task ResultsNeverOverwrite()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("Plan.pdf", TwoPagePdf());
        await Run(pdf, OutputFormat.Txt);
        var second = Assert.Single(await Run(pdf, OutputFormat.Txt));
        Assert.Equal("Plan 2.txt", Path.GetFileName(second));
    }

    [Fact]
    public void PdfTextFindsLinesParagraphGapsAndWideGaps()
    {
        using var folder = new TempFolder();
        var pdf = folder.Bytes("T.pdf", Fixtures.Pdf(
        [
            ("First line", 11, 72, 700), ("second line", 11, 72, 686),
            ("After a gap", 11, 72, 640),
            ("Left", 11, 72, 600), ("Right", 11, 300, 600),
        ]));
        var page = Assert.Single(PdfText.Pages(pdf));
        var lines = page.Lines.Select(l => l.Text).ToList();
        Assert.Equal(["First line", "second line", "", "After a gap", "", "Left Right"], lines.Take(6));
        Assert.Equal("Left\tRight", page.Lines[^1].RowText);
    }
}

public class EdgePrintingTests
{
    [Fact]
    public void FindsEdgeInProgramFilesX86ThenProgramFilesThenTheRegistry()
    {
        var x86 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        var x64 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
        string? Env(string name) => name switch { "ProgramFiles(x86)" => @"C:\Program Files (x86)", "ProgramFiles" => @"C:\Program Files", _ => null };

        Assert.Equal(x86, EdgePrinter.Locate(Env, p => p == x86 || p == x64, () => null));
        Assert.Equal(x64, EdgePrinter.Locate(Env, p => p == x64, () => null));
        Assert.Equal(@"D:\Edge\msedge.exe", EdgePrinter.Locate(Env, p => p == @"D:\Edge\msedge.exe", () => @"D:\Edge\msedge.exe"));
        Assert.Null(EdgePrinter.Locate(Env, _ => false, () => @"D:\Edge\msedge.exe"));
    }

    [Fact]
    public async Task EdgeIsRunHeadlessWithAPrivateProfile()
    {
        using var folder = new TempFolder();
        var fake = new FakeTool();
        string? profile = null;
        fake.Behaviour = (invocation, _) =>
        {
            profile = FakeTool.ValueOf(invocation, "--user-data-dir");
            Assert.True(Directory.Exists(profile));
            File.WriteAllBytes(FakeTool.ValueOf(invocation, "--print-to-pdf")!, Fixtures.Pdf(Fixtures.Lines("x")));
            return Task.CompletedTask;
        };
        var printer = new EdgePrinter(fake.Runner, () => @"C:\fake\msedge.exe");
        var output = folder.Combine("out.pdf");
        await printer.PrintHtmlAsync("<p>hello</p>", output);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(@"C:\fake\msedge.exe", call.Executable);
        Assert.Contains("--headless=new", call.Arguments);
        Assert.Contains("--no-pdf-header-footer", call.Arguments);
        Assert.Contains("--disable-gpu", call.Arguments);
        Assert.Equal(output, FakeTool.ValueOf(call.Invocation, "--print-to-pdf"));
        Assert.StartsWith("file:///", call.Arguments[^1]);
        Assert.EndsWith(".html", call.Arguments[^1]);
        Assert.NotNull(call.Invocation.Timeout);
        Assert.False(Directory.Exists(profile), "the private profile is removed afterwards");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task MissingEdgeIsAClearError()
    {
        using var folder = new TempFolder();
        var printer = new EdgePrinter(new FakeTool().Runner, () => null);
        var error = await Assert.ThrowsAsync<ConversionException>(() => printer.PrintHtmlAsync("<p>x</p>", folder.Combine("o.pdf")));
        Assert.Contains("Microsoft Edge isn’t installed", error.Message);
    }

    [Fact]
    public async Task AnEdgeThatWritesNothingIsReported()
    {
        using var folder = new TempFolder();
        var printer = new EdgePrinter(new FakeTool().Runner, () => @"C:\fake\msedge.exe");
        var error = await Assert.ThrowsAsync<ConversionException>(() => printer.PrintHtmlAsync("<p>x</p>", folder.Combine("o.pdf")));
        Assert.Contains("couldn’t print", error.Message);
    }

    [Fact]
    public async Task ATimedOutEdgeIsReportedAsSuch()
    {
        using var folder = new TempFolder();
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("msedge", "", timedOut: true) };
        var printer = new EdgePrinter(fake.Runner, () => @"C:\fake\msedge.exe");
        var error = await Assert.ThrowsAsync<ConversionException>(() => printer.PrintHtmlAsync("<p>x</p>", folder.Combine("o.pdf")));
        Assert.Contains("took too long", error.Message);
    }

    [Fact]
    public async Task RealEdgePrintsAMultiPageDocumentToPdf()
    {
        if (!Fixtures.EdgeAvailable) return;   // no Edge on this machine
        using var folder = new TempFolder();
        var blocks = new List<Block> { new HeadingBlock(1, [new TextRun("Printed By Edge")]) };
        for (var i = 0; i < 80; i++) blocks.Add(new ParagraphBlock([new TextRun($"Paragraph number {i} with enough words to take a line or two on the page.")]));
        blocks.Add(new TableBlock([[[new TextRun("Col A")], [new TextRun("Col B")]], [[new TextRun("cell one")], [new TextRun("cell two")]]], true));
        var output = folder.Combine("edge.pdf");
        await EdgePrinter.Default.PrintHtmlAsync(HtmlWriter.PrintPage(new DocumentModel("Edge", blocks)), output);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 4));
        var pages = Fixtures.PageTexts(output);
        Assert.InRange(pages.Length, 2, 4);
        Assert.Contains("Printed By Edge", pages[0]);
        Assert.Contains("Paragraph number 79", string.Join(" ", pages));
        Assert.Contains("cell two", pages[^1]);
        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal((612, 792), ((int)Math.Round(document.GetPage(1).Width), (int)Math.Round(document.GetPage(1).Height)));
    }
}
