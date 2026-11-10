using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Tour;

/// <summary>
/// The guided tour (SPEC §6). <see cref="Show"/> is what the tray menu and Settings call (<see cref="AppCommands.TakeTour"/>);
/// <see cref="ShowOnFirstRunIfNeeded"/> opens it by itself on the first launch only. While the window is open, wheel
/// activity from <see cref="IWheelService"/> marks steps under way and done.
/// </summary>
public sealed class TourService : IDisposable
{
    /// <summary>Remembered in the preferences once the tour has been shown.</summary>
    public const string ShownKey = "tour.shown";

    private readonly IPreferences _preferences;
    private readonly IWheelService _wheels;
    private readonly IShelfService _shelves;
    private readonly IToastService _toasts;
    private readonly TourProgress _progress;
    private TourWindow? _window;
    private TourSession? _session;
    private bool _disposed;

    public TourService(IPreferences preferences, IWheelService wheels, IShelfService shelves, IToastService toasts)
    {
        _preferences = preferences;
        _wheels = wheels;
        _shelves = shelves;
        _toasts = toasts;
        _progress = new TourProgress(preferences);
        _wheels.Activity += OnWheelActivity;
    }

    /// <summary>Opens a settings tab ("wheels", "formats"…); the tour's buttons use it. The composition root sets it to <c>settings.Show</c>.</summary>
    public Action<string?> OpenSettings { get; set; } = _ => { };

    /// <summary>The folder the practice files live in; settable for tests and screenshots.</summary>
    public string? PracticeFolder { get; set; }

    /// <summary>Where to put the window instead of centring it (screenshots).</summary>
    internal Point? FixedPosition { get; set; }

    internal TourProgress Progress => _progress;

    /// <summary>The open window, for the demo.</summary>
    internal Window? Window => _window;

    /// <summary>Shows the tour, or brings it to the front when it is open already. Picks up where the person left off.</summary>
    public void Show() => RunOnUi(ShowCore);

    /// <summary>Shows the tour the first time OpenShelf runs, and never by itself again.</summary>
    public void ShowOnFirstRunIfNeeded() => RunOnUi(() =>
    {
        if (HasBeenShown(_preferences)) return;
        ShowCore();
    });

    internal static bool HasBeenShown(IPreferences preferences) => preferences.GetJson(ShownKey) == "true";

    private void ShowCore()
    {
        if (_disposed) return;
        _preferences.SetJson(ShownKey, "true");

        // Start Over in Settings clears the saved progress before asking for the tour.
        var had = _progress.Completed.Count;
        _progress.Reload();

        if (_window is null)
        {
            _session = new TourSession(PracticeFolder);
            _window = new TourWindow(_progress, _session, new TourHost
            {
                Preferences = _preferences,
                Shelves = _shelves,
                Toasts = _toasts,
                OpenSettings = tab => OpenSettings(tab),
            })
            {
                FixedPosition = FixedPosition,
            };
            _window.Closed += (_, _) =>
            {
                _progress.IsActive = false;
                _session?.Stop();
                _window = null;
            };
            _progress.Show(TourStep.Welcome);
            _progress.IsActive = true;
            _session.Start();
            _window.Show();
        }
        else
        {
            if (had > 0 && _progress.Completed.Count == 0) _progress.Show(TourStep.Welcome);
            else _progress.Show(_progress.Current);
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
        }
        // Bring it to the front even when Windows won't let a background app take focus.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Activate();
    }

    private void OnWheelActivity(WheelEvent e)
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) _progress.Record(e);
        else dispatcher.BeginInvoke(() => _progress.Record(e));
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wheels.Activity -= OnWheelActivity;
        RunOnUi(() => _window?.Close());
    }
}
