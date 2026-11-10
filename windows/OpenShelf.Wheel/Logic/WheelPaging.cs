namespace OpenShelf.Wheel;

public enum SlotKind { Item, More, Back, Empty }

/// <summary>What one wedge of a wheel shows on the current page. Port of WheelSlot in WheelPaging.swift.</summary>
/// <param name="Item">Index into the wheel's entries for an <see cref="SlotKind.Item"/>, else -1.</param>
/// <param name="TargetPage">The page More or Back turns to (pages are numbered from 0).</param>
public readonly record struct WheelSlot(SlotKind Kind, int Item = -1, int TargetPage = 0, int PageCount = 1)
{
    public static WheelSlot ForItem(int index) => new(SlotKind.Item, index);
    public static WheelSlot More(int toPage, int of) => new(SlotKind.More, -1, toPage, of);
    public static WheelSlot Back(int toPage, int of) => new(SlotKind.Back, -1, toPage, of);
    public static WheelSlot Empty => new(SlotKind.Empty);

    public bool IsEmpty => Kind == SlotKind.Empty;
    public bool IsPageTurn => Kind is SlotKind.More or SlotKind.Back;

    /// <summary>"More · 2 of 2" / "Back · 1 of 2"; null for items and gaps (the window supplies an item's title).</summary>
    public string? PageLabel => Kind switch
    {
        SlotKind.More => $"More · {TargetPage + 1} of {PageCount}",
        SlotKind.Back => $"Back · {TargetPage + 1} of {PageCount}",
        _ => null,
    };
}

/// <summary>
/// Splits long wheels into pages so their wedges stay big enough to hit mid-drag. Up to seven entries show all;
/// a longer wheel shows seven wedges: six entries then More in the last wedge (clockwise from 12 o'clock, about
/// 10 o'clock), with Back in that same wedge on the last page and empty wedges filling any gap.
/// </summary>
public static class WheelPaging
{
    public const int SlotCount = 7;
    public const int ItemsPerPage = SlotCount - 1;
    public const int PageTurnSlot = SlotCount - 1;

    public static int PageCount(int itemCount) =>
        itemCount > SlotCount ? (itemCount + ItemsPerPage - 1) / ItemsPerPage : 1;

    public static IReadOnlyList<WheelSlot> Slots(int itemCount, int page)
    {
        var pages = PageCount(itemCount);
        if (pages <= 1) return Enumerable.Range(0, itemCount).Select(WheelSlot.ForItem).ToArray();
        page = Math.Clamp(page, 0, pages - 1);
        var start = page * ItemsPerPage;
        var end = Math.Min(start + ItemsPerPage, itemCount);
        var slots = new List<WheelSlot>();
        for (var i = start; i < end; i++) slots.Add(WheelSlot.ForItem(i));
        while (slots.Count < ItemsPerPage) slots.Add(WheelSlot.Empty);
        slots.Add(page == pages - 1 ? WheelSlot.Back(0, pages) : WheelSlot.More(page + 1, pages));
        return slots;
    }
}

/// <summary>
/// Decides when resting on More or Back turns the page: once the pointer has stayed for <see cref="Delay"/>, then
/// not again until it leaves and comes back. Port of PageTurnDwell.
/// </summary>
public sealed class PageTurnDwell
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

    private TimeSpan? _enteredAt;
    private bool _hasTurned;

    public bool IsPending => _enteredAt is not null && !_hasTurned;

    /// <summary>True when the page should turn now.</summary>
    public bool Update(bool isOnPageTurn, TimeSpan now)
    {
        if (!isOnPageTurn) { _enteredAt = null; _hasTurned = false; return false; }
        if (_enteredAt is not { } entered) { _enteredAt = now; return false; }
        if (_hasTurned || now - entered < Delay) return false;
        _hasTurned = true;
        return true;
    }

    /// <summary>The page turned some other way (Return, a click or a drop); a pointer still on the wedge must leave first.</summary>
    public void PageDidTurn(bool isOnPageTurn, TimeSpan now)
    {
        _enteredAt = isOnPageTurn ? _enteredAt ?? now : null;
        _hasTurned = isOnPageTurn;
    }
}

/// <summary>Order of entries on wheels long enough to page. Port of WheelOrdering.ordered; catalogs use it to put everyday options first.</summary>
public static class WheelOrdering
{
    /// <summary><paramref name="list"/> sorted by position in <paramref name="priority"/>; anything missing follows in its original order.</summary>
    public static List<T> Ordered<T>(IEnumerable<T> list, IReadOnlyList<T> priority) where T : notnull
    {
        var rank = new Dictionary<T, int>();
        for (var i = 0; i < priority.Count; i++) rank.TryAdd(priority[i], i);
        return list.Select((element, offset) => (rank: rank.TryGetValue(element, out var r) ? r : priority.Count, offset, element))
            .OrderBy(x => x.rank).ThenBy(x => x.offset).Select(x => x.element).ToList();
    }
}
