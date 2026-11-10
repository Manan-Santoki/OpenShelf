using System.Windows;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

/// <summary>The shapes the Crop tool offers. <see cref="Value"/> is width divided by height, or null for a free crop.</summary>
public sealed record CropRatio(string Title, double? Value)
{
    public static readonly CropRatio Free = new("Free", null);
    public static readonly CropRatio Square = new("1:1", 1);
    public static readonly CropRatio Landscape = new("16:9", 16.0 / 9);
    public static readonly CropRatio Tall = new("9:16", 9.0 / 16);
    public static readonly CropRatio Standard = new("4:3", 4.0 / 3);
    public static readonly CropRatio Portrait = new("3:4", 3.0 / 4);
    public static readonly IReadOnlyList<CropRatio> All = [Free, Square, Landscape, Tall, Standard, Portrait];
}

public enum CropHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

public static class CropHandles
{
    public static readonly IReadOnlyList<CropHandle> All = Enum.GetValues<CropHandle>();
    public static bool MovesLeft(this CropHandle h) => h is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft;
    public static bool MovesRight(this CropHandle h) => h is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight;
    public static bool MovesTop(this CropHandle h) => h is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight;
    public static bool MovesBottom(this CropHandle h) => h is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight;
    public static bool IsCorner(this CropHandle h) => (h.MovesLeft() || h.MovesRight()) && (h.MovesTop() || h.MovesBottom());

    public static Point PositionIn(this CropHandle h, Rect rect) => new(
        h.MovesLeft() ? rect.Left : h.MovesRight() ? rect.Right : rect.Left + rect.Width / 2,
        h.MovesTop() ? rect.Top : h.MovesBottom() ? rect.Bottom : rect.Top + rect.Height / 2);
}

/// <summary>Geometry for moving and resizing a crop rectangle in image pixels. Crops are always whole pixels inside the
/// image, so what the size fields show is exactly what is saved. Port of CropMath in CropEditor.swift.</summary>
public static class CropMath
{
    /// <summary>The crop after dragging <paramref name="handle"/> (or the whole crop, for null) by <paramref name="delta"/> image pixels.</summary>
    public static Rect Adjusted(Rect rect, CropHandle? handle, Vector delta, double? ratio, Rect bounds, double minimumSize)
    {
        if (handle is not { } h)
        {
            var moved = Moved(rect, delta, bounds);
            var origin = new Point(Math.Round(moved.Left, MidpointRounding.AwayFromZero), Math.Round(moved.Top, MidpointRounding.AwayFromZero));
            return Snapped(new Rect(origin, rect.Size), bounds);
        }
        var free = FreelyResized(rect, h, delta, bounds, minimumSize);
        if (ratio is not { } r) return Snapped(free, bounds);
        return Snapped(Constrained(rect, free, h, r, bounds, minimumSize), bounds);
    }

