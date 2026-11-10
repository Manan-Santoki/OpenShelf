using System.IO.Compression;
using SkiaSharp;

namespace OpenShelf.Convert.Documents;

/// <summary>Decoding and encoding of the pictures that documents carry (SkiaSharp).</summary>
public static class Pictures
{
    /// <summary>A picture as a block: PNG, JPEG and GIF pass through, everything else Skia can decode is re-encoded as PNG.
    /// Null when the bytes are not a picture.</summary>
    public static ImageBlock? Block(byte[] data, string alt = "", int? width = null, int? height = null)
    {
        try
        {
            using var stream = new SKMemoryStream(data);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var info = codec.Info;
            var extension = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Png => "png",
                SKEncodedImageFormat.Jpeg => "jpeg",
                SKEncodedImageFormat.Gif => "gif",
                _ => null,
            };
            if (extension is null)
            {
                using var bitmap = SKBitmap.Decode(data);
                if (bitmap is null) return null;
                using var image = SKImage.FromBitmap(bitmap);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                return new ImageBlock(png.ToArray(), "png", width ?? info.Width, height ?? info.Height, alt);
            }
            return new ImageBlock(data, extension, width ?? info.Width, height ?? info.Height, alt);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap, double quality)
    {
        // JPEG has no alpha: flatten on white.
        using var flat = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            using var source = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(), null);
        }
        using var image = SKImage.FromBitmap(flat);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, (int)Math.Round(Math.Clamp(quality, 0, 1) * 100));
        return data.ToArray();
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>Small helpers for building zip packages.</summary>
public static class Zips
{
    public static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal) =>
        Add(zip, name, new System.Text.UTF8Encoding(false).GetBytes(content), level);

    public static void Add(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(content);
    }

    public static byte[]? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void DeleteFolderQuietly(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
