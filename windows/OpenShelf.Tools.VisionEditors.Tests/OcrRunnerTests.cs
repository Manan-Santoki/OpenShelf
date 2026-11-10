using OpenShelf.Core;
using OpenShelf.Convert;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors.Tests;

public class OcrRunnerTests
{

    private static OcrRunner.Recognise Succeeding(List<string>? seen = null) => (path, output, progress, cancel) =>
    {
        seen?.Add(Path.GetFileName(path));
        progress.Report(0.5);
        return Task.FromResult(OutputNaming.Destination(path, "OCR", output == OcrOutput.Text ? "txt" : "pdf"));
    };

    [Fact]
    public void The_title_is_the_file_name_or_the_count()
    {
        Assert.Equal("Scan.pdf", OcrRunner.Title([@"C:\x\Scan.pdf"]));
        Assert.Equal("3 files", OcrRunner.Title(["a.png", "b.png", "c.pdf"]));
    }

    [Fact]
    public void The_detail_line_only_appears_for_several_files()
    {
        Assert.Null(OcrRunner.Detail(0, 1));
        Assert.Equal("Reading 2 of 3…", OcrRunner.Detail(1, 3));
    }

    [Fact]
    public async Task Reads_each_file_in_order_and_returns_what_was_saved()
    {
        var seen = new List<string>();
        var result = await new OcrRunner(Succeeding(seen)).RunAsync(["a.png", "b.pdf", "c.png"], OcrOutput.SearchablePdf);
        Assert.Equal(["a.png", "b.pdf", "c.png"], seen);
        Assert.Equal(3, result.Saved.Count);
        Assert.Empty(result.Failed);
        Assert.EndsWith("a OCR.pdf", result.Saved[0]);
    }

    [Fact]
    public async Task Text_output_asks_for_text_files()
    {
        var result = await new OcrRunner(Succeeding()).RunAsync(["a.png"], OcrOutput.Text);
        Assert.EndsWith("a OCR.txt", result.Saved[0]);
    }

