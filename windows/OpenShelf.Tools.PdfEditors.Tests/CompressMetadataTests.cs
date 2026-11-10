using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors.Tests;

public class CompressModelTests
{
    private static CompressModel Model(params string[] paths) => new(PdfFileState.ReadAll(paths));

    [Fact]
    public void Starts_on_Balanced_with_the_estimate_pending()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 2));
        Assert.Equal(PdfCompressionLevel.Balanced, model.Level);
        Assert.Equal("Estimating…", model.EstimateText);
        Assert.Equal("", model.SavingText);
        Assert.False(model.IsKnownNotSmaller);
    }

    [Fact]
    public void The_estimate_of_the_first_pdf_is_scaled_to_the_whole_batch()
    {
        Assert.Equal(300, CompressModel.Scale(100, 400, 1200));
        Assert.Equal(50, CompressModel.Scale(50, 0, 1000));
    }

    [Fact]
    public void A_smaller_estimate_reads_with_its_saving()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 2));
        var size = model.TotalOriginal;
        model.SetEstimate(size / 2);
        Assert.Equal($"≈ {PdfText.Bytes(size / 2)}", model.EstimateText);
        Assert.InRange(model.SavingPercent, 49, 51);
        Assert.EndsWith("% smaller)", model.SavingText);
        Assert.False(model.IsKnownNotSmaller);
    }

    [Fact]
    public void An_estimate_that_would_not_be_smaller_says_so_and_blocks_a_single_pdf()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 2));
        model.SetEstimate(model.TotalOriginal + 100);
        Assert.Equal("  (no smaller — nothing left to squeeze)", model.SavingText);
        Assert.True(model.IsKnownNotSmaller);
    }

    [Fact]
    public void With_several_pdfs_the_estimate_names_the_count_and_never_blocks()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 2), dir.MakePdf("b.pdf", 3));
        model.SetEstimate(model.Files[0].Size * 2);
        Assert.Contains("for 2 PDFs", model.EstimateText);
        Assert.Equal("2 PDFs", model.Title);
        Assert.EndsWith("in total", model.Subtitle);
        Assert.False(model.IsKnownNotSmaller);
    }

    [Fact]
    public void A_failed_estimate_says_so()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeLocked("Secret.pdf"));
        model.StartEstimate();
        model.FailEstimate();
        Assert.Equal("Couldn’t estimate the size", model.EstimateText);
        Assert.Equal("“Secret.pdf” is password-protected.", model.Problem);
    }

    [Fact]
    public void The_not_smaller_messages_match_the_Mac()
    {
        Assert.Equal("Already as small as it gets — nothing saved.", CompressModel.NotSmallerMessage(1));
        Assert.Equal("These 3 PDFs are already as small as they get — nothing saved.", CompressModel.NotSmallerMessage(3));
    }

    [Fact]
    public void Saving_tiny_pdfs_writes_nothing_and_explains()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 1), dir.MakePdf("b.pdf", 1));
        var outcome = model.Save(null, CancellationToken.None, out var message);
        Assert.False(outcome.SavedAnything);
        Assert.Equal(2, outcome.Unchanged);
        Assert.Equal("These 2 PDFs are already as small as they get — nothing saved.", message);
        Assert.Equal(["a.pdf", "b.pdf"], dir.Names());
    }

    [Fact]
    public void Saving_a_locked_pdf_fails_with_the_password_message()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeLocked("Secret.pdf"));
        var error = Assert.Throws<SaveException>(() => model.Save(null, CancellationToken.None, out _));
        Assert.Equal("“Secret.pdf” is password-protected.", error.Message);
    }

    [Fact]
    public void A_cancelled_compression_leaves_nothing_behind()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 1));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.Save(null, cancel.Token, out _));
        Assert.Equal(["a.pdf"], dir.Names());
    }
}

public class MetadataModelTests
{
    private static MetadataModel Model(params string[] paths) => new(PdfFileState.ReadAll(paths));

    /// <summary>A PDF with no document info at all (PDFsharp stamps every file it writes, so the stamp is stripped).</summary>
    private static string Clean(TempFolder dir, string name, int pages = 1)
    {
        var source = dir.MakePdf("src-" + name, pages);
        var clean = PdfMetadata.RemoveAllAsync(source).GetAwaiter().GetResult();
        File.Delete(source);
        File.Move(clean, dir.File(name));
        return dir.File(name);
    }

    private static string WithInfo(TempFolder dir, string name = "Report.pdf") =>
        dir.MakePdf(name, 2, info => { info.Title = "Quarterly"; info.Author = "Jordan"; info.Keywords = "a, b"; });

