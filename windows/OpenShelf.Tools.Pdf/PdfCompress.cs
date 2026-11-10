using System.IO.Compression;
using OpenShelf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace OpenShelf.Tools.Pdf;

/// <summary>How hard to squeeze a PDF.</summary>
public enum PdfCompressionLevel
{
    /// <summary>Re-encodes images at high quality. Best for print.</summary>
    Light,
    /// <summary>Scales images to screen resolution. Good for sharing.</summary>
    Balanced,
    /// <summary>Smallest file; images get noticeably softer.</summary>
    Strong,
}

/// <summary>Titles, summaries and image settings of the compression levels.</summary>
public static class PdfCompressionLevels
{
    public static string Title(this PdfCompressionLevel level) => level switch
    {
        PdfCompressionLevel.Light => "Light",
        PdfCompressionLevel.Balanced => "Balanced",
        _ => "Strong",
    };

    public static string Summary(this PdfCompressionLevel level) => level switch
    {
        PdfCompressionLevel.Light => "Re-encodes images at high quality. Best for print.",
        PdfCompressionLevel.Balanced => "Scales images to screen resolution. Good for sharing.",
        _ => "Smallest file; images get noticeably softer.",
    };

    /// <summary>JPEG quality (0-100), target resolution (dpi) and longest image edge (px) — the Mac tool's settings.</summary>
    public static (int Quality, int Dpi, int MaxSize) ImageSettings(this PdfCompressionLevel level) => level switch
    {
        PdfCompressionLevel.Light => (85, 220, 4000),
        PdfCompressionLevel.Balanced => (70, 144, 2400),
        _ => (45, 96, 1600),
    };
}

/// <summary>What compressing one PDF did (or, for an estimate, would do).</summary>
/// <param name="Path">The original.</param>
/// <param name="OutputPath">The new "Name Compressed.pdf", or null when nothing was saved (estimate, or no gain).</param>
/// <param name="OriginalSize">Bytes before.</param>
/// <param name="CompressedSize">Bytes after (what the compressed file is or would be, even when it was not saved).</param>
/// <param name="Saved">Whether a smaller file was written.</param>
public sealed record PdfCompressResult(string Path, string? OutputPath, long OriginalSize, long CompressedSize, bool Saved)
{
    /// <summary>Bytes saved; negative when the result grew.</summary>
    public long SavedBytes => OriginalSize - CompressedSize;

    /// <summary>The compressed file as a fraction of the original (1 = no change).</summary>
    public double Ratio => OriginalSize <= 0 ? 1 : (double)CompressedSize / OriginalSize;

    /// <summary>Whether the result is worth saving: only when smaller.</summary>
    public bool WouldBeSmaller => PdfCompressor.IsWorthSaving(CompressedSize, OriginalSize);

    /// <summary>"4.2 MB → 1.1 MB (74% smaller)" when it shrank, otherwise "Already as small as it gets — nothing saved."</summary>
    public string Message => WouldBeSmaller
        ? $"{PdfCompressor.FormatBytes(OriginalSize)} → {PdfCompressor.FormatBytes(CompressedSize)} ({Math.Max(1, (int)Math.Round((1 - Ratio) * 100))}% smaller)"
        : "Already as small as it gets — nothing saved.";
}

/// <summary>Several PDFs compressed: per-file results and the summary for a toast.</summary>
/// <param name="Outcome">Saved and unchanged counts ("2 compressed · 1 can’t get smaller").</param>
/// <param name="Results">One entry per input, in order.</param>
public sealed record PdfCompressBatch(PdfOutcome Outcome, IReadOnlyList<PdfCompressResult> Results)
{
    /// <summary>The Mac's message when nothing got smaller: "Already as small…" for one file, "These 3 PDFs are already as small as they get — nothing saved." for several; null when something was saved.</summary>
    public string? NothingSavedMessage => Outcome.SavedAnything ? null
        : Results.Count == 1 ? "Already as small as it gets — nothing saved."
        : $"These {Results.Count} PDFs are already as small as they get — nothing saved.";
}

