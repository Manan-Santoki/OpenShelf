using ImageMagick;

namespace OpenShelf.Tools.Imaging;

public enum CompressMode { Quality, TargetSize }

/// <summary>"Keep" keeps JPEG, PNG and WebP as they are; other formats become JPEG (PNG when see-through). The Windows build
/// writes no HEIC, so JPEG is the one format to switch to.</summary>
public enum CompressFormat { KeepOriginal, Jpeg }

/// <summary>How to compress: a fixed quality, or a target size relative to the original.</summary>
public sealed record CompressOptions
{
    public CompressMode Mode { get; init; } = CompressMode.Quality;
    /// <summary>10 to 100.</summary>
    public double Quality { get; init; } = 75;
    /// <summary>Percentage of the original file size, 10 to 90.</summary>
    public double TargetPercent { get; init; } = 50;
    public CompressFormat Format { get; init; } = CompressFormat.KeepOriginal;
    /// <summary>Longest edge in pixels; null keeps the original size.</summary>
    public int? MaxDimension { get; init; }
}

/// <summary>Encodes images to meet <see cref="CompressOptions"/> and saves "Name Compressed.jpg". Port of ImageCompressor.</summary>
public static class CompressOperation
{
    public const string Suffix = "Compressed";

    public static string NotSmallerMessage(int count) => count == 1
        ? "Already as small as it gets — nothing saved."
        : $"These {count} files are already as small as they get — nothing saved.";

    /// <summary>The format a compressed copy is written in.</summary>
    public static MagickFormat OutputFormat(MagickFormat source, bool isTransparent, CompressFormat format)
    {
        if (format == CompressFormat.Jpeg) return MagickFormat.Jpeg;
        var family = ImageFiles.Family(source);
        if (family is MagickFormat.Jpeg or MagickFormat.Png) return family;
        if (family == MagickFormat.WebP && ImageFiles.CanWrite(family)) return family;
        return isTransparent ? MagickFormat.Png : MagickFormat.Jpeg;
    }

