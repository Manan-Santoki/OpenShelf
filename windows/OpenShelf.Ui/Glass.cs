using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace OpenShelf.Ui;

/// <summary>
/// Frosted glass for short-lived overlays. Windows cannot blur behind a
/// per-pixel-transparent window with soft edges, so we photograph the screen
/// under the overlay just before it appears and show that photo blurred inside
/// it. Fine for something on screen for a second or two (the wheel, toasts).
/// </summary>
public static class Glass
{
    /// <summary>Copies a screen rectangle given in PHYSICAL pixels. Returns null if the capture fails.</summary>
    public static BitmapSource? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            var ok = BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY);
            SelectObject(memory, previous);
            if (!ok) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// An element showing <paramref name="snapshot"/> blurred, sized <paramref name="width"/> x <paramref name="height"/> DIPs.
    /// Capture a little more than you show and clip the parent, because a blur fades out at its own edges.
    /// </summary>
    public static FrameworkElement Backdrop(BitmapSource snapshot, double width, double height, double blurRadius = 28)
    {
        var image = new Image
        {
            Source = snapshot,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Effect = new BlurEffect { Radius = blurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            CacheMode = new BitmapCache(),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
        return image;
    }

    private const int SRCCOPY = 0x00CC0020;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
