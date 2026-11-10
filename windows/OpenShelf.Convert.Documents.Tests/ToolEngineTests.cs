using System.IO.Compression;
using OpenShelf.Convert.Documents;

namespace OpenShelf.Convert.Documents.Tests;

public class LibreOfficeTests
{
    private static string MakeDocx(TempFolder folder, string name = "Report.docx")
    {
        var path = folder.Combine(name);
        WordWriter.Write(Fixtures.RichModel(), path);
        return path;
    }

    /// <summary>A fake soffice that writes <paramref name="make"/>'s bytes as the converted file in --outdir.</summary>
    private static FakeTool Soffice(Func<string, byte[]> make, string? extension = null)
    {
        var fake = new FakeTool();
        fake.Behaviour = (invocation, _) =>
        {
            var outdir = FakeTool.ValueOf(invocation, "--outdir")!;
            var source = invocation.Arguments[^1];
            var filter = FakeTool.ValueOf(invocation, "--convert-to")!;
            var ext = extension ?? filter.Split(':')[0];
            File.WriteAllBytes(Path.Combine(outdir, Path.GetFileNameWithoutExtension(source) + "." + ext), make(filter));
            return Task.CompletedTask;
        };
        return fake;
    }

    [Fact]
    public async Task WordToPdfUsesTheMacsArgumentSet()
    {
        using var folder = new TempFolder();
        var fake = Soffice(_ => Fixtures.Pdf(Fixtures.Lines("from libreoffice")));
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var docx = MakeDocx(folder);

        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Pdf)));
        Assert.Equal("Report.pdf", Path.GetFileName(output));
        Assert.Contains("from libreoffice", Fixtures.PageTexts(output)[0]);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(Fixtures.WithLibreOffice.LibreOffice, call.Executable);
        var args = call.Arguments;
        Assert.StartsWith("-env:UserInstallation=file:///", args[0]);
        Assert.Equal(["--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--convert-to", "pdf", "--outdir"], args.Skip(1).Take(8));
        Assert.Equal(docx, args[^1]);
        Assert.Equal(TimeSpan.FromSeconds(120), call.Invocation.Timeout);
        // A private profile inside the workspace, not the user's own.
        Assert.Contains("openshelf-office-", args[0]);
        Assert.EndsWith("/profile", args[0]);
        Assert.Equal(["Report.docx", "Report.pdf"], folder.Names());
    }

    [Theory]
    [InlineData("Sheet.xls", OutputFormat.Xlsx, "xlsx")]
    [InlineData("Sheet.xls", OutputFormat.Csv, "csv:Text - txt - csv (StarCalc):44,34,76,1")]
    [InlineData("Sheet.ods", OutputFormat.Pdf, "pdf")]
    [InlineData("Sheet.xls", OutputFormat.Ods, "ods")]
    [InlineData("Data.csv", OutputFormat.Ods, "ods")]
    [InlineData("Book.xlsx", OutputFormat.Ods, "ods")]
    public async Task SpreadsheetFilters(string name, OutputFormat format, string filter)
    {
        using var folder = new TempFolder();
        var source = folder.File(name, "x");
        var fake = Soffice(_ => [1, 2, 3], format.FileExtension());
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(source, format)));
        Assert.Equal(filter, FakeTool.ValueOf(fake.Calls[0].Invocation, "--convert-to"));
        Assert.Equal(Path.GetFileNameWithoutExtension(name) + "." + format.FileExtension(), Path.GetFileName(output));
    }

    [Fact]
    public async Task ASuccessfulExitWithNoFileIsAnError()
    {
        using var folder = new TempFolder();
        var source = folder.File("Sheet.xls", "x");
        var fake = new FakeTool();
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Xlsx)));
        Assert.Equal("LibreOffice couldn’t convert “Sheet.xls”.", error.Message);
        Assert.Equal(["Sheet.xls"], folder.Names());
    }

    [Fact]
    public async Task ATimeoutIsReported()
    {
        using var folder = new TempFolder();
        var source = folder.File("Sheet.xls", "x");
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("soffice", "", timedOut: true) };
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Csv)));
        Assert.Equal("LibreOffice took too long to convert “Sheet.xls”.", error.Message);
    }

    [Fact]
    public async Task WhenLibreOfficeFailsAWordFileIsConvertedTheBuiltInWay()
    {
        using var folder = new TempFolder();
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("soffice", "boom") };
        var edge = new FakeTool
        {
            Behaviour = (invocation, _) =>
            {
                File.WriteAllBytes(FakeTool.ValueOf(invocation, "--print-to-pdf")!, Fixtures.Pdf(Fixtures.Lines("from edge")));
                return Task.CompletedTask;
            },
        };
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner, new EdgePrinter(edge.Runner, () => @"C:\fake\msedge.exe"));
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(MakeDocx(folder), OutputFormat.Pdf)));
        Assert.Single(fake.Calls);
        Assert.Single(edge.Calls);
        Assert.Contains("from edge", Fixtures.PageTexts(output)[0]);
        Assert.Equal(["Report.docx", "Report.pdf"], folder.Names());
    }

    [Fact]
    public async Task SpreadsheetFailuresHaveNoFallback()
    {
        using var folder = new TempFolder();
        var source = folder.File("Sheet.ods", "x");
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("soffice", "cannot open") };
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Xlsx)));
        Assert.Contains("cannot open", error.Message);
        Assert.Equal(["Sheet.ods"], folder.Names());
    }

    [Theory]
    [InlineData("Deck.pptx", OutputFormat.Jpg, "Deck Slide 1.jpg", "Deck Slide 2.jpg", "Deck Slide 3.jpg")]
    [InlineData("Deck.odp", OutputFormat.Png, "Deck Slide 1.png", "Deck Slide 2.png", "Deck Slide 3.png")]
    [InlineData("Deck.ppt", OutputFormat.Jpg, "Deck Slide 1.jpg", "Deck Slide 2.jpg", "Deck Slide 3.jpg")]
    public async Task SlidesBecomePageImagesThroughAPdf(string name, OutputFormat format, params string[] expected)
    {
        using var folder = new TempFolder();
        var source = folder.File(name, "x");
        var fake = Soffice(_ => Fixtures.Pdf(Fixtures.Lines("one"), Fixtures.Lines("two"), Fixtures.Lines("three")), "pdf");
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var files = await converter.ConvertAsync(new ConversionRequest(source, format));
        Assert.Equal("pdf", FakeTool.ValueOf(fake.Calls[0].Invocation, "--convert-to"));
        Assert.Equal(expected, files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 1000));
        Assert.Equal(new[] { name }.Concat(expected).Order(), folder.Names().Order());
    }

    [Fact]
    public async Task OneSlideKeepsThePlainName()
    {
        using var folder = new TempFolder();
        var source = folder.File("Single.pptx", "x");
        var fake = Soffice(_ => Fixtures.Pdf(Fixtures.Lines("only")), "pdf");
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        Assert.Equal("Single.png", Path.GetFileName(Assert.Single(await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Png)))));
    }

    [Fact]
    public async Task SlidesToPdfNeedNothingElse()
    {
        using var folder = new TempFolder();
        var source = folder.File("Deck.pptx", "x");
        var fake = Soffice(_ => Fixtures.Pdf(Fixtures.Lines("slide")), "pdf");
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        Assert.Equal("Deck.pdf", Path.GetFileName(Assert.Single(await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf)))));
    }

    [Fact]
    public void WhoHandlesWhat()
    {
        var registry = EngineRegistry.BuiltIn();
        DocumentEngines.Register(registry);
        Type? Engine(string name, OutputFormat format, ExternalTools tools) =>
            registry.Find(new ConversionRequest(@"C:\x\" + name, format) { Tools = tools })?.GetType();

        Assert.Equal(typeof(DocumentEngine), Engine("a.docx", OutputFormat.Pdf, Fixtures.None));
        Assert.Equal(typeof(LibreOfficeEngine), Engine("a.docx", OutputFormat.Pdf, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(LibreOfficeEngine), Engine("a.rtf", OutputFormat.Pdf, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(DocumentEngine), Engine("a.html", OutputFormat.Pdf, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(DocumentEngine), Engine("a.docx", OutputFormat.Txt, Fixtures.WithLibreOffice));
        Assert.Null(Engine("a.xls", OutputFormat.Xlsx, Fixtures.None));
        Assert.Equal(typeof(LibreOfficeEngine), Engine("a.xls", OutputFormat.Xlsx, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(LibreOfficeEngine), Engine("a.csv", OutputFormat.Ods, Fixtures.WithLibreOffice));
        Assert.Null(Engine("a.csv", OutputFormat.Ods, Fixtures.None));
        Assert.Equal(typeof(TablePdfEngine), Engine("a.csv", OutputFormat.Pdf, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(TablePdfEngine), Engine("a.xlsx", OutputFormat.Pdf, Fixtures.None));
        Assert.Equal(typeof(SpreadsheetEngine), Engine("a.csv", OutputFormat.Xlsx, Fixtures.WithLibreOffice));
        Assert.Null(Engine("a.pptx", OutputFormat.Pdf, Fixtures.None));
        Assert.Equal(typeof(LibreOfficeEngine), Engine("a.pptx", OutputFormat.Pdf, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(PdfEngine), Engine("a.pdf", OutputFormat.Docx, Fixtures.Everything));
        Assert.Equal(typeof(CalibreEngine), Engine("a.pdf", OutputFormat.Mobi, Fixtures.WithCalibre));
        Assert.Null(Engine("a.pdf", OutputFormat.Mobi, Fixtures.None));
        Assert.Equal(typeof(IWorkEngine), Engine("a.pages", OutputFormat.Pdf, Fixtures.None));
        Assert.Equal(typeof(IWorkEngine), Engine("a.key", OutputFormat.Png, Fixtures.WithLibreOffice));
        Assert.Equal(typeof(PandocEngine), Engine("a.docx", OutputFormat.Md, Fixtures.WithPandoc));
        Assert.Equal(typeof(DocumentEngine), Engine("a.docx", OutputFormat.Md, Fixtures.None));
        Assert.Equal(typeof(DocumentEngine), Engine("a.txt", OutputFormat.Md, Fixtures.WithPandoc));
    }
}

public class IWorkTests
{
    private static byte[] PreviewPdf(int pages) => Fixtures.Pdf(Enumerable.Range(1, pages).Select(i => Fixtures.Lines($"preview page {i}")).ToArray());

    private static string Package(TempFolder folder, string name, Dictionary<string, byte[]> files)
    {
        var path = folder.Combine(name);
        using var file = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, data) in files) Zips.Add(zip, entry, data);
        return path;
    }

    private static string FolderPackage(TempFolder folder, string name, Dictionary<string, byte[]> files)
    {
        foreach (var (entry, data) in files) folder.Bytes($"{name}/{entry}", data);
        return folder.Combine(name);
    }

    private static readonly Converter NoTools = Fixtures.ConverterWith(Fixtures.None);

    [Fact]
    public async Task PdfPreviewGivesPdfJpgAndPngWithoutLibreOffice()
    {
        using var folder = new TempFolder();
        var preview = PreviewPdf(2);
        var pages = Package(folder, "Essay.pages", new() { ["QuickLook/Preview.pdf"] = preview, ["Index/Document.iwa"] = [1, 2, 3] });

        var pdf = Assert.Single(await NoTools.ConvertAsync(new ConversionRequest(pages, OutputFormat.Pdf)));
        Assert.Equal("Essay.pdf", Path.GetFileName(pdf));
        Assert.Equal(preview, File.ReadAllBytes(pdf));

        var jpgs = await NoTools.ConvertAsync(new ConversionRequest(pages, OutputFormat.Jpg));
        Assert.Equal(["Essay Page 1.jpg", "Essay Page 2.jpg"], jpgs.Select(Path.GetFileName));
        var pngs = await NoTools.ConvertAsync(new ConversionRequest(pages, OutputFormat.Png));
        Assert.Equal(2, pngs.Count);
        Assert.Equal([0x89, 0x50], File.ReadAllBytes(pngs[0]).Take(2));
    }

    [Fact]
    public async Task KeynoteSlidesAreLabelledSlide()
    {
        using var folder = new TempFolder();
        var key = Package(folder, "Deck.key", new() { ["QuickLook/Preview.pdf"] = PreviewPdf(2) });
        var files = await NoTools.ConvertAsync(new ConversionRequest(key, OutputFormat.Png));
        Assert.Equal(["Deck Slide 1.png", "Deck Slide 2.png"], files.Select(Path.GetFileName));
    }

    [Fact]
    public async Task AJpegPreviewGivesJpgAndPng()
    {
        using var folder = new TempFolder();
        using var bitmap = new SkiaSharp.SKBitmap(60, 40);
        bitmap.Erase(SkiaSharp.SKColors.Orange);
        var jpeg = Pictures.EncodeJpeg(bitmap, 0.9);
        var numbers = Package(folder, "Budget.numbers", new() { ["preview.jpg"] = jpeg, ["Index.zip"] = [0] });

        var jpg = Assert.Single(await NoTools.ConvertAsync(new ConversionRequest(numbers, OutputFormat.Jpg)));
        Assert.Equal("Budget.jpg", Path.GetFileName(jpg));
        Assert.Equal(jpeg, File.ReadAllBytes(jpg));
        var png = Assert.Single(await NoTools.ConvertAsync(new ConversionRequest(numbers, OutputFormat.Png)));
        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((60, 40), (decoded.Width, decoded.Height));
        // A picture preview cannot make a PDF.
        var error = await Assert.ThrowsAsync<ConversionException>(() => NoTools.ConvertAsync(new ConversionRequest(numbers, OutputFormat.Pdf)));
        Assert.Contains("Install LibreOffice in Settings → Extra Formats", error.Message);
    }

    [Fact]
    public async Task FolderPackagesWorkToo()
    {
        using var folder = new TempFolder();
        var package = FolderPackage(folder, "Old.pages", new() { ["QuickLook/Preview.pdf"] = PreviewPdf(1) });
        var pdf = Assert.Single(await NoTools.ConvertAsync(new ConversionRequest(package, OutputFormat.Pdf)));
        Assert.Equal("Old.pdf", Path.GetFileName(pdf));
        Assert.Contains("preview page 1", Fixtures.PageTexts(pdf)[0]);
    }

    [Fact]
    public async Task PackagesZippedByHandKeepTheirContentsInAFolder()
    {
        using var folder = new TempFolder();
        var pages = Package(folder, "Hand.pages", new() { ["Hand/QuickLook/Preview.pdf"] = PreviewPdf(1) });
        Assert.Single(await NoTools.ConvertAsync(new ConversionRequest(pages, OutputFormat.Pdf)));
    }

    [Fact]
    public async Task NoPreviewAndNoLibreOfficeSaysWhatToInstall()
    {
        using var folder = new TempFolder();
        var pages = Package(folder, "Empty.pages", new() { ["Index/Document.iwa"] = [1] });
        var error = await Assert.ThrowsAsync<ConversionException>(() => NoTools.ConvertAsync(new ConversionRequest(pages, OutputFormat.Pdf)));
        Assert.Equal("“Empty.pages” has no preview inside to convert. Install LibreOffice in Settings → Extra Formats to convert Pages files.", error.Message);
        Assert.Equal(["Empty.pages"], folder.Names());
    }

    [Fact]
    public async Task LibreOfficeConvertsWhenInstalled()
    {
        using var folder = new TempFolder();
        var pages = Package(folder, "Essay.pages", new() { ["QuickLook/Preview.pdf"] = PreviewPdf(1) });
        var fake = new FakeTool
        {
            Behaviour = (invocation, _) =>
            {
                File.WriteAllText(Path.Combine(FakeTool.ValueOf(invocation, "--outdir")!, "Essay.docx"), "docx from LO");
                return Task.CompletedTask;
            },
        };
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);
        var docx = Assert.Single(await converter.ConvertAsync(new ConversionRequest(pages, OutputFormat.Docx)));
        Assert.Equal("docx from LO", File.ReadAllText(docx));
        Assert.Equal("docx", FakeTool.ValueOf(fake.Calls[0].Invocation, "--convert-to"));
    }

    [Fact]
    public async Task WhenLibreOfficeFailsThePreviewStillDoesWhatItCan()
    {
        using var folder = new TempFolder();
        var preview = PreviewPdf(1);
        var pages = Package(folder, "Essay.pages", new() { ["QuickLook/Preview.pdf"] = preview });
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("soffice", "cannot read") };
        var converter = Fixtures.ConverterWith(Fixtures.WithLibreOffice, fake.Runner);

        Assert.Equal(preview, File.ReadAllBytes(Assert.Single(await converter.ConvertAsync(new ConversionRequest(pages, OutputFormat.Pdf)))));
        // The preview cannot make a DOCX, so the LibreOffice error stands.
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(pages, OutputFormat.Docx)));
        Assert.Contains("cannot read", error.Message);
    }

    [Fact]
    public void AppleFormatsAreNeverOffered()
    {
        foreach (var name in new[] { "a.docx", "a.xlsx", "a.pptx", "a.csv" })
        {
            var formats = ConversionCatalog.Formats(@"C:\x\" + name, Fixtures.Everything);
            Assert.DoesNotContain(OutputFormat.Pages, formats);
            Assert.DoesNotContain(OutputFormat.Numbers, formats);
            Assert.DoesNotContain(OutputFormat.Key, formats);
        }
    }

    [Fact]
    public void EntryNamesAreReadFromTheCentralDirectory()
    {
        using var folder = new TempFolder();
        var pages = Package(folder, "A.pages", new() { ["QuickLook/Preview.pdf"] = [1], ["QuickLook/Thumbnail.jpg"] = [2], ["Index/x.iwa"] = [3] });
        Assert.Equal("QuickLook/Preview.pdf", IWorkPreview.Find(pages)?.Path);
        Assert.Equal([OutputFormat.Pdf, OutputFormat.Jpg, OutputFormat.Png], IWorkPreview.Find(pages)!.Outputs);
    }
}

public class PandocTests
{
    private static string MakeDocx(TempFolder folder, string name = "Report.docx")
    {
        var path = folder.Combine(name);
        WordWriter.Write(Fixtures.RichModel(), path);
        return path;
    }

    private static FakeTool Pandoc(string content = "pandoc made this")
    {
        var fake = new FakeTool();
        fake.Behaviour = (invocation, _) =>
        {
            File.WriteAllText(FakeTool.ValueOf(invocation, "--output")!, content);
            return Task.CompletedTask;
        };
        return fake;
    }

    [Fact]
    public async Task DocxToMarkdownArguments()
    {
        using var folder = new TempFolder();
        var fake = Pandoc();
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, fake.Runner);
        var docx = MakeDocx(folder);
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Md)));
        Assert.Equal("pandoc made this", File.ReadAllText(output));
        Assert.Equal("Report.md", Path.GetFileName(output));

        var call = Assert.Single(fake.Calls);
        Assert.Equal(Fixtures.WithPandoc.Pandoc, call.Executable);
        Assert.Equal([docx, "--from", "docx", "--to", "gfm", "--output", output, "--resource-path", folder.Path, "--wrap", "none"], call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(180), call.Invocation.Timeout);
    }

    [Fact]
    public async Task EpubAndOdtAreStandaloneWithAPageTitle()
    {
        using var folder = new TempFolder();
        var fake = Pandoc();
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, fake.Runner);
        var docx = MakeDocx(folder, "My Book.docx");
        await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Epub));
        await converter.ConvertAsync(new ConversionRequest(docx, OutputFormat.Odt));
        Assert.Contains("epub3", fake.Calls[0].Arguments);
        Assert.Contains("--standalone", fake.Calls[0].Arguments);
        Assert.Contains("pagetitle=My Book", fake.Calls[0].Arguments);
        Assert.Contains("odt", fake.Calls[1].Arguments);
        Assert.DoesNotContain("pagetitle=My Book", fake.Calls[1].Arguments);
    }

    [Fact]
    public async Task BooksGoThroughPandocToDocxMarkdownAndHtml()
    {
        using var folder = new TempFolder();
        var book = folder.File("Novel.epub", "x");
        var fake = Pandoc();
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, fake.Runner);
        await converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Md));
        await converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Html));
        Assert.Contains("gfm-raw_html", fake.Calls[0].Arguments);
        Assert.DoesNotContain("--standalone", fake.Calls[0].Arguments);
        Assert.Contains("--embed-resources", fake.Calls[1].Arguments);
        Assert.Contains("html5", fake.Calls[1].Arguments);
    }

    [Fact]
    public async Task PdfFromABookNeedsATypesetter()
    {
        using var folder = new TempFolder();
        var book = folder.File("Novel.epub", "x");
        var fake = Pandoc();
        var withEngine = Fixtures.ConverterWith(Fixtures.WithPandoc with { PdfEngine = @"C:\tex\bin\xelatex.exe" }, fake.Runner);
        await withEngine.ConvertAsync(new ConversionRequest(book, OutputFormat.Pdf));
        var args = fake.Calls[0].Arguments;
        Assert.Equal(@"C:\tex\bin\xelatex.exe", args[args.ToList().IndexOf("--pdf-engine") + 1]);
        Assert.Contains("geometry:margin=1in", args);
        Assert.DoesNotContain("--to", args);
        Assert.Contains(@"C:\tex\bin", fake.Calls[0].Invocation.ExtraEnvironment!["PATH"]);

        Assert.Null(Fixtures.ConverterWith(Fixtures.WithPandoc).Registry.Find(new ConversionRequest(book, OutputFormat.Pdf) { Tools = Fixtures.WithPandoc }));
    }

    [Fact]
    public async Task WhenPandocFailsADocumentIsConvertedTheBuiltInWay()
    {
        using var folder = new TempFolder();
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("pandoc", "bad input") };
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, fake.Runner);
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(MakeDocx(folder), OutputFormat.Md)));
        Assert.Single(fake.Calls);
        Assert.StartsWith("# Annual Report", File.ReadAllText(output));
        Assert.Equal(["Report.docx", "Report.md"], folder.Names());
    }

    [Fact]
    public async Task WhenPandocWritesNothingTheBuiltInWayRuns()
    {
        using var folder = new TempFolder();
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, new FakeTool().Runner);
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(MakeDocx(folder), OutputFormat.Epub)));
        Assert.Contains("OEBPS/content.opf", Fixtures.ZipNames(output));
    }

    [Fact]
    public async Task ABookThatPandocCannotReadFailsWithoutFallback()
    {
        using var folder = new TempFolder();
        var book = folder.File("Novel.epub", "x");
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("pandoc", "not an epub") };
        var converter = Fixtures.ConverterWith(Fixtures.WithPandoc, fake.Runner);
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Docx)));
        Assert.Contains("not an epub", error.Message);
        Assert.Equal(["Novel.epub"], folder.Names());
    }

    [Fact]
    public void MarkdownSourcesAreNotPandocsHere()
    {
        var registry = EngineRegistry.BuiltIn();
        DocumentEngines.Register(registry);
        Assert.Null(registry.Find(new ConversionRequest(@"C:\x\a.md", OutputFormat.Docx) { Tools = Fixtures.WithPandoc }));
    }
}

