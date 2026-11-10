using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

/// <summary>Moves pixels between Magick.NET images and WPF bitmaps.</summary>
public static class MagickWpf
{
    /// <summary>A frozen BGRA bitmap of <paramref name="image"/> (not modified).</summary>
    public static BitmapSource ToBitmap(IMagickImage<byte> image)
    {
        using var copy = image.Clone();
        ToSrgb(copy);
        if (!copy.HasAlpha) copy.Alpha(AlphaAction.Opaque);
        var width = (int)copy.Width;
        var height = (int)copy.Height;
        var bytes = copy.GetPixels().ToByteArray(PixelMapping.BGRA) ?? new byte[width * height * 4];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Converts CMYK, grey and other colour spaces to sRGB so they show correctly.</summary>
    public static void ToSrgb(IMagickImage<byte> image)
    {
        try
        {
            if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Gray or ColorSpace.Lab or ColorSpace.XYZ or ColorSpace.YCbCr)
                image.TransformColorSpace(ColorProfiles.SRGB);
        }
        catch (MagickException) { }
    }

    /// <summary>A Magick image from a WPF bitmap (any pixel format).</summary>
    public static MagickImage FromBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var bytes = new byte[width * height * 4];
        converted.CopyPixels(bytes, width * 4, 0);
        if (converted.Format == PixelFormats.Pbgra32) Unpremultiply(bytes);
        var image = new MagickImage();
        image.ReadPixels(bytes, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        return image;
    }

    /// <summary>Renders what <paramref name="draw"/> paints, <paramref name="width"/> by <paramref name="height"/> pixels, onto transparency.</summary>
    public static MagickImage Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return FromBitmap(target);
    }

    private static void Unpremultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 0 || a == 255) continue;
            bgra[i] = (byte)Math.Min(255, (bgra[i] * 255 + a / 2) / a);
            bgra[i + 1] = (byte)Math.Min(255, (bgra[i + 1] * 255 + a / 2) / a);
            bgra[i + 2] = (byte)Math.Min(255, (bgra[i + 2] * 255 + a / 2) / a);
        }
    }
}
