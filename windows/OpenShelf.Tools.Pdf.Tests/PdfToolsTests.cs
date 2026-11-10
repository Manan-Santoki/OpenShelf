using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace OpenShelf.Tools.Pdf.Tests;

public class RangeTests
{
    [Theory]
    [InlineData("1-3, 5, 8-", 9, "0,1,2|4|7,8")]
    [InlineData("2", 5, "1")]
    [InlineData("-2, 4-", 5, "0,1|3,4")]
    [InlineData(" 3 - 4 ,1", 5, "2,3|0")]
    public void Valid_ranges_parse_to_zero_based_groups(string text, int pages, string expected)
    {
        var result = PdfSplitter.ParseRanges(text, pages);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal(expected, string.Join("|", result.Groups.Select(g => string.Join(",", g))));
    }

    [Theory]
    [InlineData("", "Enter page ranges")]
    [InlineData("abc", "isn’t a page or range")]
    [InlineData("1-2-3", "isn’t a page or range")]
    [InlineData("0-2", "outside pages 1–5")]
    [InlineData("4-9", "outside pages 1–5")]
    [InlineData("4-2", "runs backwards")]
    [InlineData("1,,x", "“x” isn’t")]
    public void Invalid_ranges_say_what_is_wrong(string text, string message)
    {
        var result = PdfSplitter.ParseRanges(text, 5);
        Assert.False(result.IsValid);
        Assert.Contains(message, result.Error);
    }

    [Fact]
    public void Every_page_and_chunks()
    {
        Assert.Equal(3, PdfSplitter.EveryPage(3).Count);
        var chunks = PdfSplitter.Chunks(5, 2);
        Assert.Equal(new[] { "0,1", "2,3", "4" }, chunks.Select(g => string.Join(",", g)));
        Assert.Empty(PdfSplitter.Chunks(5, 0));
        Assert.Empty(PdfSplitter.Chunks(0, 2));
    }

    [Fact]
    public void Ranges_against_the_longest_are_cut_to_each_pdf()
    {
        var rule = new PdfSplitRule { Mode = PdfSplitMode.Ranges, RangesText = "1-2, 4-9" };
        var six = rule.Groups(6, rangeLimit: 9);
        Assert.True(six.IsValid);
        Assert.Equal(new[] { "0,1", "3,4,5" }, six.Groups.Select(g => string.Join(",", g)));
        var three = rule.Groups(3, rangeLimit: 9);
        Assert.Equal(new[] { "0,1" }, three.Groups.Select(g => string.Join(",", g)));
        // On its own, 9 is out of range for a 6-page PDF.
        Assert.False(rule.Groups(6).IsValid);
    }
}

public class SplitTests
{
    [Fact]
    public async Task Every_page_makes_one_file_per_page_with_the_right_text()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Book.pdf", "alpha", "bravo", "charlie");
        var files = await PdfSplitter.SplitAsync([new PdfSplitJob(source, new PdfSplitRule())]);

