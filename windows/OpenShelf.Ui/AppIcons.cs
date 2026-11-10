using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenShelf.Ui;

/// <summary>
/// The OpenShelf logo as window and tray icons, read from the embedded <c>OpenShelf.ico</c> (the one the exe carries): the title bar, taskbar
/// button and Alt+Tab list of every window get frames at exactly the sizes Windows asks for at the window's scaling.
/// </summary>
public static class AppIcons
{
    const int WM_SETICON = 0x0080;
    const int SM_CXICON = 11;
    const int SM_CXSMICON = 49;

    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);

    static List<BitmapFrame>? _frames;

    /// <summary>Gives every window of the app the logo as soon as it has a handle. Call once at start-up.</summary>
    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => { if (ReferenceEquals(s, e.OriginalSource)) Apply((Window)s); }));

    /// <summary>Sets the window's small and big icon (and follows it to another monitor's scaling). Safe to call more than once.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => Apply(window);
            return;
        }
        Set(window, handle);
        window.DpiChanged -= OnDpiChanged;
        window.DpiChanged += OnDpiChanged;
    }

    static void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        var window = (Window)sender;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) Set(window, handle, e.NewDpi.PixelsPerInchX);
    }

    static void Set(Window window, IntPtr handle, double? ppi = null)
    {
        var dpi = (uint)Math.Round(ppi ?? VisualTreeHelper.GetDpi(window).PixelsPerInchX);
        Send(handle, 0, Create(GetSystemMetricsForDpi(SM_CXSMICON, dpi)));
        Send(handle, 1, Create(GetSystemMetricsForDpi(SM_CXICON, dpi)));
    }

    static void Send(IntPtr hwnd, int kind, IntPtr icon)
    {
        // Windows keeps the icons it is given until they are replaced, so the old one is released here.
        if (icon == IntPtr.Zero) return;
        var old = SendMessageW(hwnd, WM_SETICON, (IntPtr)kind, icon);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>
    /// A new HICON of <paramref name="size"/> pixels square, or zero when none could be made. The caller owns it and must DestroyIcon it
    /// (or hand it to <c>System.Drawing.Icon.FromHandle</c>, clone, then destroy). Sizes the .ico has a frame for are used as they are.
    /// </summary>
    public static IntPtr Create(int size)
    {
        try
        {
            size = Math.Max(16, size);
            var frames = Frames();
            var frame = frames.FirstOrDefault(f => f.PixelWidth == size);
            if (frame is null)
            {
                // No frame of that size: scale the nearest larger one down with a good filter.
                var source = frames.Where(f => f.PixelWidth > size).OrderBy(f => f.PixelWidth).FirstOrDefault() ?? frames[^1];
                var scaled = new TransformedBitmap(source, new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
                frame = BitmapFrame.Create(scaled);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            return CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, size, size, 0);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>The icon size the notification area wants at the system scaling (16 at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %).</summary>
    public static int SmallIconSize(uint dpi) => GetSystemMetricsForDpi(SM_CXSMICON, dpi);

    /// <summary>Every frame of the embedded .ico, smallest first.</summary>
    public static IReadOnlyList<BitmapFrame> Frames() => _frames ??= Load();

    static List<BitmapFrame> Load()
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("OpenShelf.ico")
            ?? throw new InvalidOperationException("OpenShelf.ico is not embedded");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var list = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
        foreach (var f in list) f.Freeze();
        return list;
    }
}
