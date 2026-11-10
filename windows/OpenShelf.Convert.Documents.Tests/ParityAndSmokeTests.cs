using OpenShelf.Convert.Documents;

namespace OpenShelf.Convert.Documents.Tests;

/// <summary>Every conversion the wheel offers has an engine behind it.</summary>
public class CatalogParityTests
{
    private static readonly string[] Samples =
    [
        "a.pdf", "a.docx", "a.doc", "a.odt", "a.rtf", "a.txt", "a.html", "a.epub", "a.mobi", "a.azw3", "a.csv", "a.tsv", "a.xlsx", "a.xls", "a.ods",
        "a.pptx", "a.ppt", "a.odp", "a.pages", "a.numbers", "a.key",
    ];

    public static IEnumerable<object[]> ToolSets() =>
    [
        ["nothing", Fixtures.None],
        ["LibreOffice", Fixtures.WithLibreOffice],
        ["Pandoc", Fixtures.WithPandoc],
        ["Pandoc with a typesetter", Fixtures.WithPandoc with { PdfEngine = @"C:\fake\xelatex.exe" }],
        ["calibre", Fixtures.WithCalibre],
        ["everything", Fixtures.Everything],
    ];

    [Theory]
    [MemberData(nameof(ToolSets))]
    public void EveryOfferedFormatHasAnEngine(string _, ExternalTools tools)
    {
        var registry = EngineRegistry.BuiltIn();
        DocumentEngines.Register(registry);
        var missing = new List<string>();
        foreach (var sample in Samples)
        {
            var path = @"C:\nowhere\" + sample;
            foreach (var format in ConversionCatalog.Formats(path, tools))
            {
                if (format is OutputFormat.Zip or OutputFormat.Tar or OutputFormat.Tgz or OutputFormat.Extract) continue;
                // Pictures from pages and slides, and the other engines' kinds, are not asked of this registry.
                if (registry.Find(new ConversionRequest(path, format) { Tools = tools }) is null) missing.Add($"{sample} -> {format}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void WithoutAnyToolTheWheelOffersWhatThisPackageBuilds()
    {
        var formats = ConversionCatalog.Formats(@"C:\x\a.pdf", Fixtures.None);
        Assert.Equal([OutputFormat.Docx, OutputFormat.Jpg, OutputFormat.Png, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Pptx, OutputFormat.Xlsx, OutputFormat.Zip], formats);
        Assert.Equal([OutputFormat.Pdf, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Html, OutputFormat.Rtf, OutputFormat.Zip],
            ConversionCatalog.Formats(@"C:\x\a.docx", Fixtures.None));
        Assert.Equal([OutputFormat.Zip], ConversionCatalog.Formats(@"C:\x\a.pptx", Fixtures.None));
    }
}

/// <summary>The real tools, when this machine has them. Each test returns early when its tool is missing.</summary>
public class RealToolSmokeTests
{
    private static readonly ExternalTools Detected = ExternalTools.Detect(ToolLocations.Standard);

    [Fact]
    public async Task PandocConvertsAWordDocument()
    {
        if (Detected.Pandoc is null) return;
        using var folder = new TempFolder();
        var docx = folder.Combine("Report.docx");
        WordWriter.Write(Fixtures.RichModel(), docx);
        var converter = Fixtures.ConverterWith(Detected);

        var md = Assert.Single(await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Md)));
        var markdown = File.ReadAllText(md);
        Assert.Contains("# Annual Report", markdown);
        Assert.Contains("**bold**", markdown);
        Assert.Contains("https://example.com/a?b=1&c=2", markdown);

        var epub = Assert.Single(await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Epub)));
        Assert.Contains("mimetype", Fixtures.ZipNames(epub));
        Assert.Contains(Fixtures.ZipNames(epub), n => n.EndsWith(".xhtml"));

        var odt = Assert.Single(await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Odt)));
        var model = OdtReader.Read(odt);   // what Pandoc wrote, read by the built-in ODT reader
        Assert.Contains("Annual Report", DocumentStructure.PlainText(model.Blocks));
        Assert.Equal(["Report.docx", "Report.epub", "Report.md", "Report.odt"], folder.Names().Order());
    }

    [Fact]
    public async Task PandocReadsEpubBooksAndOdt()
    {
        if (Detected.Pandoc is null) return;
        using var folder = new TempFolder();
        var epub = folder.Combine("Book.epub");
        var (body, images) = EpubWriter.BodyFor(Fixtures.RichModel().Blocks);
        EpubWriter.Write("Book", [new EpubChapter("One", body)], epub, images);
        var converter = Fixtures.ConverterWith(Detected);

        var md = Assert.Single(await converter.ConvertAsync(new ConversionRequest(epub, OutputFormat.Md)));
        Assert.Contains("Annual Report", File.ReadAllText(md));
        var docx = Assert.Single(await converter.ConvertAsync(new ConversionRequest(epub, OutputFormat.Docx)));
        Assert.Contains("Annual Report", DocumentStructure.PlainText(DocxReader.Read(docx).Blocks));

        // ODT is read built in; Pandoc reads our ODT too.
        var odt = folder.Combine("Doc.odt");
        OdtWriter.Write(Fixtures.RichModel(), odt);
        Assert.Contains("step two", File.ReadAllText(Assert.Single(await converter.ConvertAsync(new ConversionRequest(odt, OutputFormat.Md)))));
    }

    [Fact]
    public async Task LibreOfficeConvertsAWordDocumentToPdf()
    {
        if (Detected.LibreOffice is null) return;
        using var folder = new TempFolder();
        var docx = folder.Combine("Report.docx");
        WordWriter.Write(Fixtures.RichModel(), docx);
        var pdf = Assert.Single(await Fixtures.ConverterWith(Detected).ConvertAsync(new ConversionRequest(docx, OutputFormat.Pdf)));
        Assert.Contains("Annual Report", Fixtures.PageTexts(pdf)[0]);
    }

    [Fact]
    public async Task CalibreConvertsABook()
    {
        if (Detected.EbookConvert is null) return;
        using var folder = new TempFolder();
        var epub = folder.Combine("Book.epub");
        var (body, images) = EpubWriter.BodyFor(Fixtures.RichModel().Blocks);
        EpubWriter.Write("Book", [new EpubChapter("One", body)], epub, images);
        var azw3 = Assert.Single(await Fixtures.ConverterWith(Detected).ConvertAsync(new ConversionRequest(epub, OutputFormat.Azw3)));
        Assert.True(new FileInfo(azw3).Length > 100);
    }
}
