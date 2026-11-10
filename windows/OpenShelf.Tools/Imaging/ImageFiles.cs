using ImageMagick;
using OpenShelf.Core;

namespace OpenShelf.Tools.Imaging;

/// <summary>Reading images upright and writing new copies beside the original without overwriting anything.
/// Port of EditableImage.swift's load and save, on Magick.NET.</summary>
public static class ImageFiles
{
    /// <summary>Reads the first frame of <paramref name="path"/> with its EXIF turn applied, so the pixels are upright.</summary>
    public static MagickImage ReadUpright(string path)
    {
        try
        {
            var image = Read(path);
            image.AutoOrient();
            ResetOrientation(image);
            return image;
        }
        catch (MagickException e)
        {
            throw new IOException($"“{Path.GetFileName(path)}” couldn’t be opened as an image.", e);
        }
    }

    /// <summary>Reads the first frame. Vector pictures (SVG) are drawn at a useful size: their own, scaled so the long side is 1024 to 4096 px.</summary>
    private static MagickImage Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)) return new MagickImage(path);
        var info = new MagickImageInfo(path);
        var longest = Math.Max(info.Width, info.Height);
        var scale = longest == 0 ? 1 : Math.Min(Math.Max(1, 1024.0 / longest), 4096.0 / longest);
        return new MagickImage(path, new MagickReadSettings { Density = new Density(96 * scale) });
    }

    /// <summary>Marks the pixels as upright, in the image and in its EXIF profile, so viewers do not turn them again.</summary>
    public static void ResetOrientation(IMagickImage<byte> image)
    {
        image.Orientation = Orientation.TopLeft;
        try
        {
            if (image.GetExifProfile() is { } exif)
            {
                if (exif.GetValue(ExifTag.Orientation) is not null) exif.SetValue(ExifTag.Orientation, (ushort)1);
                image.SetProfile(exif);
            }
        }
        catch (MagickException) { }
    }

    /// <summary>Updates the pixel size EXIF records, which an edit has made stale.</summary>
    public static void UpdateExifSize(IMagickImage<byte> image)
    {
        try
        {
            if (image.GetExifProfile() is not { } exif) return;
            var changed = false;
            if (exif.GetValue(ExifTag.PixelXDimension) is not null) { exif.SetValue(ExifTag.PixelXDimension, new Number(image.Width)); changed = true; }
            if (exif.GetValue(ExifTag.PixelYDimension) is not null) { exif.SetValue(ExifTag.PixelYDimension, new Number(image.Height)); changed = true; }
            if (changed) image.SetProfile(exif);
        }
        catch (MagickException) { }
    }

    /// <summary>The format family of a file: JPEG variants all count as <see cref="MagickFormat.Jpeg"/> and so on.</summary>
    public static MagickFormat Family(MagickFormat format) => format switch
    {
        MagickFormat.Jpg or MagickFormat.Jpeg or MagickFormat.Pjpeg or MagickFormat.Jpe => MagickFormat.Jpeg,
        MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png00 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 => MagickFormat.Png,
        MagickFormat.Gif or MagickFormat.Gif87 => MagickFormat.Gif,
        MagickFormat.Tif or MagickFormat.Tiff or MagickFormat.Tiff64 or MagickFormat.Ptif => MagickFormat.Tiff,
        MagickFormat.Bmp or MagickFormat.Bmp2 or MagickFormat.Bmp3 => MagickFormat.Bmp,
        MagickFormat.Heif => MagickFormat.Heic,
        _ => format,
    };

    /// <summary>Whether the bundled ImageMagick can write <paramref name="format"/> (no file is touched).</summary>
    public static bool CanWrite(MagickFormat format)
    {
        var family = Family(format);
        return MagickNET.SupportedFormats.Any(info => info.Format == family && info.SupportsWriting);
    }

    /// <summary>Picture formats a copy may stay in. Others the encoder can technically write (SVG, PDF, ICO, PSD) turn into PNG.</summary>
    private static readonly HashSet<MagickFormat> Preserved =
        [MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.Gif, MagickFormat.Tiff, MagickFormat.Bmp, MagickFormat.WebP, MagickFormat.Heic, MagickFormat.Avif];

    /// <summary>The format a copy is written in: the original's when it can be written, else PNG.</summary>
    public static MagickFormat OutputFormat(MagickFormat source)
    {
        var family = Family(source);
        return Preserved.Contains(family) && CanWrite(family) ? family : MagickFormat.Png;
    }

    /// <summary>The extension for <paramref name="format"/>, keeping the original's own spelling when the format is unchanged.</summary>
    public static string Extension(string sourcePath, MagickFormat sourceFormat, MagickFormat format)
    {
        var original = Path.GetExtension(sourcePath).TrimStart('.');
        if (Family(format) == Family(sourceFormat) && original.Length > 0) return original;
        return Family(format) switch
        {
            MagickFormat.Jpeg => "jpg",
            MagickFormat.Tiff => "tif",
            var other => other.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>The format of the file at <paramref name="path"/>, from its contents.</summary>
    public static MagickFormat FormatOf(string path)
    {
        try { return new MagickImageInfo(path).Format; }
        catch (MagickException) { return MagickFormat.Unknown; }
    }

    /// <summary>JPEG and BMP have no transparency: flatten onto white for them.</summary>
    public static void FlattenIfNeeded(IMagickImage<byte> image, MagickFormat format)
    {
        if (Family(format) is MagickFormat.Jpeg or MagickFormat.Bmp && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            image.Alpha(AlphaAction.Off);
        }
    }

    /// <summary>
    /// Saves <paramref name="image"/> next to <paramref name="sourcePath"/> as "name suffix.ext", never over an existing
    /// file. Returns the new path.
    /// </summary>
    /// <param name="format">Output format; defaults to the original's when it can be written, otherwise PNG.</param>
    /// <param name="quality">Lossy quality 1..100 for JPEG and WebP; null keeps what the image carries.</param>
    /// <param name="sourceFormat">The original's format, when the caller already knows it.</param>
    public static string SaveNext(IMagickImage<byte> image, string sourcePath, string suffix, MagickFormat? format = null,
        uint? quality = null, MagickFormat? sourceFormat = null)
    {
        var original = sourceFormat ?? FormatOf(sourcePath);
        var output = format is { } f ? Family(f) : OutputFormat(original);
        var extension = Extension(sourcePath, original, output);
        for (var attempt = 0; ; attempt++)
        {
            var destination = OutputNaming.Destination(sourcePath, suffix, extension);
            SaveException.CheckWritable(destination);
            FileStream stream;
            try
            {
                stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(destination) && attempt < 8)
            {
                continue;   // someone else made that name first: pick the next free one
            }
            catch (Exception e) when (e is not SaveException)
            {
                throw SaveException.From(e, destination);
            }
            try
            {
                using (stream) WriteTo(image, stream, output, quality);
                return destination;
            }
            catch (Exception e)
            {
                TryDelete(destination);
                throw e is MagickException ? SaveException.CouldNotSave(Path.GetFileName(destination)) : SaveException.From(e, destination);
            }
        }
    }

    /// <summary>Writes already-encoded bytes next to the original without overwriting.</summary>
    public static string SaveBytesNext(byte[] data, string sourcePath, string suffix, string extension)
    {
        for (var attempt = 0; ; attempt++)
        {
            var destination = OutputNaming.Destination(sourcePath, suffix, extension);
            SaveException.CheckWritable(destination);
            try
            {
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(data);
                return destination;
            }
            catch (IOException) when (File.Exists(destination) && attempt < 8) { }
            catch (Exception e) when (e is not SaveException)
            {
                TryDelete(destination);
                throw SaveException.From(e, destination);
            }
        }
    }

    /// <summary>Encodes to bytes (for estimating sizes, and for sizes found by trying qualities).</summary>
    public static byte[] Encode(IMagickImage<byte> image, MagickFormat format, uint? quality = null)
    {
        using var stream = new MemoryStream();
        WriteTo(image, stream, format, quality);
        return stream.ToArray();
    }

    private static void WriteTo(IMagickImage<byte> image, Stream stream, MagickFormat format, uint? quality)
    {
        using var copy = image.Clone();
        FlattenIfNeeded(copy, format);
        if (quality is { } q) copy.Quality = q;
        copy.Write(stream, format);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether any pixel is see-through, judged from a downscaled copy (an alpha channel alone does not mean the image uses it).</summary>
    public static bool IsTransparent(IMagickImage<byte> image)
    {
        if (!image.HasAlpha) return false;
        using var small = image.Clone();
        var scale = Math.Min(1.0, 512.0 / Math.Max(small.Width, small.Height));
        if (scale < 1) small.Resize(new Percentage(scale * 100));
        var pixels = small.GetPixels().ToByteArray(PixelMapping.RGBA);
        if (pixels is null) return true;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 255) return true;
        return false;
    }
}
