using ImageMagick;
using OpenShelf.Convert;
using SkiaSharp;

namespace OpenShelf.Vision;

/// <summary>Decoded pixels: straight-alpha BGRA, upright (EXIF orientation applied).</summary>
internal sealed class PixelImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Width x Height x 4 bytes, B G R A.</summary>
    public byte[] Bgra { get; }
    /// <summary>Pixels per inch as stored in the file (72 when unknown).</summary>
    public double Dpi { get; }

    public PixelImage(int width, int height, byte[] bgra, double dpi = 72)
    {
        Width = width; Height = height; Bgra = bgra; Dpi = dpi > 1 ? dpi : 72;
    }

    /// <summary>Reads any image format ImageMagick understands. Throws <see cref="ConversionException"/> for damaged files.</summary>
    public static PixelImage Load(string path)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            if (image.ColorSpace is ColorSpace.CMYK)
                image.TransformColorSpace(ColorProfiles.SRGB);
            var dpi = 72.0;
            if (image.Density is { X: > 1 } density)
                dpi = density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
            var bytes = image.GetPixelsUnsafe().ToByteArray(PixelMapping.BGRA) ?? throw ConversionException.Unreadable(path);
            return new PixelImage((int)image.Width, (int)image.Height, bytes, dpi);
        }
        catch (MagickException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    public static PixelImage FromBitmap(SKBitmap bitmap, double dpi = 72)
    {
        SKBitmap? converted = null;
        try
        {
            if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType == SKAlphaType.Premul)
            {
                converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                if (!bitmap.CopyTo(converted, SKColorType.Bgra8888)) throw new InvalidOperationException("Could not convert the bitmap.");
            }
            var source = converted ?? bitmap;
            var width = source.Width;
            var height = source.Height;
            var bytes = new byte[width * height * 4];
            var span = source.GetPixelSpan();
            var stride = source.RowBytes;
            for (var y = 0; y < height; y++)
                span.Slice(y * stride, width * 4).CopyTo(bytes.AsSpan(y * width * 4));
            return new PixelImage(width, height, bytes, dpi);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>The picture over white, fully opaque. Recognition and JPEG have no transparency.</summary>
    public PixelImage OnWhite()
    {
        var copy = new byte[Bgra.Length];
        for (var i = 0; i < Bgra.Length; i += 4)
        {
            var a = Bgra[i + 3];
            for (var c = 0; c < 3; c++)
                copy[i + c] = a == 255 ? Bgra[i + c] : (byte)((Bgra[i + c] * a + 255 * (255 - a) + 127) / 255);
            copy[i + 3] = 255;
        }
        return new PixelImage(Width, Height, copy, Dpi);
    }

    /// <summary>A copy no larger than <paramref name="maxSide"/> on its longest side (the same image when already smaller).</summary>
    public PixelImage Fit(int maxSide)
    {
        if (Math.Max(Width, Height) <= maxSide) return this;
        var scale = (double)maxSide / Math.Max(Width, Height);
        return Resize(Math.Max((int)(Width * scale), 1), Math.Max((int)(Height * scale), 1));
    }

    public PixelImage Resize(int width, int height)
    {
        using var source = ToBitmap();
        using var target = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("Resize failed.");
        return FromBitmap(target, Dpi);
    }

    public SKBitmap ToBitmap()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Bgra.CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    /// <summary>JPEG bytes (flattened over white).</summary>
    public byte[] ToJpeg(int quality = 90)
    {
        using var bitmap = OnWhite().ToBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
