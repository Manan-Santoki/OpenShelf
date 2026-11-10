using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>Builds the wheels' contents (spike set) and implements their actions.</summary>
internal sealed class ActionWheels(IShelfService shelves, IToastService toasts)
{
    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff"];

    /// <summary>The wheel for <paramref name="kind"/> and these files, or null when there is nothing to offer.</summary>
    public WheelModel? Build(WheelKind kind, IReadOnlyList<string> files) => kind switch
    {
        WheelKind.Actions => BuildActions(),
        WheelKind.Convert => BuildConvert(files),
        _ => null,
    };

    public void Unavailable(WheelKind kind, ScreenPoint near)
    {
        if (kind == WheelKind.Convert) toasts.Show("Nothing to convert here yet", near);
    }

    // ---- Actions ----

    private WheelModel BuildActions()
    {
        var items = new List<WheelItem> { new("Shelf", (paths, at) => { shelves.NewShelf(paths, at); return Task.CompletedTask; }) };
        if (shelves.HasOpenShelf)
            items.Add(new WheelItem("Add to shelf", (paths, _) => { shelves.AddToRecentShelf(paths); return Task.CompletedTask; }));
        items.Add(new WheelItem("Copy", (paths, at) =>
        {
            var list = new StringCollection();
            list.AddRange(paths.ToArray());
            WithClipboard(() => Clipboard.SetFileDropList(list));
            toasts.Show(paths.Count == 1 ? "Copied" : $"Copied {paths.Count} files", at);
            return Task.CompletedTask;
        }));
        items.Add(new WheelItem("Copy path", (paths, at) =>
        {
            WithClipboard(() => Clipboard.SetText(string.Join(Environment.NewLine, paths)));
            toasts.Show(paths.Count == 1 ? "Copied path" : $"Copied {paths.Count} paths", at);
            return Task.CompletedTask;
        }));
        items.Add(new WheelItem("Terminal", (paths, _) => { OpenTerminal(paths[0]); return Task.CompletedTask; }));
        return new WheelModel("Actions", items);
    }

    private static void WithClipboard(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (COMException) when (attempt < 4) { Thread.Sleep(40); }
        }
    }

    private static void OpenTerminal(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder);
            Process.Start(psi);
            Log.Write($"terminal: wt.exe in {folder}");
        }
        catch (Exception ex)
        {
            Log.Write($"wt.exe failed ({ex.Message}); falling back to powershell");
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = false });
        }
    }

    // ---- Convert ----

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    private static string Canonical(string ext) => ext.ToLowerInvariant() switch { "jpeg" => "jpg", "tiff" => "tif", var e => e };

    private WheelModel? BuildConvert(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || !files.All(IsImage)) return null;
        var present = files.Select(f => Canonical(Path.GetExtension(f).TrimStart('.'))).ToHashSet();
        var items = new List<WheelItem>();
        foreach (var (title, ext) in new[] { ("PNG", "png"), ("JPG", "jpg"), ("BMP", "bmp"), ("TIFF", "tif"), ("GIF", "gif") })
        {
            if (present.Contains(ext)) continue;
            var outExt = ext == "tif" ? "tiff" : ext;
            items.Add(new WheelItem(title, (paths, at) => ConvertAsync(paths, title, outExt, at)));
        }
        return items.Count == 0 ? null : new WheelModel("Convert", items);
    }

    private async Task ConvertAsync(IReadOnlyList<string> paths, string title, string ext, ScreenPoint at)
    {
        var done = 0;
        string? failure = null;
        foreach (var path in paths)
        {
            try
            {
                var dest = OutputNaming.Destination(path, null, ext);
                await Task.Run(() => ConvertImage(path, dest, ext));
                done++;
                Log.Write($"converted {path} -> {dest}");
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log.Error($"convert {path}", ex);
            }
        }
        if (failure is not null)
            toasts.Show(done == 0 ? $"Could not convert: {failure}" : $"Converted {done} of {paths.Count}: {failure}", at);
        else
            toasts.Show(done == 1 ? $"Converted to {title}" : $"Converted {done} files to {title}", at);
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
