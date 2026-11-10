using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenShelf.Ui;

/// <summary>
/// For the demo executables: when the environment variable OPENSHELF_DEMO_DPI is set (1.25, 1.5, 2), a window is rendered to a PNG as if the
/// monitor ran at that scaling instead of being copied from the screen. Layout rounding, text metrics and icon snapping follow that scale,
/// which is how clipping that only shows at 125 or 150 % is reproduced on a 100 % monitor.
/// </summary>
public static class OffscreenShot
{
    public static bool TryCapture(Window window, string file)
    {
        var text = Environment.GetEnvironmentVariable("OPENSHELF_DEMO_DPI");
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale <= 0)
            return false;
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        window.InvalidateMeasure();
        window.UpdateLayout();
        var width = (int)Math.Round(window.ActualWidth * scale);
        var height = (int)Math.Round(window.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        encoder.Save(stream);
        return true;
    }
}
