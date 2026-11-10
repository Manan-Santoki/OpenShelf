using System.IO.Compression;
using OpenShelf.Convert;
using OpenShelf.Convert.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace OpenShelf.Convert.Documents.Tests;

/// <summary>A fresh folder deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-docs-" + Guid.NewGuid().ToString("N")[..10]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string File(string name, string text)
    {
        var path = Combine(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public string Bytes(string name, byte[] data)
    {
        var path = Combine(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One recorded tool run.</summary>
public sealed record Call(string Executable, IReadOnlyList<string> Arguments, ToolInvocation Invocation);

/// <summary>A fake tool runner: records every invocation and lets the test write what the tool would have.</summary>
public sealed class FakeTool
{
    public List<Call> Calls { get; } = [];
    public Func<ToolInvocation, Action<string>?, Task>? Behaviour { get; set; }

    public ToolRunner Runner => async (invocation, onLine, ct) =>
    {
        Calls.Add(new Call(invocation.Executable, invocation.Arguments.ToList(), invocation));
        ct.ThrowIfCancellationRequested();
        if (Behaviour is not null) await Behaviour(invocation, onLine);
    };

    /// <summary>The value after <paramref name="flag"/> in the invocation, or the part after "flag=".</summary>
    public static string? ValueOf(ToolInvocation invocation, string flag)
    {
        var list = invocation.Arguments;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == flag && i + 1 < list.Count) return list[i + 1];
            if (list[i].StartsWith(flag + "=", StringComparison.Ordinal)) return list[i][(flag.Length + 1)..];
        }
        return null;
    }
}

public static class Fixtures
{
    public static readonly string Fake = @"C:\fake\tool.exe";

    public static ExternalTools None => ExternalTools.None;
    public static ExternalTools WithLibreOffice => ExternalTools.None with { LibreOffice = @"C:\fake\soffice.exe" };
    public static ExternalTools WithPandoc => ExternalTools.None with { Pandoc = @"C:\fake\pandoc.exe" };
    public static ExternalTools WithCalibre => ExternalTools.None with { EbookConvert = @"C:\fake\ebook-convert.exe" };
    public static ExternalTools Everything => ExternalTools.None with
    {
        LibreOffice = @"C:\fake\soffice.exe", Pandoc = @"C:\fake\pandoc.exe", EbookConvert = @"C:\fake\ebook-convert.exe", PdfEngine = @"C:\fake\xelatex.exe",
    };

    public static bool EdgeAvailable => EdgePrinter.Locate() is not null;

    /// <summary>A text PDF made with PdfPig: each page is a list of (text, size, x, y) lines.</summary>
    public static byte[] Pdf(params (string Text, double Size, double X, double Y)[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.Letter);
            foreach (var (text, size, x, y) in lines) page.AddText(text, size, new PdfPoint(x, y), regular);
        }
        return builder.Build();
    }

    /// <summary>A page of plain text lines, body size, top to bottom.</summary>
    public static (string, double, double, double)[] Lines(params string[] lines) =>
        lines.Select((line, index) => (line, 11.0, 72.0, 720.0 - index * 16)).ToArray();

    /// <summary>A PDF with pages that have nothing on them (a scan has pictures only).</summary>
    public static byte[] BlankPdf(int pages)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++) builder.AddPage(PageSize.Letter);
        return builder.Build();
    }

    public static string[] PageTexts(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(page => page.Text).ToArray();
    }

    public static int PageCount(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.NumberOfPages;
    }

    public static string ZipText(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        var data = Zips.ReadEntry(zip, entry) ?? throw new InvalidOperationException($"{entry} missing from {path}");
        return System.Text.Encoding.UTF8.GetString(data);
    }

    public static string[] ZipNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    /// <summary>A small PNG.</summary>
    public static byte[] Png(int width = 40, int height = 20)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        return Pictures.EncodePng(bitmap);
    }

    public static Converter ConverterWith(ExternalTools tools, ToolRunner? runner = null, EdgePrinter? edge = null)
    {
        var registry = EngineRegistry.BuiltIn();
        DocumentEngines.Register(registry, runner, edge);
        return new Converter(registry, new ExternalToolsProvider(() => tools));
    }

    /// <summary>A model with a heading, styled text, a list, a table, a link and a picture.</summary>
    public static DocumentModel RichModel() => new("Report", new Block[]
    {
        new HeadingBlock(1, [new TextRun("Annual Report")]),
        new ParagraphBlock([new TextRun("Plain, "), new TextRun("bold", Bold: true), new TextRun(", "), new TextRun("italic", Italic: true), new TextRun(" and "),
            new TextRun("underlined", Underline: true), new TextRun(". See "), new TextRun("the site", Link: "https://example.com/a?b=1&c=2"), new TextRun(".")]),
        new HeadingBlock(2, [new TextRun("Lists")]),
        new ListItemBlock(false, 0, [new TextRun("first bullet")]),
        new ListItemBlock(false, 0, [new TextRun("second bullet")]),
        new ListItemBlock(true, 0, [new TextRun("step one")]),
        new ListItemBlock(true, 0, [new TextRun("step two")]),
        new TableBlock(
        [
            [[new TextRun("Name")], [new TextRun("Qty")]],
            [[new TextRun("Apples")], [new TextRun("3")]],
            [[new TextRun("Pears")], [new TextRun("12")]],
        ], true),
        new ImageBlock(Png(), "png", 40, 20, "blue box"),
        new ParagraphBlock([new TextRun("The end.")]),
    });
}
