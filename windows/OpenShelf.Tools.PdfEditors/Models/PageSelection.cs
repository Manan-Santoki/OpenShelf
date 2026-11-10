using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>How a click changes the page selection.</summary>
public enum SelectMode
{
    /// <summary>Plain click: only this page.</summary>
    Replace,
    /// <summary>Ctrl-click: add or remove this page.</summary>
    Toggle,
    /// <summary>Shift-click: every page from the last clicked one to this one.</summary>
    Range,
}

/// <summary>The selected pages of the Pages editor, by the page's stable id (so they stay selected while moving).</summary>
public sealed class PageSelection
{
    private readonly HashSet<Guid> _ids = [];
    private Guid? _anchor;

    public int Count => _ids.Count;
    public bool IsEmpty => _ids.Count == 0;
    public bool Contains(Guid id) => _ids.Contains(id);
    public IReadOnlyCollection<Guid> Ids => _ids;

    public void Clear()
    {
        _ids.Clear();
        _anchor = null;
    }

    public void Click(Guid id, IReadOnlyList<PdfPageEdit> pages, SelectMode mode)
    {
        switch (mode)
        {
            case SelectMode.Toggle:
                if (!_ids.Remove(id)) _ids.Add(id);
                _anchor = id;
                break;
            case SelectMode.Range when _anchor is { } anchor:
            {
                var from = IndexOf(pages, anchor);
                var to = IndexOf(pages, id);
                if (from < 0 || to < 0) goto default;
                _ids.Clear();
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) _ids.Add(pages[i].Id);
                break;
            }
            default:
                _ids.Clear();
                _ids.Add(id);
                _anchor = id;
                break;
        }
    }

    public void SelectAll(IReadOnlyList<PdfPageEdit> pages)
    {
        _ids.Clear();
        foreach (var page in pages) _ids.Add(page.Id);
    }

    /// <summary>Forgets pages that no longer exist (deleted, or restored away by undo).</summary>
    public void Prune(IReadOnlyList<PdfPageEdit> pages)
    {
        var present = pages.Select(p => p.Id).ToHashSet();
        _ids.RemoveWhere(id => !present.Contains(id));
        if (_anchor is { } a && !present.Contains(a)) _anchor = null;
    }

    /// <summary>The selected pages' positions, ascending.</summary>
    public IReadOnlyList<int> Indices(IReadOnlyList<PdfPageEdit> pages) =>
        pages.Select((p, i) => (p, i)).Where(x => _ids.Contains(x.p.Id)).Select(x => x.i).ToList();

    private static int IndexOf(IReadOnlyList<PdfPageEdit> pages, Guid id)
    {
        for (var i = 0; i < pages.Count; i++) if (pages[i].Id == id) return i;
        return -1;
    }
}

/// <summary>The arithmetic of dragging pages to a new place: which order to preview while the pointer moves, and where the
/// pages land when it is released.</summary>
public static class PageReorder
{
    /// <summary>The order with the <paramref name="moving"/> pages (kept in their current order) taken out and put back
    /// starting at <paramref name="insertAt"/>, an index into the list without them.</summary>
    public static IReadOnlyList<Guid> Order(IReadOnlyList<Guid> order, IReadOnlySet<Guid> moving, int insertAt)
    {
        var block = order.Where(moving.Contains).ToList();
        var rest = order.Where(id => !moving.Contains(id)).ToList();
        rest.InsertRange(Math.Clamp(insertAt, 0, rest.Count), block);
        return rest;
    }

    /// <summary>Where the moving pages should land when the pointer is over <paramref name="hover"/>: a page after the
    /// block puts the block after it, a page before puts the block before it. Null when the pointer is over the block
    /// itself or over nothing.</summary>
    public static int? InsertionIndex(IReadOnlyList<Guid> displayOrder, IReadOnlySet<Guid> moving, Guid? hover)
    {
        if (hover is not { } target || moving.Contains(target)) return null;
        var rest = displayOrder.Where(id => !moving.Contains(id)).ToList();
        var r = rest.IndexOf(target);
        if (r < 0) return null;
        var hoverPosition = displayOrder.ToList().IndexOf(target);
        var firstMoving = displayOrder.ToList().FindIndex(moving.Contains);
        return hoverPosition > firstMoving ? r + 1 : r;
    }
}
