using System.IO;
namespace OpenShelf.Shelf;

/// <summary>Click selection following Explorer's Ctrl/Shift conventions.</summary>
internal sealed class SelectionState<T> where T : notnull
{
    readonly HashSet<T> _selected = new();
    T? _anchor;
    bool _hasAnchor;

    public IReadOnlyCollection<T> Selected => _selected;
    public int Count => _selected.Count;
    public bool Contains(T item) => _selected.Contains(item);

    /// <summary>A click on <paramref name="item"/>: Ctrl toggles, Shift selects the range from the anchor, plain selects only it.</summary>
    public void Click(IReadOnlyList<T> items, T item, bool ctrl, bool shift)
    {
        if (ctrl)
        {
            if (!_selected.Remove(item)) _selected.Add(item);
            SetAnchor(item);
            return;
        }
        if (shift && _hasAnchor)
        {
            int from = IndexOf(items, _anchor!), to = IndexOf(items, item);
            if (from >= 0 && to >= 0)
            {
                _selected.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _selected.Add(items[i]);
                return;
            }
        }
        _selected.Clear();
        _selected.Add(item);
        SetAnchor(item);
    }

    public void SelectAll(IEnumerable<T> items) { _selected.Clear(); foreach (var i in items) _selected.Add(i); }
    public void Clear() => _selected.Clear();

    /// <summary>Selects only this item and makes it the anchor for a later Shift-click.</summary>
    public void SelectOnly(T item) { _selected.Clear(); _selected.Add(item); SetAnchor(item); }

    public void Set(IEnumerable<T> items)
    {
        var next = new HashSet<T>(items);
        _selected.Clear();
        foreach (var i in next) _selected.Add(i);
    }

    /// <summary>Drops anything no longer in <paramref name="items"/> (after removals).</summary>
    public void Prune(IEnumerable<T> items)
    {
        var keep = new HashSet<T>(items);
        _selected.RemoveWhere(i => !keep.Contains(i));
        if (_hasAnchor && !keep.Contains(_anchor!)) { _hasAnchor = false; _anchor = default; }
    }

    void SetAnchor(T item) { _anchor = item; _hasAnchor = true; }

    static int IndexOf(IReadOnlyList<T> items, T item)
    {
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < items.Count; i++) if (cmp.Equals(items[i], item)) return i;
        return -1;
    }
}

/// <summary>Selection by dragging a rectangle over items, following Explorer's rules: a plain drag selects what the
/// rectangle touches, Shift adds to the existing selection and Ctrl toggles the touched items.</summary>
internal static class RubberBand
{
    public static HashSet<T> Selection<T>(IReadOnlyDictionary<T, Box> frames, Box rect, IReadOnlyCollection<T> initial, bool ctrl, bool shift)
        where T : notnull
    {
        var touched = new HashSet<T>(frames.Where(f => f.Value.Intersects(rect)).Select(f => f.Key));
        if (ctrl)
        {
            var result = new HashSet<T>(initial);
            result.SymmetricExceptWith(touched);
            return result;
        }
        if (shift)
        {
            var result = new HashSet<T>(initial);
            result.UnionWith(touched);
            return result;
        }
        return touched;
    }
}

/// <summary>Path handling for what goes on a shelf.</summary>
internal static class ShelfPaths
{
    /// <summary>Full path without a trailing separator (roots keep theirs); null when it isn't a usable path.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var full = Path.GetFullPath(raw);
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The incoming paths that aren't already on the shelf (case-insensitive) and aren't repeated, in order.</summary>
    public static List<string> NewPaths(IEnumerable<string> existing, IEnumerable<string?> incoming)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing) { var n = Normalize(e); if (n != null) seen.Add(n); }
        var result = new List<string>();
        foreach (var raw in incoming)
        {
            var n = Normalize(raw);
            if (n != null && seen.Add(n)) result.Add(n);
        }
        return result;
    }
}