    private static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary><paramref name="rect"/> with each edge on a whole pixel, kept inside <paramref name="bounds"/>, at least one pixel across.</summary>
    public static Rect Snapped(Rect rect, Rect bounds)
    {
        var minX = Math.Min(Math.Max(Round(rect.Left), bounds.Left), bounds.Right - 1);
        var minY = Math.Min(Math.Max(Round(rect.Top), bounds.Top), bounds.Bottom - 1);
        var maxX = Math.Max(Math.Min(Round(rect.Right), bounds.Right), minX + 1);
        var maxY = Math.Max(Math.Min(Round(rect.Bottom), bounds.Bottom), minY + 1);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary><paramref name="rect"/> resized from its top-left corner to a typed size. The typed side is kept exactly; with a
    /// ratio the other side follows. Only a size that does not fit the image is scaled back.</summary>
    public static Rect Resized(Rect rect, int? width, int? height, double? ratio, Rect bounds, double minimumSize)
    {
        var newWidth = width ?? rect.Width;
        var newHeight = height ?? rect.Height;
        if (ratio is { } r)
        {
            if (width is not null) newHeight = Round(newWidth / r);
            else newWidth = Round(newHeight * r);
        }
        var smallestW = Math.Min(minimumSize, bounds.Width);
        var smallestH = Math.Min(minimumSize, bounds.Height);
        var fits = newWidth >= smallestW && newWidth <= bounds.Width && newHeight >= smallestH && newHeight <= bounds.Height;
        if (!fits)
        {
            newWidth = Math.Min(Math.Max(newWidth, smallestW), bounds.Width);
            newHeight = Math.Min(Math.Max(newHeight, smallestH), bounds.Height);
            if (ratio is { } q)
            {
                if (newWidth / newHeight > q) newWidth = Round(newHeight * q);
                else newHeight = Round(newWidth / q);
            }
        }
        var x = Math.Min(rect.Left, bounds.Right - newWidth);
        var y = Math.Min(rect.Top, bounds.Bottom - newHeight);
        return Snapped(new Rect(x, y, newWidth, newHeight), bounds);
    }

    public static Rect Moved(Rect rect, Vector delta, Rect bounds)
    {
        var x = Math.Min(Math.Max(rect.Left + delta.X, bounds.Left), bounds.Right - rect.Width);
        var y = Math.Min(Math.Max(rect.Top + delta.Y, bounds.Top), bounds.Bottom - rect.Height);
        return new Rect(x, y, rect.Width, rect.Height);
    }

    public static Rect FreelyResized(Rect rect, CropHandle handle, Vector delta, Rect bounds, double minimumSize)
    {
        double minX = rect.Left, maxX = rect.Right, minY = rect.Top, maxY = rect.Bottom;
        if (handle.MovesLeft()) minX = Math.Min(Math.Max(rect.Left + delta.X, bounds.Left), maxX - minimumSize);
        if (handle.MovesRight()) maxX = Math.Max(Math.Min(rect.Right + delta.X, bounds.Right), minX + minimumSize);
        if (handle.MovesTop()) minY = Math.Min(Math.Max(rect.Top + delta.Y, bounds.Top), maxY - minimumSize);
        if (handle.MovesBottom()) maxY = Math.Max(Math.Min(rect.Bottom + delta.Y, bounds.Bottom), minY + minimumSize);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Fits <paramref name="proposal"/> to <paramref name="ratio"/>, anchored at the corner or edge opposite the handle.</summary>
    public static Rect Constrained(Rect rect, Rect proposal, CropHandle handle, double ratio, Rect bounds, double minimumSize)
    {
        var horizontal = handle.MovesLeft() || handle.MovesRight();
        var vertical = handle.MovesTop() || handle.MovesBottom();
        double width;
        if (horizontal && vertical)
        {
            var widthChange = Math.Abs(proposal.Width - rect.Width) / rect.Width;
            var heightChange = Math.Abs(proposal.Height - rect.Height) / rect.Height;
            width = widthChange >= heightChange ? proposal.Width : proposal.Height * ratio;
        }
        else if (horizontal) width = proposal.Width;
        else width = proposal.Height * ratio;

        var midX = rect.Left + rect.Width / 2;
        var midY = rect.Top + rect.Height / 2;
        var maxWidth = handle.MovesLeft() ? rect.Right - bounds.Left
            : handle.MovesRight() ? bounds.Right - rect.Left
            : 2 * Math.Min(midX - bounds.Left, bounds.Right - midX);
        var maxHeight = handle.MovesTop() ? rect.Bottom - bounds.Top
            : handle.MovesBottom() ? bounds.Bottom - rect.Top
            : 2 * Math.Min(midY - bounds.Top, bounds.Bottom - midY);

        width = Math.Min(width, Math.Min(maxWidth, maxHeight * ratio));
        width = Math.Max(width, Math.Max(minimumSize, minimumSize * ratio));
        var height = width / ratio;

        var x = handle.MovesLeft() ? rect.Right - width : handle.MovesRight() ? rect.Left : midX - width / 2;
        var y = handle.MovesTop() ? rect.Bottom - height : handle.MovesBottom() ? rect.Top : midY - height / 2;
        return new Rect(x, y, width, height);
    }

    /// <summary><paramref name="rect"/> as fractions of <paramref name="bounds"/>, so one crop can be carried to images of other sizes.</summary>
    public static Rect Unit(Rect rect, Rect bounds) => new(
        (rect.Left - bounds.Left) / bounds.Width,
        (rect.Top - bounds.Top) / bounds.Height,
        rect.Width / bounds.Width,
        rect.Height / bounds.Height);

    /// <summary>A crop given as fractions of an image, placed on an image of <paramref name="bounds"/> in whole pixels: "the top half"
    /// stays the top half whatever the size. With a ratio the crop keeps that shape on images of another shape.</summary>
    public static Rect Mapped(Rect unit, Rect bounds, double? ratio)
    {
        var rect = new Rect(bounds.Left + unit.Left * bounds.Width, bounds.Top + unit.Top * bounds.Height, unit.Width * bounds.Width, unit.Height * bounds.Height);
        if (ratio is { } r)
        {
            var width = rect.Width;
            var height = width / r;
            if (height > bounds.Height) { height = bounds.Height; width = height * r; }
            if (width > bounds.Width) { width = bounds.Width; height = width / r; }
            width = Round(width);
            height = Round(height);
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            var x = Math.Min(Math.Max(Round(cx - width / 2), bounds.Left), bounds.Right - width);
            var y = Math.Min(Math.Max(Round(cy - height / 2), bounds.Top), bounds.Bottom - height);
            rect = new Rect(x, y, width, height);
        }
        return Snapped(rect, bounds);
    }

    /// <summary>The largest rect with <paramref name="ratio"/> that fits in <paramref name="bounds"/>, centred on <paramref name="center"/>
    /// as far as the bounds allow.</summary>
    public static Rect Largest(double ratio, Point center, Rect bounds)
    {
        var width = bounds.Width;
        var height = Math.Min(Round(width / ratio), bounds.Height);
        if (Round(width / ratio) > bounds.Height)
        {
            height = bounds.Height;
            width = Math.Min(Round(height * ratio), bounds.Width);
        }
        var x = Math.Min(Math.Max(Round(center.X - width / 2), bounds.Left), bounds.Right - width);
        var y = Math.Min(Math.Max(Round(center.Y - height / 2), bounds.Top), bounds.Bottom - height);
        return Snapped(new Rect(x, y, width, height), bounds);
    }
}

/// <summary>Saves cropped copies next to the originals ("Name Cropped.jpg").</summary>
public static class CropOperation
{
    public const string Suffix = "Cropped";

    /// <summary>Crops an in-memory image to <paramref name="crop"/> (whole pixels inside the image).</summary>
    public static void Apply(IMagickImage<byte> image, Rect crop)
    {
        var bounds = new Rect(0, 0, image.Width, image.Height);
        var r = CropMath.Snapped(crop, bounds);
        image.Crop(new MagickGeometry((int)r.X, (int)r.Y, (uint)r.Width, (uint)r.Height));
        image.ResetPage();
        ImageFiles.UpdateExifSize(image);
    }

    /// <summary>Saves <paramref name="path"/> cropped to <paramref name="crop"/> (its pixels, origin top-left).</summary>
    public static string Save(string path, Rect crop)
    {
        using var image = ImageFiles.ReadUpright(path);
        Apply(image, crop);
        return ImageFiles.SaveNext(image, path, Suffix);
    }

    /// <summary>Crops and saves each image; images whose crop is the whole image are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, Rect Crop, Size Size)> jobs, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(jobs, new SaveOutcome { SavedLabel = "cropped", UnchangedLabel = "not cropped" },
            job => job.Crop == new Rect(0, 0, job.Size.Width, job.Size.Height) ? null : Save(job.Path, job.Crop), progress, cancellation);
}
