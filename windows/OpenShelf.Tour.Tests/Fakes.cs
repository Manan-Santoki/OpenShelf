using OpenShelf.Core;

namespace OpenShelf.Tour.Tests;

internal sealed class FakePreferences : IPreferences
{
    public event Action? Changed;
    public Dictionary<string, string> Json { get; } = [];
    public IReadOnlyDictionary<WheelKind, Modifiers> Triggers { get; set; } = new Dictionary<WheelKind, Modifiers>
    {
        [WheelKind.Convert] = Modifiers.Shift,
        [WheelKind.Tools] = Modifiers.Shift | Modifiers.Alt,
        [WheelKind.Actions] = Modifiers.Control,
    };
    public void SetTrigger(WheelKind wheel, Modifiers modifiers) { var d = new Dictionary<WheelKind, Modifiers>(Triggers) { [wheel] = modifiers }; Triggers = d; Changed?.Invoke(); }
    public HotKey? SelectionHotKey { get; set; } = new(Modifiers.Control | Modifiers.Alt, 0x20);
    public bool ShakeToOpen { get; set; } = true;
    public bool RemoveAfterDragOut { get; set; }
    public bool RestoreShelves { get; set; } = true;
    public bool HideDockedShelves { get; set; } = true;
    public bool ResultsBackOnShelf { get; set; } = true;
    public bool OpenAtLogin { get; set; }
    public bool AutomaticUpdates { get; set; } = true;
    public string? GetJson(string key) => Json.GetValueOrDefault(key);
    public void SetJson(string key, string? json) { if (json is null) Json.Remove(key); else Json[key] = json; }
}

internal sealed class FakeWheels : IWheelService
{
    public event Action<WheelEvent>? Activity;
    public void Start() { }
    public void OpenForSelection() { }
    public void Dispose() { }
    public void Raise(WheelEvent e) => Activity?.Invoke(e);
    public bool HasSubscribers => Activity is not null;
}

internal sealed class FakeShelves : IShelfService
{
    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
    public bool HasOpenShelf => false;
    public void ShowAll() { }
    public void CloseAll() { }
    public IReadOnlyList<string> SelectionForWheel() => [];
    public object? DraggingShelf => null;
    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) { }
    public void Flush() { }
}

internal sealed class FakeToasts : IToastService
{
    public List<string> Shown { get; } = [];
    public void Show(string message, ScreenPoint near) => Shown.Add(message);
}

internal static class Sta
{
    /// <summary>Runs the action on a fresh STA thread (WPF rendering and windows need one) and rethrows its failure.</summary>
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}

internal static class Events
{
    public static WheelEvent Ran(WheelKind kind, string id, string path, bool options = false) => new(kind, id, [path], false, options);
    public static WheelEvent Ran(WheelKind kind, string id, params string[] paths) => new(kind, id, paths, false, false);
    public static WheelEvent Opened(WheelKind kind, string path = "a.jpg", bool keyboard = false) => new(kind, null, [path], keyboard, false);
}
