namespace OpenShelf.Core;

// The seams between projects. Feature projects depend on these, never on each
// other; OpenShelf.App wires the implementations together. Orchestrator-owned:
// propose changes rather than editing.

/// <summary>A point on the virtual screen in PHYSICAL pixels (what GetCursorPos returns).
/// Every service boundary uses physical pixels; convert to WPF DIPs only inside a window.</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>A global keyboard shortcut: modifiers plus a Win32 virtual-key code.</summary>
public readonly record struct HotKey(Modifiers Modifiers, int VirtualKey);

// ---------------------------------------------------------------- shelves

/// <summary>Shelves: floating windows that hold file references. Implemented in OpenShelf.Shelf.</summary>
public interface IShelfService
{
    /// <summary>Opens a new shelf holding <paramref name="paths"/> next to <paramref name="near"/>.</summary>
    void NewShelf(IReadOnlyList<string> paths, ScreenPoint near);

    /// <summary>Adds to the most recently used shelf (revealing it). False when no shelf is open.</summary>
    bool AddToRecentShelf(IReadOnlyList<string> paths);

    bool HasOpenShelf { get; }
    void ShowAll();
    void CloseAll();

    /// <summary>Files the keyboard wheel should act on: the focused shelf's selection, else the most
    /// recently used shelf's selection when OpenShelf is in front, else empty (SPEC §2).</summary>
    IReadOnlyList<string> SelectionForWheel();

    /// <summary>The shelf a drag is currently coming out of, or null. Opaque token for <see cref="AddResults"/>.</summary>
    object? DraggingShelf { get; }

    /// <summary>Puts conversion/tool results on <paramref name="sourceShelf"/> when the
    /// "add results back to the shelf" preference is on and the shelf is still open.</summary>
    void AddResults(IReadOnlyList<string> paths, object? sourceShelf);

    /// <summary>Saves open shelves now (called on quit).</summary>
    void Flush();
}

// ---------------------------------------------------------------- feedback

/// <summary>Small confirmation card next to the pointer ("Copied path"). Implemented in OpenShelf.Shelf.</summary>
public interface IToastService
{
    void Show(string message, ScreenPoint near);
}

/// <summary>The progress card for a batch ("Converting to JPG · 1 of 3 saved", with a cancel button). SPEC §2.3.</summary>
public interface IProgressService
{
    /// <summary>Shows a card next to <paramref name="near"/>. Cancelling the card cancels <paramref name="cancel"/>.</summary>
    IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel);
}

public interface IProgressCard : IDisposable
{
    /// <summary>Thread-safe. <paramref name="fraction"/> is 0..1 within the current file, or null when unknown.</summary>
    void Report(int completed, double? fraction = null, string? detail = null);

    /// <summary>Closes the card and shows <paramref name="message"/> as a toast in its place.</summary>
    void Finish(string message);
}

// ---------------------------------------------------------------- wheels

/// <summary>Mid-drag and keyboard wheels. Implemented in OpenShelf.Wheel.</summary>
public interface IWheelService : IDisposable
{
    /// <summary>Starts watching for file drags, trigger keys and the global shortcut.</summary>
    void Start();

    /// <summary>Opens the Convert wheel at the pointer for the shelf or Explorer selection (SPEC §2, "Without a drag").</summary>
    void OpenForSelection();

    /// <summary>Raised when a wheel opens or a wedge runs, for the tour (SPEC §6).</summary>
    event Action<WheelEvent>? Activity;
}

/// <param name="Kind">The wheel.</param>
/// <param name="EntryId">Null when the wheel merely opened; the entry's id when a wedge ran.</param>
/// <param name="Keyboard">True for the keyboard wheel, false for a mid-drag wheel.</param>
public sealed record WheelEvent(WheelKind Kind, string? EntryId, IReadOnlyList<string> Paths, bool Keyboard, bool OptionsRequested);

/// <summary>What the Convert and Tools wheels offer for a set of files. The Actions wheel is built into OpenShelf.Wheel.</summary>
public interface IWheelCatalog
{
    WheelContent Convert(IReadOnlyList<string> paths);
    WheelContent Tools(IReadOnlyList<string> paths);
}

