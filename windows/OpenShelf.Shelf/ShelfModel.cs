using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenShelf.Shelf;

/// <summary>One file reference on a shelf.</summary>
internal sealed class ShelfItem : INotifyPropertyChanged
{
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    ImageSource? _thumbnail;
    bool _thumbnailRequested;
    // The shell struggles with dozens of simultaneous thumbnail requests; a few at a time is quicker overall.
    static readonly SemaphoreSlim Gate = new(3);

    public ShelfItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(Name)) Name = path;
    }

    public string Path { get; }
    public string Name { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Loads the shell thumbnail off the UI thread, once.</summary>
    public void RequestThumbnail(int width, int height)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        var path = Path;
        Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                System.Windows.Media.Imaging.BitmapSource? bmp = null;
                for (int attempt = 0; attempt < 3 && bmp == null; attempt++)
                {
                    bmp = ShellThumbnails.Load(path, width, height);
                    if (bmp == null) await Task.Delay(250);
                }
                if (bmp == null) return;
                _ = _dispatcher.BeginInvoke(() => Thumbnail = bmp);
            }
            finally { Gate.Release(); }
        });
    }
}

/// <summary>The contents and selection of one shelf, independent of any window.</summary>
internal sealed class ShelfModel
{
    /// <summary>Tests switch this off so no shell thumbnails are requested.</summary>
    public static bool LoadThumbnails { get; set; } = true;

    public ObservableCollection<ShelfItem> Items { get; } = new();
    public SelectionState<ShelfItem> Selection { get; } = new();

    /// <summary>Whether the shelf has ever held items, so an emptied shelf closes itself
    /// while a freshly created empty one stays open.</summary>
    public bool HasHadItems { get; private set; }

    /// <summary>Raised after any change to the selection.</summary>
    public event Action? SelectionChanged;

    /// <summary>Adds paths, skipping duplicates (case-insensitive full path). Returns the number added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var fresh = ShelfPaths.NewPaths(Items.Select(i => i.Path), paths);
        foreach (var full in fresh)
        {
            var item = new ShelfItem(full);
            Items.Add(item);
            if (LoadThumbnails) item.RequestThumbnail(256, 256);
        }
        if (Items.Count > 0) HasHadItems = true;
        return fresh.Count;
    }

    public void Remove(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList()) Items.Remove(item);
        Selection.Prune(Items);
        SelectionChanged?.Invoke();
    }

    /// <summary>Removes items whose file no longer exists.</summary>
    public int RemoveMissing()
    {
        var gone = Items.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList();
        if (gone.Count > 0) Remove(gone);
        return gone.Count;
    }

    public IReadOnlyList<ShelfItem> Selected => Items.Where(Selection.Contains).ToList();

    /// <summary>The selection, or every item when nothing is selected.</summary>
    public IReadOnlyList<ShelfItem> SelectedOrAll => Selection.Count == 0 ? Items.ToList() : Selected;

    public void Click(ShelfItem item, bool ctrl, bool shift) { Selection.Click(Items, item, ctrl, shift); SelectionChanged?.Invoke(); }
    public void SelectOnly(ShelfItem item) { Selection.Set(new[] { item }); SelectionChanged?.Invoke(); }
    public void SelectAll() { Selection.SelectAll(Items); SelectionChanged?.Invoke(); }
    public void ClearSelection() { if (Selection.Count == 0) return; Selection.Clear(); SelectionChanged?.Invoke(); }
    public void SetSelection(IEnumerable<ShelfItem> items) { Selection.Set(items); SelectionChanged?.Invoke(); }
}