    [Fact]
    public async Task Progress_runs_from_zero_to_one_across_the_batch_with_the_file_in_the_detail()
    {
        var reports = new List<OcrProgress>();
        await new OcrRunner(Succeeding()).RunAsync(["a.png", "b.png"], OcrOutput.Text, reports.Add);
        Assert.Equal(0, reports[0].Fraction);
        Assert.Equal("Reading 1 of 2…", reports[0].Detail);
        Assert.Contains(reports, r => r.Detail == "Reading 2 of 2…" && r.Fraction >= 0.5);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).Order()));
    }

    [Fact]
    public async Task A_file_that_fails_does_not_stop_the_others()
    {
        OcrRunner.Recognise recognise = (path, output, progress, cancel) =>
            Path.GetFileName(path) == "b.png" ? throw ConversionException.Failed("No text was found.") : Succeeding()(path, output, progress, cancel);
        var result = await new OcrRunner(recognise).RunAsync(["a.png", "b.png", "c.png"], OcrOutput.Text);
        Assert.Equal(2, result.Saved.Count);
        Assert.Equal([("b.png", "No text was found.")], result.Failed);
    }

    [Fact]
    public async Task Having_no_ocr_language_stops_the_whole_run()
    {
        var calls = 0;
        OcrRunner.Recognise recognise = (_, _, _, _) => { calls++; throw new OcrNoLanguageException(); };
        var error = await Assert.ThrowsAsync<OcrNoLanguageException>(() => new OcrRunner(recognise).RunAsync(["a.png", "b.png"], OcrOutput.Text));
        Assert.Equal(1, calls);
        Assert.Contains("Settings > Time & language > Language & region", error.Message);
    }

    [Fact]
    public async Task Cancelling_stops_between_files_and_keeps_what_was_saved()
    {
        using var source = new CancellationTokenSource();
        var seen = new List<string>();
        OcrRunner.Recognise recognise = async (path, output, progress, cancel) =>
        {
            var saved = await Succeeding(seen)(path, output, progress, cancel);
            if (seen.Count == 2) source.Cancel();
            return saved;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OcrRunner(recognise).RunAsync(["a.png", "b.png", "c.png", "d.png"], OcrOutput.Text, null, source.Token));
        Assert.Equal(["a.png", "b.png"], seen);
    }

    [Fact]
    public async Task Cancelling_before_starting_reads_nothing()
    {
        var seen = new List<string>();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new OcrRunner(Succeeding(seen)).RunAsync(["a.png"], OcrOutput.Text, null, source.Token));
        Assert.Empty(seen);
    }

    [Fact]
    public void One_saved_file_closes_with_its_name()
    {
        var outcome = OcrRunner.ToOutcome(new OcrRunResult([@"C:\x\Scan OCR.pdf"], []), OcrOutput.SearchablePdf);
        Assert.Equal("Saved · Scan OCR.pdf", outcome.Message);
    }

    [Fact]
    public void Several_saved_files_close_with_the_count_and_the_format()
    {
        var pdfs = OcrRunner.ToOutcome(new OcrRunResult(["a.pdf", "b.pdf", "c.pdf"], []), OcrOutput.SearchablePdf);
        Assert.Equal("Saved 3 files · as searchable PDFs", pdfs.Message);
        var text = OcrRunner.ToOutcome(new OcrRunResult(["a.txt", "b.txt"], []), OcrOutput.Text);
        Assert.Equal("Saved 2 files · as text", text.Message);
    }

    [Fact]
    public void Files_that_failed_are_counted_in_the_toast()
    {
        var outcome = OcrRunner.ToOutcome(new OcrRunResult(["a.pdf", "b.pdf"], [("c.png", "No text was found.")]), OcrOutput.SearchablePdf);
        Assert.True(outcome.SavedAnything);
        Assert.Equal("2 saved · 1 failed", outcome.Message);
    }

    [Fact]
    public void The_failure_message_for_one_file_is_its_reason()
    {
        Assert.Equal("No text was found.", OcrRunner.FailureMessage(new OcrRunResult([], [("a.png", "No text was found.")])));
    }

    [Fact]
    public void The_failure_message_for_several_files_names_the_first_and_counts_the_rest()
    {
        var result = new OcrRunResult([], [(@"C:\x\a.png", "Unreadable."), ("b.png", "Unreadable."), ("c.png", "Unreadable.")]);
        Assert.Equal("“a.png”: Unreadable. (and 2 more files failed)", OcrRunner.FailureMessage(result));
        Assert.Equal("“a.png”: Unreadable. (and 1 more file failed)", OcrRunner.FailureMessage(new OcrRunResult([], [("a.png", "Unreadable."), ("b.png", "Unreadable.")])));
    }

    [Theory]
    [InlineData(new[] { "a.png" }, true)]
    [InlineData(new[] { "a.JPG", "b.heic" }, true)]
    [InlineData(new[] { "scan.pdf" }, true)]
    [InlineData(new[] { "scan.pdf", "a.png" }, true)]
    [InlineData(new[] { "notes.txt" }, false)]
    [InlineData(new[] { "a.png", "notes.docx" }, false)]
    [InlineData(new[] { "clip.mp4" }, false)]
    [InlineData(new string[0], false)]
    public void Takes_images_and_pdfs_alone_or_mixed(string[] paths, bool expected)
    {
        Assert.Equal(expected, OcrRunner.Applies(paths));
    }

    // ---- the real engine (skipped when Windows has no OCR language) -----------------------------------------------

    [FactWhenOcr]
    public async Task Reads_a_real_picture_to_text_and_a_real_scan_to_a_searchable_pdf()
    {
        using var dir = new TempFolder();
        var picture = Pictures.Text(dir.File("Invoice.png"), "Invoice 2041", "Total due 1280");
        var scan = Pictures.ScannedPdf(dir.File("Scan.pdf"), picture);
        var runner = new OcrRunner();

        var text = await runner.RunAsync([picture], OcrOutput.Text);
        Assert.Empty(text.Failed);
        Assert.Equal(dir.File("Invoice OCR.txt"), text.Saved[0]);
        Assert.Contains("Invoice", File.ReadAllText(text.Saved[0]));

        var pdf = await runner.RunAsync([scan], OcrOutput.SearchablePdf);
        Assert.Empty(pdf.Failed);
        Assert.Equal(dir.File("Scan OCR.pdf"), pdf.Saved[0]);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf.Saved[0]).Take(5).ToArray()));
        Assert.Equal(new[] { "Invoice.png", "Invoice OCR.txt", "Scan OCR.pdf", "Scan.pdf" }.Order(), dir.Names().Order());
    }

    [FactWhenOcr]
    public async Task A_picture_with_no_text_is_reported_not_thrown()
    {
        using var dir = new TempFolder();
        var blank = Pictures.Plain(dir.File("Blank.png"));
        var text = Pictures.Text(dir.File("Words.png"), "Shopping list");
        var result = await new OcrRunner().RunAsync([blank, text], OcrOutput.Text);
        Assert.Single(result.Failed);
        Assert.Equal(blank, result.Failed[0].Path);
        Assert.Single(result.Saved);
        Assert.False(File.Exists(dir.File("Blank OCR.txt")));
    }
}
