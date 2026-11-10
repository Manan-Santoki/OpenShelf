using OpenShelf.Core;
using OpenShelf.Tools.Pdf;
using SkiaSharp;

namespace OpenShelf.Tools.PdfEditors.Tests;

public class RegistrationTests
{
    private static (ToolsCatalog Catalog, FakeToasts Toasts, FakeProgress Progress, FakeShelves Shelves) NewCatalog()
    {
        var toasts = new FakeToasts();
        var progress = new FakeProgress();
        var shelves = new FakeShelves();
        var catalog = new ToolsCatalog(toasts, progress, shelves);
        PdfTools.Register(catalog);
        return (catalog, toasts, progress, shelves);
    }

    private static string Png(TempFolder dir, string name)
    {
        using var bitmap = new SKBitmap(60, 40);
        bitmap.Erase(new SKColor(30, 100, 200));
        using var image = SKImage.FromBitmap(bitmap);
        File.WriteAllBytes(dir.File(name), image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        return dir.File(name);
    }

    [Fact]
    public void The_pdf_tools_are_registered_in_the_Mac_order_with_stable_ids()
    {
        using var dir = new TempFolder();
        var (catalog, _, _, _) = NewCatalog();
        var one = catalog.For([dir.MakePdf("a.pdf", 2)]);
        Assert.Equal(["tool.pdf.pages=Pages", "tool.pdf.split=Split", "tool.pdf.compress=Compress", "tool.pdf.metadata=Metadata"], one.Entries.Select(e => $"{e.Id}={e.Title}"));
        Assert.All(one.Entries, e => Assert.False(string.IsNullOrEmpty(e.Glyph)));
    }

    [Fact]
    public void Several_pdfs_get_Combine_first_then_the_pdf_tools()
    {
        using var dir = new TempFolder();
        var (catalog, _, _, _) = NewCatalog();
        var content = catalog.For([dir.MakePdf("a.pdf", 2), dir.MakePdf("b.pdf", 1)]);
        Assert.Equal(["Combine", "Pages", "Split", "Compress", "Metadata"], content.Entries.Select(e => e.Title));
        Assert.Equal("tool.combine", content.Entries[0].Id);
    }

    [Fact]
    public void Combine_takes_mixed_pdfs_and_images_but_nothing_else()
    {
        using var dir = new TempFolder();
        var (catalog, _, _, _) = NewCatalog();
        var pdf = dir.MakePdf("a.pdf", 1);
        var png = Png(dir, "p.png");
        Assert.Equal(["Combine"], catalog.For([pdf, png]).Entries.Select(e => e.Title));
        Assert.Contains("Combine", catalog.For([png, png]).Entries.Select(e => e.Title));
        // One PDF alone has nothing to combine; a text file is not a PDF or an image.
        Assert.DoesNotContain("Combine", catalog.For([pdf]).Entries.Select(e => e.Title));
        var text = dir.File("n.txt");
        File.WriteAllText(text, "hi");
        Assert.DoesNotContain("Combine", catalog.For([pdf, text]).Entries.Select(e => e.Title));
        Assert.DoesNotContain("Pages", catalog.For([pdf, png]).Entries.Select(e => e.Title));
    }

    [Fact]
    public void One_image_does_not_get_the_pdf_tools()
    {
        using var dir = new TempFolder();
        var (catalog, _, _, _) = NewCatalog();
        var titles = catalog.For([Png(dir, "p.png")]).Entries.Select(e => e.Id).ToList();
        Assert.DoesNotContain(titles, id => id.StartsWith("tool.pdf."));
    }

    [Fact]
    public async Task Combine_runs_with_the_progress_card_and_a_toast_and_hands_the_result_to_the_shelf()
    {
        using var dir = new TempFolder();
        var (catalog, _, progress, shelves) = NewCatalog();
        var pdf = dir.MakePdf("a.pdf", 2);
        var png = Png(dir, "p.png");
        var run = new WheelRun([pdf, png], new ScreenPoint(10, 10), false, null);
        await catalog.For(run.Paths).Entries.Single().Run(run);
        Assert.Equal(["Combining into PDF"], progress.Titles);
        Assert.Equal(["Saved · Combined.pdf"], progress.Finished);
        Assert.Equal([dir.File("Combined.pdf")], shelves.Added);
        Assert.Equal(3, TempFolder.PageCount(dir.File("Combined.pdf")));
    }

    [Fact]
    public async Task Combine_can_be_cancelled_from_the_card_and_leaves_nothing()
    {
        using var dir = new TempFolder();
        var (catalog, _, progress, shelves) = NewCatalog();
        progress.CancelOnBegin = true;
        var run = new WheelRun([dir.MakePdf("a.pdf", 1), dir.MakePdf("b.pdf", 1)], new ScreenPoint(0, 0), false, null);
        await PdfTools.CombineAsync(run, catalog.Services);
        Assert.Equal(["Cancelled"], progress.Finished);
        Assert.Empty(shelves.Added);
        Assert.Equal(["a.pdf", "b.pdf"], dir.Names());
    }

    [Fact]
    public async Task Combine_with_a_locked_pdf_says_so_and_writes_nothing()
    {
        using var dir = new TempFolder();
        var (catalog, _, progress, shelves) = NewCatalog();
        var run = new WheelRun([dir.MakePdf("a.pdf", 1), dir.MakeLocked("Secret.pdf")], new ScreenPoint(0, 0), false, null);
        await PdfTools.CombineAsync(run, catalog.Services);
        Assert.Equal(["Couldn’t combine · “Secret.pdf” is password-protected."], progress.Finished);
        Assert.Empty(shelves.Added);
        Assert.Equal(["a.pdf", "Secret.pdf"], dir.Names());
    }

    [Fact]
    public void Registering_again_replaces_rather_than_duplicates()
    {
        var (catalog, _, _, _) = NewCatalog();
        var before = catalog.Tools.Count;
        PdfTools.Register(catalog);
        Assert.Equal(before, catalog.Tools.Count);
        Assert.Equal(1, catalog.Tools.Count(t => t.Id == "tool.combine"));
    }
}
