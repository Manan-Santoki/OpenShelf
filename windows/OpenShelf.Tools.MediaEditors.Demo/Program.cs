using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Tools.Media;
using Point = System.Windows.Point;

namespace OpenShelf.Tools.MediaEditors.Demo;

/// <summary>Opens a media editor in a fixed state on clips made with ffmpeg and saves a screenshot of it. No mouse or keyboard is used
/// and playback is muted.
/// Usage: --tool trim|speed|crop|rotate|split|merge|compress|mute  [--kind video|audio]  [--files N]  [--state a,b]  [--shot f.png]
/// [--at x,y]  [--save | --press Key]  [--shot-during ms f.png]  [--cancel-after ms]  [--shot2 f.png]  [--log f.txt]  [--keep]
/// [--no-ffmpeg]   or   --wheel f.txt (what the wheel offers)   or   --glyphs f.png (the wedge icons).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => Array.IndexOf(args, name) >= 0;
        var tool = Arg("--tool") ?? "trim";
        var kind = Arg("--kind") ?? "video";
        var files = int.Parse(Arg("--files") ?? "1");
        var state = Arg("--state") ?? "default";
        var shot = Arg("--shot");
        var at = (Arg("--at") ?? "760,480").Split(',').Select(int.Parse).ToArray();

        DemoSettings.Muted = true;
        var temp = Path.Combine(Path.GetTempPath(), "openshelf-mediaeditors-demo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var tools = ExternalTools.Detect(ToolLocations.Standard);
        var paths = Clips.Make(temp, tools, kind, files, tool);

        var services = new ToolServices(new Toasts(), new Progress(), new Shelves());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var engine = Flag("--no-ffmpeg") ? new MediaEditor(() => new ExternalTools()) : new MediaEditor(() => tools);
        var operations = new MediaOperations(engine);

        var catalog = new ToolsCatalog(services.Toasts, services.Progress, services.Shelves, registerBuiltIns: false);
        MediaTools.Register(catalog, operations);

        if (Arg("--wheel") is { } wheelFile)
        {
            var mkv = Path.Combine(temp, "clip.mkv");
            File.WriteAllText(mkv, "x");
            var lines = new List<string>();
            var vids = Clips.Make(temp, tools, "video", 2, "wheel");
            var auds = Clips.Make(temp, tools, "audio", 2, "wheel");
            foreach (var (label, group) in new (string, string[])[]
            {
                ("1 video", [vids[0]]), ("2 videos", vids), ("1 audio", [auds[0]]), ("2 audios", auds),
                ("video+audio", [vids[0], auds[0]]), ("mkv", [mkv]),
            })
            {
                var content = catalog.For(group);
                lines.Add($"{label}: " + string.Join(", ", content.Entries.Select(e => $"{e.Title}({e.Id})")) + (content.EmptyMessage is { } m ? " | " + m : ""));
            }
            File.WriteAllLines(wheelFile, lines);
            Directory.Delete(temp, true);
            return 0;
        }

        if (Arg("--glyphs") is { } glyphFile)
        {
            app.Startup += async (_, _) =>
            {
                var panel = new System.Windows.Controls.WrapPanel { Width = 760, Background = System.Windows.Media.Brushes.Black };
                var seen = new HashSet<string>();
                foreach (var def in catalog.Tools.OrderBy(t => t.Order))
                    panel.Children.Add(Cell(def.Glyph, def.Title));
                foreach (var code in (Arg("--candidates") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    panel.Children.Add(Cell(((char)System.Convert.ToInt32(code, 16)).ToString(), code));
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
            try
            {
                var id = tool switch
                {
                    "trim" => MediaToolIds.Trim, "speed" => MediaToolIds.Speed, "crop" => MediaToolIds.Crop, "rotate" => MediaToolIds.Rotate,
                    "split" => MediaToolIds.Split, "merge" => MediaToolIds.Merge, "compress" => MediaToolIds.Compress, "mute" => MediaToolIds.Mute,
                    _ => throw new ArgumentException("unknown tool " + tool),
                };
                var entry = catalog.For(paths).Entries.FirstOrDefault(e => e.Id == id);
                if (entry is null) { Log.Add("the wheel does not offer " + id + " for these files"); }
                else
                {
                    await entry.Run(run);
                    if (tool == "mute")
                    {
                        for (var i = 0; i < 200 && !Log.Lines.Any(l => l.StartsWith("progress: ")); i++) await Task.Delay(100);
                    }
                }
                var window = Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault();
                if (window is not null)
                {
                    await Task.Delay(300);
                    window.Dispatcher.Invoke(() => ((IMediaEditorDemo)window).ApplyDemoState(state), DispatcherPriority.Loaded);
                    await Task.Delay(int.Parse(Arg("--settle") ?? "2500"));
                    if (window is TrimEditor te) Log.Add("trim edits: " + string.Join(" | ", te.State.Edits.All.Select((e, i) => $"{e} of {te.State.Durations[i]} edited={te.State.Edits.IsEdited(i)}")));
                    if (shot is not null) Capture(window, shot);

                    if (Flag("--save") || Arg("--press") is not null)
                    {
                        if (Arg("--press") is { } keyName)
                        {
                            var key = Enum.Parse<System.Windows.Input.Key>(keyName);
                            var source = PresentationSource.FromVisual(window);
                            var press = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
                            ((System.Windows.Input.Keyboard.FocusedElement as UIElement) ?? window).RaiseEvent(press);
                        }
                        else
                        {
                            ((MediaEditorWindow)window).ApplyForDemo();
                        }
                        if (Arg("--shot-during") is { } during && Arg("--during-file") is { } duringFile)
                        {
                            await Task.Delay(int.Parse(during));
                            Capture(window, duringFile);
                        }
                        if (Arg("--cancel-after") is { } cancelAfter)
                        {
                            await Task.Delay(int.Parse(cancelAfter));
                            ((MediaEditorWindow)window).CancelExportForDemo();
                            await Task.Delay(1500);
                            Log.Add("after cancel: window " + (window.IsVisible ? "still open" : "closed"));
                            if (Arg("--shot2") is { } cancelShot && window.IsVisible) Capture(window, cancelShot);
                        }
                        else
                        {
                            var limit = int.Parse(Arg("--wait") ?? "60000");
                            for (var waited = 0; waited < limit && window.IsVisible; waited += 100) await Task.Delay(100);
                            Log.Add("after save: window " + (window.IsVisible ? "still open" : "closed"));
                            if (Arg("--shot2") is { } shot2 && window.IsVisible) Capture(window, shot2);
                        }
                    }
                    await Task.Delay(200);
                    if (window.IsVisible) window.Close();
                }
                else if (shot is not null)
                {
                    Log.Add("no editor window opened");
                }
                // Hidden files are the staged exports: list everything so a leftover shows.
                Log.Add("folder: " + string.Join(", ", Directory.GetFileSystemEntries(temp).Select(f => Path.GetFileName(f) + $" ({new FileInfo(f).Length})")));
                if (Arg("--log") is { } logFile) File.WriteAllLines(logFile, Log.Lines);
                if (Flag("--keep")) Log.Add("kept: " + temp);
            }
            catch (Exception e)
            {
                Log.Add("demo failed: " + e);
                if (Arg("--log") is { } logFile) File.WriteAllLines(logFile, Log.Lines);
            }
            app.Shutdown();
        };
        app.Run();
        if (Flag("--keep") && Arg("--log") is { } kept) File.AppendAllLines(kept, ["kept: " + temp]);
        else { try { Directory.Delete(temp, true); } catch (IOException) { } }
        return 0;
    }

    private static System.Windows.Controls.StackPanel Cell(string glyph, string title)
    {
        var cell = new System.Windows.Controls.StackPanel { Width = 84, Margin = new Thickness(6) };
        cell.Children.Add(new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = OpenShelf.Ui.Theme.IconFont, FontSize = 28, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        cell.Children.Add(new System.Windows.Controls.TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        return cell;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static void Capture(Window window, string file)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle);
        DwmGetWindowAttribute(handle, 9, out var r, Marshal.SizeOf<Rect>());   // DWMWA_EXTENDED_FRAME_BOUNDS
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, ImageFormat.Png);
    }
}

/// <summary>Short clips made with ffmpeg's test sources.</summary>
internal static class Clips
{
    public static string[] Make(string folder, ExternalTools tools, string kind, int count, string tool)
    {
        var ffmpeg = tools.Ffmpeg ?? throw new InvalidOperationException("ffmpeg is needed to make the demo clips");
        var paths = new List<string>();
        // Different shapes and lengths: landscape, portrait, small.
        (int W, int H, int Seconds, string Name)[] shapes = [(640, 360, 8, "Beach day"), (360, 640, 6, "Phone clip"), (480, 270, 4, "Short one"), (640, 480, 10, "Old camera")];
        for (var i = 0; i < count; i++)
        {
            var (w, h, seconds, name) = shapes[i % shapes.Length];
            if (kind == "audio")
            {
                var path = Path.Combine(folder, (i == 0 ? "Voice memo" : $"Memo {i + 1}") + ".m4a");
                // A tone that swells and fades, so the waveform has a shape.
                Run(ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={330 + 110 * i}:duration={seconds}", "-af", "tremolo=f=0.5:d=0.9,volume=0.8", "-c:a", "aac", path);
                paths.Add(path);
                continue;
            }
            var video = Path.Combine(folder, name + ".mp4");
            if (tool == "compress")
                Run(ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={w * 2}x{h * 2}:rate=30:duration={seconds}", "-f", "lavfi", "-i", "sine=frequency=440",
                    "-vf", "noise=alls=12:allf=t", "-c:v", "libx264", "-b:v", "9M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video);
            else
                Run(ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={w}x{h}:rate=25:duration={seconds}", "-f", "lavfi", "-i", $"sine=frequency={440 + 110 * i}",
                    "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video);
            paths.Add(video);
        }
        return paths.ToArray();
    }

    private static void Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + error);
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
