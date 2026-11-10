using OpenShelf.Core;

namespace OpenShelf.Shelf;

/// <summary>Owns all open shelf windows. Used on the WPF UI thread.</summary>
public sealed class ShelfService : IShelfService
{
    // Index 0 is the most recently used shelf.
    readonly List<ShelfWindow> _windows = new();

    public bool HasOpenShelf => _windows.Count > 0;

    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near)
    {
        try
        {
            var model = new ShelfModel();
            model.Add(paths ?? Array.Empty<string>());
            var window = new ShelfWindow(model);
            window.Interacted += MakeRecent;
            window.Closed += (_, _) =>
            {
                _windows.Remove(window);
                Log.Info($"Shelf closed ({_windows.Count} open)");
            };
            _windows.Insert(0, window);
            window.ShowNear(near);
            Log.Info($"Shelf opened with {model.Items.Count} file(s) at {near.X},{near.Y} ({_windows.Count} open)");
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
            window.BringToTop();
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
        try { foreach (var w in _windows.AsEnumerable().Reverse().ToList()) w.BringToTop(); }
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

    void MakeRecent(ShelfWindow window)
    {
        int i = _windows.IndexOf(window);
        if (i <= 0) { if (i == 0) window.BringToTop(); return; }
        _windows.RemoveAt(i);
        _windows.Insert(0, window);
        window.BringToTop();
    }
}
