using OpenShelf.Core;

namespace OpenShelf.App;

/// <summary>
/// Passes everything through to the shelves, and tells a listener about every result a tool
/// hands back, so watched folders never treat OpenShelf's own output as a new arrival.
/// </summary>
public sealed class ResultTap(IShelfService inner) : IShelfService
{
    public Action<IReadOnlyList<string>>? Saved { get; set; }

    public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) => inner.NewShelf(paths, near);
    public bool AddToRecentShelf(IReadOnlyList<string> paths) => inner.AddToRecentShelf(paths);
    public bool HasOpenShelf => inner.HasOpenShelf;
    public void ShowAll() => inner.ShowAll();
    public void CloseAll() => inner.CloseAll();
    public IReadOnlyList<string> SelectionForWheel() => inner.SelectionForWheel();
    public object? DraggingShelf => inner.DraggingShelf;

    public void AddResults(IReadOnlyList<string> paths, object? sourceShelf)
    {
        Saved?.Invoke(paths);
        inner.AddResults(paths, sourceShelf);
    }

    public void Flush() => inner.Flush();
}
