using System.Windows;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

public enum RedactionStyle { Box, Pixelate, Blur }

/// <summary>How an area is hidden: each area keeps its own, so one image can mix a black box over a name with a blurred face.</summary>
/// <param name="Strength">0 to 100; ignored by the black box.</param>
public sealed record RedactionLook(RedactionStyle Style, double Strength)
{
    public static string Title(RedactionStyle style) => style switch
    {
        RedactionStyle.Box => "Black box",
        RedactionStyle.Pixelate => "Pixelate",
        _ => "Blur",
    };
}

/// <summary>One hidden area. <see cref="Rect"/> is in image pixels, origin top-left.</summary>
public sealed record RedactRegion(Guid Id, Rect Rect, RedactionLook Look)
{
    public static RedactRegion Create(Rect rect, RedactionLook look) => new(Guid.NewGuid(), rect, look);
}

/// <summary>Hides areas of an image and saves "Name Redacted.png". Strips metadata. Port of Redaction and ImageRedactor.</summary>
public static class RedactOperation
{
    public const string Suffix = "Redacted";

    /// <summary>Hides each area in its own look. Strength is relative to the image's size, so a downscaled preview looks like the
    /// full-resolution result.</summary>
    public static void Apply(IMagickImage<byte> image, IEnumerable<(Rect Rect, RedactionLook Look)> areas)
    {
        var longSide = Math.Max(image.Width, image.Height);
        // Each area is cut from the untouched original, so overlapping looks do not compound.
        using var original = image.Clone();
        foreach (var (rect, look) in areas)
        {
            var amount = Math.Clamp(look.Strength, 0, 100) / 100;
            var x0 = (int)Math.Clamp(Math.Floor(rect.Left), 0, image.Width - 1);
            var y0 = (int)Math.Clamp(Math.Floor(rect.Top), 0, image.Height - 1);
            var x1 = (int)Math.Clamp(Math.Ceiling(rect.Right), x0 + 1, image.Width);
            var y1 = (int)Math.Clamp(Math.Ceiling(rect.Bottom), y0 + 1, image.Height);
            var w = (uint)(x1 - x0);
            var h = (uint)(y1 - y0);
            switch (look.Style)
            {
                case RedactionStyle.Box:
                    using (var black = new MagickImage(MagickColors.Black, w, h))
                        image.Composite(black, x0, y0, CompositeOperator.Over);
                    break;
                case RedactionStyle.Pixelate:
                    PixelateInto(image, original, x0, y0, w, h, Math.Max(longSide * (0.006 + amount * 0.044), 2));
                    break;
                default:
                    BlurInto(image, original, x0, y0, w, h, Math.Max(longSide * (0.003 + amount * 0.025), 1));
                    break;
            }
        }
    }

    private static IMagickImage<byte> CropCopy(IMagickImage<byte> image, MagickGeometry area)
    {
        var copy = image.Clone();
        copy.Crop(area);
        copy.ResetPage();
        return copy;
    }

    private static void PixelateInto(IMagickImage<byte> image, IMagickImage<byte> original, int x0, int y0, uint w, uint h, double block)
    {
        var size = Math.Max(2, (int)Math.Round(block));
        // Blocks sit on a grid anchored at the image's corner, so neighbouring areas line up.
        var gx0 = x0 / size * size;
        var gy0 = y0 / size * size;
        var gx1 = Math.Min((int)original.Width, (x0 + (int)w + size - 1) / size * size);
        var gy1 = Math.Min((int)original.Height, (y0 + (int)h + size - 1) / size * size);
        using var area = CropCopy(original, new MagickGeometry(gx0, gy0, (uint)(gx1 - gx0), (uint)(gy1 - gy0)));
        area.ResetPage();
        var cols = (uint)Math.Max(1, (int)Math.Ceiling((double)area.Width / size));
        var rows = (uint)Math.Max(1, (int)Math.Ceiling((double)area.Height / size));
        var fullW = area.Width;
        var fullH = area.Height;
        area.FilterType = FilterType.Box;
        area.Resize(new MagickGeometry(cols, rows) { IgnoreAspectRatio = true });
        area.FilterType = FilterType.Point;
        area.Resize(new MagickGeometry(cols * (uint)size, rows * (uint)size) { IgnoreAspectRatio = true });
        area.Crop(new MagickGeometry(0, 0, fullW, fullH));
        area.ResetPage();
        area.Crop(new MagickGeometry(x0 - gx0, y0 - gy0, w, h));
        area.ResetPage();
        image.Composite(area, x0, y0, CompositeOperator.Over);
    }

    private static void BlurInto(IMagickImage<byte> image, IMagickImage<byte> original, int x0, int y0, uint w, uint h, double sigma)
    {
        // Blur a patch with room around it, so the area's edges blur from real neighbours, then keep only the area.
        var margin = (int)Math.Ceiling(sigma * 3);
        var px0 = Math.Max(0, x0 - margin);
        var py0 = Math.Max(0, y0 - margin);
        var px1 = Math.Min((int)original.Width, x0 + (int)w + margin);
        var py1 = Math.Min((int)original.Height, y0 + (int)h + margin);
        using var patch = CropCopy(original, new MagickGeometry(px0, py0, (uint)(px1 - px0), (uint)(py1 - py0)));
        patch.ResetPage();
        ImageOps.SoftBlur(patch, sigma);
        patch.Crop(new MagickGeometry(x0 - px0, y0 - py0, w, h));
        patch.ResetPage();
        image.Composite(patch, x0, y0, CompositeOperator.Over);
    }

    /// <summary>Saves <paramref name="path"/> with the areas hidden and its metadata removed, keeping the colour profile.</summary>
    public static string Save(string path, IReadOnlyList<RedactRegion> regions)
    {
        using var image = ImageFiles.ReadUpright(path);
        Apply(image, regions.Select(r => (r.Rect, r.Look)));
        // Redacted copies should not carry location or camera details either.
        MetadataStripper.Apply(image, keepsColorProfile: true);
        return ImageFiles.SaveNext(image, path, Suffix);
    }

    /// <summary>Hides each image's areas and saves it; images without areas are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, IReadOnlyList<RedactRegion> Regions)> jobs, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(jobs, new SaveOutcome { SavedLabel = "redacted", UnchangedLabel = "not redacted" },
            job => job.Regions.Count == 0 ? null : Save(job.Path, job.Regions), progress, cancellation);
}
