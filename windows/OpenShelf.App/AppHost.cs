using System.Windows;
using OpenShelf.Core;
using OpenShelf.Shelf;
using OpenShelf.Wheel;

namespace OpenShelf.App;

/// <summary>Wires the services together. Owned by the orchestrator.</summary>
public static class AppHost
{
    public static IShelfService? Shelves { get; private set; }
    public static IToastService? Toasts { get; private set; }
    private static IWheelService? _wheels;
    private static ITrayService? _tray;

    public static void Start(Application app)
    {
        var shelves = new ShelfService();
        var toasts = new ToastService();
        Shelves = shelves;
        Toasts = toasts;

        _tray = new TrayService(shelves, app.Shutdown);
        _tray.Start();

        _wheels = new WheelService(shelves, toasts);
        _wheels.Start();
    }

    public static void Stop()
    {
        _wheels?.Dispose();
        _tray?.Dispose();
    }
}
