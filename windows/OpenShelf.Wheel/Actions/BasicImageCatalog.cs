using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>
/// The built-in image conversion (PNG, JPG, BMP, TIFF, GIF through WPF's codecs). Stands in for the real catalog until
/// OpenShelf.Convert provides one; the Tools wheel is empty ("Tools arrive soon").
/// </summary>
public sealed class BasicImageCatalog(IToastService toasts, IShelfService? shelves = null) : IWheelCatalog
{
    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff"];

    public WheelContent Tools(IReadOnlyList<string> paths) => new([], "Tools arrive soon");

    public WheelContent Convert(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(IsImage)) return new([], "Nothing to convert here yet");
        var present = paths.Select(f => Canonical(Path.GetExtension(f).TrimStart('.'))).ToHashSet();
        var entries = new List<WheelEntry>();
        foreach (var (title, ext) in new[] { ("PNG", "png"), ("JPG", "jpg"), ("BMP", "bmp"), ("TIFF", "tif"), ("GIF", "gif") })
        {
            if (present.Contains(ext)) continue;
            var outExt = ext == "tif" ? "tiff" : ext;
            entries.Add(new WheelEntry("convert." + ext, title, run => ConvertAsync(run, title, outExt)));
        }
        return entries.Count == 0 ? new([], "Nothing to convert here yet") : new(entries);
    }

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    private static string Canonical(string ext) => ext.ToLowerInvariant() switch { "jpeg" => "jpg", "tiff" => "tif", var e => e };

    private async Task ConvertAsync(WheelRun run, string title, string ext)
    {
        var done = 0;
        string? failure = null;
        var results = new List<string>();
        foreach (var path in run.Paths)
        {
            try
            {
                var dest = OutputNaming.Destination(path, null, ext);
                await Task.Run(() => ConvertImage(path, dest, ext));
                results.Add(dest);
                done++;
                Log.Write($"converted {path} -> {dest}");
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log.Error($"convert {path}", ex);
            }
        }
        if (results.Count > 0) shelves?.AddResults(results, run.SourceShelf);
        if (failure is not null)
            toasts.Show(done == 0 ? $"Could not convert: {failure}" : $"Converted {done} of {run.Paths.Count}: {failure}", run.Pointer);
        else
            toasts.Show(done == 1 ? $"Converted to {title}" : $"Converted {done} files to {title}", run.Pointer);
    }

    private static void ConvertImage(string source, string dest, string ext)
    {
        BitmapSource frame;
        using (var input = File.OpenRead(source))
            frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

        BitmapEncoder encoder;
        switch (ext)
        {
            case "png": encoder = new PngBitmapEncoder(); break;
            case "jpg":
                encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                frame = OnWhite(frame); // JPEG has no alpha
                break;
            case "bmp": encoder = new BmpBitmapEncoder(); break;
            case "tiff": encoder = new TiffBitmapEncoder(); break;
            case "gif": encoder = new GifBitmapEncoder(); break;
            default: throw new NotSupportedException(ext);
        }
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = File.Create(dest);
        encoder.Save(output);
    }

    private static BitmapSource OnWhite(BitmapSource src)
    {
        try
        {
            var rect = new Rect(0, 0, src.PixelWidth, src.PixelHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(src, rect);
            }
            var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Log.Error("OnWhite", ex);
            var fc = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            fc.Freeze();
            return fc;
        }
    }
}
