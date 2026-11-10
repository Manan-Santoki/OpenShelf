using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OpenShelf.Vision;

/// <summary>The recognition languages installed in Windows (Settings > Time &amp; language > Language &amp; region).</summary>
public static class OcrLanguages
{
    /// <summary>Languages Windows can read right now. Empty when no OCR feature is installed.</summary>
    public static IReadOnlyList<OcrLanguage> Installed() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName)).ToList();

    /// <summary>Whether at least one recognition language is installed.</summary>
    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;
}

/// <summary>Windows.Media.Ocr, wrapped. Recognition runs on this PC.</summary>
internal sealed class WindowsOcr
{
    private readonly OcrEngine _engine;

    private WindowsOcr(OcrEngine engine) => _engine = engine;

    /// <summary>The engine for <paramref name="tag"/>, else for the user's profile languages, else for any installed language.</summary>
    /// <exception cref="OcrNoLanguageException">Nothing is installed.</exception>
    public static WindowsOcr Create(string? tag)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var language = new Language(tag);
            if (OcrEngine.IsLanguageSupported(language)) engine = OcrEngine.TryCreateFromLanguage(language);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            if (first is not null) engine = OcrEngine.TryCreateFromLanguage(first);
        }
        return engine is null ? throw new OcrNoLanguageException() : new WindowsOcr(engine);
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(PixelImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fitted = image.OnWhite().Fit((int)OcrEngine.MaxImageDimension);
        using var writer = new DataWriter();
        writer.WriteBytes(fitted.Bgra);
        var buffer = writer.DetachBuffer();
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, fitted.Width, fitted.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct);
        var width = (double)fitted.Width;
        var height = (double)fitted.Height;
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text) || line.Words.Count == 0) continue;
            var left = line.Words.Min(w => w.BoundingRect.Left);
            var top = line.Words.Min(w => w.BoundingRect.Top);
            var right = line.Words.Max(w => w.BoundingRect.Right);
            var bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            lines.Add(new OcrLine(text, Clamp(left / width), Clamp(1 - bottom / height), Clamp((right - left) / width), Clamp((bottom - top) / height)));
        }
        return ReadingOrder(lines);
    }

    private static double Clamp(double v) => Math.Min(Math.Max(v, 0), 1);

    /// <summary>Top to bottom, then left to right within a row (same rule as the Mac).</summary>
    internal static List<OcrLine> ReadingOrder(IEnumerable<OcrLine> lines)
    {
        // Rows are built top to bottom (a line joins the row when its middle is within half a line of the row's first line),
        // then each row is read left to right. Clustering keeps the order consistent where a pairwise comparison would not.
        var ordered = new List<OcrLine>();
        var row = new List<OcrLine>();
        foreach (var line in lines.OrderByDescending(l => l.Y + l.Height / 2))
        {
            if (row.Count > 0)
            {
                var first = row[0];
                var distance = Math.Abs(first.Y + first.Height / 2 - (line.Y + line.Height / 2));
                if (distance > Math.Min(first.Height, line.Height) / 2)
                {
                    ordered.AddRange(row.OrderBy(l => l.X));
                    row.Clear();
                }
            }
            row.Add(line);
        }
        ordered.AddRange(row.OrderBy(l => l.X));
        return ordered;
    }
}
