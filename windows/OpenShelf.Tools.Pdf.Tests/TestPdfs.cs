using PdfSharp.Pdf;
using SkiaSharp;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace OpenShelf.Tools.Pdf.Tests;

/// <summary>A temp folder that goes away with the test, and builders for real PDFs and images.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "os-pdf-" + Guid.NewGuid().ToString("N")[..8]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public IEnumerable<string> Files() => Directory.EnumerateFileSystemEntries(Path).Select(System.IO.Path.GetFileName)!;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }

    /// <summary>A PDF with one page per text, each saying its text in Helvetica. Sizes are US Letter unless given.</summary>
    public string MakePdf(string name, params string[] texts) => MakePdfWithTitle(name, null, texts);

    public string MakePdfWithTitle(string name, string? title, params string[] texts)
    {
        var path = File(name);
        using var document = new PdfDocument();
        foreach (var text in texts)
        {
            var page = document.AddPage();
            page.Width = 612;
            page.Height = 792;
            var font = new PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Helvetica");
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font;
            var resources = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            var content = new PdfDictionary(document);
            document.Internals.AddObject(content);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes($"BT /F1 36 Tf 72 700 Td ({text}) Tj ET"));
            page.Elements["/Contents"] = content.Reference!;
        }
        if (title is not null) document.Info.Title = title;
        document.Save(path);
        return path;
    }

    public string MakePng(string name, int width, int height, bool noise = false, bool transparent = false)
    {
        var path = File(name);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var random = new Random(7);
        var pixels = bitmap.GetPixelSpan();
        var row = new byte[4];
        for (var i = 0; i < width * height; i++)
        {
            if (noise)
            {
                // smooth gradient plus mild noise: compresses well as JPEG, badly as lossless
                var x = i % width; var y = i / width;
                pixels[i * 4] = (byte)Math.Clamp(x * 255 / width + random.Next(-12, 12), 0, 255);
                pixels[i * 4 + 1] = (byte)Math.Clamp(y * 255 / height + random.Next(-12, 12), 0, 255);
                pixels[i * 4 + 2] = (byte)Math.Clamp(128 + random.Next(-12, 12), 0, 255);
            }
            else { pixels[i * 4] = 200; pixels[i * 4 + 1] = 30; pixels[i * 4 + 2] = 30; }
            pixels[i * 4 + 3] = (byte)(transparent && (i % width) < width / 2 ? 0 : 255);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        System.IO.File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public string MakeJpeg(string name, int width, int height)
    {
        var path = File(name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(10, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        System.IO.File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>Rewrites a PNG so it states <paramref name="dpi"/> (a pHYs chunk after IHDR).</summary>
    public static string WithDpi(string pngPath, int dpi)
    {
        var data = System.IO.File.ReadAllBytes(pngPath);
        var perMetre = (int)Math.Round(dpi / 0.0254);
        var body = new List<byte>();
        body.AddRange("pHYs"u8.ToArray());
        foreach (var v in new[] { perMetre, perMetre }) body.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        body.Add(1);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        crc ^= 0xFFFFFFFFu;
        var chunk = new List<byte> { 0, 0, 0, 9 };
        chunk.AddRange(body);
        chunk.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        var insertAt = 8 + 12 + 13; // signature + IHDR chunk
        var result = data[..insertAt].Concat(chunk).Concat(data[insertAt..]).ToArray();
        System.IO.File.WriteAllBytes(pngPath, result);
        return pngPath;
    }

    public static string[] TextOf(string pdf)
    {
        using var document = Pig.Open(pdf);
        return document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))).ToArray();
    }

    public static int PageCount(string pdf)
    {
        using var document = Pig.Open(pdf);
        return document.NumberOfPages;
    }
}