        Assert.Equal(3, files.Saved.Count);
        Assert.Equal(new[] { "Book Page 1.pdf", "Book Page 2.pdf", "Book Page 3.pdf" }, files.Saved.Select(Path.GetFileName));
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, files.Saved.Select(f => Assert.Single(TempFolder.TextOf(f))));
        Assert.True(File.Exists(source));
        Assert.DoesNotContain(dir.Files(), f => f.StartsWith(".os-"));
    }

    [Fact]
    public async Task Ranges_make_the_named_parts()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Book.pdf", "p1", "p2", "p3", "p4", "p5", "p6");
        var rule = new PdfSplitRule { Mode = PdfSplitMode.Ranges, RangesText = "1-3, 5, 6-" };
        var outcome = await PdfSplitter.SplitAsync([new PdfSplitJob(source, rule)]);

        Assert.Equal(new[] { "Book Pages 1-3.pdf", "Book Page 5.pdf", "Book Pages 6-6.pdf".Replace("Pages 6-6", "Page 6") }, outcome.Saved.Select(Path.GetFileName));
        Assert.Equal(new[] { "p1", "p2", "p3" }, TempFolder.TextOf(outcome.Saved[0]));
        Assert.Equal(new[] { "p5" }, TempFolder.TextOf(outcome.Saved[1]));
        Assert.Equal(new[] { "p6" }, TempFolder.TextOf(outcome.Saved[2]).Select(t => t));
    }

    [Fact]
    public async Task Chunks_of_two_leave_a_shorter_last_part()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b", "c", "d", "e");
        var parts = await PdfSplitter.SplitAsync(source, PdfSplitter.Chunks(5, 2));
        Assert.Equal(new[] { 2, 2, 1 }, parts.Select(TempFolder.PageCount));
    }

    [Fact]
    public async Task Preview_lists_the_parts_and_writes_nothing()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b", "c", "d");
        var before = dir.Files().ToList();
        var plans = await PdfSplitter.PlanAsync([new PdfSplitJob(source, new PdfSplitRule { Mode = PdfSplitMode.Chunks, ChunkSize = 3 })]);
        var plan = Assert.Single(plans);
        Assert.Equal(4, plan.PageCount);
        Assert.True(plan.WillSplit);
        Assert.Equal(new[] { "Doc Pages 1-3.pdf", "Doc Page 4.pdf" }, plan.Parts.Select(p => p.FileName));
        Assert.Equal(before, dir.Files().ToList());
    }

    [Fact]
    public async Task Several_pdfs_read_ranges_against_the_longest_and_cut_to_each()
    {
        using var dir = new TempFolder();
        var six = dir.MakePdf("Six.pdf", "s1", "s2", "s3", "s4", "s5", "s6");
        var three = dir.MakePdf("Three.pdf", "t1", "t2", "t3");
        var nine = dir.MakePdf("Nine.pdf", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9");
        var rule = new PdfSplitRule { Mode = PdfSplitMode.Ranges, RangesText = "4-9" };
        var jobs = new[] { new PdfSplitJob(six, rule), new PdfSplitJob(three, rule), new PdfSplitJob(nine, rule) };

        var plans = await PdfSplitter.PlanAsync(jobs);
        Assert.True(plans[0].WillSplit);
        Assert.False(plans[1].WillSplit);
        Assert.Empty(plans[1].Parts);

        var outcome = await PdfSplitter.SplitAsync(jobs);
        Assert.Equal(2, outcome.Saved.Count);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("2 split · 1 not split", outcome.Title);
        Assert.Equal(new[] { "s4", "s5", "s6" }, TempFolder.TextOf(outcome.Saved[0]));
        Assert.Equal(new[] { "n4", "n5", "n6", "n7", "n8", "n9" }, TempFolder.TextOf(outcome.Saved[1]));
    }

    [Fact]
    public async Task A_rule_that_keeps_the_whole_pdf_changes_nothing()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("One.pdf", "x", "y");
        var rule = new PdfSplitRule { Mode = PdfSplitMode.Ranges, RangesText = "1-2" };
        var outcome = await PdfSplitter.SplitAsync([new PdfSplitJob(source, rule), new PdfSplitJob(dir.MakePdf("Two.pdf", "z"), rule)]);
        Assert.Empty(outcome.Saved);
        Assert.Equal("Nothing saved", outcome.Title);
    }

    [Fact]
    public async Task Splitting_twice_never_overwrites()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b");
        var first = await PdfSplitter.SplitAsync(source, PdfSplitter.EveryPage(2));
        var second = await PdfSplitter.SplitAsync(source, PdfSplitter.EveryPage(2));
        Assert.Empty(first.Intersect(second));
        Assert.Equal(4, first.Concat(second).Distinct().Count());
        Assert.Equal(new[] { "a" }, TempFolder.TextOf(first[0]));
    }

    [Fact]
    public async Task Bad_ranges_for_a_single_pdf_throw_a_clear_message()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b");
        var rule = new PdfSplitRule { Mode = PdfSplitMode.Ranges, RangesText = "5" };
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfSplitter.SplitAsync([new PdfSplitJob(source, rule)]));
        Assert.Contains("outside pages 1–2", error.Message);
        Assert.Single(dir.Files());
    }

    [Fact]
    public async Task A_damaged_pdf_fails_clearly_and_leaves_nothing()
    {
        using var dir = new TempFolder();
        var path = dir.File("Broken.pdf");
        File.WriteAllText(path, "%PDF-1.4\nthis is not really a pdf");
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfSplitter.SplitAsync(path, [[0]]));
        Assert.Equal(PdfToolErrorKind.Unreadable, error.Kind);
        Assert.Contains("Broken.pdf", error.Message);
        Assert.Equal(new[] { "Broken.pdf" }, dir.Files());
    }

    [Fact]
    public async Task Cancelling_leaves_no_files()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b", "c");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSplitter.SplitAsync(source, PdfSplitter.EveryPage(3), null, cts.Token));
        Assert.Equal(new[] { "Doc.pdf" }, dir.Files());
    }

    [Fact]
    public async Task Progress_reaches_one()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a", "b", "c");
        var values = new List<double>();
        await PdfSplitter.SplitAsync([new PdfSplitJob(source, new PdfSplitRule())], new SyncProgress(values.Add));
        Assert.Equal(1.0, values[^1]);
        Assert.All(values, v => Assert.InRange(v, 0, 1));
    }
}

