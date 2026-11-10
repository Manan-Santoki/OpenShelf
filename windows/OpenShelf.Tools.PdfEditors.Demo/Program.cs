global using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Tools.Pdf;
using PdfSharp.Pdf;
using SkiaSharp;

namespace OpenShelf.Tools.PdfEditors.Demo;

/// <summary>Opens a PDF editor in a fixed state on generated PDFs and saves a screenshot of it. No mouse or keyboard is used.
/// Usage: --tool pages|split|compress|metadata  [--files 1|3]  [--state a,b]  [--locked]  --shot file.png  [--at x,y]
/// [--press Return --shot2 file.png]  [--log file.txt].  Also: --wheel file.txt prints what the Tools wheel offers.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var tool = Arg("--tool") ?? "pages";
        var count = int.Parse(Arg("--files") ?? "1");
        var state = Arg("--state") ?? "default";
        var shot = Arg("--shot");
        var at = (Arg("--at") ?? "760,480").Split(',').Select(int.Parse).ToArray();

        var temp = Path.Combine(Path.GetTempPath(), "openshelf-pdf-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var paths = MakePdfs(temp, args.Contains("--text") ? "text" : tool, count, args.Contains("--locked"), args.Contains("--damaged"));

        var services = new ToolServices(new Toasts(), new Progress(), new Shelves());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        if (Arg("--wheel") is { } wheelFile)
        {
            var catalog = new ToolsCatalog(services.Toasts, services.Progress, services.Shelves);
            PdfTools.Register(catalog);
            var png = Path.Combine(temp, "photo.png");
            File.WriteAllBytes(png, MakePng());
            var lines = new List<string>();
            foreach (var group in new[] { paths.Take(1).ToArray(), paths, new[] { paths[0], png }, new[] { png, png }, new[] { png } })
            {
                var content = catalog.For(group);
                lines.Add($"{string.Join(" + ", group.Select(Path.GetFileName))}: " + string.Join(", ", content.Entries.Select(e => $"{e.Id}={e.Title}")));
            }
            File.WriteAllLines(wheelFile, lines);
            Directory.Delete(temp, true);
            return 0;
        }

        var run = new WheelRun(paths, new ScreenPoint(at[0], at[1]), false, null);
        app.Startup += async (_, _) =>
        {
            if (Arg("--combine") is { } combineLog)
            {
                // The one-click Combine, run through the progress card and toast.
                await PdfTools.CombineAsync(run, services);
                File.WriteAllLines(combineLog, Log.Lines.Concat(Directory.GetFiles(temp).Select(f => "file: " + Path.GetFileName(f))));
                app.Shutdown();
                return;
            }
            var files = PdfFileState.ReadAll(paths);
            var host = new EditorHost(run, services);
            EditorWindow window = tool switch
            {
                "pages" => new PdfPagesWindow(host, files),
                "split" => new PdfSplitWindow(host, files),
                "compress" => new PdfCompressWindow(host, files),
                "metadata" => new PdfMetadataWindow(host, files),
                _ => throw new ArgumentException("unknown tool " + tool),
            };
            window.Show();
            window.Dispatcher.InvokeAsync(() => ((IPdfEditorDemo)window).ApplyDemoState(state), DispatcherPriority.Loaded);
            await Task.Delay(int.Parse(Arg("--wait") ?? "2500"));
            if (shot is not null) Capture(window, shot);
            if (Arg("--press") is { } keyName)
            {
                // Raises the key event on the window itself; the real keyboard is not used.
                var key = Enum.Parse<System.Windows.Input.Key>(keyName);
                var source = PresentationSource.FromVisual(window);
                var press = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                ((System.Windows.Input.Keyboard.FocusedElement as UIElement) ?? window).RaiseEvent(press);
                for (var i = 0; i < 80 && window.IsVisible; i++) await Task.Delay(100);
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

    private static readonly string[] Names = ["Annual report.pdf", "Contract draft.pdf", "Field notes.pdf", "Invoice 0412.pdf", "Menu.pdf"];

    /// <summary>PDFs that look like something: coloured pages with a heading, one landscape page now and then. Compress gets
    /// photographic pages (so there is something to squeeze) and Metadata gets document info.</summary>
    private static string[] MakePdfs(string folder, string tool, int count, bool locked, bool damaged)
    {
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, Names[i % Names.Length]);
            if (tool == "compress") MakePhotoPdf(path, 2 + i);
            else MakePdf(path, 7 - i * 2 > 2 ? 7 - i * 2 : 3, i, tool == "metadata" && i != 1);
            paths.Add(path);
        }
        if (locked)
        {
            var path = Path.Combine(folder, "Tax return (locked).pdf");
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.SecuritySettings.UserPassword = "pw";
                document.SecuritySettings.OwnerPassword = "owner";
                document.Save(path);
            }
            paths.Add(path);
        }
        if (damaged)
        {
            var path = Path.Combine(folder, "Broken scan.pdf");
            File.WriteAllText(path, "%PDF-1.4\nthis is not really a pdf\n");
            paths.Add(path);
        }
        return paths.ToArray();
    }

    private static readonly (double R, double G, double B)[] Colors =
    [
        (0.93, 0.80, 0.55), (0.62, 0.78, 0.93), (0.78, 0.90, 0.70), (0.95, 0.68, 0.68), (0.80, 0.72, 0.93), (0.98, 0.90, 0.60), (0.70, 0.90, 0.88),
    ];

    private static void MakePdf(string path, int pages, int seed, bool withInfo)
    {
        using var document = new PdfDocument();
        for (var n = 0; n < pages; n++)
        {
            var page = document.AddPage();
            var landscape = n == 2;
            page.Width = landscape ? 792 : 612;
            page.Height = landscape ? 612 : 792;
            var font = new PdfSharp.Pdf.PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Helvetica-Bold");
            var fonts = new PdfSharp.Pdf.PdfDictionary(document);
            fonts.Elements["/F1"] = font;
            var resources = new PdfSharp.Pdf.PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            var (r, g, b) = Colors[(n + seed) % Colors.Length];
            var w = (int)page.Width.Point;
            var h = (int)page.Height.Point;
            var content = new PdfSharp.Pdf.PdfDictionary(document);
            document.Internals.AddObject(content);
            var text = FormattableString.Invariant(
                $"{r:0.00} {g:0.00} {b:0.00} rg 0 0 {w} {h} re f 0.15 0.15 0.2 rg 48 {h - 48 - 120} {w - 96} 120 re f 1 1 1 rg BT /F1 40 Tf 72 {h - 130} Td (Page {n + 1}) Tj ET 0.15 0.15 0.2 rg 48 {h - 400} {w - 200} 14 re f 48 {h - 440} {w - 260} 14 re f 48 {h - 480} {w - 140} 14 re f");
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes(text));
            page.Elements["/Contents"] = content.Reference!;
        }
        if (withInfo)
        {
            document.Info.Title = "Annual report 2024";
            document.Info.Author = "Jordan Ellis";
            document.Info.Subject = "Results and outlook";
            document.Info.Keywords = "finance, annual, 2024";
            document.Info.Creator = "Word";
        }
        document.Save(path);
    }

    private static void MakePhotoPdf(string path, int pages)
    {
        var folder = Path.GetDirectoryName(path)!;
        var images = new List<string>();
        for (var i = 0; i < pages; i++)
        {
            var file = Path.Combine(folder, $"scan-{Path.GetFileNameWithoutExtension(path)}-{i}.jpg");
            File.WriteAllBytes(file, MakePhoto(2200, 1500, i));
            images.Add(file);
        }
        var combined = PdfCombiner.CombineAsync(images).GetAwaiter().GetResult();
        File.Move(combined, path, overwrite: true);
        foreach (var image in images) File.Delete(image);
    }

    private static byte[] MakePhoto(int width, int height, int seed)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(seed + 3);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(40, 60, 120), new SKColor(250, 170, 90)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
        }
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Clamp(pixels[i] + random.Next(-40, 40), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + random.Next(-40, 40), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + random.Next(-40, 40), 0, 255);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }

    private static byte[] MakePng()
    {
        using var bitmap = new SKBitmap(400, 300);
        bitmap.Erase(new SKColor(40, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>Renders the window's own visual tree, so another window on top of it (other demos run at the same time) cannot get into the picture.</summary>
    private static void Capture(Window window, string file)
    {
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        var width = (int)Math.Ceiling(visual.ActualWidth * scale);
        var height = (int)Math.Ceiling(visual.ActualHeight * scale);
        var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        target.Render(backdrop);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        encoder.Save(stream);
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
    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel)
    {
        Log.Add($"progress card: {title} ({total})");
        return new Card();
    }

    private sealed class Card : IProgressCard
    {
        public void Report(int completed, double? fraction = null, string? detail = null) { }
        public void Finish(string message) => Log.Add("progress: " + message);
        public void Dispose() { }
    }
}
