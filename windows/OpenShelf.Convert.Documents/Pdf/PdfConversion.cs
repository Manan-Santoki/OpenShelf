using PDFtoImage;
using SkiaSharp;

namespace OpenShelf.Convert.Documents;

/// <summary>What a PDF becomes: DOCX, JPG/PNG pages, TXT, MD, EPUB, PPTX and XLSX. Port of PDFConversion in DocumentConversion.swift.</summary>
public static class PdfConversion
{
    public static readonly IReadOnlyList<OutputFormat> Formats =
        [OutputFormat.Docx, OutputFormat.Jpg, OutputFormat.Png, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Pptx, OutputFormat.Xlsx];

    /// <summary>Converts <paramref name="source"/> (a PDF file), writing through <paramref name="outputPath"/>.</summary>
    /// <param name="stem">What results are named after.</param>
    /// <param name="pageLabel">How numbered image files are labelled ("Page 2", "Slide 2").</param>
    /// <param name="imageQuality">JPEG quality for page images; null for the usual 0.92.</param>
    public static IReadOnlyList<string> Convert(
        string source, OutputFormat format, string stem, Func<string, string> outputPath,
        string pageLabel = "Page", double? imageQuality = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var written = new List<string>();
        try
        {
            switch (format)
            {
                case OutputFormat.Txt:
                {
                    var pages = TextPages(source, ct, progress);
                    var destination = outputPath($"{stem}.txt");
                    written.Add(destination);
                    File.WriteAllText(destination, string.Join("\n", pages.Select(p => p.Text)), new System.Text.UTF8Encoding(false));
                    break;
                }
                case OutputFormat.Docx:
                    written.Add(ToDocx(source, stem, outputPath, progress, ct));
                    break;
                case OutputFormat.Jpg or OutputFormat.Png:
                    PageImages(source, format, stem, outputPath, pageLabel, imageQuality ?? 0.92, written, progress, ct);
                    break;
                case OutputFormat.Md:
                {
                    var pages = TextPages(source, ct, progress);
                    var body = PdfText.BodySize(pages);
                    var markdown = string.Join("\n---\n\n", pages.Select(page => DocumentStructure.Markdown(PdfText.Blocks(page, body))));
                    var destination = outputPath($"{stem}.md");
                    written.Add(destination);
                    File.WriteAllText(destination, markdown, new System.Text.UTF8Encoding(false));
                    break;
                }
                case OutputFormat.Epub:
                    written.Add(ToEpub(source, stem, outputPath, progress, ct));
                    break;
                case OutputFormat.Pptx:
                    written.Add(ToPptx(source, stem, outputPath, progress, ct));
                    break;
                case OutputFormat.Xlsx:
                {
                    var pages = TextPages(source, ct, progress);
                    var sheets = pages.Select((page, index) => ($"Page {index + 1}", (IReadOnlyList<IReadOnlyList<string>>)PdfText.Rows(page).Select(r => (IReadOnlyList<string>)r).ToList())).ToList();
                    var destination = outputPath($"{stem}.xlsx");
                    written.Add(destination);
                    XlsxWriter.Write(sheets, destination, ExcelLimit.Rows, ct);
                    break;
                }
                default:
                    throw ConversionException.Unsupported($"PDF to {format.Title()}");
            }
        }
        catch
        {
            foreach (var path in written) Zips.DeleteQuietly(path);
            throw;
        }
        progress?.Report(1);
        return written;
    }

    /// <summary>Each page's text, refusing PDFs that have none (scans).</summary>
    private static List<PdfPageText> TextPages(string source, CancellationToken ct, IProgress<double>? progress)
    {
        var pages = PdfText.Pages(source, ct, progress is null ? null : new Progress<double>(v => progress.Report(v * 0.9)));
        if (pages.All(page => page.IsBlank)) throw PdfText.NoText(source);
        return pages;
    }

