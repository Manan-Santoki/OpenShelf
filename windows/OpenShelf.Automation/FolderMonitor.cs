namespace OpenShelf.Automation;

/// <summary>
/// Watches one folder (top level only) and reports new, finished files. A FileSystemWatcher says when something
/// changed; the folder is then listed on a timer thread, every <see cref="ArrivalTimings.Poll"/> while a new file is
/// still settling, and every <see cref="SlowScan"/> otherwise (so missed events and a vanished folder are noticed).
/// Never throws: a folder that goes away calls <c>onUnavailable</c> once.
/// </summary>
public sealed class FolderMonitor : IDisposable
{
    public static TimeSpan SlowScan { get; set; } = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly ArrivalTimings _timings;
    private readonly Action<IReadOnlyList<string>> _onArrival;
    private readonly Action _onUnavailable;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private ArrivalTracker? _tracker;
    private bool _disposed;

    public FolderMonitor(string path, Action<IReadOnlyList<string>> onArrival, Action onUnavailable, ArrivalTimings? timings = null)
    {
        _path = path;
        _onArrival = onArrival;
        _onUnavailable = onUnavailable;
        _timings = timings ?? ArrivalTimings.Default;
    }

    /// <summary>Takes the first listing and starts watching. False when the folder is missing or unreadable.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return false;
            var existing = List(withIdentity: true, known: null);
            if (existing is null) return false;
            _tracker = new ArrivalTracker(existing, _timings);
            try
            {
                _watcher = new FileSystemWatcher(_path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += (_, _) => Arm(_timings.Poll);
                _watcher.Changed += (_, _) => Arm(_timings.Poll);
                _watcher.Renamed += (_, _) => Arm(_timings.Poll);
                _watcher.Deleted += (_, _) => Arm(_timings.Poll);
                _watcher.Error += (_, _) => Arm(_timings.Poll);
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Without events the slow scan and the settle polling still find files.
            }
            _timer = new Timer(_ => Scan(), null, SlowScan, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    private void Arm(TimeSpan due)
    {
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            try { _timer.Change(due, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    private void Scan()
    {
        IReadOnlyList<string> ready = [];
        var gone = false;
        lock (_gate)
        {
            if (_disposed || _tracker is null) return;
            var entries = List(withIdentity: false, known: _tracker);
            if (entries is null) gone = true;
            else
            {
                try { ready = _tracker.Observe(entries, DateTime.UtcNow); } catch { }
                try { _timer?.Change(_tracker.HasPending ? _timings.Poll : SlowScan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }
        if (gone)
        {
            Dispose();
            try { _onUnavailable(); } catch { }
            return;
        }
        if (ready.Count > 0)
        {
            try { _onArrival(ready.Select(n => Path.Combine(_path, n)).ToList()); } catch { }
        }
    }

    /// <summary>Lists the folder; null when it cannot be read. Identity and busy checks are only done where needed:
    /// ids for every file at the first listing, and ids plus busy flags for names the tracker does not know yet.</summary>
    private List<ArrivalTracker.Entry>? List(bool withIdentity, ArrivalTracker? known)
    {
        try
        {
            var list = new List<ArrivalTracker.Entry>();
            foreach (var info in new DirectoryInfo(_path).EnumerateFiles())
            {
                try
                {
                    var regular = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) == 0;
                    if (!regular || ArrivalFilter.IsIgnored(info.Name)) { list.Add(new ArrivalTracker.Entry(info.Name, null, 0, false)); continue; }
                    if (withIdentity) { list.Add(new ArrivalTracker.Entry(info.Name, FileIdentity.IdOf(info.FullName), info.Length)); continue; }
                    if (known!.IsKnown(info.Name)) { list.Add(new ArrivalTracker.Entry(info.Name, null, info.Length)); continue; }
                    list.Add(new ArrivalTracker.Entry(info.Name, FileIdentity.IdOf(info.FullName), info.Length, true, FileIdentity.IsBusy(info.FullName)));
                }
                catch (IOException) { /* vanished between listing and inspection */ }
            }
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _watcher?.Dispose(); } catch { }
            _timer?.Dispose();
        }
    }
}