internal sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

public class CombineTests
{
    [Fact]
    public async Task Pdfs_and_images_become_one_pdf_in_order()
    {
        using var dir = new TempFolder();
        var a = dir.MakePdf("A.pdf", "first", "second");
        var png = dir.MakePng("pic.png", 200, 100);
        var jpg = dir.MakeJpeg("photo.jpg", 144, 288);
        var b = dir.MakePdf("B.pdf", "last");

        var combined = await PdfCombiner.CombineAsync([a, png, jpg, b]);

        Assert.Equal(dir.File("Combined.pdf"), combined);
        Assert.Equal(5, TempFolder.PageCount(combined));
        using var document = Pig.Open(combined);
        var pages = document.GetPages().ToList();
        Assert.Equal("first", pages[0].Text);
        Assert.Equal("second", pages[1].Text);
        Assert.Equal("last", pages[4].Text);
        // Images with no stated resolution are drawn at 72 dpi: one pixel is one point.
        Assert.Equal(200, pages[2].Width, 0.5);
        Assert.Equal(100, pages[2].Height, 0.5);
        Assert.Equal(144, pages[3].Width, 0.5);
        Assert.Equal(288, pages[3].Height, 0.5);
        Assert.Single(pages[2].GetImages());
        Assert.Single(pages[3].GetImages());
        Assert.DoesNotContain(dir.Files(), f => f.StartsWith(".os-"));
    }

    [Fact]
    public async Task Image_pages_render_the_picture()
    {
        using var dir = new TempFolder();
        var png = dir.MakePng("pic.png", 120, 80, transparent: true);
        var combined = await PdfCombiner.CombineAsync([png]);
        var document = await PdfPagesDocument.OpenAsync(combined);
        var thumbnail = await document.RenderThumbnailAsync(0, 120);
        Assert.Equal(120, thumbnail.Width);
        Assert.Equal(80, thumbnail.Height);
        // left half transparent (white page shows), right half red
        int Pixel(int x, int y, int channel) => thumbnail.Bgra[(y * thumbnail.Width + x) * 4 + channel];
        Assert.True(Pixel(10, 40, 1) > 240 && Pixel(10, 40, 2) > 240);
        Assert.True(Pixel(100, 40, 2) > 150 && Pixel(100, 40, 1) < 80);
    }

