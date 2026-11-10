global using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ImageMagick;
using OpenShelf.Core;
using OpenShelf.Tools.Editors;
using OpenShelf.Tools.Imaging;
using Point = System.Windows.Point;

namespace OpenShelf.Tools.Demo;

/// <summary>Opens a tool editor in a fixed state on generated pictures and saves a screenshot of it. No mouse or keyboard is used.
/// Usage: --tool crop|resize|compress|metadata|edit|annotate|addbg|redact  [--files 1|3]  [--state name]  --shot file.png  [--at x,y]
/// Also: --wheel prints what the Tools wheel offers for the generated pictures.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var tool = Arg("--tool") ?? "crop";
        var files = int.Parse(Arg("--files") ?? "1");
        var state = Arg("--state") ?? "default";
        var shot = Arg("--shot");
        var at = (Arg("--at") ?? "760,480").Split(',').Select(int.Parse).ToArray();

        var temp = Path.Combine(Path.GetTempPath(), "openshelf-tools-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = MakePictures(temp, files);

        var services = new ToolServices(new Toasts(), new Progress(), new Shelves());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        if (Arg("--wheel") is { } wheelFile)
        {
            var catalog = new ToolsCatalog(services.Toasts, services.Progress, services.Shelves);
            var lines = new List<string>();
            foreach (var group in new[] { paths, new[] { paths[0] }, new[] { Path.Combine(temp, "notes.txt") } })
            {
                var content = catalog.For(group);
                lines.Add($"{group.Length} file(s): " + string.Join(", ", content.Entries.Select(e => $"{e.Id}={e.Title}")) + (content.EmptyMessage is { } m ? " | " + m : ""));
            }
            File.WriteAllLines(wheelFile, lines);
            Directory.Delete(temp, true);
            return 0;
        }

        if (Arg("--glyphs") is { } glyphFile)
        {
            // The wedge icons as the wheel will draw them, for checking the code points.
            app.Startup += async (_, _) =>
            {
                var catalog = new ToolsCatalog(services.Toasts, services.Progress, services.Shelves);
                var panel = new System.Windows.Controls.WrapPanel { Width = 700, Background = System.Windows.Media.Brushes.Black };
                foreach (var tool in catalog.Tools.OrderBy(t => t.Order))
                {
                    var cell = new System.Windows.Controls.StackPanel { Width = 84, Margin = new Thickness(6) };
                    cell.Children.Add(new OpenShelf.Ui.IconView(tool.Glyph, 28, System.Windows.Media.Brushes.White));
                    cell.Children.Add(new System.Windows.Controls.TextBlock { Text = tool.Title.ToUpperInvariant(), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
                    panel.Children.Add(cell);
                }
                var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100, Topmost = true, Background = System.Windows.Media.Brushes.Black };
                window.Show();
                await Task.Delay(800);
                Capture(window, glyphFile);
                window.Close();
                app.Shutdown();
            };
            app.Run();
            return 0;
        }

        var run = new WheelRun(paths, new ScreenPoint(at[0], at[1]), false, null);
        app.Startup += async (_, _) =>
        {
            var images = paths.Select(EditableImage.Load).ToList();
            var host = new EditorHost(run, services);
            EditorWindow window = tool switch
            {
                "crop" => new CropEditor(host, images),
                "resize" => new ResizeEditor(host, images),
                "compress" => new CompressEditor(host, images),
                "metadata" => new MetadataEditor(host, images),
                "edit" => new AdjustEditor(host, images),
                "annotate" => new AnnotateEditor(host, images),
                "addbg" => new AddBackgroundEditor(host, images),
                "redact" => new RedactEditor(host, images),
                _ => throw new ArgumentException("unknown tool " + tool),
            };
            window.Show();
            window.Dispatcher.InvokeAsync(() => ((IEditorDemo)window).ApplyDemoState(state), DispatcherPriority.Loaded);
            await Task.Delay(1500);
            if (shot is not null) Capture(window, shot);
            if (Arg("--press") is { } keyName)
            {
                // Raises the key event on the window itself; the real keyboard is not used.
                var key = Enum.Parse<System.Windows.Input.Key>(keyName);
                Log.Add("focus: " + System.Windows.Input.Keyboard.FocusedElement?.GetType().Name);
                var source = PresentationSource.FromVisual(window);
                var press = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                ((System.Windows.Input.Keyboard.FocusedElement as UIElement) ?? window).RaiseEvent(press);
                for (var i = 0; i < 40 && window.IsVisible; i++) await Task.Delay(100);
                Log.Add("after " + keyName + ": window " + (window.IsVisible ? "still open" : "closed"));
                if (Arg("--shot2") is { } shot2 && window.IsVisible) Capture(window, shot2);
            }
            await Task.Delay(200);
            if (window.IsVisible) window.Close();
            if (Arg("--log") is { } logFile) File.WriteAllLines(logFile, Log.Lines.Concat(Directory.GetFiles(temp).Select(f => "file: " + Path.GetFileName(f))));
            app.Shutdown();
        };
        app.Run();
        try { Directory.Delete(temp, true); } catch (IOException) { }
        return 0;
    }

    /// <summary>A few pictures that look like something: a sky gradient, sun and hills, plus camera details and a GPS fix on the first.</summary>
    private static string[] MakePictures(string folder, int count)
    {
        var paths = new List<string>();
        var sizes = new[] { (1600, 1000), (1200, 1200), (1000, 1500), (1800, 900), (1400, 1000) };
        var names = new[] { "Sunrise at the lake.jpg", "Harbour.jpg", "Street market.jpg", "Panorama.jpg", "Old town.jpg" };
        for (var i = 0; i < count; i++)
        {
            var (w, h) = sizes[i % sizes.Length];
            using var image = new MagickImage(new MagickColor("#2B2D5B"), (uint)w, (uint)h);
            using (var sky = new MagickImage("gradient:#2B2D5B-#F4A259", (uint)w, (uint)h)) image.Composite(sky, 0, 0, CompositeOperator.Over);
            image.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor("#FFE9A8")), new ImageMagick.Drawing.DrawableCircle(w * 0.7, h * 0.45, w * 0.7 + h * 0.10, h * 0.45));
            image.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor("#1D3B4A")), new ImageMagick.Drawing.DrawablePolygon(
                new PointD(0, h), new PointD(0, h * 0.7), new PointD(w * 0.25, h * 0.55), new PointD(w * 0.5, h * 0.72), new PointD(w * 0.8, h * 0.58), new PointD(w, h * 0.7), new PointD(w, h)));
            image.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor("#0E2330")), new ImageMagick.Drawing.DrawablePolygon(
                new PointD(0, h), new PointD(0, h * 0.86), new PointD(w * 0.35, h * 0.78), new PointD(w * 0.7, h * 0.88), new PointD(w, h * 0.8), new PointD(w, h)));
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Fujifilm");
            exif.SetValue(ExifTag.Model, "X-T5");
            exif.SetValue(ExifTag.LensModel, "XF 23mm F2 R WR");
            exif.SetValue(ExifTag.Software, "Capture One 23");
            exif.SetValue(ExifTag.DateTimeOriginal, "2024:06:21 05:12:44");
            exif.SetValue(ExifTag.FNumber, new Rational(56u, 10u, false));
            exif.SetValue(ExifTag.ExposureTime, new Rational(1u, 500u, false));
            exif.SetValue(ExifTag.FocalLength, new Rational(23u, 1u, false));
            exif.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 160 });
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(46u, 1u, false), new Rational(30u, 1u, false), new Rational(12u, 1u, false) });
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(6u, 1u, false), new Rational(34u, 1u, false), new Rational(9u, 1u, false) });
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            if (i != 1) image.SetProfile(exif);
            var path = Path.Combine(folder, names[i % names.Length]);
            image.Write(path, MagickFormat.Jpeg);
            paths.Add(path);
        }
        return paths.ToArray();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static void Capture(Window window, string file)
    {
        if (OpenShelf.Ui.OffscreenShot.TryCapture(window, file)) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle);
        DwmGetWindowAttribute(handle, 9, out var r, Marshal.SizeOf<Rect>());   // DWMWA_EXTENDED_FRAME_BOUNDS
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, ImageFormat.Png);
    }
}

internal static class Log
{
    public static readonly List<string> Lines = [];
    public static void Add(string line) { lock (Lines) Lines.Add(line); }
}

internal sealed class Toasts : IToastService
{
    public void Show(string message, ScreenPoint near) => Log.Add("toast: " + message);
}

internal sealed class Shelves : IShelfService
{
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) => Log.Add("shelf: " + string.Join(", ", paths.Select(Path.GetFileName)));
    public void Flush() { }
}

internal sealed class Progress : IProgressService
{
    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel) => new Card();

    private sealed class Card : IProgressCard
    {
        public void Report(int completed, double? fraction = null, string? detail = null) { }
        public void Finish(string message) => Log.Add("progress: " + message);
        public void Dispose() { }
    }
}