public class CalibreTests
{
    private static FakeTool Calibre()
    {
        var fake = new FakeTool();
        fake.Behaviour = (invocation, onLine) =>
        {
            onLine?.Invoke("1% Converting input to HTML...");
            onLine?.Invoke("34% Running transforms on e-book...");
            onLine?.Invoke("Output saved to " + invocation.Arguments[1]);
            File.WriteAllText(invocation.Arguments[1], "book from calibre");
            return Task.CompletedTask;
        };
        return fake;
    }

    [Theory]
    [InlineData("34% Running transforms", 0.34)]
    [InlineData("100% Done", 1.0)]
    [InlineData("Output saved to x", null)]
    [InlineData("% nothing", null)]
    public void ReadsProgress(string line, double? expected) => Assert.Equal(expected, CalibreEngine.FractionFromProgressLine(line));

    [Fact]
    public async Task ArgumentsAreTwoAbsolutePathsAndTheEnvironmentDisablesTheGpu()
    {
        using var folder = new TempFolder();
        var book = folder.File("-odd name.epub", "x");
        var fake = Calibre();
        var converter = Fixtures.ConverterWith(Fixtures.WithCalibre, fake.Runner);
        var progress = new List<double>();
        var output = Assert.Single(await converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Mobi), new SyncProgress(progress.Add)));

        var call = Assert.Single(fake.Calls);
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(book, call.Arguments[0]);
        Assert.True(Path.IsPathRooted(call.Arguments[1]));
        Assert.EndsWith(".mobi", call.Arguments[1]);
        Assert.NotEqual(folder.Path, Path.GetDirectoryName(call.Arguments[1]));   // calibre works in a folder of its own
        Assert.Equal("--disable-gpu", call.Invocation.ExtraEnvironment!["QTWEBENGINE_CHROMIUM_FLAGS"]);
        Assert.Equal("-odd name.mobi", Path.GetFileName(output));
        Assert.Equal("book from calibre", File.ReadAllText(output));
        Assert.Contains(0.34, progress);
        Assert.Equal(1.0, progress[^1]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(call.Arguments[1])), "the workspace is removed");
        Assert.Equal(["-odd name.epub", "-odd name.mobi"], folder.Names().Order());
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Theory]
    [InlineData("a.epub", OutputFormat.Azw3)]
    [InlineData("a.mobi", OutputFormat.Epub)]
    [InlineData("a.azw3", OutputFormat.Pdf)]
    [InlineData("a.azw", OutputFormat.Epub)]
    [InlineData("a.pdf", OutputFormat.Mobi)]
    [InlineData("a.pdf", OutputFormat.Azw3)]
    public void CalibreTakesBooksAndPdfsToKindleFormats(string name, OutputFormat format) =>
        Assert.True(new CalibreEngine().CanConvert(new ConversionRequest(@"C:\x\" + name, format) { Tools = Fixtures.WithCalibre }));

    [Theory]
    [InlineData("a.docx", OutputFormat.Mobi)]
    [InlineData("a.epub", OutputFormat.Docx)]
    [InlineData("a.epub", OutputFormat.Epub)]
    [InlineData("a.pdf", OutputFormat.Epub)]
    public void OtherConversionsAreNotCalibres(string name, OutputFormat format) =>
        Assert.False(new CalibreEngine().CanConvert(new ConversionRequest(@"C:\x\" + name, format) { Tools = Fixtures.WithCalibre }));

    [Fact]
    public async Task ACrashIsRetriedOnceButAnErrorIsNot()
    {
        using var folder = new TempFolder();
        var book = folder.File("Book.epub", "x");
        var attempts = 0;
        var crashing = new FakeTool
        {
            Behaviour = (invocation, _) =>
            {
                if (++attempts == 1) throw new ProcessFailure("ebook-convert", "", crashed: true);
                File.WriteAllText(invocation.Arguments[1], "ok");
                return Task.CompletedTask;
            },
        };
        var converter = Fixtures.ConverterWith(Fixtures.WithCalibre, crashing.Runner);
        Assert.Single(await converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Pdf)));
        Assert.Equal(2, crashing.Calls.Count);

        var failing = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("ebook-convert", "DRM protected") };
        var error = await Assert.ThrowsAsync<ConversionException>(() => Fixtures.ConverterWith(Fixtures.WithCalibre, failing.Runner).ConvertAsync(new ConversionRequest(book, OutputFormat.Mobi)));
        Assert.Contains("DRM protected", error.Message);
        Assert.Single(failing.Calls);
        Assert.Equal(["Book.epub", "Book.pdf"], folder.Names());
    }

    [Fact]
    public async Task ACrashThatKeepsHappeningIsReportedAsOne()
    {
        using var folder = new TempFolder();
        var book = folder.File("Book.epub", "x");
        var fake = new FakeTool { Behaviour = (_, _) => throw new ProcessFailure("ebook-convert", "", crashed: true) };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Fixtures.ConverterWith(Fixtures.WithCalibre, fake.Runner).ConvertAsync(new ConversionRequest(book, OutputFormat.Pdf)));
        Assert.Contains("crashed", error.Message);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(["Book.epub"], folder.Names());
    }

    [Fact]
    public async Task NoOutputFileIsAnErrorAndCancellingLeavesNothing()
    {
        using var folder = new TempFolder();
        var book = folder.File("Book.epub", "x");
        var silent = new FakeTool();
        var converter = Fixtures.ConverterWith(Fixtures.WithCalibre, silent.Runner);
        var error = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Mobi)));
        Assert.Equal("calibre couldn’t convert “Book.epub”.", error.Message);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(new ConversionRequest(book, OutputFormat.Mobi), null, cancel.Token));
        Assert.Equal(["Book.epub"], folder.Names());
    }

    [Fact]
    public async Task TheRunnerKeepsCalibreOutputOutOfTheFolderUntilItIsComplete()
    {
        using var folder = new TempFolder();
        var book = folder.File("Book.epub", "x");
        var fake = Calibre();
        var converter = Fixtures.ConverterWith(Fixtures.WithCalibre, fake.Runner);
        var result = await new ConversionRunner(converter).ConvertAsync([book], OutputFormat.Mobi);
        Assert.Equal(["Book.mobi"], result.Saved.Select(Path.GetFileName));
        Assert.Equal(["Book.epub", "Book.mobi"], folder.Names());
    }
}
