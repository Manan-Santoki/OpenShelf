using System.Windows;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

public enum ResizeBy { Percent, LongEdge, Exact }

/// <summary>How new image sizes are worked out. Port of ResizeRule in ResizeEditor.swift.</summary>
public sealed record ResizeRule
{
    public ResizeBy Mode { get; init; } = ResizeBy.Percent;
    /// <summary>1 to 400.</summary>
    public double Percent { get; init; } = 50;
    /// <summary>Pixels on the longest side.</summary>
    public int LongEdge { get; init; } = 1920;
    /// <summary>Pixels; 0 means "follow the other side's proportion".</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public bool AllowsEnlarging { get; init; }

    /// <summary>The new size for an image of <paramref name="size"/>, in whole pixels. Without <see cref="AllowsEnlarging"/>, results
    /// larger than the original are scaled back to it.</summary>
    public Size TargetSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return size;
        double w, h;
        switch (Mode)
        {
            case ResizeBy.Percent:
                w = size.Width * Percent / 100;
                h = size.Height * Percent / 100;
                break;
            case ResizeBy.LongEdge:
                var scale = (double)LongEdge / Math.Max(size.Width, size.Height);
                w = size.Width * scale;
                h = size.Height * scale;
                break;
            default:
                switch (Width > 0, Height > 0)
                {
                    case (true, true): w = Width; h = Height; break;
                    case (true, false): w = Width; h = Width * size.Height / size.Width; break;
                    case (false, true): w = Height * size.Width / size.Height; h = Height; break;
                    default: w = size.Width; h = size.Height; break;
                }
                break;
        }
        if (!AllowsEnlarging && (w > size.Width || h > size.Height))
        {
            var shrink = Math.Min(size.Width / w, size.Height / h);
            w *= shrink;
            h *= shrink;
        }
        return new Size(Math.Max(1, Math.Round(w, MidpointRounding.AwayFromZero)), Math.Max(1, Math.Round(h, MidpointRounding.AwayFromZero)));
    }

    /// <summary>Whether an image of <paramref name="size"/> would come out a different size.</summary>
    public bool Changes(Size size) => TargetSize(size) != size;

    /// <summary>Why an image is left as it is, as in "1 resized · 2 already smaller".</summary>
    public string UnchangedLabel => AllowsEnlarging ? "already that size" : "already smaller";
}

/// <summary>High-quality scaling and saving for the Resize tool ("Name Resized.jpg", same format).</summary>
public static class ResizeOperation
{
    public const string Suffix = "Resized";

    /// <summary>Scales <paramref name="image"/> to <paramref name="size"/> with Lanczos resampling, keeping transparency.</summary>
    public static void Apply(IMagickImage<byte> image, Size size)
    {
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)size.Width, (uint)size.Height) { IgnoreAspectRatio = true });
        ImageFiles.UpdateExifSize(image);
    }

    public static string Save(string path, Size target)
    {
        using var image = ImageFiles.ReadUpright(path);
        var format = ImageFiles.OutputFormat(ImageFiles.FormatOf(path));
        Apply(image, target);
        // Lossy copies keep the quality the original was saved at.
        uint? quality = ImageFiles.Family(format) is MagickFormat.Jpeg or MagickFormat.WebP ? Math.Max(image.Quality, 85) : null;
        return ImageFiles.SaveNext(image, path, Suffix, quality: quality);
    }

    /// <summary>Resizes and saves each image; images whose size would not change are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, Size Size)> images, ResizeRule rule, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(images, new SaveOutcome { SavedLabel = "resized", UnchangedLabel = rule.UnchangedLabel },
            item =>
            {
                var target = rule.TargetSize(item.Size);
                return target == item.Size ? null : Save(item.Path, target);
            }, progress, cancellation);
}
