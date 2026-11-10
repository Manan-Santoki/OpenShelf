using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Ui;

namespace OpenShelf.Settings;

/// <summary>A small vector wheel (ring, six spokes, hub dot) that takes the colour of its surroundings. Used as the Wheels tab icon.</summary>
internal sealed class WheelIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(WheelIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public WheelIcon(double size)
    {
        Width = Height = size;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = (Brush)GetValue(ForegroundProperty);
        Draw(dc, brush, ActualWidth);
    }

    /// <summary>Draws the wheel filling a square of <paramref name="size"/>.</summary>
    public static void Draw(DrawingContext dc, Brush brush, double size)
    {
        var c = new Point(size / 2, size / 2);
        var stroke = Math.Max(1, size * 0.075);
        var ring = size / 2 - stroke / 2 - size * 0.02;
        var pen = new Pen(brush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawEllipse(null, pen, c, ring, ring);
        var hub = size * 0.13;
        for (var i = 0; i < 6; i++)
        {
            var a = i * Math.PI / 3;
            var d = new Vector(Math.Sin(a), -Math.Cos(a));
            dc.DrawLine(pen, c + d * (hub + stroke), c + d * (ring - stroke * 0.6));
        }
        dc.DrawEllipse(brush, null, c, hub * 0.8, hub * 0.8);
    }
}

/// <summary>The window icon, drawn in code at whatever size is asked for: a white ring and centre dot on the accent colour.</summary>
internal static class AppIcon
{
    private const int WM_SETICON = 0x0080;

    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var r = size * 0.22;
            dc.DrawRoundedRectangle(Theme.Brush(Theme.Accent), null, new Rect(0, 0, size, size), r, r);
            var pad = size * 0.2;
            dc.PushTransform(new TranslateTransform(pad, pad));
            WheelIcon.Draw(dc, Brushes.White, size - 2 * pad);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Sets the title-bar and taskbar icons from real renderings at the sizes Windows asks for.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        SetIcon(handle, 0, (int)Math.Round(16 * dpi));
        SetIcon(handle, 1, (int)Math.Round(32 * dpi));
    }

    private static void SetIcon(IntPtr hwnd, int kind, int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();
        var icon = CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, size, size, 0);
        if (icon != IntPtr.Zero) SendMessageW(hwnd, WM_SETICON, (IntPtr)kind, icon);
    }
}

