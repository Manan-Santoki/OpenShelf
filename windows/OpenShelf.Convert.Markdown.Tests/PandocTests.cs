using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert.Markdown.Tests;

public class PandocTests
{
    private static readonly ExternalTools Pandoc = ExternalTools.None with { Pandoc = @"C:\fake\pandoc.exe" };
    private static readonly ExternalTools PandocWithTypesetter = Pandoc with { PdfEngine = @"C:\fake\tex\xelatex.exe" };

    private static ExternalTools? RealTools()
    {
        var tools = ExternalTools.Detect(ToolLocations.Standard);
        return tools.Pandoc is null ? null : tools;
    }

    private static EngineRegistry Registry(ToolRunner? pandocRunner = null, HtmlPdfPrinter? printer = null)
    {
        var registry = new EngineRegistry();
        var builtIn = new MarkdownEngine(printer);
        registry.Register(new PandocMarkdownEngine(pandocRunner, builtIn), MarkdownEngines.PandocPriority);
        registry.Register(builtIn, MarkdownEngines.BuiltInPriority);
        return registry;
    }

    /// <summary>Stands in for Pandoc: records the call and writes <paramref name="content"/> to --output.</summary>
    private sealed class FakePandoc(string content = "pandoc")
    {
        public List<ToolInvocation> Invocations { get; } = [];

