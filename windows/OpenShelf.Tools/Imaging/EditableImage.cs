using System.Windows.Media.Imaging;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

/// <summary>An image file opened for an editor: its upright size, a screen-sized preview and a thumbnail. The full-resolution
/// pixels are read again from the file when a save needs them, so a long selection does not hold every photo in memory.</summary>
public sealed class EditableImage
{
    public const int PreviewLongSide = 1600;

    private EditableImage(string path, int width, int height, MagickFormat format, bool hasAlpha, bool isTransparent, bool isAnimated,
        BitmapSource preview, BitmapSource thumbnail, long fileBytes)
    {
        Path = path;
        Width = width;
        Height = height;
        Format = format;
        HasAlpha = hasAlpha;
        IsTransparent = isTransparent;
        IsAnimated = isAnimated;
        Preview = preview;
        Thumbnail = thumbnail;
        FileBytes = fileBytes;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>Upright pixel size.</summary>
    public int Width { get; }
    public int Height { get; }
    public MagickFormat Format { get; }
    public bool HasAlpha { get; }
    /// <summary>Whether some pixel is see-through.</summary>
    public bool IsTransparent { get; }
    public bool IsAnimated { get; }
    public long FileBytes { get; }
    /// <summary>Upright pixels, at most <see cref="PreviewLongSide"/> on the long side.</summary>
    public BitmapSource Preview { get; }
    public BitmapSource Thumbnail { get; }

    public System.Windows.Size PixelSize => new(Width, Height);

    public static EditableImage Load(string path)
    {
        using var image = ImageFiles.ReadUpright(path);
        var format = ImageFiles.FormatOf(path);
        var animated = false;
        if (ImageFiles.Family(format) is MagickFormat.Gif or MagickFormat.WebP)
        {
            try
            {
                using var frames = new MagickImageCollection();
                frames.Ping(path);
                animated = frames.Count > 1;
            }
            catch (MagickException) { }
        }
        var transparent = ImageFiles.IsTransparent(image);
        var hasAlpha = image.HasAlpha;
        var width = (int)image.Width;
        var height = (int)image.Height;
        using var preview = Scaled(image, PreviewLongSide);
        using var thumb = Scaled(preview, 160);
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }
        return new EditableImage(path, width, height, format, hasAlpha, transparent, animated,
            MagickWpf.ToBitmap(preview), MagickWpf.ToBitmap(thumb), size);
    }

    /// <summary>A copy no larger than <paramref name="longSide"/> pixels on its long side.</summary>
    public static MagickImage Scaled(IMagickImage<byte> image, int longSide)
    {
        var copy = (MagickImage)image.Clone();
        var longest = Math.Max(copy.Width, copy.Height);
        if (longest > longSide)
        {
            var scale = (double)longSide / longest;
            copy.Resize((uint)Math.Max(1, Math.Round(copy.Width * scale)), (uint)Math.Max(1, Math.Round(copy.Height * scale)));
        }
        return copy;
    }
}