    [Fact]
    public async Task Combining_again_gets_a_new_name()
    {
        using var dir = new TempFolder();
        var a = dir.MakePdf("A.pdf", "x");
        var first = await PdfCombiner.CombineAsync([a]);
        var second = await PdfCombiner.CombineAsync([a]);
        Assert.Equal("Combined.pdf", Path.GetFileName(first));
        Assert.Equal("Combined 2.pdf", Path.GetFileName(second));
    }

    [Fact]
    public async Task A_bad_file_fails_the_whole_combine_with_no_output()
    {
        using var dir = new TempFolder();
        var good = dir.MakePdf("Good.pdf", "x");
        var bad = dir.File("Bad.png");
        File.WriteAllText(bad, "not an image");
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfCombiner.CombineAsync([good, bad]));
        Assert.Contains("Bad.png", error.Message);
        Assert.Equal(new[] { "Bad.png", "Good.pdf" }, dir.Files().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_password_protected_pdf_is_reported_as_locked()
    {
        using var dir = new TempFolder();
        var plain = dir.MakePdf("Plain.pdf", "secret");
        var locked = dir.File("Locked.pdf");
        using (var document = PdfReader.Open(plain, PdfDocumentOpenMode.Modify))
        {
            document.SecuritySettings.UserPassword = "pw";
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(locked);
        }
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfCombiner.CombineAsync([plain, locked]));
        Assert.Equal(PdfToolErrorKind.Locked, error.Kind);
        Assert.Equal("“Locked.pdf” is password-protected.", error.Message);
        Assert.DoesNotContain("Combined.pdf", dir.Files());

        var info = await PdfMetadata.ReadAsync(locked);
        Assert.True(info.IsLocked);
        await Assert.ThrowsAsync<PdfToolException>(() => PdfPagesDocument.OpenAsync(locked));
    }

    [Fact]
    public async Task Nothing_to_combine_is_an_error()
    {
        await Assert.ThrowsAsync<PdfToolException>(() => PdfCombiner.CombineAsync([]));
    }

    [Fact]
    public async Task Progress_is_reported_per_file()
    {
        using var dir = new TempFolder();
        var values = new List<double>();
        await PdfCombiner.CombineAsync([dir.MakePdf("A.pdf", "a"), dir.MakePdf("B.pdf", "b")], new SyncProgress(values.Add));
        Assert.Equal(3, values.Count);
        Assert.Equal(1 / 3.0, values[0], 3);
        Assert.Equal(2 / 3.0, values[1], 3);
        Assert.Equal(1.0, values[2], 3);
    }
}

