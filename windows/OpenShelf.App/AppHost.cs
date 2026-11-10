using System.Windows;
using OpenShelf.Automation;
using OpenShelf.Convert;
using OpenShelf.Convert.Documents;
using OpenShelf.Convert.Markdown;
using OpenShelf.Core;
using OpenShelf.Install;
using OpenShelf.Settings;
using OpenShelf.Shelf;
using OpenShelf.Tools;
using OpenShelf.Wheel;

namespace OpenShelf.App;

/// <summary>Wires the services together. Owned by the orchestrator.</summary>
public static class AppHost
{
    public static AppCommands Commands { get; } = new();
    public static IPreferences? Preferences { get; private set; }
    public static IShelfService? Shelves { get; private set; }
    public static IToastService? Toasts { get; private set; }
    public static IProgressService? Progress { get; private set; }
    public static IWheelService? Wheels { get; private set; }
    public static ISettingsWindow? Settings { get; private set; }
    public static ConversionWheelCatalog? Catalog { get; private set; }

    private static PreferencesStore? _store;
    private static ITrayService? _tray;
    private static ToolInstaller? _installer;
    private static OpenShelf.Update.UpdateService? _updates;
    private static WheelService? _wheelHost;
    private static CommandCoalescer? _coalescer;
    private static WatchedFolderService? _watched;
    private static OpenShelf.Tour.TourService? _tour;

    public static void Start(Application app)
    {
        _store = new PreferencesStore();
        Preferences = _store;

        var toasts = new ToastService();
        var shelves = new ShelfService(_store, toasts);
        Toasts = toasts;
        Shelves = shelves;
        Progress = new ProgressService(toasts);

        // Conversion engines beyond the built-in ones (images, archives, sheets, subtitles).
        MarkdownEngines.Register(EngineRegistry.Default);
        DocumentEngines.Register(EngineRegistry.Default);
        MediaEngines.Register(EngineRegistry.Default);

        _installer = new ToolInstaller();
        var settings = new SettingsWindowService(_store, Commands) { ToolInstaller = _installer };
        Settings = settings;
        var options = new OutputOptionsService(_store);

        var catalog = new ConversionWheelCatalog(_store, Progress, toasts, shelves)
        {
            OptionsPrompt = async (format, files, pointer) =>
            {
                var chosen = await options.AskAsync(format.Id(), format.Title(), files, pointer);
                return chosen is null ? null : ConversionWheelCatalog.ToOptions(chosen);
            },
        };
        Catalog = catalog;

        // Tools hand their results to the shelves through this tap, so the folder watcher hears about them too.
        var toolShelves = new ResultTap(shelves);
        var tools = new ToolsCatalog(toasts, Progress, toolShelves);
        ToolRegistration.RegisterAll(tools);
        catalog.ToolsProvider = tools.For;
        var wheels = new WheelService(shelves, toasts, _store, catalog);
        Wheels = wheels;
        _wheelHost = wheels;

        _coalescer = new CommandCoalescer(command =>
            Application.Current?.Dispatcher.BeginInvoke(() => RunCommand(command)));
        _watched = WatchedFolderService.ForPreferences(_store, arrival =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnFolderArrival(arrival)));
        catalog.Saved += _watched.NoteOutputs;
        toolShelves.Saved = _watched.NoteOutputs;

        Commands.OpenSettings = () => settings.Show();
        Commands.OpenWheelForSelection = wheels.OpenForSelection;
        _tour = new OpenShelf.Tour.TourService(_store, wheels, shelves, toasts) { OpenSettings = tab => settings.Show(tab) };
        Commands.TakeTour = _tour.Show;
        settings.ExplorerEntryEnabled = () => ExplorerIntegration.IsRegistered();
        settings.SetExplorerEntry = SetExplorerEntry;
        _updates = UpdateWiring.Create(_store, toasts, CursorPoint, beforeRestart: Stop);
        Commands.CheckForUpdates = () => _ = _updates.CheckInteractivelyAsync();
        Commands.Quit = app.Shutdown;

