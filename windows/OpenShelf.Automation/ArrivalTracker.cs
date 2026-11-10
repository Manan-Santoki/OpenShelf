using System.Globalization;

namespace OpenShelf.Automation;

/// <summary>Names a watched folder never reacts to: hidden files, Office lock files, unfinished downloads
/// and other temporary files. Port of ArrivalFilter in ArrivalTracker.swift, plus Windows litter.</summary>
public static class ArrivalFilter
{
    /// <summary>Extensions of files still being written by a browser or download tool; the finished file is renamed without them.</summary>
    public static readonly IReadOnlySet<string> PartialExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "download", "crdownload", "part", "partial", "opdownload", "tmp", "temp", "screenshot",
    };

    public static bool IsIgnored(string name)
    {
        if (name.Length == 0 || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(name).TrimStart('.');
        return PartialExtensions.Contains(ext);
    }
}

/// <param name="Settle">A new file is reported once its size has stayed the same this long.</param>
/// <param name="EmptySettle">The same for an empty file, in case it is about to be filled.</param>
/// <param name="Poll">How often the folder is listed while a new file is still arriving.</param>
public sealed record ArrivalTimings(TimeSpan Settle, TimeSpan EmptySettle, TimeSpan Poll)
{
    public static ArrivalTimings Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500));
}

/// <summary>
/// Decides which files in a watched folder are new and finished, from repeated listings of it. Pure state.
/// Files present at the first listing are never reported. A file renamed inside the folder (same file id) and one
/// saved over in place under the same name are not new. A new file is reported once its size has stayed the same
/// for the settle time (longer when empty) and it is not still open for writing.
/// Port of ArrivalTracker.swift. Names compare case-insensitively, as on NTFS.
/// </summary>
public sealed class ArrivalTracker
{
    /// <param name="FileId">Identifies the file across renames; null when unknown.</param>
    /// <param name="IsBusy">Another process still has the file open for writing.</param>
    public sealed record Entry(string Name, ulong? FileId, long Size, bool IsRegularFile = true, bool IsBusy = false);

    private sealed record Candidate(ulong? FileId, long Size, bool IsBusy, DateTime UnchangedSince);

    private readonly ArrivalTimings _timings;
    private readonly Dictionary<string, ulong?> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Candidate> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="existing">The folder's contents when watching starts.</param>
    public ArrivalTracker(IEnumerable<Entry> existing, ArrivalTimings? timings = null)
    {
        _timings = timings ?? ArrivalTimings.Default;
        foreach (var entry in existing.Where(IsEligible)) _known[entry.Name] = entry.FileId;
    }

    /// <summary>True while some new file is still being written, so the folder needs listing again shortly.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>True for a name already accounted for (the caller can skip expensive per-file checks for it).</summary>
    public bool IsKnown(string name) => _known.ContainsKey(name);

    /// <summary>Takes a fresh listing and returns the names of new files that finished arriving since the last one, in natural order.</summary>
    public IReadOnlyList<string> Observe(IEnumerable<Entry> entries, DateTime now)
    {
        var eligible = entries.Where(IsEligible).ToList();
        var present = new HashSet<string>(eligible.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        // Taken before forgetting names that went away, which renamed files' old names did.
        var knownIds = new HashSet<ulong>(_known.Values.Where(v => v.HasValue).Select(v => v!.Value));
        foreach (var gone in _known.Keys.Where(k => !present.Contains(k)).ToList()) _known.Remove(gone);
        foreach (var gone in _pending.Keys.Where(k => !present.Contains(k)).ToList()) _pending.Remove(gone);

        var ready = new List<string>();
        foreach (var entry in eligible)
        {
            if (_known.ContainsKey(entry.Name))
            {
                // Same file, or one saved over it in place: not new either way.
                if (entry.FileId.HasValue) _known[entry.Name] = entry.FileId;
                continue;
            }
            if (entry.FileId is { } id && knownIds.Contains(id))
            {
                _known[entry.Name] = id; // renamed inside the folder
                _pending.Remove(entry.Name);
                continue;
            }
            if (!_pending.TryGetValue(entry.Name, out var candidate)
                || candidate.Size != entry.Size || candidate.IsBusy != entry.IsBusy || candidate.FileId != entry.FileId)
            {
                // Just appeared, or changed since the last listing: start waiting again.
                _pending[entry.Name] = new Candidate(entry.FileId, entry.Size, entry.IsBusy, now);
                continue;
            }
            var wait = entry.Size > 0 ? _timings.Settle : _timings.EmptySettle;
            if (entry.IsBusy || now - candidate.UnchangedSince < wait) continue;
            _pending.Remove(entry.Name);
            _known[entry.Name] = entry.FileId;
            ready.Add(entry.Name);
        }
        ready.Sort(Natural);
        return ready;
    }

    private static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    private static bool IsEligible(Entry entry) => entry.IsRegularFile && !ArrivalFilter.IsIgnored(entry.Name);
}