public class PagesTests
{
    [Fact]
    public async Task Reorder_rotate_delete_and_save()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "one", "two", "three", "four");
        var document = await PdfPagesDocument.OpenAsync(source);
        Assert.False(document.IsChanged);

        document.Move(3, 0);          // four one two three
        document.Rotate(1, 90);       // "one" turned
        document.Delete(2);           // drops "two"
        Assert.True(document.IsChanged);
        Assert.Equal(new[] { 3, 0, 2 }, document.Pages.Select(p => p.SourceIndex));

        var saved = await document.SaveAsync();
        Assert.Equal("Doc Edited.pdf", Path.GetFileName(saved));
        Assert.Equal(new[] { "four", "one", "three" }, TempFolder.TextOf(saved).Select(t => t.Replace(" ", "")));
        using var read = Pig.Open(saved);
        Assert.Equal(new[] { 0, 90, 0 }, read.GetPages().Select(p => p.Rotation.Value));
        // Original untouched.
        Assert.Equal(4, TempFolder.PageCount(source));
    }

    [Fact]
    public async Task Rotation_adds_to_the_pages_own_rotation()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdf("Doc.pdf", "a");
        using (var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[0].Rotate = 90;
            doc.Save(source);
        }
        var model = await PdfPagesDocument.OpenAsync(source);
        model.Rotate(0, 270);
        model.Rotate(0, 90);
        model.Rotate(0, -180); // net 180 extra
        var saved = await model.SaveAsync();
        using var read = Pig.Open(saved);
        Assert.Equal(270, read.GetPage(1).Rotation.Value);
    }

    [Fact]
    public async Task Undo_and_redo_walk_back_through_edits()
    {
        using var dir = new TempFolder();
        var model = await PdfPagesDocument.OpenAsync(dir.MakePdf("Doc.pdf", "a", "b", "c"));
        var changes = 0;
        model.Changed += (_, _) => changes++;
        model.Delete(0);
        model.RotateAll(90);
        Assert.Equal(2, model.Pages.Count);
        model.Undo();
        Assert.All(model.Pages, p => Assert.Equal(0, p.Rotation));
        model.Undo();
        Assert.Equal(3, model.Pages.Count);
        Assert.False(model.IsChanged);
        model.Redo();
        Assert.Equal(2, model.Pages.Count);
        Assert.True(model.CanRedo);
        model.Move(0, 0);              // no-op does nothing
        Assert.Equal(5, changes);      // delete, rotate, undo, undo, redo
    }

    [Fact]
    public async Task Deleting_every_page_cannot_be_saved()
    {
        using var dir = new TempFolder();
        var model = await PdfPagesDocument.OpenAsync(dir.MakePdf("Doc.pdf", "a", "b"));
        model.Delete([0, 1]);
        var error = await Assert.ThrowsAsync<PdfToolException>(() => model.SaveAsync());
        Assert.Equal(PdfToolErrorKind.NothingToSave, error.Kind);
        Assert.Single(dir.Files());
    }

    [Fact]
    public async Task Thumbnails_are_sized_turned_and_available_as_png_and_pixels()
    {
        using var dir = new TempFolder();
        var model = await PdfPagesDocument.OpenAsync(dir.MakePdf("Doc.pdf", "a", "b"));
        var upright = await model.RenderThumbnailAsync(0, 200);
        Assert.Equal(155, upright.Width);   // letter: 612 x 792 scaled to a 200 px long edge
        Assert.Equal(200, upright.Height);
        Assert.Equal(upright.Width * upright.Height * 4, upright.Bgra.Length);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, upright.Png.Take(4).ToArray());
        Assert.Contains(Enumerable.Range(0, upright.Width * upright.Height).Select(i => upright.Bgra[i * 4]), b => b < 128); // some ink

        model.Rotate(0, 90);
        var turned = await model.RenderThumbnailAsync(0, 200);
        Assert.Equal(200, turned.Width);
        Assert.Equal(155, turned.Height);

        var first = await PdfThumbnails.FirstPageAsync(dir.File("Doc.pdf"), 100);
        Assert.Equal(100, first.Height);
        var broken = dir.File("Broken.pdf");
        File.WriteAllText(broken, "nope");
        var placeholder = await PdfThumbnails.FirstPageAsync(broken, 100);
        Assert.Equal(100, placeholder.Height);
        Assert.Equal(75, placeholder.Width);
    }

    [Fact]
    public async Task MoveMany_keeps_the_block_in_order()
    {
        using var dir = new TempFolder();
        var model = await PdfPagesDocument.OpenAsync(dir.MakePdf("Doc.pdf", "a", "b", "c", "d", "e"));
        model.MoveMany([1, 3], 0);
        Assert.Equal(new[] { 1, 3, 0, 2, 4 }, model.Pages.Select(p => p.SourceIndex));
    }

    [Fact]
    public async Task Batch_save_counts_unchanged_documents()
    {
        using var dir = new TempFolder();
        var one = await PdfPagesDocument.OpenAsync(dir.MakePdf("One.pdf", "a", "b"));
        var two = await PdfPagesDocument.OpenAsync(dir.MakePdf("Two.pdf", "a", "b"));
        one.Delete(0);
        var outcome = await PdfPagesEditor.SaveAsync([one, two, null]);
        Assert.Single(outcome.Saved);
        Assert.Equal(2, outcome.Unchanged);
        Assert.Equal("1 edited · 2 unchanged", outcome.Title);
    }
}

