using ImageMagick;
using OpenShelf.Core;
using OpenShelf.Vision;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ZXing;
using ZXing.Common;

namespace OpenShelf.Tools.VisionEditors.Tests;

/// <summary>A fact skipped (with the reason) when this PC has no Windows OCR language.</summary>
public sealed class FactWhenOcrAttribute : FactAttribute
{
    public FactWhenOcrAttribute()
    {
        if (!OcrService.IsAvailable) Skip = "No Windows OCR language is installed on this machine.";
    }
}

/// <summary>A scratch folder that goes away with the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshelf-visioneditors-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => Directory.GetFileSystemEntries(Path).Select(System.IO.Path.GetFileName).Order().ToArray()!;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Pictures for the tests, made with ImageMagick and ZXing.</summary>
public static class Pictures
{
    /// <summary>A dark red ellipse with a lighter core on a plain light grey backdrop: the model finds a subject.</summary>
    public static string SubjectOnPlain(string path, int width = 640, int height = 480)
    {
        using var image = new MagickImage(new MagickColor("#EBEBEB"), (uint)width, (uint)height);
        image.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor("#AA1E28")),
            new ImageMagick.Drawing.DrawableEllipse(width / 2.0, height / 2.0, width * 0.28, height * 0.36, 0, 360));
        image.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor("#E6783C")),
            new ImageMagick.Drawing.DrawableEllipse(width / 2.0, height / 2.0, width * 0.12, height * 0.16, 0, 360));
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A flat grey picture: no subject.</summary>
    public static string Plain(string path, int width = 640, int height = 480)
    {
        using var image = new MagickImage(new MagickColor("#C8CDD2"), (uint)width, (uint)height);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A QR code on white, quiet zone included.</summary>
    public static string Qr(string path, string text, int module = 8)
    {
        var matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 });
        const int quiet = 4;
        var width = (matrix.Width + 2 * quiet) * module;
        var height = (matrix.Height + 2 * quiet) * module;
        var gray = new byte[width * height * 3];
        Array.Fill(gray, (byte)255);
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                if (matrix[x, y])
                    for (var dy = 0; dy < module; dy++)
                        for (var dx = 0; dx < module; dx++)
                            {
                            var o = (((y + quiet) * module + dy) * width + (x + quiet) * module + dx) * 3;
                            gray[o] = gray[o + 1] = gray[o + 2] = 0;
                        }
        using var image = new MagickImage(gray, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A white picture with large black lines of text (what OCR is given).</summary>
    public static string Text(string path, params string[] lines)
    {
        using var image = new MagickImage(MagickColors.White, 1400, (uint)(120 + lines.Length * 110));
        for (var i = 0; i < lines.Length; i++)
            image.Draw(new ImageMagick.Drawing.DrawableFont("Arial"), new ImageMagick.Drawing.DrawableFontPointSize(56),
                new ImageMagick.Drawing.DrawableFillColor(MagickColors.Black), new ImageMagick.Drawing.DrawableText(60, 110 + i * 110, lines[i]));
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A PDF whose single page is a picture of text, with no text layer: a scan.</summary>
    public static string ScannedPdf(string path, string pictureOfText)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var graphics = XGraphics.FromPdfPage(page))
        using (var picture = XImage.FromFile(pictureOfText))
            graphics.DrawImage(picture, 0, 0, page.Width.Point, page.Width.Point * picture.PixelHeight / picture.PixelWidth);
        document.Save(path);
        return path;
    }

    public static (uint Width, uint Height) SizeOf(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    public static IMagickColor<byte> At(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }
}

public sealed class FakeToasts : IToastService
{
    public List<string> Messages { get; } = [];
    public void Show(string message, ScreenPoint near) => Messages.Add(message);
}

public sealed class FakeShelves : IShelfService
{
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) { }
    public void Flush() { }
}

public sealed class FakeProgress : IProgressService
{
    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel) => new Card();

    private sealed class Card : IProgressCard
    {
        public void Report(int completed, double? fraction = null, string? detail = null) { }
        public void Finish(string message) { }
        public void Dispose() { }
    }
}
