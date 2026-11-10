using System.Windows;
using ImageMagick;
using ImageMagick.Drawing;

namespace OpenShelf.Tools.Imaging;

public enum CanvasRatio { Auto, Square, Landscape, Portrait }

public static class CanvasRatios
{
    public static readonly IReadOnlyList<CanvasRatio> All = Enum.GetValues<CanvasRatio>();

    public static string Title(this CanvasRatio ratio) => ratio switch
    {
        CanvasRatio.Auto => "Auto",
        CanvasRatio.Square => "1:1",
        CanvasRatio.Landscape => "16:9",
        _ => "4:5",
    };

    /// <summary>Width divided by height, or null to fit the padded image exactly.</summary>
    public static double? Value(this CanvasRatio ratio) => ratio switch
    {
        CanvasRatio.Auto => null,
        CanvasRatio.Square => 1,
        CanvasRatio.Landscape => 16.0 / 9,
        _ => 4.0 / 5,
    };
}

/// <summary>An sRGB colour as 0xRRGGBB.</summary>
public readonly record struct HexColor(uint Hex)
{
    public byte R => (byte)((Hex >> 16) & 0xFF);
    public byte G => (byte)((Hex >> 8) & 0xFF);
    public byte B => (byte)(Hex & 0xFF);
    public MagickColor ToMagick() => new(R, G, B, 255);
    public static implicit operator HexColor(uint hex) => new(hex);
}

public static class SolidColors
{
    public static readonly IReadOnlyList<HexColor> All = [0xFFFFFFu, 0xF5C542u, 0x7BC950u, 0x2FA38Au, 0x5BC0D9u, 0x3D6FE0u, 0x4B47A8u, 0x2C2F36u, 0x8E5BD6u];
    public static readonly IReadOnlyList<string> Names = ["White", "Yellow", "Green", "Teal", "Cyan", "Blue", "Indigo", "Charcoal", "Purple"];
}

/// <summary>A soft background: a diagonal base gradient with blurred colour blobs on top. Centres are unit coordinates from the
/// top left; radii are fractions of the longer side.</summary>
public sealed record GradientStyle(HexColor[] Base, GradientStyle.Blob[] Blobs)
{
    public readonly record struct Blob(HexColor Color, double X, double Y, double Radius);

    public static readonly IReadOnlyList<GradientStyle> All =
    [
        new([0xFFB38Au, 0xFF6F91u], [new(0xFFD86Fu, 0.2, 0.2, 0.6), new(0xB39DFFu, 0.85, 0.9, 0.6)]),
        new([0xF4F1FFu, 0xDDE7FFu], [new(0xFFC8E1u, 0.8, 0.15, 0.55), new(0xA7D3FFu, 0.15, 0.85, 0.55)]),
        new([0xBFF0C8u, 0xFFF3A6u], [new(0x6FD3C7u, 0.85, 0.2, 0.55), new(0xFF9FB2u, 0.2, 0.85, 0.55)]),
        new([0xFBE3E8u, 0xEAD7F7u], [new(0xFFD1BAu, 0.2, 0.2, 0.55), new(0xCDB4FFu, 0.8, 0.8, 0.55)]),
        new([0xE8B5F0u, 0xF7C6D9u], [new(0xB28DFFu, 0.2, 0.8, 0.6), new(0xFFE0F0u, 0.8, 0.2, 0.5)]),
        new([0x2B2D5Bu, 0x7A3E73u], [new(0xE8566Bu, 0.8, 0.25, 0.55), new(0xF4A259u, 0.25, 0.85, 0.5)]),
        new([0x5EC8F2u, 0x6A7BFFu], [new(0xA6F0E4u, 0.2, 0.2, 0.55), new(0xC9A7FFu, 0.85, 0.85, 0.55)]),
        new([0xDDF6F7u, 0xB4E1F0u], [new(0xFFFFFFu, 0.3, 0.25, 0.5), new(0x9ED8E0u, 0.8, 0.8, 0.55)]),
    ];

