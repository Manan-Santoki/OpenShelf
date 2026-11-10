using System.Text;
using OpenShelf.Vision;
using SkiaSharp;
using ZXing;
using ZXing.QrCode;

namespace OpenShelf.Vision.Tests;

/// <summary>A fact that is reported as skipped (with the reason) when its condition does not hold.</summary>
public sealed class FactWhenAttribute : FactAttribute
{
    public FactWhenAttribute(string condition)
    {
        var reason = condition switch
        {
            "OcrLanguage" => OcrLanguages.IsAvailable ? null : "No Windows OCR language is installed on this machine.",
            "Model" => new ModelStore().IsModelAvailable ? null : "The Remove Background model is not downloaded (run EnsureModelAsync once, or set it in %LOCALAPPDATA%\\OpenShelf\\models).",
            _ => null,
        };
        if (reason is not null) Skip = reason;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-vision-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(System.IO.Path.GetFileName).Order().ToArray()!;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Generate
{
    /// <summary>A white page with black text lines in a large sans-serif font.</summary>
    public static SKBitmap TextBitmap(IReadOnlyList<string> lines, int width = 1400, int fontSize = 56)
    {
        var height = 120 + lines.Count * (fontSize * 2);
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, fontSize);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (var i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i], 60, 100 + i * fontSize * 2, font, paint);
        return bitmap;
    }

    public static string TextImage(string path, IReadOnlyList<string> lines, int width = 1400, int fontSize = 56)
    {
        using var bitmap = TextBitmap(lines, width, fontSize);
        Save(bitmap, path);
        return path;
    }

    public static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        var format = System.IO.Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        using var data = image.Encode(format, 95);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>A QR code of <paramref name="text"/> drawn at <paramref name="module"/> pixels per module, with its quiet zone.</summary>
    public static SKBitmap QrBitmap(string text, int module = 8, bool inverted = false)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
        var bitmap = new SKBitmap(matrix.Width * module, matrix.Height * module);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(inverted ? SKColors.Black : SKColors.White);
        using var paint = new SKPaint { Color = inverted ? SKColors.White : SKColors.Black };
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) canvas.DrawRect(x * module, y * module, module, module, paint);
        return bitmap;
    }

    /// <summary>A white canvas with the given codes placed at (x, y).</summary>
    public static string QrSheet(string path, int width, int height, params (string Text, int X, int Y)[] codes)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        foreach (var (text, x, y) in codes)
        {
            using var code = QrBitmap(text);
            canvas.DrawBitmap(code, x, y);
        }
        Save(bitmap, path);
        return path;
    }

    /// <summary>A dark red ellipse with a lighter core on a plain light grey background.</summary>
    public static string SubjectOnPlain(string path, int width = 640, int height = 480)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(235, 235, 235));
        using var body = new SKPaint { Color = new SKColor(170, 30, 40), IsAntialias = true };
        using var core = new SKPaint { Color = new SKColor(230, 120, 60), IsAntialias = true };
        var cx = width / 2f; var cy = height / 2f;
        canvas.DrawOval(cx, cy, width * 0.28f, height * 0.36f, body);
        canvas.DrawOval(cx, cy, width * 0.12f, height * 0.16f, core);
        Save(bitmap, path);
        return path;
    }

    public static string PlainImage(string path, int width = 640, int height = 480)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(200, 205, 210));
        Save(bitmap, path);
        return path;
    }

    /// <summary>A real PDF whose pages each hold one line of Helvetica text (so every page has extractable text).</summary>
    public static string TextPdf(string path, params string[] pageTexts) => TextPdf(path, false, pageTexts);

    /// <param name="inherit">Put the page size and resources on the Pages node, as many real PDFs do.</param>
    public static string TextPdf(string path, bool inherit, params string[] pageTexts)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = string.Join(" ", pageTexts.Select((_, i) => $"{3 + i * 2} 0 R"));
        var fontRef = 3 + pageTexts.Length * 2;
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageTexts.Length}" + (inherit ? $" /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontRef} 0 R >> >>" : "") + " >>");
        var fontId = 3 + pageTexts.Length * 2;
        for (var i = 0; i < pageTexts.Length; i++)
        {
            var content = $"BT /F1 24 Tf 72 700 Td ({pageTexts[i]}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R" + (inherit ? "" : $" /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R >> >>") + $" /Contents {4 + i * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
        return path;
    }

    /// <summary>A PDF made only of page pictures (a "scan"): one page per line set, no text objects.</summary>
    public static string ScanPdf(string path, params IReadOnlyList<string>[] pages)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        foreach (var lines in pages)
        {
            using var bitmap = TextBitmap(lines, 1275, 48);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(bitmap.Width * 72.0 / 150);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(bitmap.Height * 72.0 / 150);
            using var stream = new MemoryStream(data.ToArray());
            using var picture = PdfSharp.Drawing.XImage.FromStream(stream);
            using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            graphics.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
        }
        document.Save(path);
        return path;
    }
}