    private static string ToDocx(string source, string stem, Func<string, string> outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var pages = PdfText.Pages(source, ct, progress is null ? null : new Progress<double>(v => progress.Report(v * 0.4)));
        var destination = outputPath($"{stem}.docx");
        if (pages.All(page => page.IsBlank))
        {
            // Scanned PDFs have no text layer; keep the pages as pictures instead.
            var images = new List<DocxImage>();
            var count = Math.Max(pages.Count, 1);
            foreach (var bitmap in PdfRenderer.Render(source, 150, ct))
            {
                using (bitmap) images.Add(new DocxImage(Pictures.EncodePng(bitmap), bitmap.Width, bitmap.Height));
                progress?.Report(0.4 + 0.6 * images.Count / count);
            }
            DocxWriter.Write(images, destination);
            return destination;
        }
        var body = PdfText.BodySize(pages);
        var blocks = pages.SelectMany(page => PdfText.Blocks(page, body)).ToList();
        WordWriter.Write(new DocumentModel(stem, blocks), destination);
        return destination;
    }

    private static void PageImages(string source, OutputFormat format, string stem, Func<string, string> outputPath, string pageLabel, double quality,
        List<string> written, IProgress<double>? progress, CancellationToken ct)
    {
        var count = PdfRenderer.PageCount(source);
        var index = 0;
        // Rendered and written a page at a time: 500 pages at 300 dpi held together come to about 16 GB.
        foreach (var bitmap in PdfRenderer.Render(source, PdfRenderer.PrintDpi, ct))
        {
            using (bitmap)
            {
                index++;
                var name = count == 1 ? stem : $"{stem} {pageLabel} {index}";
                var destination = outputPath($"{name}.{format.FileExtension()}");
                written.Add(destination);
                File.WriteAllBytes(destination, format == OutputFormat.Jpg ? Pictures.EncodeJpeg(bitmap, quality) : Pictures.EncodePng(bitmap));
            }
            progress?.Report((double)index / Math.Max(count, 1));
        }
    }

    private static string ToEpub(string source, string stem, Func<string, string> outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        // A chapter per page: text where the page has some, the page image where it doesn't.
        var pages = PdfText.Pages(source, ct, progress is null ? null : new Progress<double>(v => progress.Report(v * 0.5)));
        var body = PdfText.BodySize(pages);
        var chapters = new List<EpubChapter>();
        var images = new List<EpubImage>();
        for (var index = 0; index < pages.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            string html;
            if (pages[index].IsBlank)
            {
                using var bitmap = RenderOne(source, index, 150);
                var name = $"page{index + 1}.jpg";
                images.Add(new EpubImage(name, Pictures.EncodeJpeg(bitmap, 0.85), "image/jpeg"));
                html = $"<p><img src=\"images/{name}\" alt=\"Page {index + 1}\"/></p>";
            }
            else html = DocumentStructure.Xhtml(PdfText.Blocks(pages[index], body));
            chapters.Add(new EpubChapter($"Page {index + 1}", html));
            progress?.Report(0.5 + 0.5 * (index + 1) / Math.Max(pages.Count, 1));
        }
        var destination = outputPath($"{stem}.epub");
        EpubWriter.Write(stem, chapters, destination, images);
        return destination;
    }

    private static SKBitmap RenderOne(string source, int index, int dpi)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Conversion.ToImage(stream, index, options: new RenderOptions(Dpi: dpi));
    }

    private static string ToPptx(string source, string stem, Func<string, string> outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var sizes = PdfRenderer.PageSizes(source);
        var first = sizes.Count > 0 ? sizes[0] : (720.0, 540.0);
        var destination = outputPath($"{stem}.pptx");
        var done = 0;
        IEnumerable<SlidePicture> Slides()
        {
            foreach (var bitmap in PdfRenderer.Render(source, 160, ct))
            {
                using (bitmap)
                {
                    var jpeg = Pictures.EncodeJpeg(bitmap, 0.85);
                    done++;
                    progress?.Report((double)done / Math.Max(sizes.Count, 1));
                    yield return new SlidePicture(jpeg, bitmap.Width, bitmap.Height);
                }
            }
        }
        PptxWriter.Write(Slides(), first.Item1, first.Item2, destination);
        return destination;
    }
}

/// <summary>PDF as a source: DOCX, JPG, PNG, TXT, MD, EPUB, PPTX and XLSX, with no external tool.</summary>
public sealed class PdfEngine : IConversionEngine
{
    public bool CanConvert(FileKind kind, OutputFormat format) => kind == FileKind.Pdf && PdfConversion.Formats.Contains(format);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => PdfConversion.Convert(request.SourcePath, request.Format, request.SourceStem, request.OutputPath,
            imageQuality: request.Options.ImageQuality, progress: progress, ct: cancellationToken), cancellationToken);
}
