using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>Watches for file drags and trigger chords and shows the Convert / Actions wheels.</summary>
public sealed class WheelService : IWheelService
{
    private readonly IToastService _toasts;
    private readonly ActionWheels _wheels;
    private DragMonitor? _monitor;
    private WheelWindow? _window;
    private ScreenPoint _lastPointer;

    public WheelService(IShelfService shelves, IToastService toasts)
    {
        _toasts = toasts;
        _wheels = new ActionWheels(shelves, toasts);
    }

    /// <summary>Call on the WPF UI thread.</summary>
    public void Start()
    {
        if (_monitor is not null) return;
        _window = new WheelWindow
        {
            BuildModel = _wheels.Build,
            Unavailable = kind => _wheels.Unavailable(kind, _lastPointer),
            Dropped = (item, files, at) => item.Run(files, at),
        };
        _window.Warmup();
        _monitor = new DragMonitor();
        _monitor.DragStarted += p => { _lastPointer = p; _window?.HideNow(); };
        _monitor.DragEnded += () => _window?.DragEnded();
        _monitor.ChordChosen += OnChord;
        _monitor.Start();
        Log.Write("wheel service started");
    }

    private void OnChord(WheelKind kind, ScreenPoint pointer)
    {
        try
        {
            _lastPointer = pointer;
            if (kind == WheelKind.Tools)
            {
                _toasts.Show("Tools arrive in the next milestone", pointer);
                return;
            }
            _window?.ShowProbe(kind, pointer);
        }
        catch (Exception ex) { Log.Error("OnChord", ex); }
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _monitor = null;
        try { _window?.Close(); } catch (Exception ex) { Log.Error("Dispose", ex); }
        _window = null;
    }
}