    [Fact]
    public void One_pdf_with_info_shows_it_and_offers_Save_only_after_a_change()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir));
        Assert.True(model.IsSingle);
        Assert.Equal("Quarterly", model.Fields.Title);
        Assert.Equal("Jordan", model.Fields.Author);
        Assert.Equal("Save", model.PrimaryTitle);
        Assert.False(model.CanSave);
        model.Fields = model.Fields with { Title = "Annual" };
        Assert.True(model.CanSave);
        model.Reset();
        Assert.False(model.CanSave);
    }

    [Fact]
    public void Clearing_every_field_offers_a_clean_copy_only_when_there_is_something_to_remove()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir));
        model.RemoveAll();
        Assert.True(model.Fields.IsEmpty);
        Assert.True(model.CanSave);

        // A PDF with nothing but the writer's own stamp is already clean.
        var plain = Model(Clean(dir, "Plain.pdf"));
        Assert.False(plain.CanSave);
    }

    [Fact]
    public void The_header_detail_says_pages_and_size_or_how_many_have_info()
    {
        using var dir = new TempFolder();
        var one = Model(WithInfo(dir));
        Assert.StartsWith("2 pages · ", one.Detail);
        Assert.Equal("Report.pdf", one.Title);
        var many = Model(WithInfo(dir, "a.pdf"), Clean(dir, "b.pdf"), WithInfo(dir, "c.pdf"));
        Assert.Equal("2 of 3 have document info", many.Detail);
        Assert.Equal("3 PDFs", many.Title);
        Assert.Equal("Remove Metadata", many.PrimaryTitle);
    }

    [Fact]
    public void Several_pdfs_can_be_stripped_when_any_has_info()
    {
        using var dir = new TempFolder();
        Assert.True(Model(WithInfo(dir, "a.pdf"), Clean(dir, "b.pdf")).CanSave);
        Assert.False(Model(Clean(dir, "c.pdf"), Clean(dir, "d.pdf")).CanSave);
    }

    [Fact]
    public void Each_file_in_the_list_has_a_status()
    {
        using var dir = new TempFolder();
        var files = PdfFileState.ReadAll([WithInfo(dir, "a.pdf"), Clean(dir, "b.pdf"), dir.MakeLocked("c.pdf"), dir.MakeDamaged("d.pdf")]);
        Assert.Equal([("Has document info", false), ("Clean", false), ("Locked", true), ("Unreadable", true)], files.Select(MetadataModel.Status));
    }

    [Fact]
    public void A_locked_pdf_blocks_saving_with_the_Mac_wording()
    {
        using var dir = new TempFolder();
        var single = Model(dir.MakeLocked("Secret.pdf"));
        Assert.False(single.CanSave);
        Assert.False(single.ShowsForm);
        Assert.Equal("Password-protected PDFs can’t be changed.", single.Warning);
        Assert.StartsWith("Password-protected · ", single.Detail);

        var batch = Model(WithInfo(dir), dir.MakeLocked("Secret2.pdf"));
        Assert.False(batch.CanSave);
        Assert.Equal("Password-protected PDFs can’t be changed.", batch.Warning);
    }

    [Fact]
    public void A_damaged_pdf_shows_its_message_and_blocks_saving()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeDamaged("Broken.pdf"));
        Assert.False(model.CanSave);
        Assert.Equal("“Broken.pdf” couldn’t be opened as a PDF.", model.Warning);
        Assert.Equal("Can’t be opened", model.Detail);
    }

    [Fact]
    public void Saving_edited_fields_writes_a_Metadata_copy()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir));
        model.Fields = model.Fields with { Title = "Annual", Keywords = "x,  y" };
        var outcome = model.Save(null, CancellationToken.None);
        Assert.Equal([dir.File("Report Metadata.pdf")], outcome.Saved);
        var info = TempFolder.InfoOf(dir.File("Report Metadata.pdf"), out var document);
        using (document)
        {
            Assert.Equal("Annual", info.Title);
            Assert.Equal("x, y", info.Keywords);
            Assert.Equal("Jordan", info.Author);
        }
    }

    [Fact]
    public void Saving_with_every_field_cleared_writes_a_Clean_copy()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir));
        model.RemoveAll();
        var outcome = model.Save(null, CancellationToken.None);
        Assert.Equal([dir.File("Report Clean.pdf")], outcome.Saved);
        var info = TempFolder.InfoOf(dir.File("Report Clean.pdf"), out var document);
        using (document)
        {
            Assert.True(string.IsNullOrEmpty(info.Title));
            Assert.True(string.IsNullOrEmpty(info.Author));
        }
    }

    [Fact]
    public void Saving_several_strips_the_ones_with_info_and_counts_the_clean()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir, "a.pdf"), Clean(dir, "b.pdf"));
        var outcome = model.Save(null, CancellationToken.None);
        Assert.Equal([dir.File("a Clean.pdf")], outcome.Saved);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("1 cleaned · 1 already clean · a Clean.pdf", outcome.Message);
    }

    [Fact]
    public void A_cancelled_save_leaves_no_file()
    {
        using var dir = new TempFolder();
        var model = Model(WithInfo(dir));
        model.RemoveAll();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.Save(null, cancel.Token));
        Assert.Equal(["Report.pdf"], dir.Names());
    }
}

public class FileStateTests
{
    [Fact]
    public void Files_report_size_pages_and_problems_without_throwing()
    {
        using var dir = new TempFolder();
        var good = PdfFileState.Read(dir.MakePdf("a.pdf", 3));
        Assert.True(good.IsUsable);
        Assert.Equal(3, good.PageCount);
        Assert.True(good.Size > 0);
        Assert.Equal("a.pdf", good.Name);

        var locked = PdfFileState.Read(dir.MakeLocked("b.pdf"));
        Assert.True(locked.IsLocked);
        Assert.False(locked.IsUnreadable);
        Assert.Equal("“b.pdf” is password-protected.", locked.Problem);

        var damaged = PdfFileState.Read(dir.MakeDamaged("c.pdf"));
        Assert.True(damaged.IsUnreadable);
        Assert.Equal("“c.pdf” couldn’t be opened as a PDF.", damaged.Problem);

        var missing = PdfFileState.Read(dir.File("nothing.pdf"));
        Assert.True(missing.IsUnreadable);
    }

    [Fact]
    public void Text_helpers_match_the_Mac_wording()
    {
        Assert.Equal("1 page", PdfText.Pages(1));
        Assert.Equal("12 pages", PdfText.Pages(12));
        Assert.Equal("Save PDFs", PdfText.Counted("Save", 0));
        Assert.Equal("Split 1 PDF", PdfText.Counted("Split", 1));
        Assert.Equal("Split 4 PDFs", PdfText.Counted("Split", 4));
        Assert.Equal("4.2 MB", PdfText.Bytes(4_200_000));
        Assert.Equal("512 bytes", PdfText.Bytes(512));
    }
}
