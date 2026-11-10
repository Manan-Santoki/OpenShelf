global using System.IO;
global using Path = System.IO.Path;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Tools.Imaging;
using OpenShelf.Tools.VisionEditors;
using OpenShelf.Vision;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace OpenShelf.Tools.VisionEditors.Demo;

/// <summary>
/// Opens the OCR and Remove BG editors in a fixed state on generated pictures and saves a screenshot of the window; also runs Read QR.
/// No mouse or keyboard is used; keys are raised on the window itself.
/// Usage: --tool ocr|removebg|qr|wheel|glyphs  [--files N]  [--state a,b]  [--shot f.png]  [--press Enter --shot2 f.png]  [--log f.txt]  [--at x,y]
/// Remove BG states: original, next, second, download (model "missing", stuck at 42%), failed. OCR states: text, progress, nolanguage.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var tool = Arg("--tool") ?? "ocr";
        var files = int.Parse(Arg("--files") ?? "1");
        var state = Arg("--state") ?? "default";
        var shot = Arg("--shot");
        var at = (Arg("--at") ?? "760,480").Split(',').Select(int.Parse).ToArray();

        var temp = Path.Combine(Path.GetTempPath(), "openshelf-visioneditors-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var services = new ToolServices(new Toasts(), new Progress(), new Shelves());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var pointer = new ScreenPoint(at[0], at[1]);

        if (tool == "wheel")
        {
            var catalog = new ToolsCatalog(services.Toasts, services.Progress, services.Shelves);
            VisionTools.Register(catalog);
            var lines = new List<string>();
            var pdf = Path.Combine(temp, "Scan.pdf");
            foreach (var group in new[] { new[] { Path.Combine(temp, "a.png") }, new[] { Path.Combine(temp, "a.png"), Path.Combine(temp, "b.jpg") },
                         new[] { pdf }, new[] { pdf, Path.Combine(temp, "a.png") }, new[] { Path.Combine(temp, "notes.txt") } })
            {
                var content = catalog.For(group);
                lines.Add(string.Join(" + ", group.Select(Path.GetFileName)) + ": " + string.Join(", ", content.Entries.Select(e => $"{e.Id}={e.Title}")) + (content.EmptyMessage is { } m ? " | " + m : ""));
            }
            lines.Add("model bundled: " + (ModelStore.Default.FindModel() ?? "NO"));
            if (Arg("--log") is { } wheelLog) File.WriteAllLines(wheelLog, lines);
            Directory.Delete(temp, true);
            return 0;
        }

        if (tool == "glyphs")
        {
            var candidates = (Arg("--glyphs") ?? "E8B9,E8EF,E8E9,E7BA,E783").Split(',');
            app.Startup += async (_, _) =>
            {
                var panel = new System.Windows.Controls.WrapPanel { Width = 760, Background = System.Windows.Media.Brushes.Black };
                foreach (var code in candidates)
                {
                    var cell = new System.Windows.Controls.StackPanel { Width = 84, Margin = new Thickness(6) };
                    cell.Children.Add(new System.Windows.Controls.TextBlock { Text = ((char)System.Convert.ToInt32(code, 16)).ToString(), FontFamily = OpenShelf.Ui.Theme.IconFont, FontSize = 28, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                    cell.Children.Add(new System.Windows.Controls.TextBlock { Text = code, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
                    panel.Children.Add(cell);
                }
                var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100, Topmost = true, Background = System.Windows.Media.Brushes.Black };
                window.Show();
                await Task.Delay(800);
                if (shot is not null) Capture(window, shot);
                window.Close();
                app.Shutdown();
            };
            app.Run();
            return 0;
        }

        var paths = tool switch
        {
            "ocr" => MakeDocuments(temp, files),
            "qr" => [MakeQr(temp, "https://openshelf.example/welcome?from=demo")],
            _ => MakePhotos(temp, files),
        };
        var run = new WheelRun(paths, pointer, false, null);

        app.Startup += async (_, _) =>
        {
            if (tool == "qr")
            {
                await RunQr(paths, pointer, services);
            }
            else
            {
                var host = new EditorHost(run, services);
                EditorWindow window;
                if (tool == "ocr")
                {
                    window = new OcrEditor(host, paths, isAvailable: state.Contains("nolanguage") ? () => false : null);
                }
                else
                {
                    var images = paths.Select(EditableImage.Load).ToList();
                    IBackgroundEngine? engine = null;
                    if (state.Contains("download")) engine = new StuckDownloadEngine();
                    if (state.Contains("failed")) engine = new FailingEngine();
                    if (state.Contains("realdownload"))
                        engine = new BackgroundEngine(new BackgroundRemover(new ModelStore(Path.Combine(temp, "dl"), bundledDirectory: "")));
                    window = new RemoveBackgroundEditor(host, images, engine);
                }
                window.Show();
                window.Dispatcher.InvokeAsync(() => ((IEditorDemo)window).ApplyDemoState(state), DispatcherPriority.Loaded);
                if (state.Contains("original"))
                {
                    // The Original button only works once the cutout is ready, as for a person.
                    await Task.Delay(2500);
                    window.Dispatcher.Invoke(() => ((IEditorDemo)window).ApplyDemoState("original"));
                }
                await Task.Delay(int.Parse(Arg("--wait") ?? "3500"));
                if (shot is not null) Capture(window, shot);
                if (Arg("--press") is { } keyName)
                {
                    // Raises the key event on the window itself; the real keyboard is not used.
                    var key = Enum.Parse<System.Windows.Input.Key>(keyName);
                    var source = PresentationSource.FromVisual(window);
                    var press = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                    ((System.Windows.Input.Keyboard.FocusedElement as UIElement) ?? window).RaiseEvent(press);
                    if (Arg("--during") is { } during) { await Task.Delay(int.Parse(during)); if (Arg("--shot3") is { } shot3 && window.IsVisible) Capture(window, shot3); }
                    for (var i = 0; i < 150 && window.IsVisible; i++) await Task.Delay(100);
                    Log.Add("after " + keyName + ": window " + (window.IsVisible ? "still open" : "closed"));
                    if (Arg("--shot2") is { } shot2 && window.IsVisible) Capture(window, shot2);
                }
                await Task.Delay(300);
                if (window.IsVisible) window.Close();
            }
            if (Arg("--log") is { } logFile) File.WriteAllLines(logFile, Log.Lines.Concat(Directory.GetFiles(temp).Select(f => "file: " + Path.GetFileName(f))));
            app.Shutdown();
        };
        app.Run();
        try { Directory.Delete(temp, true); } catch (IOException) { }
        return 0;
    }

    /// <summary>Runs the real Read QR path with the real clipboard, then puts the previous clipboard text back.</summary>
    private static async Task RunQr(string[] paths, ScreenPoint pointer, ToolServices services)
    {
        string? before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch (Exception) { }
        try
        {
            await QrTool.RunAsync(paths, pointer, services.Toasts);
            Log.Add("clipboard: " + Clipboard.GetText());
        }
        finally
        {
            try { if (before is not null) Clipboard.SetText(before); else Clipboard.Clear(); } catch (Exception) { }
        }
        var empty = Path.Combine(Path.GetDirectoryName(paths[0])!, "plain.png");
        File.WriteAllBytes(empty, MakePlainPng());
        await QrTool.RunAsync([empty], pointer, services.Toasts);
        await QrTool.RunAsync([Path.Combine(Path.GetDirectoryName(paths[0])!, "missing.png")], pointer, services.Toasts);
    }

    // ---------------------------------------------------------------- generated pictures

    private static string[] MakeDocuments(string folder, int count)
    {
        var texts = new[]
        {
            new[] { "Invoice 2041", "Total due: 1,280 EUR", "Thank you for your order" },
            new[] { "Meeting notes", "Ship the Windows build", "Review the Tools wheel" },
            new[] { "Shopping list", "Bread and butter", "Coffee beans" },
        };
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, new[] { "Invoice scan.png", "Meeting notes.png", "Shopping list.png" }[i % 3]);
            var lines = texts[i % 3];
            using var bitmap = new SKBitmap(1400, 120 + lines.Length * 110);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Arial");
            using var font = new SKFont(typeface, 56);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (var l = 0; l < lines.Length; l++) canvas.DrawText(lines[l], 60, 110 + l * 110, font, paint);
            Save(bitmap, path);
            paths.Add(path);
        }
        return paths.ToArray();
    }

    /// <summary>Photo-like pictures: a figure on a plain backdrop (has a subject), and a flat grey picture (has none).</summary>
    private static string[] MakePhotos(string folder, int count)
    {
        var names = new[] { "Portrait.png", "Empty wall.png", "Teapot.png", "Garden gnome.png", "Lamp.png" };
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, names[i % names.Length]);
            var empty = i == 1;
            using var bitmap = new SKBitmap(900 + 100 * (i % 3), 700);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(empty ? new SKColor(200, 205, 210) : new SKColor(235, 232, 226));
            if (!empty)
            {
                var cx = bitmap.Width / 2f;
                using var body = new SKPaint { Color = new SKColor(170, 30, 40), IsAntialias = true };
                using var core = new SKPaint { Color = new SKColor(240, 190, 120), IsAntialias = true };
                using var dark = new SKPaint { Color = new SKColor(40, 40, 60), IsAntialias = true };
                canvas.DrawOval(cx, 470, 230, 190, body);
                canvas.DrawCircle(cx, 230, 120, core);
                canvas.DrawCircle(cx - 40, 215, 14, dark);
                canvas.DrawCircle(cx + 40, 215, 14, dark);
            }
            Save(bitmap, path);
            paths.Add(path);
        }
        return paths.ToArray();
    }

    private static string MakeQr(string folder, string text)
    {
        var path = Path.Combine(folder, "Welcome QR.png");
        var matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 });
        const int module = 8, quiet = 4;
        using var bitmap = new SKBitmap((matrix.Width + 2 * quiet) * module, (matrix.Height + 2 * quiet) * module);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) canvas.DrawRect((x + quiet) * module, (y + quiet) * module, module, module, paint);
        Save(bitmap, path);
        return path;
    }

    private static byte[] MakePlainPng()
    {
        using var bitmap = new SKBitmap(300, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(200, 205, 210));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
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

/// <summary>An engine whose model is missing and whose download never finishes: the "getting the model" state.</summary>
internal sealed class StuckDownloadEngine : IBackgroundEngine
{
    public bool IsModelAvailable => false;
    public async Task EnsureModelAsync(IProgress<double>? progress, CancellationToken cancel)
    {
        progress?.Report(0.42);
        await Task.Delay(Timeout.Infinite, cancel);
    }
    public Task<Cutout> CutOutAsync(string path, CancellationToken cancel) => throw new NotSupportedException();
    public Task<string> SaveAsync(string path, Cutout cutout, CancellationToken cancel) => throw new NotSupportedException();
}

/// <summary>An engine that is offline: the model cannot be downloaded.</summary>
internal sealed class FailingEngine : IBackgroundEngine
{
    public bool IsModelAvailable => false;
    public Task EnsureModelAsync(IProgress<double>? progress, CancellationToken cancel) =>
        throw new ModelUnavailableException("The Remove Background model couldn’t be downloaded. Check your internet connection and try again.");
    public Task<Cutout> CutOutAsync(string path, CancellationToken cancel) => throw new NotSupportedException();
    public Task<string> SaveAsync(string path, Cutout cutout, CancellationToken cancel) => throw new NotSupportedException();
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
