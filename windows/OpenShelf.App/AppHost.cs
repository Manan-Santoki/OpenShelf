using System.Windows;
using OpenShelf.Core;
using OpenShelf.Settings;
using OpenShelf.Shelf;
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

    public static void Start(Application app)
    {
        _store = new PreferencesStore();
        Preferences = _store;

        var toasts = new ToastService();
        var shelves = new ShelfService(_store, toasts);
        Toasts = toasts;
        Shelves = shelves;
        Progress = new ProgressService(toasts);

        var settings = new SettingsWindowService(_store, Commands);
        Settings = settings;

        var catalog = new ConversionWheelCatalog(_store, Progress, toasts, shelves);
        Catalog = catalog;
        var wheels = new WheelService(shelves, toasts, _store, catalog);
        Wheels = wheels;

        Commands.OpenSettings = () => settings.Show();
        Commands.OpenWheelForSelection = wheels.OpenForSelection;
        Commands.TakeTour = () => toasts.Show("The tour arrives in a later build", CursorPoint());
        Commands.CheckForUpdates = () => toasts.Show("Update checks arrive in a later build", CursorPoint());
        Commands.Quit = app.Shutdown;

        _tray = new TrayService(shelves, Commands);
        _tray.Start();
        wheels.Start();
    }

    /// <summary>A second launch asks the running copy to show Settings (SPEC §4).</summary>
    public static void ShowSettingsFromSecondLaunch() => Settings?.Show();

    public static void Stop()
    {
        Shelves?.Flush();
        Wheels?.Dispose();
        _tray?.Dispose();
        _store?.Flush();
        _store?.Dispose();
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
