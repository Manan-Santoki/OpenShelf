using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Shelf;

/// <summary>Owns all open shelf windows. Used on the WPF UI thread.</summary>
public sealed class ShelfService : IShelfService, IShelfHost
{
    readonly IPreferences _preferences;
    readonly IToastService _toasts;
    readonly string _storePath;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly PreviewController _preview = new();

    // Index 0 is the most recently used shelf.
    readonly List<ShelfWindow> _windows = new();
    bool _restored, _flushed;

    public ShelfService(IPreferences preferences, IToastService toasts) : this(preferences, toasts, ShelfStore.DefaultPath) { }

    internal ShelfService(IPreferences preferences, IToastService toasts, string storePath)
    {
        _preferences = preferences;
        _toasts = toasts;
        _storePath = storePath;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        // Saved shelves reopen once the dispatcher is running; nothing is saved before that so they are never overwritten.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestoreSaved));
    }

    IPreferences IShelfHost.Preferences => _preferences;
    PreviewController IShelfHost.Preview => _preview;
    void IShelfHost.Toast(string message, ScreenPoint near) => _toasts.Show(message, near);
    void IShelfHost.Interacted(ShelfWindow window) => MakeRecent(window);
    void IShelfHost.Changed(ShelfWindow window) => ScheduleSave();

    // ---------- IShelfService ----------