        public Task Run(ToolInvocation invocation, Action<string>? onLine, CancellationToken token)
        {
            Invocations.Add(invocation);
            var arguments = invocation.Arguments.ToList();
            File.WriteAllText(arguments[arguments.IndexOf("--output") + 1], content);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void MarkdownEnginesRegisterPandocAheadOfTheBuiltInOne()
    {
        var registry = new EngineRegistry();
        MarkdownEngines.Register(registry);
        Assert.IsType<PandocMarkdownEngine>(registry.Engines[0]);
        Assert.IsType<MarkdownEngine>(registry.Engines[1]);
        var md = @"C:\notes\a.md";
        Assert.IsType<PandocMarkdownEngine>(registry.Find(new ConversionRequest(md, OutputFormat.Docx) { Tools = Pandoc }));
        Assert.IsType<MarkdownEngine>(registry.Find(new ConversionRequest(md, OutputFormat.Docx) { Tools = ExternalTools.None }));
        // Plain text reads better without Pandoc's wrapping.
        Assert.IsType<MarkdownEngine>(registry.Find(new ConversionRequest(md, OutputFormat.Txt) { Tools = Pandoc }));
        // PDFs need a typesetter.
        Assert.IsType<MarkdownEngine>(registry.Find(new ConversionRequest(md, OutputFormat.Pdf) { Tools = Pandoc }));
        Assert.IsType<PandocMarkdownEngine>(registry.Find(new ConversionRequest(md, OutputFormat.Pdf) { Tools = PandocWithTypesetter }));
        Assert.Null(registry.Find(new ConversionRequest(@"C:\notes\a.txt", OutputFormat.Docx) { Tools = Pandoc }));
    }

    [Theory]
    [InlineData(OutputFormat.Docx, "docx")]
    [InlineData(OutputFormat.Html, "html5")]
    [InlineData(OutputFormat.Epub, "epub3")]
    [InlineData(OutputFormat.Rtf, "rtf")]
    public void PandocArguments(OutputFormat format, string writer)
    {
        var args = PandocMarkdownEngine.Arguments(@"C:\Users\me\Notes\hello.md", format, @"C:\out\hello.x", null)!;
        Assert.Equal(new[] { @"C:\Users\me\Notes\hello.md", "--from", "markdown", "--to", writer }, args.Take(5));
        Assert.Contains("--standalone", args);
        Assert.Equal(format is OutputFormat.Html or OutputFormat.Epub, args.Contains("pagetitle=hello"));
        Assert.Contains(@"C:\Users\me\Notes", args);
        Assert.Equal(@"C:\out\hello.x", args[args.IndexOf("--output") + 1]);
    }

    [Fact]
    public void PandocPdfArguments()
    {
        Assert.Null(PandocMarkdownEngine.Arguments(@"C:\a.md", OutputFormat.Pdf, @"C:\o.pdf", null));
        var args = PandocMarkdownEngine.Arguments(@"C:\a.md", OutputFormat.Pdf, @"C:\o.pdf", @"C:\tex\xelatex.exe")!;
        Assert.Contains(@"C:\tex\xelatex.exe", args);
        Assert.Contains("geometry:margin=1in", args);
        Assert.DoesNotContain("--to", args);
        var weasy = PandocMarkdownEngine.Arguments(@"C:\a.md", OutputFormat.Pdf, @"C:\o.pdf", @"C:\bin\weasyprint.exe")!;
        Assert.DoesNotContain("geometry:margin=1in", weasy);
    }

    [Fact]
    public async Task PandocWritesBesideTheOriginalWithA180SecondLimit()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes");
        var fake = new FakePandoc();
        var converter = new Converter(Registry(fake.Run), new ExternalToolsProvider(() => Pandoc));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Docx));
        Assert.Equal(["notes.docx"], result.Select(Path.GetFileName));
        Assert.Equal("pandoc", File.ReadAllText(result[0]));
        var call = Assert.Single(fake.Invocations);
        Assert.Equal(@"C:\fake\pandoc.exe", call.Executable);
        Assert.Equal(TimeSpan.FromSeconds(180), call.Timeout);
        Assert.Equal(source, call.Arguments[0]);
    }

    [Fact]
    public async Task PandocRunsThroughTheRunnerWithoutLeavingStagingFolders()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes");
        var fake = new FakePandoc();
        var runner = new ConversionRunner(new Converter(Registry(fake.Run), new ExternalToolsProvider(() => Pandoc)));
        var result = await runner.ConvertAsync([source], OutputFormat.Html, ConversionOptions.Automatic, null, CancellationToken.None, null);
        Assert.Single(result.Saved);
        Assert.Equal(["notes.html", "notes.md"], folder.Names());
        Assert.Equal("pandoc", File.ReadAllText(folder.Combine("notes.html")));
    }

    [Fact]
    public async Task AFailingPandocFallsBackToTheBuiltInConversion()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes\n\nSome **bold** text.");
        var converter = new Converter(
            Registry((_, _, _) => throw new ProcessFailure("pandoc", "bad input", 3)),
            new ExternalToolsProvider(() => Pandoc));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Html));
        Assert.Equal("notes.html", Path.GetFileName(result[0]));
        Assert.Contains("<strong>bold</strong>", File.ReadAllText(result[0]));
    }

    [Fact]
    public async Task APandocThatWritesNothingFallsBackToo()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes\n\nSome **bold** text.");
        var converter = new Converter(Registry((_, _, _) => Task.CompletedTask), new ExternalToolsProvider(() => Pandoc));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Docx));
        Assert.Equal(["notes.docx"], result.Select(Path.GetFileName));
        Assert.Contains("word/document.xml", Zips.Names(result[0]));
    }

    [Fact]
    public async Task APandocThatTimesOutFallsBack()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "Some **bold** text.");
        var converter = new Converter(
            Registry((_, _, _) => throw new ProcessFailure("pandoc", "timed out", 1, timedOut: true)),
            new ExternalToolsProvider(() => Pandoc));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Epub));
        Assert.Contains("mimetype", Zips.Names(result[0]));
    }

    [Fact]
    public async Task CancellingDoesNotFallBack()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes");
        using var cts = new CancellationTokenSource();
        var converter = new Converter(
            Registry((_, _, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }),
            new ExternalToolsProvider(() => Pandoc));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Html), null, cts.Token));
        Assert.Equal(["notes.md"], folder.Names());
    }

    [Fact]
    public async Task PandocPdfPutsTheTypesettersFolderOnPath()
    {
        using var folder = new TempFolder();
        var source = folder.File("notes.md", "# Notes");
        var fake = new FakePandoc("%PDF-fake");
        var converter = new Converter(Registry(fake.Run), new ExternalToolsProvider(() => PandocWithTypesetter));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf));
        Assert.Equal("notes.pdf", Path.GetFileName(result[0]));
        var call = Assert.Single(fake.Invocations);
        Assert.StartsWith(@"C:\fake\tex;", call.ExtraEnvironment!["PATH"]);
        Assert.Contains("--pdf-engine", call.Arguments);
    }

    [Fact]
    public async Task RealPandocMakesDocxHtmlEpubAndRtfFromMarkdown()
    {
        if (RealTools() is not { } tools) return; // Pandoc is not installed here
        using var folder = new TempFolder();
        var source = folder.File("real.md", Samples.Pandoc);
        var converter = new Converter(Registry(), new ExternalToolsProvider(() => tools with { PdfEngine = null }));
        foreach (var format in new[] { OutputFormat.Docx, OutputFormat.Html, OutputFormat.Epub, OutputFormat.Rtf })
        {
            var result = await converter.ConvertAsync(new ConversionRequest(source, format));
            Assert.Equal($"real.{format.FileExtension()}", Path.GetFileName(result[0]));
            Assert.True(new FileInfo(result[0]).Length > 200, $"{format} is empty");
        }
        using (var zip = ZipFile.OpenRead(folder.Combine("real.docx")))
        {
            var document = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(document.Open(), Encoding.UTF8);
            Assert.Contains("An h1 header", reader.ReadToEnd());
        }
        var html = File.ReadAllText(folder.Combine("real.html"));
        Assert.Contains("<title>real</title>", html);
        Assert.Contains("hemp canvas", html);
        Assert.StartsWith("{\\rtf", File.ReadAllText(folder.Combine("real.rtf")));
        Assert.Contains("mimetype", Zips.Names(folder.Combine("real.epub")));
    }
}
