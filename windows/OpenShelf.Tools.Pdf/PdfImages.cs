using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace OpenShelf.Tools.Pdf;

/// <summary>Turns image files into PDF pages and builds image XObjects.</summary>
internal static class PdfImages
{
    /// <summary>Adds a page the size of the image at its stated resolution (72 dpi when it states none). A plain JPEG is
    /// embedded as it is; anything else is stored losslessly (Flate), with transparency as a soft mask.</summary>
    public static void AddImagePage(PdfDocument document, string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw PdfToolException.UnreadableImage(path, e); }

        PdfDictionary image;
        double widthPt, heightPt;
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(data));
            if (codec is null) throw PdfToolException.UnreadableImage(path);
            var info = codec.Info;
            var (dpiX, dpiY) = Dpi(data, codec.EncodedFormat);
            var origin = codec.EncodedOrigin;
            var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            int pixelsWide = turned ? info.Height : info.Width, pixelsHigh = turned ? info.Width : info.Height;
            widthPt = pixelsWide * 72.0 / dpiX;
            heightPt = pixelsHigh * 72.0 / dpiY;

            if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg && origin == SKEncodedOrigin.TopLeft && JpegComponents(data) is 1 or 3)
            {
                image = JpegXObject(document, data, info.Width, info.Height, JpegComponents(data));
            }
            else
            {
                using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
                    ?? throw PdfToolException.UnreadableImage(path);
                using var upright = Orient(bitmap, origin);
                image = FlateXObject(document, upright);
            }
        }
        catch (PdfToolException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException) { throw PdfToolException.UnreadableImage(path, e); }

        AddPage(document, image, widthPt, heightPt);
    }

    private static void AddPage(PdfDocument document, PdfDictionary image, double widthPt, double heightPt)
    {
        // PDF pages are limited to 14400 pt (200 in) a side; scale huge scans down to fit.
        var fit = Math.Min(1.0, 14400.0 / Math.Max(widthPt, heightPt));
        widthPt *= fit;
        heightPt *= fit;
        var page = document.AddPage();
        page.Width = widthPt;
        page.Height = heightPt;
        var resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(document);
        page.Elements["/Resources"] = resources;
        var xobjects = new PdfDictionary(document);
        xobjects.Elements["/Im0"] = image.Reference!;
        resources.Elements["/XObject"] = xobjects;
        var content = FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q");
        var stream = new PdfDictionary(document);
        document.Internals.AddObject(stream);
        stream.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
        page.Elements["/Contents"] = stream.Reference!;
    }

    public static PdfDictionary JpegXObject(PdfDocument document, byte[] jpeg, int width, int height, int components)
    {
        var image = NewImage(document, width, height, components == 1 ? "/DeviceGray" : "/DeviceRGB");
        image.Elements["/Filter"] = new PdfName("/DCTDecode");
        image.CreateStream(jpeg);
        return image;
    }

    /// <summary>A lossless RGB image (Flate) with a soft mask when any pixel is not opaque. <paramref name="bitmap"/> must be unpremultiplied RGBA.</summary>
    public static PdfDictionary FlateXObject(PdfDocument document, SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var translucent = false;
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0, p = 0, a = 0; a < alpha.Length; a++, i += 4)
        {
            rgb[p++] = pixels[i];
            rgb[p++] = pixels[i + 1];
            rgb[p++] = pixels[i + 2];
            alpha[a] = pixels[i + 3];
            if (pixels[i + 3] != 255) translucent = true;
        }
        var image = NewImage(document, width, height, "/DeviceRGB");
        image.Elements["/Filter"] = new PdfName("/FlateDecode");
        image.CreateStream(Deflate(rgb));
        if (translucent)
        {
            var mask = NewImage(document, width, height, "/DeviceGray");
            mask.Elements["/Filter"] = new PdfName("/FlateDecode");
            mask.CreateStream(Deflate(alpha));
            image.Elements["/SMask"] = mask.Reference!;
        }
        return image;
    }

    private static PdfDictionary NewImage(PdfDocument document, int width, int height, string colorSpace)
    {
        var image = new PdfDictionary(document);
        document.Internals.AddObject(image);
        image.Elements["/Type"] = new PdfName("/XObject");
        image.Elements["/Subtype"] = new PdfName("/Image");
        image.Elements.SetInteger("/Width", width);
        image.Elements.SetInteger("/Height", height);
        image.Elements["/ColorSpace"] = new PdfName(colorSpace);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        return image;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>The bitmap turned upright according to its EXIF-style origin.</summary>
    public static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return source.Copy();
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = turned ? source.Height : source.Width;
        var height = turned ? source.Width : source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// <summary>Number of colour components of a JPEG (1 grey, 3 colour, 4 CMYK), or 0 when it cannot be told.</summary>
    public static int JpegComponents(byte[] d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            if (marker == 0xFF) { i++; continue; }
            var length = (d[i + 2] << 8) | d[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return d[i + 9];
            i += 2 + length;
        }
        return 0;
    }

    /// <summary>Stated resolution from a PNG pHYs chunk or JFIF density; 72 when absent or implausible.</summary>
    public static (double X, double Y) Dpi(byte[] d, SKEncodedImageFormat format)
    {
        double x = 72, y = 72;
        try
        {
            if (format == SKEncodedImageFormat.Png)
            {
                var i = 8;
                while (i + 12 <= d.Length)
                {
                    var length = (int)(((uint)d[i] << 24) | ((uint)d[i + 1] << 16) | ((uint)d[i + 2] << 8) | d[i + 3]);
                    var type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
                    if (type == "pHYs" && length >= 9 && d[i + 16] == 1)
                    {
                        double px = (double)(((uint)d[i + 8] << 24) | ((uint)d[i + 9] << 16) | ((uint)d[i + 10] << 8) | d[i + 11]);
                        double py = (double)(((uint)d[i + 12] << 24) | ((uint)d[i + 13] << 16) | ((uint)d[i + 14] << 8) | d[i + 15]);
                        x = px * 0.0254;
                        y = py * 0.0254;
                        break;
                    }
                    if (type is "IDAT" or "IEND") break;
                    i += 12 + length;
                }
            }
            else if (format == SKEncodedImageFormat.Jpeg && d.Length > 20 && d[2] == 0xFF && d[3] == 0xE0 && d[6] == 'J' && d[7] == 'F')
            {
                var units = d[13];
                double dx = (d[14] << 8) | d[15], dy = (d[16] << 8) | d[17];
                if (units == 1) { x = dx; y = dy; }
                else if (units == 2) { x = dx * 2.54; y = dy * 2.54; }
            }
        }
        catch { /* keep 72 */ }
        if (x < 10 || x > 4800 || double.IsNaN(x)) x = 72;
        if (y < 10 || y > 4800 || double.IsNaN(y)) y = 72;
        return (x, y);
    }
}
