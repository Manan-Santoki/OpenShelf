using PDFtoImage;
using SkiaSharp;

namespace OpenShelf.Convert.Documents;

/// <summary>Draws PDF pages (PDFium, through PDFtoImage). Pages are rendered one at a time, so a 500-page book at 300 dpi never sits in memory.</summary>
public static class PdfRenderer
{
    /// <summary>Page images are rendered at print resolution.</summary>
    public const int PrintDpi = 300;

    public static int PageCount(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Conversion.GetPageCount(stream);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failure(path, e);
        }
    }

    /// <summary>Each page's size in points (as displayed, rotation applied).</summary>
    public static IReadOnlyList<(double Width, double Height)> PageSizes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Conversion.GetPageSizes(stream).Select(size => ((double)size.Width, (double)size.Height)).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failure(path, e);
        }
    }

    /// <summary>The pages as bitmaps, rendered lazily; dispose each one. Each page is size x dpi / 72 pixels, rounded.</summary>
    public static IEnumerable<SKBitmap> Render(string path, int dpi, CancellationToken ct = default)
    {
        var sizes = PageSizes(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ConversionException.Unreadable(path); }
        using (stream)
        {
            for (var index = 0; index < sizes.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var width = Math.Max((int)Math.Round(sizes[index].Width * dpi / 72), 1);
                var height = Math.Max((int)Math.Round(sizes[index].Height * dpi / 72), 1);
                SKBitmap bitmap;
                try { bitmap = Conversion.ToImage(stream, index, leaveOpen: true, options: new RenderOptions(Width: width, Height: height)); }
                catch (Exception e) when (e is not OperationCanceledException) { throw Failure(path, e); }
                yield return bitmap;
            }
        }
    }

    private static Exception Failure(string path, Exception e) =>
        PdfText.IsPasswordProblem(e) ? PdfText.PasswordProtected(path) : ConversionException.Unreadable(path);
}
