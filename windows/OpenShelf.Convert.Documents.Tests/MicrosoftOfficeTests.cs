using System.IO.Compression;
using System.Text;
using OpenShelf.Convert;
using OpenShelf.Convert.Documents;
using UglyToad.PdfPig;
using Xunit;

namespace OpenShelf.Convert.Documents.Tests;

/// <summary>Office stand-in: records calls, then writes a file or fails.</summary>
internal sealed class FakeOffice : IOfficeAutomation
{
    public List<(OfficeApp App, OutputFormat Format)> Calls { get; } = [];
    public bool Fail { get; set; }

    public Task ConvertAsync(OfficeApp app, string source, OutputFormat format, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((app, format));
        if (Fail) throw ConversionException.Failed("Office said no.");
        File.WriteAllText(output, "converted by " + app);
        return Task.CompletedTask;
    }
}

public class MicrosoftOfficeSelectionTests
{
    internal static string Out(TempFolder folder) => Directory.CreateDirectory(folder.Combine("out")).FullName;

    private static ExternalTools With(params OfficeApp[] apps) => ExternalTools.None with { MicrosoftOffice = apps };

    private static Type? Engine(string name, OutputFormat format, ExternalTools tools)
    {
        var registry = EngineRegistry.BuiltIn();
        DocumentEngines.Register(registry);
        return registry.Find(new ConversionRequest(@"C:\x\" + name, format) { Tools = tools })?.GetType();
    }

    [Fact]
    public void OfficeIsAskedBeforeLibreOfficeAndTheBuiltInWay()
    {
        var both = With(OfficeApp.Word, OfficeApp.Excel, OfficeApp.PowerPoint) with { LibreOffice = Fixtures.Fake };
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.docx", OutputFormat.Pdf, both));
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.doc", OutputFormat.Docx, both));
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.rtf", OutputFormat.Pdf, both));
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.xlsx", OutputFormat.Pdf, both));
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.pptx", OutputFormat.Png, both));
        // Formats Office doesn't write here stay with the other engines.
        Assert.Equal(typeof(DocumentEngine), Engine("a.docx", OutputFormat.Md, both));
        Assert.Equal(typeof(DocumentEngine), Engine("a.html", OutputFormat.Pdf, both));
    }

    [Fact]
    public void EachFileGoesToItsOwnApp()
    {
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.docx", OutputFormat.Pdf, With(OfficeApp.Word)));
        Assert.NotEqual(typeof(MicrosoftOfficeEngine), Engine("a.xlsx", OutputFormat.Pdf, With(OfficeApp.Word)));
        Assert.Null(Engine("a.pptx", OutputFormat.Pdf, With(OfficeApp.Word)));
        Assert.Equal(typeof(MicrosoftOfficeEngine), Engine("a.pptx", OutputFormat.Pdf, With(OfficeApp.PowerPoint)));
        Assert.Equal(typeof(DocumentEngine), Engine("a.docx", OutputFormat.Pdf, ExternalTools.None));
    }

    [Fact]
    public void TheWheelOffersWhatOfficeCanMake()
    {
        Assert.Contains(OutputFormat.Pdf, ConversionCatalog.Formats(@"C:\x\a.pptx", With(OfficeApp.PowerPoint)));
        Assert.DoesNotContain(OutputFormat.Pdf, ConversionCatalog.Formats(@"C:\x\a.pptx", ExternalTools.None));
        Assert.Contains(OutputFormat.Xlsx, ConversionCatalog.Formats(@"C:\x\a.xls", With(OfficeApp.Excel)));
        Assert.Contains(OutputFormat.Odt, ConversionCatalog.Formats(@"C:\x\a.docx", With(OfficeApp.Word)));
    }

    [Fact]
    public async Task WhenOfficeFailsTheNextEngineConverts()
    {
        using var folder = new TempFolder();
        var source = folder.Combine("Letter.docx");
        WordWriter.Write(new DocumentModel("Letter", [new ParagraphBlock(DocumentModel.Plain("Hello"))]), source);
        var office = new FakeOffice { Fail = true };
        var registry = new EngineRegistry();
        var engine = new MicrosoftOfficeEngine(registry, office);
        registry.Register(engine, 85);
        registry.Register(new DocumentEngine(), 0);

        var written = await engine.ConvertAsync(new ConversionRequest(source, OutputFormat.Txt) { Tools = With(OfficeApp.Word), OutputDirectory = folder.Path }, null, default);

        Assert.Single(office.Calls);
        Assert.Contains("Hello", File.ReadAllText(Assert.Single(written)));
    }

    [Fact]
    public async Task OfficeOutputIsMovedToTheResultName()
    {
        using var folder = new TempFolder();
        var source = folder.File("Report.docx", "x");
        var office = new FakeOffice();
        var engine = new MicrosoftOfficeEngine(null, office);

        var written = await engine.ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf) { Tools = With(OfficeApp.Word), OutputDirectory = Out(folder) }, null, default);

        Assert.Equal(folder.Combine(@"out\Report.pdf"), Assert.Single(written));
        Assert.Equal([(OfficeApp.Word, OutputFormat.Pdf)], office.Calls);
    }
}