    /// <summary>Paints the style into a <paramref name="width"/> by <paramref name="height"/> bitmap (RGBA bytes).</summary>
    public byte[] Paint(int width, int height)
    {
        var data = new byte[width * height * 4];
        var longest = Math.Max(width, height);
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        var length2 = (double)width * width + (double)height * height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Top left to bottom right.
            var t = Math.Clamp((x * (double)width + y * (double)height) / length2, 0, 1);
            double r = Lerp(Base[0].R, Base[1].R, t), g = Lerp(Base[0].G, Base[1].G, t), b = Lerp(Base[0].B, Base[1].B, t);
            foreach (var blob in Blobs)
            {
                var d = Math.Sqrt(Math.Pow(x - blob.X * width, 2) + Math.Pow(y - blob.Y * height, 2)) / (blob.Radius * longest);
                if (d >= 1) continue;
                var a = 0.9 * (1 - d);
                r = r * (1 - a) + blob.Color.R * a;
                g = g * (1 - a) + blob.Color.G * a;
                b = b * (1 - a) + blob.Color.B * a;
            }
            var i = (y * width + x) * 4;
            data[i] = (byte)Math.Round(r);
            data[i + 1] = (byte)Math.Round(g);
            data[i + 2] = (byte)Math.Round(b);
            data[i + 3] = 255;
        }
        return data;
    }
}

/// <summary>What the canvas is filled with.</summary>
public abstract record BackgroundPaint
{
    /// <summary>Paints a <paramref name="width"/> by <paramref name="height"/> background.</summary>
    public abstract MagickImage Make(uint width, uint height);

    public sealed record Gradient(GradientStyle Style) : BackgroundPaint
    {
        public override MagickImage Make(uint width, uint height)
        {
            // Gradients and blobs are smooth: paint a small version and scale it up.
            var longest = Math.Max(width, height);
            var scale = Math.Min(1.0, 320.0 / longest);
            var w = (int)Math.Max(2, Math.Round(width * scale));
            var h = (int)Math.Max(2, Math.Round(height * scale));
            var image = new MagickImage();
            image.ReadPixels(Style.Paint(w, h), new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));
            if (w != width || h != height)
            {
                image.FilterType = FilterType.Cubic;
                image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
            }
            return image;
        }
    }

    public sealed record Solid(HexColor Color) : BackgroundPaint
    {
        public override MagickImage Make(uint width, uint height) => new(Color.ToMagick(), width, height);
    }

    /// <summary>A picture scaled to fill, centred.</summary>
    public sealed record Picture(string Path) : BackgroundPaint
    {
        public override MagickImage Make(uint width, uint height)
        {
            try
            {
                var image = ImageFiles.ReadUpright(Path);
                image.FilterType = FilterType.Lanczos;
                image.Resize(new MagickGeometry(width, height) { FillArea = true });
                image.Crop(new MagickGeometry((int)((image.Width - width) / 2), (int)((image.Height - height) / 2), width, height));
                image.ResetPage();
                image.Alpha(AlphaAction.Off);
                return image;
            }
            catch (Exception e) when (e is IOException or MagickException)
            {
                return new MagickImage(MagickColors.White, width, height);
            }
        }
    }
}

/// <summary>The finished layout: background canvas with the image centred on it. Lengths are in output pixels.</summary>
public sealed record BackgroundComposition(BackgroundPaint Paint, double Padding, double CornerRadius, double Shadow, CanvasRatio Ratio)
{
    /// <summary>Whether the result would look any different from the image: only with a margin, rounded corners, or see-through areas
    /// for the background to show through. (A shadow with no margin falls behind the image.)</summary>
    public bool ChangesImage(Size imageSize, bool isTransparent)
    {
        var canvas = CanvasSize(imageSize);
        var sameSize = Math.Round(canvas.Width) == imageSize.Width && Math.Round(canvas.Height) == imageSize.Height;
        return !sameSize || Math.Round(CornerRadius) > 0 || isTransparent;
    }

