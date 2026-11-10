using System.Text;
using OpenShelf.Convert;
using OpenShelf.Convert.Documents;
using PDFtoImage;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace OpenShelf.Vision;

/// <summary>Reads the text in images and scanned PDF pages with the Windows OCR engine, on this PC, and saves it next to the
/// original as "Name OCR.txt" or "Name OCR.pdf" (a PDF with an invisible text layer). PDF pages that already have text keep
/// their content untouched; scanned pages are rendered and recognised one at a time and released before the next.
/// Cancellation is checked between pages; a cancelled or failed save leaves no partial file.</summary>
public sealed class OcrService
{
    /// <summary>The suffix added to saved names.</summary>
    public const string Suffix = "OCR";

    private readonly OcrOptions _options;

    public OcrService(OcrOptions? options = null) => _options = options ?? new OcrOptions();

    /// <summary>Whether Windows has a recognition language to use. When false, <see cref="RunAsync(string, OcrOutput, IProgress{double}?, CancellationToken)"/>
    /// throws <see cref="OcrNoLanguageException"/>, whose message tells the person how to install one.</summary>
    public static bool IsAvailable => OcrLanguages.IsAvailable;

    /// <summary>Recognises one image or PDF and saves the result next to it. Returns the saved path.</summary>
    /// <param name="progress">0 to 1 across the file's pages.</param>
    /// <exception cref="OcrNoLanguageException">No recognition language is installed.</exception>
    /// <exception cref="ConversionException">The file is unreadable, locked, has no text, or cannot be saved.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was saved for this file.</exception>
    public async Task<string> RunAsync(string path, OcrOutput output, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ocr = Engine();
        return await Task.Run(() => ProcessAsync(path, output, ocr, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Recognises a mixed selection of images and PDFs, one file after another. A file that fails is reported in the
    /// result and the rest continue; cancellation stops everything (files already saved stay).</summary>
    /// <param name="progress">0 to 1 across all files.</param>
    public async Task<OcrBatchResult> RunAsync(IReadOnlyList<string> paths, OcrOutput output, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ocr = Engine();
        var results = new List<OcrFileResult>();
        for (var i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = (double)i / paths.Count;
            var share = 1.0 / paths.Count;
            var inner = new Progress<double>(p => progress?.Report(offset + p * share));
            var path = paths[i];
            try
            {
                var saved = await Task.Run(() => ProcessAsync(path, output, ocr, inner, ct), ct).ConfigureAwait(false);
                results.Add(new OcrFileResult(path, saved, null));
            }
            catch (Exception e) when (e is ConversionException or VisionException)
            {
                results.Add(new OcrFileResult(path, null, e.Message));
            }
            progress?.Report((double)(i + 1) / paths.Count);
        }
        return new OcrBatchResult(results);
    }

    /// <summary>The lines in one image, without saving anything.</summary>
    public async Task<IReadOnlyList<OcrLine>> RecognizeImageAsync(string imagePath, CancellationToken ct = default)
    {
        var ocr = Engine().Value;
        var image = await Task.Run(() => PixelImage.Load(imagePath), ct).ConfigureAwait(false);
        return await ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
    }

    /// <summary>Created on first use, so a PDF whose pages all have text works without any OCR language.
    /// Images need it at once, and then <see cref="OcrNoLanguageException"/> is thrown.</summary>
    private Lazy<WindowsOcr> Engine() => new(() => WindowsOcr.Create(_options.LanguageTag), LazyThreadSafetyMode.ExecutionAndPublication);

    // ---- Work ------------------------------------------------------------------------------------------------

    private async Task<string> ProcessAsync(string path, OcrOutput output, Lazy<WindowsOcr> ocr, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(path)) throw ConversionException.Unreadable(path);
        return FileKinds.Of(path) == FileKind.Pdf
            ? await ProcessPdfAsync(path, output, ocr, progress, ct).ConfigureAwait(false)
            : await ProcessImageAsync(path, output, ocr, progress, ct).ConfigureAwait(false);
    }

    private async Task<string> ProcessImageAsync(string path, OcrOutput output, Lazy<WindowsOcr> ocr, IProgress<double>? progress, CancellationToken ct)
    {
        var image = PixelImage.Load(path);
        var lines = await ocr.Value.RecognizeAsync(image, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        if (output == OcrOutput.Text)
            return await SaveTextAsync(path, [string.Join("\n", lines.Select(l => l.Text))], ct).ConfigureAwait(false);
        return await FileStaging.SaveAsync(path, Suffix, "pdf", staged =>
        {
            using var writer = new OcrPdfWriter();
            writer.AddImagePage(image, lines);
            writer.Save(staged);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private async Task<string> ProcessPdfAsync(string path, OcrOutput output, Lazy<WindowsOcr> ocr, IProgress<double>? progress, CancellationToken ct)
    {
        // Which pages already have text, and their sizes (rotation applied), without rendering anything yet.
        var existing = PdfText.Pages(path, ct);
        var count = existing.Count;
        if (count == 0) throw ConversionException.Unreadable(path);
        var sizes = PdfRenderer.PageSizes(path);

        var texts = new string?[count];
        var recognised = new IReadOnlyList<OcrLine>[count];
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ConversionException.Unreadable(path); }
        using (stream)
        {
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                recognised[index] = [];
                if (!existing[index].IsBlank)
                {
                    texts[index] = existing[index].Text;
                }
                else
                {
                    // Render, read and release this page before the next one.
                    var width = Math.Max((int)Math.Round(sizes[index].Width * _options.RenderDpi / 72), 1);
                    var height = Math.Max((int)Math.Round(sizes[index].Height * _options.RenderDpi / 72), 1);
                    PixelImage image;
                    try
                    {
                        using var bitmap = Conversion.ToImage(stream, index, leaveOpen: true, options: new RenderOptions(Width: width, Height: height));
                        image = PixelImage.FromBitmap(bitmap, _options.RenderDpi);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { throw ConversionException.Unreadable(path); }
                    recognised[index] = await ocr.Value.RecognizeAsync(image, ct).ConfigureAwait(false);
                    texts[index] = string.Join("\n", recognised[index].Select(l => l.Text));
                }
                progress?.Report((index + 1.0) / count);
            }
        }
        ct.ThrowIfCancellationRequested();

        if (output == OcrOutput.Text)
            return await SaveTextAsync(path, texts.Select(t => t ?? ""), ct).ConfigureAwait(false);

        return await FileStaging.SaveAsync(path, Suffix, "pdf", staged =>
        {
            using var writer = new OcrPdfWriter();
            try { writer.OpenSource(path); }
            catch (PdfReaderException e)
            {
                throw e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? PdfText.PasswordProtected(path) : ConversionException.Unreadable(path);
            }
            for (var index = 0; index < writer.SourcePageCount && index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                writer.AddSourcePage(index, recognised[index]);
            }
            ct.ThrowIfCancellationRequested();
            writer.Save(staged);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<string> SaveTextAsync(string source, IEnumerable<string> pages, CancellationToken ct)
    {
        var text = string.Join("\n\n", pages);
        if (string.IsNullOrWhiteSpace(text))
            throw ConversionException.Failed($"No text was found in “{Path.GetFileName(source)}”.");
        return await FileStaging.SaveAsync(source, Suffix, "txt",
            staged => File.WriteAllTextAsync(staged, text, new UTF8Encoding(false), ct), ct).ConfigureAwait(false);
    }
}