public class MetadataTests
{
    [Fact]
    public async Task Edit_writes_a_copy_that_reads_back()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdfWithTitle("Report.pdf", "Old title", "body");
        var fields = new PdfMetadataFields
        {
            Title = "  New title ", Author = "Ada", Subject = "Testing", Keywords = "a,  b ,, c", Creator = "Me", Producer = "Acme PDF",
        };
        var saved = await PdfMetadata.SaveAsync(source, fields);

        Assert.Equal("Report Metadata.pdf", Path.GetFileName(saved));
        var info = await PdfMetadata.ReadAsync(saved);
        Assert.Equal("New title", info.Fields.Title);
        Assert.Equal("Ada", info.Fields.Author);
        Assert.Equal("Testing", info.Fields.Subject);
        Assert.Equal("a, b, c", info.Fields.Keywords);
        Assert.Equal("Me", info.Fields.Creator);
        Assert.Equal("Acme PDF", info.Fields.Producer);
        Assert.Equal(1, info.PageCount);
        Assert.True(info.FileSize > 0);
        Assert.True(info.HasMetadata);
        Assert.False(info.IsLocked);
        Assert.Equal(new[] { "body" }, TempFolder.TextOf(saved));
        using var pig = Pig.Open(saved);
        Assert.Equal("New title", pig.Information.Title);
        Assert.Equal("Ada", pig.Information.Author);
        Assert.Equal((await PdfMetadata.ReadAsync(source)).Fields.Title, "Old title");
    }

    [Fact]
    public async Task Remove_all_makes_a_clean_copy()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdfWithTitle("Report.pdf", "T", "body");
        var tagged = await PdfMetadata.SaveAsync(source, new PdfMetadataFields { Title = "Secret", Author = "Bob", Keywords = "k", Creator = "C", Producer = "P" });
        var clean = await PdfMetadata.RemoveAllAsync(tagged);

        Assert.Equal("Report Metadata Clean.pdf", Path.GetFileName(clean));
        var info = await PdfMetadata.ReadAsync(clean);
        Assert.Equal("", info.Fields.Title);
        Assert.Equal("", info.Fields.Author);
        Assert.Equal("", info.Fields.Keywords);
        Assert.Equal("", info.Fields.Creator);
        Assert.Equal("", info.Fields.Producer);
        Assert.DoesNotContain("pdfsharp.com", File.ReadAllText(clean, System.Text.Encoding.Latin1));
        Assert.DoesNotContain("<pdf:Producer>PDFsharp", File.ReadAllText(clean, System.Text.Encoding.Latin1));
        Assert.False(info.HasMetadata);
        Assert.DoesNotContain("Secret", File.ReadAllText(clean, System.Text.Encoding.Latin1));
        Assert.DoesNotContain("Bob", File.ReadAllText(clean, System.Text.Encoding.Latin1));
        Assert.Equal(new[] { "body" }, TempFolder.TextOf(clean));
    }

    [Fact]
    public async Task Saving_with_every_field_empty_is_a_clean_copy()
    {
        using var dir = new TempFolder();
        var source = dir.MakePdfWithTitle("Report.pdf", "Title here", "body");
        var saved = await PdfMetadata.SaveAsync(source, new PdfMetadataFields());
        Assert.Equal("Report Clean.pdf", Path.GetFileName(saved));
        Assert.Equal("", (await PdfMetadata.ReadAsync(saved)).Fields.Title);
    }

    [Fact]
    public async Task Batch_removal_skips_pdfs_that_are_already_clean()
    {
        using var dir = new TempFolder();
        var tagged = dir.MakePdfWithTitle("Tagged.pdf", "Has a title", "x");
        var clean = await PdfMetadata.RemoveAllAsync(dir.MakePdfWithTitle("Other.pdf", "Another", "y"));
        var outcome = await PdfMetadata.RemoveAllAsync([tagged, clean]);
        Assert.Single(outcome.Saved);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("1 cleaned · 1 already clean", outcome.Title);
    }

    [Fact]
    public async Task A_damaged_pdf_fails_clearly()
    {
        using var dir = new TempFolder();
        var path = dir.File("Broken.pdf");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfMetadata.RemoveAllAsync(path));
        Assert.Equal("“Broken.pdf” couldn’t be opened as a PDF.", error.Message);
        Assert.Equal(new[] { "Broken.pdf" }, dir.Files());
    }
}