/// <summary>Real conversions through the Office installed on this PC; each test passes trivially where the app is missing.</summary>
public class MicrosoftOfficeRealTests
{
    private static string Out(TempFolder folder) => MicrosoftOfficeSelectionTests.Out(folder);

    private const string OnePixelJpeg =
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=";

    private static ExternalTools Installed => ExternalTools.None with { MicrosoftOffice = OfficeInstallation.All.Where(OfficeInstallation.IsInstalled).ToList() };

    /// <summary>A Word file with formatting the built-in reader can't carry: a font, a colour, centring and a header.</summary>
    public static void WriteStyledDocx(string path)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/></Types>""");
        Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
        Entry("word/_rels/document.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdH" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/></Relationships>""");
        Entry("word/header1.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:hdr xmlns:w="{W}"><w:p><w:r><w:t>Running header</w:t></w:r></w:p></w:hdr>""");
        Entry("word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="{W}" xmlns:r="{R}"><w:body>
            <w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Georgia" w:hAnsi="Georgia"/><w:color w:val="C00000"/><w:sz w:val="40"/></w:rPr><w:t>Centred red Georgia</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Body text -- with "straight quotes" kept as typed.</w:t></w:r></w:p>
            <w:sectPr><w:headerReference w:type="default" r:id="rIdH"/><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr>
            </w:body></w:document>
            """);
    }

    [Fact]
    public async Task WordKeepsFontsColoursLayoutAndText()
    {
        if (!OfficeInstallation.IsInstalled(OfficeApp.Word)) return;
        using var folder = new TempFolder();
        var source = folder.Combine("Styled.docx");
        WriteStyledDocx(source);

        var written = await new MicrosoftOfficeEngine().ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf) { Tools = Installed, OutputDirectory = Out(folder) }, null, default);

        using var pdf = PdfDocument.Open(Assert.Single(written));
        var page = pdf.GetPage(1);
        Assert.Equal(595, page.Width, 1.5);                       // A4 from the document, not the PC's default paper
        Assert.Contains("Running header", page.Text);
        Assert.Contains("Body text -- with \"straight quotes\" kept as typed.", string.Join(" ", page.GetWords().Select(w => w.Text)).Replace(" ", " "), StringComparison.Ordinal);
        var title = page.Letters.Where(l => l.Value == "C").First();
        Assert.Contains("Georgia", title.FontName, StringComparison.OrdinalIgnoreCase);
        var (r, g, b) = title.Color.ToRGBValues();
        Assert.True(r > 0.6 && g < 0.2 && b < 0.2, $"colour {r},{g},{b}");
        var titleWords = page.GetWords().Where(w => w.Text is "Centred" or "Georgia").ToList();
        var middle = (titleWords.Min(w => w.BoundingBox.Left) + titleWords.Max(w => w.BoundingBox.Right)) / 2;
        Assert.Equal(page.Width / 2, middle, 6.0);
    }

    [Fact]
    public async Task ExcelPrintsAWorkbook()
    {
        if (!OfficeInstallation.IsInstalled(OfficeApp.Excel)) return;
        using var folder = new TempFolder();
        var source = folder.Combine("Budget.xlsx");
        XlsxWriter.Write([("Costs", [["Item", "Amount"], ["Rent", "1200"], ["Food", "300"]])], source);

        var written = await new MicrosoftOfficeEngine().ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf) { Tools = Installed, OutputDirectory = Out(folder) }, null, default);

        using var pdf = PdfDocument.Open(Assert.Single(written));
        Assert.Contains("Rent", pdf.GetPage(1).Text);
    }

    [Fact]
    public async Task PowerPointMakesSlidePictures()
    {
        if (!OfficeInstallation.IsInstalled(OfficeApp.PowerPoint)) return;
        using var folder = new TempFolder();
        var source = folder.Combine("Deck.pptx");
        var jpeg = System.Convert.FromBase64String(OnePixelJpeg);
        PptxWriter.Write([new SlidePicture(jpeg, 1, 1), new SlidePicture(jpeg, 1, 1)], 720, 405, source);

        var written = await new MicrosoftOfficeEngine().ConvertAsync(new ConversionRequest(source, OutputFormat.Png) { Tools = Installed, OutputDirectory = Out(folder) }, null, default);

        Assert.Equal(2, written.Count);
    }
}