    /// <summary>A short explanation when a choice changes something the user might not expect, shown under the format picker.</summary>
    public static string? FormatNote(IReadOnlyList<EditableImage> images, CompressFormat format)
    {
        var notes = new List<string>();
        if (images.Any(i => i.IsAnimated)) notes.Add("Animation isn’t kept; only the first frame is saved.");
        if (format == CompressFormat.KeepOriginal)
        {
            var switched = images.FirstOrDefault(i => OutputFormat(i.Format, i.IsTransparent, format) != ImageFiles.Family(i.Format));
            if (switched is not null)
            {
                var from = ImageFiles.Extension(switched.Path, switched.Format, switched.Format).ToUpperInvariant();
                var to = OutputFormat(switched.Format, switched.IsTransparent, format) == MagickFormat.Png ? "PNG (keeps transparency)" : "JPEG";
                notes.Add($"{from} can’t be compressed in place — saves as {to}.");
            }
        }
        if (format == CompressFormat.Jpeg && images.Any(i => i.IsTransparent)) notes.Add("JPEG has no transparency; see-through areas become white.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>Whether a compressed result is worth saving: only if it is smaller.</summary>
    public static bool IsWorthSaving(long compressedSize, long originalSize) => originalSize <= 0 || compressedSize < originalSize;

    /// <summary>Encodes an upright image according to <paramref name="options"/>. <paramref name="original"/> is the source file
    /// (for its format and size). Returns the file bytes and the format they are in.</summary>
    public static (byte[] Data, MagickFormat Format) Compress(IMagickImage<byte> upright, string original, CompressOptions options,
        bool? isTransparent = null, MagickFormat? sourceFormat = null)
    {
        var source = sourceFormat ?? ImageFiles.FormatOf(original);
        var transparent = isTransparent ?? ImageFiles.IsTransparent(upright);
        var format = OutputFormat(source, transparent, options.Format);
        using var pixels = (MagickImage)upright.Clone();
        if (options.MaxDimension is { } max) Shrink(pixels, max);
        var originalBytes = FileLength(original);

        if (format == MagickFormat.Png) return (CompressPng(pixels, transparent, options, originalBytes), format);

        if (options.Mode == CompressMode.Quality)
            return (Encode(pixels, format, (uint)Math.Clamp(Math.Round(options.Quality), 1, 100)), format);

        var target = (long)(originalBytes * options.TargetPercent / 100);
        for (var pass = 0; pass < 4; pass++)
        {
            // Find the highest quality that fits; shrink the pixels if even the lowest is too big.
            uint low = 5, high = 95;
            byte[]? best = null;
            for (var step = 0; step < 7; step++)
            {
                var quality = (low + high) / 2;
                var data = Encode(pixels, format, quality);
                if (data.Length <= target) { best = data; low = quality; }
                else high = quality;
            }
            if (best is not null) return (best, format);
            var smallest = Encode(pixels, format, 5);
            if (smallest.Length <= target) return (smallest, format);
            var scale = Math.Max(0.2, Math.Sqrt((double)target / smallest.Length) * 0.95);
            var longest = Math.Max(pixels.Width, pixels.Height) * scale;
            if (longest < 16) return (smallest, format);
            Shrink(pixels, (int)longest);
        }
        return (Encode(pixels, format, 5), format);
    }

    public static (byte[] Data, MagickFormat Format) Compress(string path, CompressOptions options)
    {
        using var image = ImageFiles.ReadUpright(path);
        return Compress(image, path, options);
    }

    /// <summary>PNG is lossless: re-encode compactly (dropping an unused alpha channel) and, for a target size, scale down until it fits.</summary>
    private static byte[] CompressPng(MagickImage pixels, bool transparent, CompressOptions options, long originalBytes)
    {
        if (!transparent && pixels.HasAlpha) pixels.Alpha(AlphaAction.Off);
        var data = EncodePng(pixels);
        if (options.Mode != CompressMode.TargetSize) return data;
        var target = (long)(originalBytes * options.TargetPercent / 100);
        for (var pass = 0; pass < 6 && data.Length > target; pass++)
        {
            var scale = Math.Max(0.2, Math.Sqrt((double)target / data.Length) * 0.95);
            var longest = Math.Max(pixels.Width, pixels.Height) * scale;
            if (longest < 16) break;
            Shrink(pixels, (int)longest);
            data = EncodePng(pixels);
        }
        return data;
    }

    private static byte[] EncodePng(MagickImage pixels)
    {
        using var copy = (MagickImage)pixels.Clone();
        copy.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        copy.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
        copy.Strip();
        return ImageFiles.Encode(copy, MagickFormat.Png);
    }

    private static byte[] Encode(MagickImage pixels, MagickFormat format, uint quality)
    {
        using var copy = (MagickImage)pixels.Clone();
        if (format == MagickFormat.Jpeg) copy.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
        return ImageFiles.Encode(copy, format, quality);
    }

    private static void Shrink(IMagickImage<byte> image, int longest)
    {
        var current = Math.Max(image.Width, image.Height);
        if (current <= longest) return;
        var scale = (double)longest / current;
        image.FilterType = FilterType.Lanczos;
        image.Resize((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale)));
        ImageFiles.UpdateExifSize(image);
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>Writes the compressed copy next to the original, e.g. "Photo Compressed.jpg". Returns null, writing nothing, when it would not be smaller.</summary>
    public static string? Save(string path, CompressOptions options)
    {
        var sourceFormat = ImageFiles.FormatOf(path);
        using var image = ImageFiles.ReadUpright(path);
        var (data, format) = Compress(image, path, options, sourceFormat: sourceFormat);
        if (!IsWorthSaving(data.Length, FileLength(path))) return null;
        return ImageFiles.SaveBytesNext(data, path, Suffix, ImageFiles.Extension(path, sourceFormat, format));
    }

    /// <summary>Compresses and saves each image; images that would not get smaller are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<string> paths, CompressOptions options, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(paths, new SaveOutcome { SavedLabel = "compressed", UnchangedLabel = "can’t get smaller" },
            path => Save(path, options), progress, cancellation);
}