public class CompressTests
{
    private static async Task<string> BigImagePdf(TempFolder dir)
    {
        var png = dir.MakePng("big.png", 1600, 1200, noise: true);
        return await PdfCombiner.CombineAsync([png]);
    }

    [Theory]
    [InlineData(PdfCompressionLevel.Light, 2600)]
    [InlineData(PdfCompressionLevel.Balanced, 2400)]
    [InlineData(PdfCompressionLevel.Strong, 1600)]
    public async Task Images_are_recompressed_and_capped_by_level(PdfCompressionLevel level, int expectedWidth)
    {
        using var dir = new TempFolder();
        var source = await PdfCombiner.CombineAsync([dir.MakePng("big.png", 2600, 1800, noise: true)]);
        var result = await PdfCompressor.CompressAsync(source, level);

        Assert.True(result.Saved, result.Message);
        Assert.Equal(source, result.Path);
        Assert.Equal("Combined Compressed.pdf", Path.GetFileName(result.OutputPath));
        Assert.Equal(new FileInfo(source).Length, result.OriginalSize);
        Assert.Equal(new FileInfo(result.OutputPath!).Length, result.CompressedSize);
        Assert.True(result.CompressedSize < result.OriginalSize / 2, $"{result.CompressedSize} vs {result.OriginalSize}");
        Assert.Contains("smaller", result.Message);

        Assert.Equal(1, TempFolder.PageCount(result.OutputPath!));
        using var pig = Pig.Open(result.OutputPath!);
        var page = pig.GetPage(1);
        Assert.Equal(2600, page.Width, 1);   // the page itself is untouched
        var image = Assert.Single(page.GetImages());
        Assert.Equal(expectedWidth, image.WidthInSamples);
    }

    [Fact]
    public async Task Images_are_scaled_to_the_resolution_they_are_shown_at()
    {
        using var dir = new TempFolder();
        // 2400 x 1800 px stated as 300 dpi: shown at 8 x 6 in. Balanced (144 dpi) needs 1152 x 864.
        var png = TempFolder.WithDpi(dir.MakePng("scan.png", 2400, 1800, noise: true), 300);
        var source = await PdfCombiner.CombineAsync([png]);
        using (var pig = Pig.Open(source)) Assert.Equal(576, pig.GetPage(1).Width, 1);

        var result = await PdfCompressor.CompressAsync(source, PdfCompressionLevel.Balanced);
        Assert.True(result.Saved);
        using var after = Pig.Open(result.OutputPath!);
        Assert.Equal(576, after.GetPage(1).Width, 1);
        var image = Assert.Single(after.GetPage(1).GetImages());
        Assert.Equal(1152, image.WidthInSamples);
        Assert.Equal(864, image.HeightInSamples);
    }

    [Fact]
    public async Task Stronger_levels_make_smaller_files()
    {
        using var dir = new TempFolder();
        var source = await BigImagePdf(dir);
        var light = await PdfCompressor.EstimateAsync(source, PdfCompressionLevel.Light);
        var strong = await PdfCompressor.EstimateAsync(source, PdfCompressionLevel.Strong);
        Assert.True(strong.CompressedSize < light.CompressedSize);
        Assert.False(light.Saved);
        Assert.Null(light.OutputPath);
        Assert.Equal(new[] { "Combined.pdf", "big.png" }, dir.Files().Order(StringComparer.Ordinal)); // estimating writes nothing
    }

