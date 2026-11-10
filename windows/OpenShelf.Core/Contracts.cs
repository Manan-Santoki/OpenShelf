namespace OpenShelf.Core;

/// <summary>A point on the virtual screen in PHYSICAL pixels (what GetCursorPos returns).
/// Every service boundary uses physical pixels; convert to WPF DIPs only inside a window.</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Shelves: floating windows that hold file references. Implemented in Shelf/.</summary>
public interface IShelfService
{
    /// <summary>Opens a new shelf holding <paramref name="paths"/> next to <paramref name="near"/>.</summary>
    void NewShelf(IReadOnlyList<string> paths, ScreenPoint near);

    /// <summary>Adds to the most recently used shelf (revealing it). False when no shelf is open.</summary>
    bool AddToRecentShelf(IReadOnlyList<string> paths);

    bool HasOpenShelf { get; }
    void ShowAll();
    void CloseAll();
}

/// <summary>Small confirmation card next to the pointer ("Copied path"). Implemented in Toast/.</summary>
public interface IToastService
{
    void Show(string message, ScreenPoint near);
}

/// <summary>Mid-drag wheels. Implemented in Drag/ and Wheel/.</summary>
public interface IWheelService : IDisposable
{
    /// <summary>Starts watching for file drags and trigger keys.</summary>
    void Start();
}

/// <summary>Notification-area icon and its menu. Implemented in Tray/.</summary>
public interface ITrayService : IDisposable
{
    void Start();
}
