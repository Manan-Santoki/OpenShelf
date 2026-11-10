using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Tour.Demo;

/// <summary>
/// Puts the tour window into a fixed state with scratch preferences and saves a screenshot of it. No mouse or keyboard is used.
/// Usage: --step welcome|convert|markdown|tools|pdf|video|shelf|keyboard|options|finish
///        --state waiting|underway|done   (the practice step's banner; "done" feeds the real completing wheel event)
///        --done convert,pdf              (steps already done, for the sidebar and the finish page)
///        --results                       (add files "made" beside the practice files)
///        --keys custom                   (Ctrl+Shift for Convert, Alt for Tools, no selection shortcut)
///        --shot file.png
///        --make-files folder             (only write the practice files there and exit)
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var step = Enum.Parse<TourStep>(Arg("--step") ?? "welcome", ignoreCase: true);
        var state = Arg("--state") ?? "waiting";
        var shot = Arg("--shot");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        if (Arg("--make-files") is { } target)
        {
            PracticeFiles.Prepare(target);
            return 0;
        }

        var folder = Path.Combine(Path.GetTempPath(), "openshelf-tour-demo-" + Guid.NewGuid().ToString("N"));
        var prefs = new DemoPreferences();
        if (Arg("--keys") == "custom")
        {
            prefs.Triggers = new Dictionary<WheelKind, Modifiers>
            {
                [WheelKind.Convert] = Modifiers.Control | Modifiers.Shift,
                [WheelKind.Tools] = Modifiers.Alt,
                [WheelKind.Actions] = Modifiers.Control | Modifiers.Alt,
            };
            prefs.SelectionHotKey = null;
        }
        if (Arg("--done") is { } done)
            prefs.SetJson(TourProgress.ProgressKey, "{\"Completed\":[" + string.Join(",", done.Split(',').Select(d => $"\"{d}\"")) + "]}");

        var wheels = new DemoWheels();
        var tour = new TourService(prefs, wheels, new DemoShelves(), new DemoToasts()) { PracticeFolder = folder, FixedPosition = new System.Windows.Point(120, 40) };

        app.Startup += (_, _) =>
        {
            tour.Show();
            tour.Window!.Topmost = true;      // keep other windows from covering the capture
            tour.Progress.Show(step);
            var wait = 1400;
            if (Arg("--results") is not null)
            {
                var timer0 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer0.Tick += (_, _) =>
                {
                    timer0.Stop();
                    File.WriteAllText(Path.Combine(folder, "Mountain Lake.png"), "x");
                    File.WriteAllText(Path.Combine(folder, "Mountain Lake Cropped.jpg"), "x");
                    File.WriteAllText(Path.Combine(folder, "Trip Notes.pdf"), "x");
                };
                timer0.Start();
                wait = 2600;
            }
            var kick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            kick.Tick += (_, _) =>
            {
                kick.Stop();
                if (state == "underway" && step.Wheel() is { } wheel) wheels.Raise(new WheelEvent(wheel, null, [Path.Combine(folder, "x.jpg")], false, false));
                else if (state == "done") wheels.Raise(Completing(step, folder));
            };
            kick.Start();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (shot is not null) { Capture(tour.Window!, shot); tour.Dispose(); app.Shutdown(); }
            };
            timer.Start();
        };
        app.Run();
        try { Directory.Delete(folder, true); } catch (IOException) { }
        return 0;
    }

    /// <summary>The wheel event that completes <paramref name="step"/>.</summary>
    private static WheelEvent Completing(TourStep step, string folder)
    {
        string P(PracticeFile f) => PracticeFiles.PathOf(f, folder);
        return step switch
        {
            TourStep.Convert => new(WheelKind.Convert, "convert.png", [P(PracticeFile.Photo)], false, false),
            TourStep.Markdown => new(WheelKind.Convert, "convert.pdf", [P(PracticeFile.Notes)], false, false),
            TourStep.Tools => new(WheelKind.Tools, "tool.crop", [P(PracticeFile.Photo)], false, false),
            TourStep.Pdf => new(WheelKind.Tools, "tool.split", [P(PracticeFile.Report)], false, false),
            TourStep.Video => new(WheelKind.Tools, "tool.trim", [P(PracticeFile.Clip)], false, false),
            TourStep.Shelf => new(WheelKind.Actions, "action.shelf", [P(PracticeFile.Notes)], false, false),
            TourStep.Keyboard => new(WheelKind.Convert, null, [P(PracticeFile.Letter)], true, false),
            _ => new(WheelKind.Convert, "convert.mp4", [P(PracticeFile.Clip)], false, true),
        };
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static void Capture(Window window, string file)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        // PrintWindow asks the window to draw itself, so another window on top of it (parallel demos) cannot get into the picture.
        GetWindowRect(handle, out var r);
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(handle, hdc, 2); }   // PW_RENDERFULLCONTENT
            finally { g.ReleaseHdc(hdc); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, ImageFormat.Png);
    }
}

internal sealed class DemoPreferences : IPreferences
{
    private readonly Dictionary<string, string> _json = [];
    public event Action? Changed;
    public IReadOnlyDictionary<WheelKind, Modifiers> Triggers { get; set; } = new Dictionary<WheelKind, Modifiers>
    {
        [WheelKind.Convert] = Modifiers.Shift,
        [WheelKind.Tools] = Modifiers.Shift | Modifiers.Alt,
        [WheelKind.Actions] = Modifiers.Control,
    };
    public void SetTrigger(WheelKind wheel, Modifiers modifiers) { Triggers = new Dictionary<WheelKind, Modifiers>(Triggers) { [wheel] = modifiers }; Changed?.Invoke(); }
    public HotKey? SelectionHotKey { get; set; } = new(Modifiers.Control | Modifiers.Alt, 0x20);
    public bool ShakeToOpen { get; set; } = true;
    public bool RemoveAfterDragOut { get; set; }
    public bool RestoreShelves { get; set; } = true;
    public bool HideDockedShelves { get; set; } = true;
    public bool ResultsBackOnShelf { get; set; } = true;
    public bool OpenAtLogin { get; set; }
    public bool AutomaticUpdates { get; set; } = true;
    public string? GetJson(string key) => _json.GetValueOrDefault(key);
    public void SetJson(string key, string? json) { if (json is null) _json.Remove(key); else _json[key] = json; }
}

internal sealed class DemoWheels : IWheelService
{
    public event Action<WheelEvent>? Activity;
    public void Start() { }
    public void OpenForSelection() { }
    public void Dispose() { }
    public void Raise(WheelEvent e) => Activity?.Invoke(e);
}

internal sealed class DemoShelves : IShelfService
{
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) { }
    public void Flush() { }
}

internal sealed class DemoToasts : IToastService
{
    public void Show(string message, ScreenPoint near) { }
}