    /// <summary>Compresses again and again until a pass can no longer make it smaller.</summary>
    private static async Task<(string Stable, PdfCompressResult Last)> Stabilise(string path)
    {
        var current = path;
        for (var i = 0; i < 12; i++)
        {
            var result = await PdfCompressor.CompressAsync(current, PdfCompressionLevel.Strong);
            if (!result.Saved) return (current, result);
            current = result.OutputPath!;
        }
        throw new Xunit.Sdk.XunitException("compression never settled");
    }

    [Fact]
    public async Task Already_small_pdfs_report_it_and_save_nothing()
    {
        using var dir = new TempFolder();
        var (stable, last) = await Stabilise(await BigImagePdf(dir));
        var files = dir.Files().Count();

        Assert.False(last.Saved);
        Assert.Null(last.OutputPath);
        Assert.False(last.WouldBeSmaller);
        Assert.True(last.CompressedSize >= last.OriginalSize);
        Assert.Equal("Already as small as it gets — nothing saved.", last.Message);
        Assert.Equal(files, dir.Files().Count());
        Assert.Equal(1, TempFolder.PageCount(stable));
    }

    [Fact]
    public async Task Batch_counts_the_ones_that_cannot_shrink()
    {
        using var dir = new TempFolder();
        var big = await BigImagePdf(dir);
        var (stable, _) = await Stabilise(await PdfCombiner.CombineAsync([dir.MakePng("other.png", 900, 600, noise: true)]));

        var batch = await PdfCompressor.CompressAsync([big, stable], PdfCompressionLevel.Strong);
        Assert.Equal(2, batch.Results.Count);
        Assert.True(batch.Results[0].Saved);
        Assert.False(batch.Results[1].Saved);
        Assert.Equal("1 compressed · 1 can’t get smaller", batch.Outcome.Title);
        Assert.Null(batch.NothingSavedMessage);

        var none = await PdfCompressor.CompressAsync([stable, stable], PdfCompressionLevel.Strong);
        Assert.False(none.Outcome.SavedAnything);
        Assert.Equal("Nothing saved", none.Outcome.Title);
        Assert.Equal("These 2 PDFs are already as small as they get — nothing saved.", none.NothingSavedMessage);
        var single = await PdfCompressor.CompressAsync([stable], PdfCompressionLevel.Strong);
        Assert.Equal("Already as small as it gets — nothing saved.", single.NothingSavedMessage);
    }

    [Fact]
    public async Task Locked_and_damaged_pdfs_fail_with_no_output()
    {
        using var dir = new TempFolder();
        var broken = dir.File("Broken.pdf");
        File.WriteAllText(broken, "garbage");
        var error = await Assert.ThrowsAsync<PdfToolException>(() => PdfCompressor.CompressAsync(broken, PdfCompressionLevel.Balanced));
        Assert.Equal(PdfToolErrorKind.Unreadable, error.Kind);
        Assert.Equal(new[] { "Broken.pdf" }, dir.Files());
    }

    [Fact]
    public async Task Text_survives_compression()
    {
        using var dir = new TempFolder();
        var withText = dir.MakePdf("Words.pdf", "keep me", "and me");
        var result = await PdfCompressor.CompressAsync(withText, PdfCompressionLevel.Strong);
        if (result.Saved) Assert.Equal(new[] { "keep me", "and me" }, TempFolder.TextOf(result.OutputPath!));
        Assert.Equal(result.WouldBeSmaller, result.Saved);
    }

    [Fact]
    public async Task Cancelling_midway_leaves_nothing_behind()
    {
        using var dir = new TempFolder();
        var source = await BigImagePdf(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfCompressor.CompressAsync(source, PdfCompressionLevel.Strong, null, cts.Token));
        Assert.Equal(new[] { "Combined.pdf", "big.png" }, dir.Files().Order(StringComparer.Ordinal));
    }
}
