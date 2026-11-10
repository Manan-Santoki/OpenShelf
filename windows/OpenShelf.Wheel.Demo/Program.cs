using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Wheel;

namespace OpenShelf.Wheel.Demo;

/// <summary>Runs WheelService with in-memory fakes so the wheel can be tested without the rest of the app. Log: %TEMP%\openshelf-wheel-demo.log.</summary>
internal static class Program
{
    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "openshelf-wheel-demo.log");
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        lock (Gate) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        File.WriteAllText(LogPath, "");
        var many = args.Contains("--many");
        string? openSelection = null;
        var i = Array.IndexOf(args, "--open-selection");
        if (i >= 0 && i + 1 < args.Length) openSelection = args[i + 1];
        var shelf = args.Contains("--shelf");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log("UNHANDLED " + e.Exception); e.Handled = true; };

        var shelves = new FakeShelves { HasOpenShelf = shelf };
        var toasts = new FakeToasts();
        var prefs = new FakePreferences();
        IWheelCatalog catalog = many ? new ManyCatalog() : new BasicImageCatalog(toasts, shelves);
        var service = new WheelService(shelves, toasts, prefs, catalog);
        service.Activity += e => Log($"ACTIVITY {e.Kind} entry={e.EntryId ?? "(opened)"} keyboard={e.Keyboard} options={e.OptionsRequested} files={e.Paths.Count}");

        app.Startup += (_, _) =>
        {
            service.Start();
            Log("demo started");
            if (openSelection is not null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Log("test hook: opening keyboard wheel for " + openSelection);
                    service.OpenKeyboardWheel([openSelection]);
                };
                timer.Start();
            }
        };
        app.Run();
        service.Dispose();
    }
}

internal sealed class FakeToasts : IToastService
{
    public void Show(string message, ScreenPoint near) => Program.Log($"TOAST '{message}' at {near.X},{near.Y}");
}

internal sealed class FakeShelves : IShelfService
{
    public bool HasOpenShelf { get; set; }
    public object? DraggingShelf => null;
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { Program.Log($"SHELF new {paths.Count} file(s)"); HasOpenShelf = true; }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) { Program.Log($"SHELF add {paths.Count} file(s)"); return HasOpenShelf; }
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) => Program.Log($"SHELF results {paths.Count}");
    public void Flush() { }
}

internal sealed class FakePreferences : IPreferences
{
    public event Action? Changed { add { } remove { } }
    public IReadOnlyDictionary<WheelKind, Modifiers> Triggers { get; } = new Dictionary<WheelKind, Modifiers>
    {
        [WheelKind.Convert] = Modifiers.Shift,
        [WheelKind.Tools] = Modifiers.Shift | Modifiers.Alt,
        [WheelKind.Actions] = Modifiers.Control,
    };
    public void SetTrigger(WheelKind wheel, Modifiers modifiers) { }
    public HotKey? SelectionHotKey { get; set; } = new HotKey(Modifiers.Control | Modifiers.Alt, 0x20);
    public bool ShakeToOpen { get; set; } = true;
    public bool RemoveAfterDragOut { get; set; }
    public bool RestoreShelves { get; set; } = true;
    public bool HideDockedShelves { get; set; } = true;
    public bool ResultsBackOnShelf { get; set; } = true;
    public bool OpenAtLogin { get; set; }
    public bool AutomaticUpdates { get; set; } = true;
    public string? GetJson(string key) => null;
    public void SetJson(string key, string? json) { }
}

/// <summary>Ten fake conversions (to test paging); Tools stays empty.</summary>
internal sealed class ManyCatalog : IWheelCatalog
{
    public WheelContent Convert(IReadOnlyList<string> paths)
    {
        var names = new[] { "PNG", "JPG", "HEIC", "WEBP", "PDF", "AVIF", "TIFF", "BMP", "DOCX", "ZIP" };
        var entries = names.Select(n => new WheelEntry("convert." + n.ToLowerInvariant(), n, run =>
        {
            Program.Log($"RUN {n} on {run.Paths.Count} file(s) options={run.OptionsRequested}");
            return Task.CompletedTask;
        }, HasOptions: n is "JPG" or "WEBP")).ToList();
        return new WheelContent(entries);
    }

    public WheelContent Tools(IReadOnlyList<string> paths) => new([], "Tools arrive soon");
}
