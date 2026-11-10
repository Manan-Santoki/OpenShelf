using OpenShelf.Convert;
using UglyToad.PdfPig;

namespace OpenShelf.Vision.Tests;

public class OcrTests
{
    private static readonly string[] Sample = ["Hello Sunday morning", "The quick brown fox", "jumps over the lazy dog"];

    private static string PdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    private static string Normalised(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ----- Without any OCR language -----

    [Fact]
    public void The_no_language_message_tells_the_person_what_to_do()
    {
        var message = new OcrNoLanguageException().Message;
        Assert.Contains("No OCR language is installed", message);
        Assert.Contains("Optical character recognition", message);
    }

    [Fact]
    public async Task A_pdf_whose_pages_all_have_text_needs_no_language_and_saves_text()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Report.pdf"), "First page text", "Second page text");
        var saved = await new OcrService().RunAsync(pdf, OcrOutput.Text);
        Assert.Equal(dir.File("Report OCR.txt"), saved);
        Assert.Equal("First page text\n\nSecond page text", File.ReadAllText(saved).Replace("\r\n", "\n"));
        Assert.Equal(["Report OCR.txt", "Report.pdf"], dir.Names());
    }

    [Fact]
    public async Task A_text_pdf_saved_as_searchable_pdf_keeps_its_pages_and_text()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Report.pdf"), "Alpha page", "Beta page");
        var saved = await new OcrService().RunAsync(pdf, OcrOutput.SearchablePdf);
        Assert.Equal(dir.File("Report OCR.pdf"), saved);
        using var document = PdfDocument.Open(saved);
        Assert.Equal(2, document.NumberOfPages);
        Assert.Equal(612, document.GetPage(1).Width);
        Assert.Equal(792, document.GetPage(1).Height);
        Assert.Contains("Alpha page", document.GetPage(1).Text);
        Assert.Contains("Beta page", document.GetPage(2).Text);
    }

    [Fact]
    public async Task Pages_that_inherit_their_size_and_fonts_survive()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Inherit.pdf"), true, "Inherited words");
        var saved = await new OcrService().RunAsync(pdf, OcrOutput.SearchablePdf);
        using var document = PdfDocument.Open(saved);
        Assert.Equal(612, document.GetPage(1).Width);
        Assert.Contains("Inherited words", document.GetPage(1).Text);
    }

    [Fact]
    public void A_layer_added_to_a_page_with_inherited_resources_keeps_the_original_content()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Inherit.pdf"), true, "Original words");
        var saved = dir.File("out.pdf");
        using (var writer = new OcrPdfWriter())
        {
            writer.OpenSource(pdf);
            writer.AddSourcePage(0, [new OcrLine("Layered words", 0.1, 0.2, 0.4, 0.04)]);
            writer.Save(saved);
        }
        using var document = PdfDocument.Open(saved);
        var text = document.GetPage(1).Text;
        Assert.Contains("Original words", text);
        Assert.Contains("Layered words", text);
        Assert.Equal(612, document.GetPage(1).Width);
    }

    [Fact]
    public async Task Names_never_overwrite_and_count_up()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Report.pdf"), "Some text");
        var first = await new OcrService().RunAsync(pdf, OcrOutput.Text);
        var second = await new OcrService().RunAsync(pdf, OcrOutput.Text);
        Assert.Equal(dir.File("Report OCR.txt"), first);
        Assert.Equal(dir.File("Report OCR 2.txt"), second);
    }

    [Fact]
    public async Task A_cancelled_run_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Report.pdf"), "A", "B", "C");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OcrService().RunAsync(pdf, OcrOutput.SearchablePdf, null, cts.Token));
        Assert.Equal(["Report.pdf"], dir.Names());
    }

    [Fact]
    public async Task Cancelling_between_pages_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var pdf = Generate.TextPdf(dir.File("Report.pdf"), "A", "B", "C");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p >= 0.3) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OcrService().RunAsync(pdf, OcrOutput.Text, progress, cts.Token));
        Assert.Equal(["Report.pdf"], dir.Names());
    }

    [Fact]
    public async Task A_failed_write_leaves_no_partial_file()
    {
        using var dir = new TempDir();
        var source = dir.File("Doc.pdf");
        File.WriteAllText(source, "x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileStaging.SaveAsync(source, "OCR", "pdf", async staged =>
        {
            await File.WriteAllTextAsync(staged, "half");
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));
        Assert.Equal(["Doc.pdf"], dir.Names());
    }

    [Fact]
    public async Task A_batch_reports_each_file_and_keeps_going()
    {
        using var dir = new TempDir();
        var good = Generate.TextPdf(dir.File("Good.pdf"), "Hello");
        var bad = dir.File("Bad.pdf");
        File.WriteAllText(bad, "not a pdf");
        var result = await new OcrService().RunAsync([bad, good], OcrOutput.Text);
        Assert.Equal(2, result.Files.Count);
        Assert.False(result.Files[0].Succeeded);
        Assert.NotNull(result.Files[0].Error);
        Assert.True(result.Files[1].Succeeded);
        Assert.Equal([dir.File("Good OCR.txt")], result.Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void The_text_layer_follows_page_rotation(int rotate)
    {
        using var dir = new TempDir();
        var plain = Generate.TextPdf(dir.File("plain.pdf"), "x");
        var rotated = dir.File("rotated.pdf");
        using (var input = PdfSharp.Pdf.IO.PdfReader.Open(plain, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var output = new PdfSharp.Pdf.PdfDocument())
        {
            output.AddPage(input.Pages[0]).Rotate = rotate;
            output.Save(rotated);
        }
        var saved = dir.File("out.pdf");
        using (var writer = new OcrPdfWriter())
        {
            writer.OpenSource(rotated);
            // Displayed page: 612 x 792, or 792 x 612 when turned a quarter. The line starts 20% across, halfway up.
            writer.AddSourcePage(0, [new OcrLine("ROTATED", 0.2, 0.5, 0.3, 0.05)]);
            writer.Save(saved);
        }
        var swap = rotate is 90 or 270;
        double dw = swap ? 792 : 612, dh = swap ? 612 : 792;
        double u = 0.2 * dw + 0.15 * dw, v = 0.5 * dh + 0.025 * dh; // the line's centre, displayed, origin bottom left
        using var document = PdfDocument.Open(saved);
        // PdfPig reports positions as displayed (turned by /Rotate), so they must land on the line's displayed centre.
        Assert.Equal(dw, document.GetPage(1).Width);
        var word = document.GetPage(1).GetWords().First(w => w.Text.Contains("ROTATED"));
        Assert.InRange(word.BoundingBox.Centroid.X, u - 25, u + 25);
        Assert.InRange(word.BoundingBox.Centroid.Y, v - 25, v + 25);
    }

    [Fact]
    public void Lines_are_read_top_to_bottom_then_left_to_right()
    {
        var lines = new[]
        {
            new OcrLine("right", 0.6, 0.80, 0.2, 0.05),
            new OcrLine("bottom", 0.1, 0.10, 0.2, 0.05),
            new OcrLine("left", 0.1, 0.81, 0.2, 0.05),
        };
        Assert.Equal(["left", "right", "bottom"], WindowsOcr.ReadingOrder(lines).Select(l => l.Text));
    }

    // ----- With a recognition language -----

    [FactWhen("OcrLanguage")]
    public async Task Reads_text_from_an_image()
    {
        using var dir = new TempDir();
        var path = Generate.TextImage(dir.File("Note.png"), Sample);
        var saved = await new OcrService().RunAsync(path, OcrOutput.Text);
        Assert.Equal(dir.File("Note OCR.txt"), saved);
        var text = Normalised(File.ReadAllText(saved));
        Assert.Contains("Hello Sunday morning", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quick brown fox", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lazy dog", text, StringComparison.OrdinalIgnoreCase);
    }

    [FactWhen("OcrLanguage")]
    public async Task Reports_lines_with_boxes_in_the_right_place()
    {
        using var dir = new TempDir();
        var path = Generate.TextImage(dir.File("Note.png"), Sample);
        var lines = await new OcrService().RecognizeImageAsync(path);
        Assert.True(lines.Count >= 3);
        // Reading order: the first line is the highest on the page (largest Y, origin at the bottom).
        Assert.True(lines[0].Y > lines[^1].Y);
        Assert.All(lines, l => { Assert.InRange(l.X, 0, 1); Assert.InRange(l.Y, 0, 1); Assert.True(l.Width > 0 && l.Height > 0); });
    }

    [FactWhen("OcrLanguage")]
    public async Task An_image_becomes_a_searchable_pdf_with_text_over_the_picture()
    {
        using var dir = new TempDir();
        var path = Generate.TextImage(dir.File("Scan.png"), Sample);
        var saved = await new OcrService().RunAsync(path, OcrOutput.SearchablePdf);
        Assert.Equal(dir.File("Scan OCR.pdf"), saved);
        using var document = PdfDocument.Open(saved);
        var page = document.GetPage(1);
        Assert.Contains("Sunday", page.Text, StringComparison.OrdinalIgnoreCase);
        // The page is the picture's size at 72 dpi and the first line's words sit near its top-left.
        var words = page.GetWords().ToList();
        var hello = words.First(w => w.Text.StartsWith("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(hello.BoundingBox.Left, 30, 140);
        Assert.True(hello.BoundingBox.Bottom > page.Height / 2, "the first line should be in the top half of the page");
        Assert.Equal(1, document.NumberOfPages);
        Assert.Single(page.GetImages());
    }

    [FactWhen("OcrLanguage")]
    public async Task A_scanned_pdf_gets_a_text_layer_and_keeps_its_picture()
    {
        using var dir = new TempDir();
        var pdf = Generate.ScanPdf(dir.File("Scan.pdf"), Sample, ["Second page about turtles"]);
        var saved = await new OcrService().RunAsync(pdf, OcrOutput.SearchablePdf);
        using var document = PdfDocument.Open(saved);
        Assert.Equal(2, document.NumberOfPages);
        Assert.Contains("fox", document.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("turtles", document.GetPage(2).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(document.GetPage(1).GetImages());
    }

    [FactWhen("OcrLanguage")]
    public async Task A_mixed_selection_of_images_and_pdfs_gets_one_result_each()
    {
        using var dir = new TempDir();
        var image = Generate.TextImage(dir.File("Photo.png"), Sample);
        var text = Generate.TextPdf(dir.File("Born digital.pdf"), "Already text");
        var scan = Generate.ScanPdf(dir.File("Scan.pdf"), ["Scanned words here"]);
        var result = await new OcrService().RunAsync([image, text, scan], OcrOutput.Text);
        Assert.Empty(result.Failed);
        Assert.Equal([dir.File("Photo OCR.txt"), dir.File("Born digital OCR.txt"), dir.File("Scan OCR.txt")], result.Saved);
        Assert.Equal("Already text", File.ReadAllText(dir.File("Born digital OCR.txt")).Trim());
        Assert.Contains("Scanned words", File.ReadAllText(dir.File("Scan OCR.txt")), StringComparison.OrdinalIgnoreCase);
    }

    [FactWhen("OcrLanguage")]
    public async Task A_pdf_mixing_text_and_scanned_pages_keeps_both()
    {
        using var dir = new TempDir();
        // Page 1 is born digital, page 2 a picture: build the picture PDF, then OCR a two-page file made from both.
        var scan = Generate.ScanPdf(dir.File("scan.pdf"), ["Only pixels here"]);
        var text = Generate.TextPdf(dir.File("text.pdf"), "Typed words");
        var mixed = dir.File("Mixed.pdf");
        using (var output = new PdfSharp.Pdf.PdfDocument())
        {
            using (var a = PdfSharp.Pdf.IO.PdfReader.Open(text, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import)) output.AddPage(a.Pages[0]);
            using (var b = PdfSharp.Pdf.IO.PdfReader.Open(scan, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import)) output.AddPage(b.Pages[0]);
            output.Save(mixed);
        }
        var saved = await new OcrService().RunAsync(mixed, OcrOutput.SearchablePdf);
        using var document = PdfDocument.Open(saved);
        Assert.Equal(2, document.NumberOfPages);
        Assert.Contains("Typed words", document.GetPage(1).Text);
        Assert.Contains("pixels", document.GetPage(2).Text, StringComparison.OrdinalIgnoreCase);
    }

    [FactWhen("OcrLanguage")]
    public async Task Cancelling_a_scanned_pdf_between_pages_saves_nothing()
    {
        using var dir = new TempDir();
        var pdf = Generate.ScanPdf(dir.File("Scan.pdf"), Sample, Sample, Sample);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OcrService().RunAsync(pdf, OcrOutput.SearchablePdf, progress, cts.Token));
        Assert.Equal(["Scan.pdf"], dir.Names());
    }

    [FactWhen("OcrLanguage")]
    public async Task A_picture_without_text_fails_as_text_and_saves_nothing()
    {
        using var dir = new TempDir();
        var path = Generate.PlainImage(dir.File("Blank.png"));
        var error = await Assert.ThrowsAsync<ConversionException>(() => new OcrService().RunAsync(path, OcrOutput.Text));
        Assert.Contains("No text was found", error.Message);
        Assert.Equal(["Blank.png"], dir.Names());
    }

    [FactWhen("OcrLanguage")]
    public void Installed_languages_are_listed()
    {
        Assert.NotEmpty(OcrLanguages.Installed());
    }
}

/// <summary>Reports on the calling thread so a test can react at the exact moment.</summary>
internal sealed class SyncProgress(Action<double> action) : IProgress<double>
{
    public void Report(double value) => action(value);
}