        // The first catalog call loads the image library and looks for external tools (about 0.7 s).
        // Do it now, off the UI thread, so the first wheel of the session opens instantly.
        _ = Task.Run(() =>
        {
            try { catalog.Convert([System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warmup.png")]); }
            catch { /* warm-up only */ }
        });

        _tray = new TrayService(shelves, Commands);
        _tray.Start();
        wheels.Start();
        _updates.Start();
        _watched.Start();
        SyncExplorerEntry();
        _tour.ShowOnFirstRunIfNeeded();
    }

    /// <summary>A second launch asks the running copy to show Settings (SPEC §4).</summary>
    public static void ShowSettingsFromSecondLaunch() => Settings?.Show();

    public static void Stop()
    {
        _tour?.Dispose();
        _watched?.Dispose();
        _coalescer?.Dispose();
        Shelves?.Flush();
        Wheels?.Dispose();
        _tray?.Dispose();
        _installer?.Dispose();
        _updates?.Dispose();
        _store?.Flush();
        _store?.Dispose();
    }

    /// <summary>Entry point for the command line, the Explorer entry and the pipe. Any thread.</summary>
    public static void Execute(AutomationCommand command) => _coalescer?.Submit(command);

    private static void RunCommand(AutomationCommand command)
    {
        var pointer = CursorPoint();
        var paths = command.Paths;
        switch (command.Kind)
        {
            case CommandKind.Wheel:
                _wheelHost?.OpenKeyboardWheel(paths);
                break;

            case CommandKind.Shelf:
                if (Shelves is { } shelves && !shelves.AddToRecentShelf(paths)) shelves.NewShelf(paths, pointer);
                break;

            case CommandKind.Convert:
            {
                var format = command.Format!.Value;
                var entry = Catalog?.Convert(paths).Entries.FirstOrDefault(e => e.Id == "convert." + format.Id());
                if (entry is null) { Toasts?.Show($"Can't convert these to {format.Title()}", pointer); break; }
                _ = entry.Run(new WheelRun(paths, pointer, OptionsRequested: false, SourceShelf: null));
                break;
            }
        }
    }

    /// <summary>A watched folder got new finished files: put them on a shelf, then run the folder's action.</summary>
    private static async void OnFolderArrival(FolderArrival arrival)
    {
        try
        {
            var pointer = CursorPoint();
            var files = arrival.Files;
            if (Shelves is { } shelves && !shelves.AddToRecentShelf(files)) shelves.NewShelf(files, pointer);

            if (arrival.Action is not { Length: > 0 } action || Catalog is null) return;
            if (action.StartsWith("convert.", StringComparison.Ordinal))
            {
                // Only the files this format applies to (SPEC 3.1).
                var targets = files.Where(f => Catalog.Convert([f]).Entries.Any(e => e.Id == action)).ToList();
                var entry = targets.Count == 0 ? null : Catalog.Convert(targets).Entries.FirstOrDefault(e => e.Id == action);
                if (entry is not null) await entry.Run(new WheelRun(targets, pointer, false, null));
            }
            else if (action.StartsWith("tool.", StringComparison.Ordinal))
            {
                foreach (var file in files)
                {
                    var entry = Catalog.Tools([file]).Entries.FirstOrDefault(e => e.Id == action);
                    if (entry is not null) await entry.Run(new WheelRun([file], pointer, false, null));
                }
            }
        }
        catch
        {
            // A failing action must not stop watching.
        }
    }

    private static void SyncExplorerEntry()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || Preferences is null) return;
            var wanted = Preferences.GetJson("explorerEntry") == "true";
            if (wanted) { if (!ExplorerIntegration.IsRegistered(exe)) ExplorerIntegration.Register(exe); }
            else if (ExplorerIntegration.IsRegistered()) ExplorerIntegration.Unregister();
        }
        catch
        {
            // Registry access denied: leave it as it was.
        }
    }

    /// <summary>For the Settings toggle ("Open with OpenShelf" in the Explorer menu).</summary>
    public static void SetExplorerEntry(bool on)
    {
        Preferences?.SetJson("explorerEntry", on ? "true" : "false");
        SyncExplorerEntry();
    }

    private static ScreenPoint CursorPoint()
    {
        GetCursorPos(out var p);
        return new ScreenPoint(p.X, p.Y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private struct POINT { public int X; public int Y; }
}