/// <param name="Entries">In display order, everyday ones first. More than 7 are paged by the wheel (SPEC §2).</param>
/// <param name="EmptyMessage">Toast shown instead of an empty wheel, e.g. "Keynote needs LibreOffice".</param>
public sealed record WheelContent(IReadOnlyList<WheelEntry> Entries, string? EmptyMessage = null);

/// <param name="Id">Stable id, e.g. "convert.jpg", "tool.crop".</param>
/// <param name="Title">Wedge label ("JPG", "Crop"); the wheel upper-cases it.</param>
/// <param name="Glyph">Optional Segoe Fluent/MDL2 icon glyph shown above the label.</param>
/// <param name="IconPath">Optional image file shown instead of a glyph (an installed app's icon).</param>
/// <param name="HasOptions">True when holding Alt should request the options window first (SPEC §2.3.1).</param>
/// <param name="Run">Runs the entry. Called on the UI thread after the drop has returned; do long work asynchronously.</param>
public sealed record WheelEntry(string Id, string Title, Func<WheelRun, Task> Run, string? Glyph = null, string? IconPath = null, bool HasOptions = false);

/// <param name="Pointer">Where the wedge was dropped or chosen; toasts and windows appear here.</param>
/// <param name="OptionsRequested">Alt was held (or Alt+Return / Alt+digit used).</param>
/// <param name="SourceShelf">Token from <see cref="IShelfService.DraggingShelf"/> or the shelf whose selection was used.</param>
public sealed record WheelRun(IReadOnlyList<string> Paths, ScreenPoint Pointer, bool OptionsRequested, object? SourceShelf);

// ---------------------------------------------------------------- preferences

/// <summary>User settings, persisted as JSON under %APPDATA%\OpenShelf. Implemented in OpenShelf.Settings.
/// Setters save and raise <see cref="Changed"/> on the UI thread.</summary>
public interface IPreferences
{
    event Action? Changed;

    /// <summary>Trigger keys per wheel: any combination of Ctrl, Alt, Shift (never Win alone), at least one.</summary>
    IReadOnlyDictionary<WheelKind, Modifiers> Triggers { get; }
    void SetTrigger(WheelKind wheel, Modifiers modifiers);

    /// <summary>Shortcut that opens the wheel for the selection; null when turned off. Default Ctrl+Alt+Space.</summary>
    HotKey? SelectionHotKey { get; set; }

    /// <summary>Shake the pointer mid-drag to open the Actions wheel. Default on.</summary>
    bool ShakeToOpen { get; set; }

    bool RemoveAfterDragOut { get; set; }   // default off
    bool RestoreShelves { get; set; }       // default on
    bool HideDockedShelves { get; set; }    // default on
    bool ResultsBackOnShelf { get; set; }   // default on
    bool OpenAtLogin { get; set; }          // default off
    bool AutomaticUpdates { get; set; }     // default on

    /// <summary>Free-form JSON values for features that own their own model (output settings, watched folders, tour progress).</summary>
    string? GetJson(string key);
    void SetJson(string key, string? json);
}

// ---------------------------------------------------------------- shell

/// <summary>Notification-area icon and its menu. Implemented in OpenShelf.Shelf.</summary>
public interface ITrayService : IDisposable
{
    void Start();
}

/// <summary>The settings window. Implemented in OpenShelf.Settings.</summary>
public interface ISettingsWindow
{
    /// <param name="tab">"general", "wheels", "shelves", "output", "formats", "updates", or null for the last used.</param>
    void Show(string? tab = null);
}

/// <summary>App-level commands the tray menu and settings invoke. The composition root fills these in.</summary>
public sealed class AppCommands
{
    public Action OpenSettings { get; set; } = () => { };
    public Action OpenWheelForSelection { get; set; } = () => { };
    public Action TakeTour { get; set; } = () => { };
    public Action CheckForUpdates { get; set; } = () => { };
    public Action Quit { get; set; } = () => { };
}