    public bool HasOpenShelf => _windows.Count > 0;
    public object? DraggingShelf => _windows.FirstOrDefault(w => w.IsDragging);

    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near)
    {
        try
        {
            var window = Open(paths ?? Array.Empty<string>());
            window.PlaceNear(near);
            Log.Info($"Shelf opened with {window.Model.Items.Count} file(s) at {near.X},{near.Y} ({_windows.Count} open)");
        }
        catch (Exception ex) { Log.Error("NewShelf", ex); }
    }

    public bool AddToRecentShelf(IReadOnlyList<string> paths)
    {
        try
        {
            if (_windows.Count == 0) return false;
            var window = _windows[0];
            int added = window.Model.Add(paths ?? Array.Empty<string>());
            window.BringForward();
            Log.Info($"Added {added} of {paths?.Count ?? 0} file(s) to recent shelf");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("AddToRecentShelf", ex);
            return false;
        }
    }

    public void ShowAll()
    {
        try
        {
            foreach (var w in _windows.AsEnumerable().Reverse().ToList()) w.BringForward();
            _windows.FirstOrDefault()?.Focus2();
        }
        catch (Exception ex) { Log.Error("ShowAll", ex); }
    }

    public void CloseAll()
    {
        try
        {
            foreach (var w in _windows.ToList())
            {
                try { w.Close(); } catch (Exception ex) { Log.Error("CloseAll item", ex); }
            }
        }
        catch (Exception ex) { Log.Error("CloseAll", ex); }
    }

    public IReadOnlyList<string> SelectionForWheel()
    {
        try
        {
            if (_windows.Count == 0) return Array.Empty<string>();
            var fg = Native.GetForegroundWindow();
            var focused = _windows.FirstOrDefault(w => w.Handle != IntPtr.Zero && w.Handle == fg);
            if (focused != null) return focused.Model.Selected.Select(i => i.Path).ToList();
            if (Native.OurProcessInFront()) return _windows[0].Model.Selected.Select(i => i.Path).ToList();
        }
        catch (Exception ex) { Log.Error("SelectionForWheel", ex); }
        return Array.Empty<string>();
    }

    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf)
    {
        try
        {
            if (!_preferences.ResultsBackOnShelf || paths == null || paths.Count == 0) return;
            if (sourceShelf is not ShelfWindow window || !_windows.Contains(window)) return;
            window.Model.Add(paths);
            ScheduleSave();
        }
        catch (Exception ex) { Log.Error("AddResults", ex); }
    }

    public void Flush()
    {
        try
        {
            _saveTimer.Stop();
            SaveNow();
            _flushed = true;
        }
        catch (Exception ex) { Log.Error("Flush", ex); }
    }

    // ---------- windows ----------

    ShelfWindow Open(IReadOnlyList<string> paths, bool expanded = false, ShelfViewMode mode = ShelfViewMode.Grid)
    {
        var model = new ShelfModel();
        model.Add(paths);
        var window = new ShelfWindow(model, this);
        if (expanded || mode != ShelfViewMode.Grid) window.SetState(expanded && model.Items.Count > 0, mode);
        window.Closed += (_, _) =>
        {
            // Quitting closes every window: keep them in the list so the final Flush still saves the open shelves.
            if (Application.Current is not { } app || app.Dispatcher.HasShutdownStarted) return;
            _windows.Remove(window);
            Log.Info($"Shelf closed ({_windows.Count} open)");
            ScheduleSave();
        };
        _windows.Insert(0, window);
        return window;
    }

    internal PreviewController Preview => _preview;

    /// <summary>Test and demo access: the open windows, most recent first.</summary>
    internal IReadOnlyList<ShelfWindow> Windows => _windows;

    /// <summary>Opens a shelf and returns its window (demo access).</summary>
    internal ShelfWindow OpenWindow(IReadOnlyList<string> paths, bool expanded = false, ShelfViewMode mode = ShelfViewMode.Grid) =>
        Open(paths, expanded, mode);

    void MakeRecent(ShelfWindow window)
    {
        int i = _windows.IndexOf(window);
        if (i < 0) return;
        if (i > 0)
        {
            _windows.RemoveAt(i);
            _windows.Insert(0, window);
        }
        window.BringToTop();
    }

    // ---------- saving and restoring ----------

    /// <summary>Reopens the shelves saved by the last session (when "restore shelves" is on). Runs once on its own shortly after
    /// the service is created; calling it again does nothing.</summary>
    public void RestoreSaved()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            if (!_preferences.RestoreShelves) return;
            foreach (var saved in ShelfStore.Load(_storePath))
            {
                var paths = saved.Items.Select(ShelfStore.Resolve).Where(p => p != null).Select(p => p!).ToList();
                if (paths.Count == 0) continue;     // gone, or nothing left: not reopened
                var mode = string.Equals(saved.ViewMode, "list", StringComparison.OrdinalIgnoreCase) ? ShelfViewMode.List : ShelfViewMode.Grid;
                var edge = saved.DockedEdge switch { "left" => ShelfEdge.Left, "right" => ShelfEdge.Right, _ => (ShelfEdge?)null };
                var window = Open(paths, saved.Expanded, mode);
                window.PlaceAt(new Box(saved.X, saved.Y, saved.Width, saved.Height), edge, pop: false);
            }
            Log.Info($"Restored {_windows.Count} shelf/shelves");
        }
        catch (Exception ex) { Log.Error("Restore", ex); }
        ScheduleSave();
    }

    void ScheduleSave()
    {
        if (!_restored || _flushed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        if (!_restored || _flushed) return;
        try
        {
            var shelves = new List<SavedShelf>();
            foreach (var w in _windows)
            {
                if (w.Model.Items.Count == 0) continue;
                var card = w.CardScreen();
                shelves.Add(new SavedShelf
                {
                    Items = w.Model.Items.Select(i => ShelfStore.Capture(i.Path)).ToList(),
                    X = card.X, Y = card.Y, Width = card.W, Height = card.H,
                    DockedEdge = w.DockedEdge switch { ShelfEdge.Left => "left", ShelfEdge.Right => "right", _ => null },
                    Expanded = w.IsExpanded,
                    ViewMode = w.ViewMode == ShelfViewMode.List ? "list" : "grid",
                });
            }
            ShelfStore.Save(_storePath, shelves);
        }
        catch (Exception ex) { Log.Error("Saving shelves", ex); }
    }
}
