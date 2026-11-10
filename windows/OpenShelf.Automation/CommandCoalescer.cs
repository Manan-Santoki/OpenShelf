using OpenShelf.Convert;

namespace OpenShelf.Automation;

/// <summary>
/// Explorer starts one process per selected file for a classic context-menu verb, so one multi-selection arrives as
/// several commands within milliseconds. This merges commands of the same kind (and format) that arrive close
/// together into one, so a single wheel opens for all the files. Thread-safe; the sink runs on a timer thread.
/// </summary>
public sealed class CommandCoalescer : IDisposable
{
    private readonly TimeSpan _quiet, _max;
    private readonly Action<AutomationCommand> _sink;
    private readonly object _gate = new();
    private readonly Dictionary<(CommandKind, OutputFormat?), Batch> _batches = [];
    private bool _disposed;

    private sealed class Batch
    {
        public List<string> Paths = [];
        public DateTime Started = DateTime.UtcNow;
        public Timer? Timer;
    }

    /// <param name="quiet">A batch is delivered once no new command joined it for this long.</param>
    /// <param name="max">...or after this long in total, however many keep arriving.</param>
    public CommandCoalescer(Action<AutomationCommand> sink, TimeSpan? quiet = null, TimeSpan? max = null)
    {
        _sink = sink;
        _quiet = quiet ?? TimeSpan.FromMilliseconds(400);
        _max = max ?? TimeSpan.FromSeconds(3);
    }

    public void Submit(AutomationCommand command)
    {
        var key = (command.Kind, command.Format);
        lock (_gate)
        {
            if (_disposed) return;
            if (!_batches.TryGetValue(key, out var batch)) _batches[key] = batch = new Batch();
            foreach (var p in command.Paths)
                if (!batch.Paths.Contains(p, StringComparer.OrdinalIgnoreCase)) batch.Paths.Add(p);
            var due = _quiet;
            var remaining = _max - (DateTime.UtcNow - batch.Started);
            if (remaining < due) due = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            if (batch.Timer is null) batch.Timer = new Timer(_ => Flush(key), null, due, Timeout.InfiniteTimeSpan);
            else batch.Timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush(( CommandKind, OutputFormat?) key)
    {
        AutomationCommand command;
        lock (_gate)
        {
            if (_disposed || !_batches.Remove(key, out var batch)) return;
            batch.Timer?.Dispose();
            command = new AutomationCommand(key.Item1, batch.Paths, key.Item2);
        }
        try { _sink(command); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var b in _batches.Values) b.Timer?.Dispose();
            _batches.Clear();
        }
    }
}
