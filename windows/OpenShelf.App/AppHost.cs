using System.Windows;
using OpenShelf.Core;

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
        // Filled in once Shelf/, Toast/, Wheel/ and Tray/ exist.
    }

    public static void Stop()
    {
        _wheels?.Dispose();
        _tray?.Dispose();
    }
}
