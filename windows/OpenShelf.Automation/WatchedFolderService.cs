using System.Text.Json;
using OpenShelf.Core;

namespace OpenShelf.Automation;

/// <summary>One configured folder, as stored in the preferences key "watchedFolders" (see OpenShelf.Settings.WatchedFolderList).</summary>
public sealed record WatchedFolderSpec(string Path, bool Enabled = true, string? Action = null);

/// <summary>New, finished files that arrived in a watched folder.</summary>
/// <param name="Action">The folder's configured action: null (only add to a shelf) or an entry id such as "convert.jpg" or "tool.compress".</param>
public sealed record FolderArrival(string FolderPath, string? Action, IReadOnlyList<string> Files);

/// <summary>Reads the same JSON the Settings project writes, without referencing it.</summary>
public static class WatchedFolderJson
{
    public const string PreferencesKey = "watchedFolders";

    /// <summary>Unreadable entries are skipped one by one; never throws.</summary>
    public static IReadOnlyList<WatchedFolderSpec> Decode(string? json)
    {
        var list = new List<WatchedFolderSpec>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(p.GetString())) continue;
                var enabled = !(e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                var action = e.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s ? s : null;
                var path = p.GetString()!;
                if (list.Any(f => WatchedFolderService.SamePath(f.Path, path))) continue;
                list.Add(new WatchedFolderSpec(path, enabled, action));
            }
        }
        catch (JsonException) { }
        return list;
    }
}

/// <summary>
/// Watches every enabled folder and calls <c>onArrival</c> with new finished files (SPEC 3.1). Originals are never
/// moved or touched here: the folder's action (convert, tool) saves its result next to the file, and that result
/// is ignored if it is reported through <see cref="NoteOutputs"/>. Settings changes are applied live with
/// <see cref="Reconcile"/>. Missing, unplugged or unreadable folders never throw; they are listed in
/// <see cref="Unavailable"/> and tried again every <see cref="RetryInterval"/>.
/// <para><c>onArrival</c> is called on a thread-pool thread; marshal to the UI thread yourself.</para>
/// </summary>
public sealed class WatchedFolderService : IDisposable
{
    public const int RecentOutputLimit = 200;
    public static TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<WatchedFolderSpec>> _source;
    private readonly Action<FolderArrival> _onArrival;
    private readonly ArrivalTimings? _timings;
    private readonly object _gate = new();
    private readonly Dictionary<string, FolderMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatchedFolderSpec> _specs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recentOutputs = [];
    private IPreferences? _preferences;
    private Action? _preferencesHandler;
    private Timer? _retry;
    private bool _started, _disposed;

    /// <param name="source">Current folder list; called on every <see cref="Reconcile"/>.</param>
    public WatchedFolderService(Func<IReadOnlyList<WatchedFolderSpec>> source, Action<FolderArrival> onArrival, ArrivalTimings? timings = null)
    {
        _source = source;
        _onArrival = onArrival;
        _timings = timings;
    }

    /// <summary>A service fed by the preferences key "watchedFolders" that reconciles whenever preferences change.</summary>
    public static WatchedFolderService ForPreferences(IPreferences preferences, Action<FolderArrival> onArrival, ArrivalTimings? timings = null)
    {
        var service = new WatchedFolderService(() => WatchedFolderJson.Decode(preferences.GetJson(WatchedFolderJson.PreferencesKey)), onArrival, timings)
        {
            _preferences = preferences,
        };
        service._preferencesHandler = service.Reconcile;
        preferences.Changed += service._preferencesHandler;
        return service;
    }

    /// <summary>Folders that are switched on but cannot be watched right now (deleted, unplugged, no access).</summary>
    public IReadOnlyCollection<string> Unavailable { get { lock (_gate) return _unavailable.ToList(); } }

    /// <summary>Raised after <see cref="Unavailable"/> changed.</summary>
    public event Action? AvailabilityChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _retry = new Timer(_ => Reconcile(), null, RetryInterval, RetryInterval);
        }
        Reconcile();
    }

    /// <summary>Starts and stops monitors to match the enabled folders; picks up changed actions. Safe to call often.</summary>
    public void Reconcile()
    {
        var changed = false;
        List<FolderMonitor> stopped = [];
        lock (_gate)
        {
            if (!_started || _disposed) return;
            IReadOnlyList<WatchedFolderSpec> list;
            try { list = _source(); } catch { return; }
            var wanted = list.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path)).GroupBy(f => Normalize(f.Path)).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var key in _monitors.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                stopped.Add(_monitors[key]);
                _monitors.Remove(key);
            }
            _specs.Clear();
            foreach (var (key, spec) in wanted) _specs[key] = spec;
            changed |= _unavailable.RemoveWhere(k => !wanted.ContainsKey(k)) > 0;

            foreach (var (key, spec) in wanted)
            {
                if (_monitors.ContainsKey(key)) continue;
                var monitor = new FolderMonitor(spec.Path, files => Deliver(key, files), () => OnVanished(key), _timings);
                bool ok;
                try { ok = monitor.Start(); } catch { ok = false; }
                if (ok) { _monitors[key] = monitor; changed |= _unavailable.Remove(key); }
                else { monitor.Dispose(); changed |= _unavailable.Add(key); }
            }
        }
        foreach (var monitor in stopped) monitor.Dispose();
        if (changed) RaiseAvailability();
    }

    /// <summary>Records files OpenShelf wrote itself (hook this to <c>ConversionWheelCatalog.Saved</c> and tool results),
    /// so they never count as arrivals.</summary>
    public void NoteOutputs(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (var p in paths)
            {
                try { _recentOutputs.Add(System.IO.Path.GetFullPath(p)); } catch { }
            }
            if (_recentOutputs.Count > RecentOutputLimit) _recentOutputs.RemoveRange(0, _recentOutputs.Count - RecentOutputLimit);
        }
    }

    private void Deliver(string key, IReadOnlyList<string> files)
    {
        WatchedFolderSpec? spec;
        List<string> fresh;
        lock (_gate)
        {
            if (_disposed || !_specs.TryGetValue(key, out spec)) return;
            fresh = files.Where(f => !_recentOutputs.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        if (fresh.Count == 0) return;
        try { _onArrival(new FolderArrival(spec.Path, spec.Action, fresh)); } catch { /* the app's handler must not kill the timer thread */ }
    }

    private void OnVanished(string key)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _monitors.Remove(key);
            if (_specs.ContainsKey(key)) _unavailable.Add(key);
        }
        RaiseAvailability();
    }

    private void RaiseAvailability()
    {
        try { AvailabilityChanged?.Invoke(); } catch { }
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.TrimEnd('\\', '/'); }
    }

    public void Dispose()
    {
        List<FolderMonitor> all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _monitors.Values.ToList();
            _monitors.Clear();
            _retry?.Dispose();
        }
        if (_preferences is not null && _preferencesHandler is not null) _preferences.Changed -= _preferencesHandler;
        foreach (var m in all) m.Dispose();
    }
}
