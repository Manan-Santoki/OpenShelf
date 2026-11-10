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
    bool _isSelected;
    bool _thumbnailRequested;

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
        private set { _thumbnail = value; Raise(nameof(Thumbnail)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Loads the shell thumbnail off the UI thread, once.</summary>
    public void RequestThumbnail(int width, int height)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        var path = Path;
        Task.Run(() =>
        {
            var bmp = ShellThumbnails.Load(path, width, height);
            if (bmp == null) return;
            _dispatcher.BeginInvoke(() => Thumbnail = bmp);
        });
    }
}

/// <summary>The contents of one shelf, independent of any window.</summary>
internal sealed class ShelfModel
{
    public ObservableCollection<ShelfItem> Items { get; } = new();

    /// <summary>Adds paths, skipping duplicates (case-insensitive full path). Returns the number added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(raw); }
            catch (Exception) { continue; }
            if (Items.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ShelfItem(full);
            Items.Add(item);
            item.RequestThumbnail(184, 124);
            added++;
        }
        return added;
    }

    public void Remove(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList()) Items.Remove(item);
    }

    /// <summary>Removes items whose file no longer exists.</summary>
    public int RemoveMissing()
    {
        var gone = Items.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList();
        Remove(gone);
        return gone.Count;
    }
}
