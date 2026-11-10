using System.Windows.Interop;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>Watches for file drags, trigger keys, shakes and the global shortcut and shows the Convert / Tools / Actions wheels.</summary>
public sealed class WheelService : IWheelService
{
    private const int HotKeyId = 0x0053;

    private readonly IShelfService _shelves;
    private readonly IToastService _toasts;
    private readonly IPreferences _preferences;
    private readonly WheelBuilder _builder;
    private DragMonitor? _monitor;
    private WheelWindow? _window;
    private HwndSource? _hotKeyWindow;
    private HotKey? _registered;
    private ScreenPoint _lastPointer;
    private TriggerMatcher _matcher = TriggerMatcher.Default;
    private bool _started, _disposed;

    public WheelService(IShelfService shelves, IToastService toasts, IPreferences preferences, IWheelCatalog catalog)
    {
        _shelves = shelves;
        _toasts = toasts;
        _preferences = preferences;
        _builder = new WheelBuilder(shelves, toasts, catalog);
    }

    public event Action<WheelEvent>? Activity;

    /// <summary>Call on the WPF UI thread.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _matcher = new TriggerMatcher(_preferences.Triggers);
        _window = new WheelWindow
        {
            BuildModel = _builder.Build,
            Unavailable = (model, near) => _builder.ToastEmpty(model, near),
            Chosen = OnChosen,
            Opened = (model, files, keyboard) => RaiseActivity(new WheelEvent(model.Kind, null, files, keyboard, false)),
            DragSource = () => _shelves.DraggingShelf,
            TriggerSource = () => _matcher,
        };
        _window.Warmup();
        _monitor = new DragMonitor { Matcher = () => _matcher, ShakeEnabled = _preferences.ShakeToOpen };
        _monitor.DragStarted += p => { _lastPointer = p; _window?.DragBegan(); };
        _monitor.DragEnded += () => _window?.DragEnded();
        _monitor.ChordChosen += OnChord;
        _monitor.Shaken += OnShake;
        _monitor.Start();

        CreateHotKeyWindow();
        ApplyHotKey();
        _preferences.Changed += OnPreferencesChanged;
        Log.Write("wheel service started");
    }

    private void OnPreferencesChanged()
    {
        try
        {
            _matcher = new TriggerMatcher(_preferences.Triggers);
            if (_monitor is not null) _monitor.ShakeEnabled = _preferences.ShakeToOpen;
            ApplyHotKey();
        }
        catch (Exception ex) { Log.Error("OnPreferencesChanged", ex); }
    }

    private void OnChord(WheelKind kind, ScreenPoint pointer)
    {
        try
        {
            _lastPointer = pointer;
            _window?.ShowProbe(kind, pointer);
        }
        catch (Exception ex) { Log.Error("OnChord", ex); }
    }

    private void OnShake(ScreenPoint pointer)
    {
        try
        {
            // A shake only opens a wheel when none is open: shaking while choosing must not swap it.
            if (_window is { IsIdle: true }) OnChord(WheelKind.Actions, pointer);
        }
        catch (Exception ex) { Log.Error("OnShake", ex); }
    }

    private void OnChosen(WheelModel model, WheelEntry entry, IReadOnlyList<string> files, ScreenPoint at, bool options, bool keyboard, object? source)
    {
        RaiseActivity(new WheelEvent(model.Kind, entry.Id, files, keyboard, options));
        var run = new WheelRun(files, at, options, source);
        // Entries run on the UI thread; they start their own long work asynchronously.
        _ = RunEntryAsync(entry, run);
    }

    private async Task RunEntryAsync(WheelEntry entry, WheelRun run)
    {
        try { await entry.Run(run); }
        catch (Exception ex)
        {
            Log.Error($"entry {entry.Id}", ex);
            _toasts.Show("Something went wrong", run.Pointer);
        }
    }

    private void RaiseActivity(WheelEvent e)
    {
        try { Activity?.Invoke(e); }
        catch (Exception ex) { Log.Error("Activity handler", ex); }
    }

    // ---- Keyboard wheel ----

    /// <summary>Opens the wheel at the pointer for the shelf selection or the foreground Explorer selection.</summary>
    public void OpenForSelection()
    {
        try
        {
            var pointer = Native.CursorPos();
            IReadOnlyList<string> files = _shelves.SelectionForWheel();
            if (files.Count == 0) files = ExplorerSelection.Get();
            if (files.Count == 0)
            {
                _toasts.Show("Select files in Explorer first", pointer);
                return;
            }
            OpenKeyboardWheel(files);
        }
        catch (Exception ex) { Log.Error("OpenForSelection", ex); }
    }

    /// <summary>Opens the keyboard wheel for known files at the pointer (also the test hook behind the shortcut).</summary>
    public void OpenKeyboardWheel(IReadOnlyList<string> files)
    {
        if (_window is null || files.Count == 0) return;
        _window.OpenKeyboard(files, Native.CursorPos(), null);
    }

    private void CreateHotKeyWindow()
    {
        var parameters = new HwndSourceParameters("OpenShelfWheelHotKey") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0 };
        _hotKeyWindow = new HwndSource(parameters);
        _hotKeyWindow.AddHook(HotKeyHook);
    }

    private nint HotKeyHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && (int)wParam == HotKeyId)
        {
            handled = true;
            OpenForSelection();
        }
        return 0;
    }

    private void ApplyHotKey()
    {
        if (_hotKeyWindow is null) return;
        var wanted = _preferences.SelectionHotKey;
        if (wanted == _registered) return;
        if (_registered is not null)
        {
            Native.UnregisterHotKey(_hotKeyWindow.Handle, HotKeyId);
            _registered = null;
        }
        if (wanted is not { } key) return;
        uint mods = Native.MOD_NOREPEAT;
        if (key.Modifiers.HasFlag(Modifiers.Alt)) mods |= Native.MOD_ALT;
        if (key.Modifiers.HasFlag(Modifiers.Control)) mods |= Native.MOD_CONTROL;
        if (key.Modifiers.HasFlag(Modifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (key.Modifiers.HasFlag(Modifiers.Win)) mods |= Native.MOD_WIN;
        if (Native.RegisterHotKey(_hotKeyWindow.Handle, HotKeyId, mods, (uint)key.VirtualKey))
        {
            _registered = key;
            Log.Write($"hot key registered: {key.Modifiers}+0x{key.VirtualKey:X}");
        }
        else Log.Write($"hot key registration failed ({key.Modifiers}+0x{key.VirtualKey:X}); another app may own it");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.Changed -= OnPreferencesChanged;
        _monitor?.Dispose();
        _monitor = null;
        try
        {
            if (_hotKeyWindow is not null)
            {
                if (_registered is not null) Native.UnregisterHotKey(_hotKeyWindow.Handle, HotKeyId);
                _hotKeyWindow.Dispose();
            }
        }
        catch (Exception ex) { Log.Error("Dispose hotkey", ex); }
        _hotKeyWindow = null;
        try { _window?.HideNow(); _window?.Close(); } catch (Exception ex) { Log.Error("Dispose", ex); }
        _window = null;
    }
}
