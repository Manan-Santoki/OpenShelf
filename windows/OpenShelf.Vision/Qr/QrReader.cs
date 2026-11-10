using OpenShelf.Convert;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;

namespace OpenShelf.Vision;

/// <summary>Finds QR codes (and Aztec, Data Matrix and PDF417 codes) in images, on this PC (ZXing.Net, Apache-2.0).
/// The caller copies the text to the clipboard and shows the matching message.</summary>
public static class QrReader
{
    /// <summary>The toast title when no code was found (same words as the Mac).</summary>
    public const string NothingFoundMessage = "No QR code found";

    /// <summary>The toast title when reading failed; put the exception message in the subtitle.</summary>
    public const string FailedMessage = "Couldn’t read QR code";

    /// <summary>"Copied QR code" for one code, "Copied 3 QR codes" for several.</summary>
    public static string CopiedMessage(int count) => count == 1 ? "Copied QR code" : $"Copied {count} QR codes";

    /// <summary>What to put on the clipboard: every code, one per line.</summary>
    public static string ClipboardText(IReadOnlyList<string> codes) => string.Join("\n", codes);

    /// <summary>Decoded payloads from every image, in reading order (top to bottom, then left to right), without duplicates.
    /// Empty when no code is found. Throws <see cref="ConversionException"/> for an unreadable image.</summary>
    public static Task<IReadOnlyList<string>> ReadAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var seen = new HashSet<string>();
            var payloads = new List<string>();
            foreach (var path in list)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var payload in Read(path, ct))
                    if (seen.Add(payload)) payloads.Add(payload);
            }
            return payloads;
        }, ct);
    }

    /// <summary>The codes in one image file, synchronously.</summary>
    public static IReadOnlyList<string> Read(string path, CancellationToken ct = default) =>
        Read(PixelImage.Load(path), ct);

    internal static IReadOnlyList<string> Read(PixelImage image, CancellationToken ct = default)
    {
        var flat = image.OnWhite();
        // Large photos first at full size, then smaller: codes in busy pictures often decode better downscaled.
        foreach (var candidate in Candidates(flat))
        {
            ct.ThrowIfCancellationRequested();
            var found = Decode(candidate);
            if (found.Count > 0) return found;
        }
        return [];
    }

    private static IEnumerable<PixelImage> Candidates(PixelImage image)
    {
        yield return image;
        if (Math.Max(image.Width, image.Height) > 1600) yield return image.Fit(1600);
        if (Math.Max(image.Width, image.Height) > 800) yield return image.Fit(800);
    }

    private static readonly Dictionary<DecodeHintType, object> QrHints = new()
    {
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
    };

    private static IReadOnlyList<string> Decode(PixelImage image)
    {
        var source = new RGBLuminanceSource(image.Bgra, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var inverted = source.invert();
        // Dark on light first, then the common binarizer swap, then light on dark.
        foreach (var (candidate, binarizer) in new (LuminanceSource, Func<LuminanceSource, Binarizer>)[]
        {
            (source, s => new HybridBinarizer(s)),
            (source, s => new GlobalHistogramBinarizer(s)),
            (inverted, s => new HybridBinarizer(s)),
        })
        {
            var results = DecodeWith(candidate, binarizer);
            if (results.Count > 0) return ReadingOrder(results);
        }
        return [];
    }

    private static List<Result> DecodeWith(LuminanceSource source, Func<LuminanceSource, Binarizer> binarizer)
    {
        var found = new List<Result>();
        // QR codes: the multi-QR reader finds several in one picture (the general reader tends to see only one).
        try
        {
            var qr = new QRCodeMultiReader().decodeMultiple(new BinaryBitmap(binarizer(source)), QrHints);
            if (qr is not null) found.AddRange(qr.Where(r => !string.IsNullOrEmpty(r.Text)));
        }
        catch (ReaderException) { }
        // Aztec, Data Matrix and PDF417.
        var reader = new BarcodeReaderGeneric(new MultiFormatReader(), binarizer, (raw, w, h, f) => new RGBLuminanceSource(raw, w, h, f))
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417],
            },
        };
        try
        {
            var others = reader.DecodeMultiple(source) ?? (reader.Decode(source) is { } single ? [single] : null);
            if (others is not null) found.AddRange(others.Where(r => !string.IsNullOrEmpty(r.Text) && found.All(f => f.Text != r.Text)));
        }
        catch (ReaderException) { }
        return found;
    }

    /// <summary>Top to bottom, then left to right within a row (codes whose centres are within half a code of each other share a row).</summary>
    private static List<string> ReadingOrder(List<Result> results)
    {
        var boxes = results.Select(r =>
        {
            var points = r.ResultPoints ?? [];
            if (points.Length == 0) return (Text: r.Text, X: 0f, Y: 0f, Size: 1f);
            var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
            return (Text: r.Text, X: (minX + maxX) / 2, Y: (minY + maxY) / 2, Size: Math.Max(Math.Max(maxX - minX, maxY - minY), 1f));
        }).OrderBy(b => b.Y).ToList();
        var ordered = new List<string>();
        var row = new List<(string Text, float X, float Y, float Size)>();
        void Flush() { ordered.AddRange(row.OrderBy(b => b.X).Select(b => b.Text)); row.Clear(); }
        foreach (var box in boxes)
        {
            if (row.Count > 0 && Math.Abs(box.Y - row.Average(b => b.Y)) > Math.Min(box.Size, row.Min(b => b.Size)) / 2) Flush();
            row.Add(box);
        }
        Flush();
        return ordered;
    }
}
