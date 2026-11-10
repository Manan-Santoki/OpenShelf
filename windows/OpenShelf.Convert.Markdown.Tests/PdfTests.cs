using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace OpenShelf.Convert.Markdown.Tests;

public class PdfTests(ITestOutputHelper output)
{
    private static readonly bool HasEdge = HtmlPdfPrinter.FindEdge() is not null;

    private static int PageCount(byte[] pdf) => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    [Fact]
    public async Task MarkdownBecomesAPdfThroughEdge()
    {
        if (!HasEdge) { output.WriteLine("Edge not installed: skipped"); return; }
        using var folder = new TempFolder();
        var source = folder.File("hello.md", Samples.Pandoc + "\n\n" + string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}. " + string.Concat(Enumerable.Repeat("More words to fill the page. ", 12)))));
        var registry = new EngineRegistry();
        MarkdownEngines.Register(registry);
        var converter = new Converter(registry, new ExternalToolsProvider(() => ExternalTools.None));
        var result = await converter.ConvertAsync(new ConversionRequest(source, OutputFormat.Pdf));
        var path = Assert.Single(result);
        Assert.Equal("hello.pdf", Path.GetFileName(path));
        var bytes = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var pages = PageCount(bytes);
        output.WriteLine($"PDF: {pages} pages, {bytes.Length} bytes");
        Assert.True(pages >= 2, $"expected several pages, found {pages}");
        // The page lives in a private temp folder, not beside the source.
        Assert.Equal(["hello.md", "hello.pdf"], folder.Names());
    }

    [Fact]
    public async Task TheSampleDocumentIsTwoOrThreePages()
    {
        if (!HasEdge) { output.WriteLine("Edge not installed: skipped"); return; }
        using var folder = new TempFolder();
        var source = folder.File("sample.md", Samples.Pandoc);
        await MarkdownConversion.ConvertAsync(source, OutputFormat.Pdf, folder.Combine("sample.pdf"));
        var bytes = File.ReadAllBytes(folder.Combine("sample.pdf"));
        output.WriteLine($"Sample PDF: {PageCount(bytes)} pages, {bytes.Length} bytes");
        Assert.InRange(PageCount(bytes), 2, 3);
    }

    [Theory]
    [InlineData(PaperSize.A4, "595", "842")]
    [InlineData(PaperSize.Letter, "612", "792")]
    public async Task PagesAreSizedForTheRegionsPaper(PaperSize paper, string width, string height)
    {
        if (!HasEdge) { output.WriteLine("Edge not installed: skipped"); return; }
        using var folder = new TempFolder();
        var html = folder.File("page.html", MarkdownHtml.PrintPage(MarkdownDocument.Parse("# Title\n\nText."), "t", paper, _ => null));
        var pdf = folder.Combine("page.pdf");
        await new HtmlPdfPrinter().PrintAsync(html, pdf);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        var box = Regex.Match(text, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        Assert.True(box.Success, "no MediaBox");
        Assert.Equal(double.Parse(width), Math.Round(double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(double.Parse(height), Math.Round(double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task LocalPicturesArePrintedAndRemoteOnesAreNot()
    {
        if (!HasEdge) { output.WriteLine("Edge not installed: skipped"); return; }
        using var folder = new TempFolder();
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 200, 100)) image.Write(folder.Combine("red.png"), ImageMagick.MagickFormat.Png);
        var source = folder.File("pic.md", "![Red box](red.png)\n\n![Remote](https://example.invalid/x.png)\n");
        await MarkdownConversion.ConvertAsync(source, OutputFormat.Pdf, folder.Combine("pic.pdf"));
        var bytes = File.ReadAllBytes(folder.Combine("pic.pdf"));
        Assert.Equal(1, PageCount(bytes));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(bytes).Replace("/Subtype/Image", "/Subtype /Image"));
    }

    [Fact]
    public async Task MissingEdgeIsAClearConversionError()
    {
        var printer = new HtmlPdfPrinter(findEdge: () => null);
        var error = await Assert.ThrowsAsync<ConversionException>(() => printer.PrintAsync("x.html", "x.pdf"));
        Assert.Contains("Microsoft Edge", error.Message);
        Assert.False(printer.IsAvailable);
    }

    [Fact]
    public async Task EdgeIsRunHeadlessWithAPrivateProfileAndAFileUrl()
    {
        using var folder = new TempFolder();
        var html = folder.File("a b.html", "<p>x</p>");
        var pdf = folder.Combine("out.pdf");
        ToolInvocation? seen = null;
        var printer = new HtmlPdfPrinter((invocation, _, _) =>
        {
            seen = invocation;
            File.WriteAllText(pdf, "%PDF-1.4 fake");
            return Task.CompletedTask;
        }, () => @"C:\fake\msedge.exe");
        await printer.PrintAsync(html, pdf);
        Assert.NotNull(seen);
        Assert.Equal(@"C:\fake\msedge.exe", seen!.Executable);
        Assert.Contains("--headless=new", seen.Arguments);
        Assert.Contains("--disable-gpu", seen.Arguments);
        Assert.Contains("--no-pdf-header-footer", seen.Arguments);
        Assert.Contains("--print-to-pdf=" + pdf, seen.Arguments);
        var profile = Assert.Single(seen.Arguments, a => a.StartsWith("--user-data-dir="))["--user-data-dir=".Length..];
        Assert.StartsWith(Path.GetTempPath(), profile);
        Assert.False(Directory.Exists(profile), "the private profile is removed afterwards");
        Assert.StartsWith("file:///", seen.Arguments[^1]);
        Assert.Contains("a%20b.html", seen.Arguments[^1]);
        Assert.Equal(TimeSpan.FromSeconds(120), seen.Timeout);
    }

    [Fact]
    public async Task EdgeFailuresBecomeMessages()
    {
        var printer = new HtmlPdfPrinter((_, _, _) => throw new ProcessFailure("msedge", "profile locked"), () => @"C:\fake\msedge.exe");
        var error = await Assert.ThrowsAsync<ConversionException>(() => printer.PrintAsync("x.html", Path.Combine(Path.GetTempPath(), "never.pdf")));
        Assert.Contains("profile locked", error.Message);
        var silent = new HtmlPdfPrinter((_, _, _) => Task.CompletedTask, () => @"C:\fake\msedge.exe");
        var empty = await Assert.ThrowsAsync<ConversionException>(() => silent.PrintAsync("x.html", Path.Combine(Path.GetTempPath(), "never2.pdf")));
        Assert.Contains("didn’t produce", empty.Message);
    }

    [Fact]
    public void PaperFollowsTheRegion()
    {
        Assert.Equal(PaperSize.Letter, PaperSizes.ForRegion("US"));
        Assert.Equal(PaperSize.Letter, PaperSizes.ForRegion("ca"));
        Assert.Equal(PaperSize.A4, PaperSizes.ForRegion("GB"));
        Assert.Equal(PaperSize.A4, PaperSizes.ForRegion("IN"));
        Assert.Equal(PaperSize.A4, PaperSizes.ForRegion(null));
    }
}
