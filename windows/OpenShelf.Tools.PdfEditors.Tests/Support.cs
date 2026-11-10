using OpenShelf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Tools.PdfEditors.Tests;

/// <summary>A temp folder that goes away with the test, and builders for real PDFs.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "os-pdfed-" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public IReadOnlyList<string> Names() => Directory.EnumerateFiles(Path).Select(System.IO.Path.GetFileName).Order().ToList()!;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }

    /// <summary>A PDF with <paramref name="pages"/> pages, each saying its number.</summary>
    public string MakePdf(string name, int pages, Action<PdfDocumentInformation>? info = null)
    {
        var path = File(name);
        using var document = new PdfDocument();
        for (var n = 1; n <= pages; n++)
        {
            var page = document.AddPage();
            page.Width = 612;
            page.Height = 792;
            var font = new PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Helvetica");
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font;
            var resources = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            var content = new PdfDictionary(document);
            document.Internals.AddObject(content);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes($"BT /F1 36 Tf 72 700 Td (Page {n}) Tj ET"));
            page.Elements["/Contents"] = content.Reference!;
        }
        info?.Invoke(document.Info);
        document.Save(path);
        return path;
    }

    public string MakeLocked(string name)
    {
        var path = File(name);
        using var document = new PdfDocument();
        document.AddPage();
        document.SecuritySettings.UserPassword = "pw";
        document.SecuritySettings.OwnerPassword = "owner";
        document.Save(path);
        return path;
    }

    public string MakeDamaged(string name)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, "%PDF-1.4\nnot really a pdf\n");
        return path;
    }

    public static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    public static PdfDocumentInformation InfoOf(string path, out PdfDocument document)
    {
        document = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        return document.Info;
    }
}

internal sealed class FakeToasts : IToastService
{
    public List<string> Shown { get; } = [];
    public void Show(string message, ScreenPoint near) => Shown.Add(message);
}

internal sealed class FakeShelves : IShelfService
{
    public List<string> Added { get; } = [];
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) => Added.AddRange(paths);
    public void Flush() { }
}

internal sealed class FakeProgress : IProgressService
{
    public List<string> Titles { get; } = [];
    public List<string> Finished { get; } = [];
    public bool CancelOnBegin { get; set; }

    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel)
    {
        Titles.Add(title);
        if (CancelOnBegin) cancel.Cancel();
        return new Card(this);
    }

    private sealed class Card(FakeProgress owner) : IProgressCard
    {
        public void Report(int completed, double? fraction = null, string? detail = null) { }
        public void Finish(string message) => owner.Finished.Add(message);
        public void Dispose() { }
    }
}
