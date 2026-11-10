using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors.Tests;

public class SplitModelTests
{
    private static SplitModel Model(params string[] paths) => new(PdfFileState.ReadAll(paths));

    [Fact]
    public void One_pdf_starts_on_every_page_and_splits_into_one_file_per_page()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 4));
        Assert.Equal("Split PDF", model.WindowTitle);
        Assert.Equal("Split", model.PrimaryTitle);
        Assert.True(model.CanApply);
        Assert.Equal(new SplitSummary("Creates 4 PDFs", Tone.Normal), model.Summary());
        Assert.Equal(["Report Page 1.pdf", "Report Page 2.pdf", "Report Page 3.pdf", "Report Page 4.pdf"], model.Preview().Select(p => p.FileName));
        Assert.Equal(["Page 1", "Page 2", "Page 3", "Page 4"], model.Preview().Select(p => p.Pages));
    }

    [Fact]
    public void Every_N_pages_previews_the_runs_with_a_shorter_last_one()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 7));
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 3 });
        Assert.Equal("Creates 3 PDFs", model.Summary().Text);
        Assert.Equal(["Report Pages 1-3.pdf", "Report Pages 4-6.pdf", "Report Page 7.pdf"], model.Preview().Select(p => p.FileName));
    }

    [Fact]
    public void A_chunk_as_big_as_the_pdf_is_not_a_split()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 3));
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 3 });
        Assert.Equal(new SplitSummary("Not split: it has 3 pages", Tone.Muted), model.Summary());
        Assert.False(model.CanApply);
        Assert.Empty(model.Preview());
    }

    [Fact]
    public void Ranges_ask_for_input_then_explain_what_is_wrong()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 5));
        model.Update(r => r with { Mode = PdfSplitMode.Ranges });
        Assert.Equal(new SplitSummary("Enter the pages for each new PDF, separated by commas.", Tone.Info), model.Summary());
        Assert.False(model.CanApply);

        model.Update(r => r with { RangesText = "1-3, 9" });
        Assert.Equal(new SplitSummary("“9” is outside pages 1–5.", Tone.Warning), model.Summary());
        model.Update(r => r with { RangesText = "4-2" });
        Assert.Equal("“4-2” runs backwards.", model.Summary().Text);
        model.Update(r => r with { RangesText = "one" });
        Assert.Equal("“one” isn’t a page or range.", model.Summary().Text);
        Assert.False(model.CanApply);

        model.Update(r => r with { RangesText = "1-3, 5" });
        Assert.Equal("Creates 2 PDFs", model.Summary().Text);
        Assert.True(model.CanApply);
        Assert.Equal(["Report Pages 1-3.pdf", "Report Page 5.pdf"], model.Preview().Select(p => p.FileName));
    }

    [Fact]
    public void Preview_names_avoid_files_that_already_exist()
    {
        using var dir = new TempFolder();
        var path = dir.MakePdf("Report.pdf", 2);
        File.WriteAllBytes(dir.File("Report Page 1.pdf"), []);
        Assert.Equal(["Report Page 1 2.pdf", "Report Page 2.pdf"], Model(path).Preview().Select(p => p.FileName));
    }

    [Fact]
    public void A_locked_pdf_says_so_and_cannot_be_split()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeLocked("Secret.pdf"));
        Assert.Equal(new SplitSummary("This PDF is password-protected.", Tone.Warning), model.Summary());
        Assert.False(model.CanApply);
        Assert.Empty(model.Preview());
    }

    [Fact]
    public void A_damaged_pdf_shows_its_own_message()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeDamaged("Broken.pdf"));
        Assert.Equal(new SplitSummary("“Broken.pdf” couldn’t be opened as a PDF.", Tone.Warning), model.Summary());
        Assert.False(model.CanApply);
    }

    // ---------------------------------------------------------------- several PDFs

    [Fact]
    public void Same_for_all_is_on_and_one_rule_serves_every_pdf()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 6), dir.MakePdf("b.pdf", 4), dir.MakePdf("c.pdf", 1));
        Assert.True(model.Edits.SameForAll);
        Assert.Equal("Split PDFs", model.WindowTitle);
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 2 });
        Assert.All(model.Edits.All, r => Assert.Equal(2, r.ChunkSize));
        // c.pdf has one page, so it stays whole and is left out of the count.
        Assert.Equal([true, true, false], model.Edited);
        Assert.Equal("Split 2 PDFs", model.PrimaryTitle);
        Assert.Equal(5, model.TotalFileCount);
        Assert.Equal("5 PDFs in all", model.TotalText);
    }

    [Fact]
    public void Ranges_are_read_against_the_longest_pdf_and_cut_to_each()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("long.pdf", 9), dir.MakePdf("mid.pdf", 6), dir.MakePdf("short.pdf", 3));
        Assert.Equal(9, model.PageLimit);
        model.Update(r => r with { Mode = PdfSplitMode.Ranges, RangesText = "1-3, 4-9" });
        Assert.Equal(["Pages 1-3", "Pages 4-9"], model.Groups(0).Groups.Select(g => PdfSplitter.Label(g)));
        Assert.Equal(["Pages 1-3", "Pages 4-6"], model.Groups(1).Groups.Select(g => PdfSplitter.Label(g)));
        // The short PDF only gets "1-3", which is the whole of it: it is not split.
        Assert.Equal(["Pages 1-3"], model.Groups(2).Groups.Select(g => PdfSplitter.Label(g)));
        Assert.Equal([true, true, false], model.Edited);
        Assert.Equal("Not split: it has 3 pages", Shown(model, 2).Text);
        Assert.Equal("Creates 2 PDFs", Shown(model, 1).Text);
    }

    private static SplitSummary Shown(SplitModel model, int index)
    {
        model.Edits.Index = index;
        return model.Summary();
    }

    [Fact]
    public void A_range_past_the_longest_pdf_is_still_an_error()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 5), dir.MakePdf("b.pdf", 3));
        model.Update(r => r with { Mode = PdfSplitMode.Ranges, RangesText = "1-6" });
        Assert.Equal("“1-6” is outside pages 1–5.", model.Summary().Text);
        Assert.Equal(0, model.EditedCount);
        Assert.False(model.CanApply);
    }

    [Fact]
    public void Turning_Same_for_all_off_lets_each_pdf_keep_its_own_rule()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 6), dir.MakePdf("b.pdf", 6));
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 2 });
        model.Edits.SameForAll = false;
        Assert.Equal(6, model.PageLimit);
        model.Update(r => r with { ChunkSize = 5 });
        Assert.Equal(5, model.Edits[0].ChunkSize);
        Assert.Equal(2, model.Edits[1].ChunkSize);
        Assert.Equal(2, model.Groups(0).Groups.Count);
        Assert.Equal(3, model.Groups(1).Groups.Count);
    }

    [Fact]
    public void Turning_Same_for_all_back_on_copies_the_shown_pdfs_rule_to_the_rest()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 6), dir.MakePdf("b.pdf", 6));
        model.Edits.SameForAll = false;
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 4 });
        model.Edits.SameForAll = true;
        Assert.Equal(PdfSplitMode.Chunks, model.Edits[1].Mode);
        Assert.Equal(4, model.Edits[1].ChunkSize);
    }

    [Fact]
    public void Locked_and_damaged_pdfs_in_a_batch_are_left_out_not_fatal()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 3), dir.MakeLocked("Secret.pdf"), dir.MakeDamaged("Broken.pdf"));
        Assert.Equal([true, false, false], model.Edited);
        Assert.True(model.CanApply);
        Assert.Equal("Split 1 PDF", model.PrimaryTitle);
        Assert.Equal(3, model.TotalFileCount);
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public void Saving_one_pdf_writes_a_file_per_part()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 5));
        model.Update(r => r with { Mode = PdfSplitMode.Chunks, ChunkSize = 2 });
        var outcome = model.Save(null, CancellationToken.None);
        Assert.Equal(["Report Page 5.pdf", "Report Pages 1-2.pdf", "Report Pages 3-4.pdf", "Report.pdf"], dir.Names());
        Assert.Equal(3, outcome.Saved.Count);
        Assert.Equal("Saved 3 files", outcome.Message);
        Assert.Equal(2, TempFolder.PageCount(dir.File("Report Pages 1-2.pdf")));
    }

    [Fact]
    public void Saving_a_batch_splits_what_it_can_and_counts_the_rest()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 4), dir.MakePdf("b.pdf", 1), dir.MakeLocked("Secret.pdf"));
        var outcome = model.Save(null, CancellationToken.None);
        Assert.Equal(4, outcome.Saved.Count);
        Assert.Equal(2, outcome.Unchanged);
        Assert.Equal("4 split · 2 not split", outcome.Message);
    }

    [Fact]
    public void A_split_that_is_cancelled_leaves_no_files()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("Report.pdf", 5));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.Save(null, cancel.Token));
        Assert.Equal(["Report.pdf"], dir.Names());
    }

    [Fact]
    public void Saving_a_locked_single_pdf_fails_with_a_message_a_person_can_read()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakeLocked("Secret.pdf"));
        var error = Assert.Throws<SaveException>(() => model.Save(null, CancellationToken.None));
        Assert.Equal("“Secret.pdf” is password-protected.", error.Message);
    }

    [Fact]
    public void Reset_puts_every_pdf_back_to_every_page()
    {
        using var dir = new TempFolder();
        var model = Model(dir.MakePdf("a.pdf", 4), dir.MakePdf("b.pdf", 4));
        model.Update(r => r with { Mode = PdfSplitMode.Ranges, RangesText = "1-2" });
        model.Edits.Reset();
        Assert.All(model.Edits.All, r => Assert.Equal(new PdfSplitRule(), r));
    }

    [Fact]
    public void The_ranges_hint_follows_the_page_limit()
    {
        using var dir = new TempFolder();
        Assert.Equal("e.g. 1-3, 4, 5-5", Model(dir.MakePdf("a.pdf", 2)).RangesPlaceholder);
        Assert.Equal("e.g. 1-3, 4, 5-12", Model(dir.MakePdf("b.pdf", 12)).RangesPlaceholder);
    }
}