    public Size CanvasSize(Size imageSize)
    {
        var padded = new Size(imageSize.Width + 2 * Padding, imageSize.Height + 2 * Padding);
        if (Ratio.Value() is not { } ratio) return padded;
        return padded.Width / padded.Height < ratio ? new Size(padded.Height * ratio, padded.Height) : new Size(padded.Width, padded.Width / ratio);
    }

    /// <summary>Renders at <paramref name="scale"/> × the output size. <paramref name="source"/> may be a downscaled copy of an image whose
    /// full size is <paramref name="imageSize"/>.</summary>
    public MagickImage Render(IMagickImage<byte> source, Size imageSize, double scale)
    {
        var canvas = CanvasSize(imageSize);
        var cw = (uint)Math.Max(1, Math.Round(canvas.Width * scale));
        var ch = (uint)Math.Max(1, Math.Round(canvas.Height * scale));
        var iw = (uint)Math.Max(1, Math.Round(imageSize.Width * scale));
        var ih = (uint)Math.Max(1, Math.Round(imageSize.Height * scale));
        var ix = (int)Math.Round((cw - iw) / 2.0);
        var iy = (int)Math.Round((ch - ih) / 2.0);

        var result = Paint.Make(cw, ch);
        result.Alpha(AlphaAction.Off);

        using var picture = (MagickImage)source.Clone();
        if (picture.Width != iw || picture.Height != ih)
        {
            picture.FilterType = FilterType.Lanczos;
            picture.Resize(new MagickGeometry(iw, ih) { IgnoreAspectRatio = true });
        }
        picture.Alpha(AlphaAction.Set);

        var radius = Math.Min(CornerRadius, Math.Min(imageSize.Width, imageSize.Height) / 2) * scale;
        if (radius >= 0.5)
        {
            using var mask = new MagickImage(MagickColors.Transparent, iw, ih);
            mask.Draw(new DrawableFillColor(MagickColors.White), new DrawableRoundRectangle(0, 0, iw - 1, ih - 1, radius, radius));
            picture.Composite(mask, CompositeOperator.DstIn);
        }

        if (Shadow > 0)
        {
            var sigma = Math.Max(0.5, Shadow * scale / 2);
            var margin = (int)Math.Ceiling(sigma * 3);
            var offsetY = (int)Math.Round(Shadow * 0.3 * scale);
            var width = iw + (uint)(2 * margin);
            var height = ih + (uint)(2 * margin);
            using var layer = new MagickImage(MagickColors.Transparent, width, height);
            using var silhouette = new MagickImage(MagickColors.Black, iw, ih);
            silhouette.Alpha(AlphaAction.Set);
            silhouette.Composite(picture, CompositeOperator.DstIn);
            layer.Composite(silhouette, margin, margin, CompositeOperator.Over);
            ImageOps.SoftBlur(layer, sigma);
            layer.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.35);
            result.Composite(layer, ix - margin, iy - margin + offsetY, CompositeOperator.Over);
        }

        result.Composite(picture, ix, iy, CompositeOperator.Over);
        return result;
    }

    /// <summary>A small square preview of a background, for the swatch row.</summary>
    public static MagickImage Swatch(BackgroundPaint paint, uint pixels = 56) => paint.Make(pixels, pixels);
}

/// <summary>Saves copies on their backgrounds next to the originals ("Name With Background.png").</summary>
public static class BackgroundOperation
{
    public const string Suffix = "With Background";

    public static string Save(string path, BackgroundComposition composition)
    {
        using var image = ImageFiles.ReadUpright(path);
        using var rendered = composition.Render(image, new Size(image.Width, image.Height), 1);
        return ImageFiles.SaveNext(rendered, path, Suffix, MagickFormat.Png);
    }

    /// <summary>Saves each image on its background; images it would not change are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, Size Size, bool IsTransparent, BackgroundComposition Composition)> jobs,
        IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(jobs, new SaveOutcome { SavedLabel = "saved", UnchangedLabel = "unchanged" },
            job => job.Composition.ChangesImage(job.Size, job.IsTransparent) ? Save(job.Path, job.Composition) : null, progress, cancellation);
}