/// <summary>Compress: downsamples and recompresses the images in a PDF and compresses its streams, keeping text and vectors.</summary>
public static class PdfCompressor
{
    /// <summary>Suffix of a compressed copy: "Report Compressed.pdf".</summary>
    public const string Suffix = "Compressed";

    /// <summary>Whether a compressed result is worth saving: only if it is smaller.</summary>
    public static bool IsWorthSaving(long compressedSize, long originalSize) => originalSize <= 0 || compressedSize < originalSize;

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Compresses in memory and reports the size, writing nothing — for an "estimated result" line.</summary>
    /// <exception cref="PdfToolException">Locked or damaged.</exception>
    public static Task<PdfCompressResult> EstimateAsync(string path, PdfCompressionLevel level,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var bytes = CompressedBytes(path, level, progress, cancellationToken);
            return new PdfCompressResult(path, null, PdfFiles.Size(path), bytes.Length, false);
        }, cancellationToken);

    /// <summary>Saves "Name Compressed.pdf" beside the original, or writes nothing when the result would not be smaller
    /// (then <see cref="PdfCompressResult.Saved"/> is false and <see cref="PdfCompressResult.Message"/> says so).</summary>
    /// <exception cref="PdfToolException">Locked or damaged; nothing is written.</exception>
    public static Task<PdfCompressResult> CompressAsync(string path, PdfCompressionLevel level,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressNow(path, level, progress, cancellationToken), cancellationToken);

    /// <summary>Compresses each PDF; files that would not get smaller are left alone and counted.</summary>
    public static Task<PdfCompressBatch> CompressAsync(IReadOnlyList<string> paths, PdfCompressionLevel level,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var results = new List<PdfCompressResult>();
            for (var i = 0; i < paths.Count; i++)
            {
                var share = new Progress<double>(p => progress?.Report((i + p) / paths.Count));
                results.Add(CompressNow(paths[i], level, share, cancellationToken));
            }
            progress?.Report(1);
            var outcome = new PdfOutcome
            {
                Saved = results.Where(r => r.Saved).Select(r => r.OutputPath!).ToList(),
                Unchanged = results.Count(r => !r.Saved),
                SavedLabel = "compressed",
                UnchangedLabel = "can’t get smaller",
            };
            return new PdfCompressBatch(outcome, results);
        }, cancellationToken);

    private static PdfCompressResult CompressNow(string path, PdfCompressionLevel level, IProgress<double>? progress, CancellationToken ct)
    {
        var original = PdfFiles.Size(path);
        var bytes = CompressedBytes(path, level, progress, ct);
        if (!IsWorthSaving(bytes.Length, original)) return new PdfCompressResult(path, null, original, bytes.Length, false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var destination = PdfFiles.WriteNew(bytes, directory, () => OutputNaming.Destination(path, Suffix, "pdf"), ct);
        progress?.Report(1);
        return new PdfCompressResult(path, destination, original, bytes.Length, true);
    }

    // MARK: The work

    private static byte[] CompressedBytes(string path, PdfCompressionLevel level, IProgress<double>? progress, CancellationToken ct)
    {
        using var document = PdfFiles.Open(path, PdfDocumentOpenMode.Modify);
        if (document.PageCount == 0) throw PdfToolException.NothingToSave();
        var (quality, dpi, maxSize) = level.ImageSettings();
        var displays = DisplaySizes(document, ct);

        var images = document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Image").ToList();
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { Recompress(images[i], displays, quality, dpi, maxSize); }
            catch (Exception e) when (e is not OperationCanceledException) { /* leave this image as it was */ }
            progress?.Report(0.9 * (i + 1) / images.Count);
        }

        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        try { return PdfFiles.ToBytes(document); }
        catch (Exception e) when (e is not OperationCanceledException) { throw PdfToolException.Unwritable(path, e); }
    }

    /// <summary>For each image object, the largest size in points at which a page draws it (top-level images only).</summary>
    private static Dictionary<int, (double W, double H)> DisplaySizes(PdfDocument document, CancellationToken ct)
    {
        var sizes = new Dictionary<int, (double, double)>();
        foreach (var page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
                if (xobjects is null) continue;
                var stack = new Stack<double[]>();
                var ctm = new[] { 1.0, 0, 0, 1, 0, 0 };
                foreach (var item in ContentReader.ReadContent(page))
                {
                    if (item is not COperator op) continue;
                    switch (op.Name)
                    {
                        case "q": stack.Push((double[])ctm.Clone()); break;
                        case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                        case "cm" when op.Operands.Count == 6:
                            var m = op.Operands.Select(Number).ToArray();
                            ctm = Multiply(m, ctm);
                            break;
                        case "Do" when op.Operands.Count == 1 && op.Operands[0] is CName name:
                            if (xobjects.Elements["/" + name.Name.TrimStart('/')] is PdfReference { Value: PdfDictionary target } reference &&
                                target.Elements.GetName("/Subtype") == "/Image")
                            {
                                var w = Math.Sqrt(ctm[0] * ctm[0] + ctm[1] * ctm[1]);
                                var h = Math.Sqrt(ctm[2] * ctm[2] + ctm[3] * ctm[3]);
                                sizes.TryGetValue(reference.ObjectNumber, out var seen);
                                sizes[reference.ObjectNumber] = (Math.Max(seen.Item1, w), Math.Max(seen.Item2, h));
                            }
                            break;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { /* sizes unknown for this page */ }
        }
        return sizes;

        static double Number(CObject o) => o switch { CInteger i => i.Value, CReal r => r.Value, _ => 0 };
        // [a b c d e f] x [a' ...]: the new matrix is applied first, then the old one.
        static double[] Multiply(double[] n, double[] o) =>
        [
            n[0] * o[0] + n[1] * o[2], n[0] * o[1] + n[1] * o[3],
            n[2] * o[0] + n[3] * o[2], n[2] * o[1] + n[3] * o[3],
            n[4] * o[0] + n[5] * o[2] + o[4], n[4] * o[1] + n[5] * o[3] + o[5],
        ];
    }

    private static void Recompress(PdfDictionary image, Dictionary<int, (double W, double H)> displays, int quality, int dpi, int maxSize)
    {
        var elements = image.Elements;
        if (elements.GetBoolean("/ImageMask") || elements.ContainsKey("/Mask") || elements.ContainsKey("/Decode")) return;
        int width = elements.GetInteger("/Width"), height = elements.GetInteger("/Height");
        if (width < 16 || height < 16) return;
        var stream = image.Stream!;
        var original = stream.Length;
        var filter = SingleFilter(elements);
        if (filter is not ("/DCTDecode" or "/FlateDecode")) return;
        var components = Components(image);
        if (components is not (1 or 3)) return;

        using var source = filter == "/DCTDecode" ? DecodeJpeg(stream.Value, components) : DecodeFlate(image, stream.Value, width, height, components);
        if (source is null) return;

        // Target size: no more pixels than the screen resolution needs where the page shows it, and never above the edge cap.
        var scale = 1.0;
        if (displays.TryGetValue(image.Internals.ObjectNumber, out var shown) && shown.W > 0 && shown.H > 0)
            scale = Math.Min(scale, Math.Max(shown.W * dpi / 72.0 / width, shown.H * dpi / 72.0 / height));
        scale = Math.Min(scale, (double)maxSize / Math.Max(width, height));
        var newWidth = Math.Max(1, (int)Math.Round(width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(height * scale));

        SKBitmap? resized = null;
        try
        {
            var toEncode = source;
            if (newWidth != width || newHeight != height)
            {
                resized = source.Resize(new SKImageInfo(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Opaque),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized is null) return;
                toEncode = resized;
            }
            using var skImage = SKImage.FromBitmap(toEncode);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (encoded is null) return;
            var jpeg = encoded.ToArray();
            if (jpeg.Length >= original) return;

            stream.Value = jpeg;
            elements["/Filter"] = new PdfName("/DCTDecode");
            elements.Remove("/DecodeParms");
            elements.SetInteger("/Width", newWidth);
            elements.SetInteger("/Height", newHeight);
            elements.SetInteger("/BitsPerComponent", 8);
            if (components == 1 || !elements.ContainsKey("/ColorSpace")) elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        }
        finally { resized?.Dispose(); }
    }

    /// <summary>The image's only filter, or null when it has several (or none).</summary>
    private static string? SingleFilter(PdfDictionary.DictionaryElements elements)
    {
        var item = elements["/Filter"];
        if (item is PdfName n) return n.Value;
        if (item is PdfArray { Elements.Count: 1 } a && a.Elements[0] is PdfName first) return first.Value;
        return null;
    }

    /// <summary>Colour components for DeviceGray/DeviceRGB/ICCBased/CalRGB images, or 0 for anything else (Indexed, CMYK, Lab...).</summary>
    private static int Components(PdfDictionary image)
    {
        var space = image.Elements["/ColorSpace"];
        if (space is PdfReference reference) space = reference.Value;
        switch (space)
        {
            case PdfName { Value: "/DeviceRGB" or "/CalRGB" }: return image.Elements.GetInteger("/BitsPerComponent") == 8 ? 3 : 0;
            case PdfName { Value: "/DeviceGray" or "/CalGray" }: return image.Elements.GetInteger("/BitsPerComponent") == 8 ? 1 : 0;
            case PdfArray { Elements.Count: >= 2 } array when array.Elements[0] is PdfName { Value: "/ICCBased" }:
                var profile = array.Elements[1] is PdfReference r ? r.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                var n = profile?.Elements.GetInteger("/N") ?? 0;
                return image.Elements.GetInteger("/BitsPerComponent") == 8 && n is 1 or 3 ? n : 0;
            default: return 0;
        }
    }

    private static SKBitmap? DecodeJpeg(byte[] data, int components)
    {
        if (PdfImages.JpegComponents(data) != components) return null; // CMYK and odd JPEGs stay as they are
        using var codec = SKCodec.Create(new MemoryStream(data));
        return codec is null ? null : SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
    }

    private static SKBitmap? DecodeFlate(PdfDictionary image, byte[] data, int width, int height, int components)
    {
        var raw = Inflate(data);
        if (raw is null) return null;
        var parms = image.Elements.GetDictionary("/DecodeParms");
        var predictor = parms?.Elements.GetInteger("/Predictor") ?? 1;
        if (predictor >= 10)
        {
            var columns = parms!.Elements.ContainsKey("/Columns") ? parms.Elements.GetInteger("/Columns") : 1;
            var colors = parms.Elements.ContainsKey("/Colors") ? parms.Elements.GetInteger("/Colors") : 1;
            if (columns != width || colors != components) return null;
            raw = UndoPngPredictor(raw, width, height, components);
            if (raw is null) return null;
        }
        else if (predictor != 1) return null;
        if (raw.Length < width * height * components) return null;

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0, s = 0, d = 0; i < width * height; i++)
        {
            if (components == 3) { pixels[d++] = raw[s++]; pixels[d++] = raw[s++]; pixels[d++] = raw[s++]; }
            else { var g = raw[s++]; pixels[d++] = g; pixels[d++] = g; pixels[d++] = g; }
            pixels[d++] = 255;
        }
        return bitmap;
    }

    private static byte[]? Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }
        catch { return null; }
    }

    private static byte[]? UndoPngPredictor(byte[] data, int width, int height, int bpp)
    {
        var row = width * bpp;
        if (data.Length < (row + 1) * height) return null;
        var result = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            var filter = data[y * (row + 1)];
            var src = y * (row + 1) + 1;
            var dst = y * row;
            for (var x = 0; x < row; x++)
            {
                int left = x >= bpp ? result[dst + x - bpp] : 0;
                int up = y > 0 ? result[dst - row + x] : 0;
                int upLeft = y > 0 && x >= bpp ? result[dst - row + x - bpp] : 0;
                int value = data[src + x];
                switch (filter)
                {
                    case 0: break;
                    case 1: value += left; break;
                    case 2: value += up; break;
                    case 3: value += (left + up) / 2; break;
                    case 4:
                        var p = left + up - upLeft;
                        int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - upLeft);
                        value += pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
                        break;
                    default: return null;
                }
                result[dst + x] = (byte)value;
            }
        }
        return result;
    }
}
