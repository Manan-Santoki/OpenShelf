using System.Globalization;

namespace OpenShelf.Convert;

/// <summary>Passes progress on until closed, then drops it; never goes backwards. Lines can still arrive from the pipe after
/// ffmpeg has exited, and a second attempt starts again from zero.</summary>
public sealed class ProgressGate(IProgress<double>? progress)
{
    private readonly object _lock = new();
    private bool _open = true;
    private double _highest;

    public void Report(double fraction)
    {
        lock (_lock)
        {
            if (!_open) return;
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction <= _highest) return;
            _highest = fraction;
            progress?.Report(fraction);
        }
    }

    public void Close()
    {
        lock (_lock) _open = false;
    }
}

/// <summary>Runs ffmpeg the way every media conversion does: quietly, with progress read from its <c>-progress pipe:1</c>
/// output against the probed duration, a timeout that grows with the duration, and cleanup of a partial file. Port of FFmpegJob.swift.</summary>
public static class FFmpegJob
{
    /// <summary>"out_time_us=1500000" as a fraction of <paramref name="duration"/> seconds; "progress=end" is 1; anything else null.</summary>
    public static double? ParseProgress(string line, double duration)
    {
        line = line.Trim();
        if (line == "progress=end") return 1;
        // ffmpeg reports out_time_us (and out_time_ms, which is microseconds too, for historical reasons).
        const string prefix = "out_time_us=";
        if (duration <= 0 || !line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var micros)) return null;
        return Math.Clamp(micros / 1_000_000 / duration, 0, 1);
    }

    /// <summary>How long ffmpeg may run: ten times the clip's length plus ten minutes, two hours when the length is unknown.</summary>
    public static TimeSpan Timeout(double duration) =>
        duration > 0 ? TimeSpan.FromSeconds(Math.Min(duration * 10 + 600, 24 * 3600)) : TimeSpan.FromHours(2);

    /// <summary>The whole command line: <c>-y -nostdin -loglevel error -i source &lt;arguments&gt; -progress pipe:1 -nostats output</c>.</summary>
    public static IReadOnlyList<string> CommandLine(string source, IEnumerable<string> arguments, string output) =>
        ["-y", "-nostdin", "-loglevel", "error", "-i", source, .. arguments, "-progress", "pipe:1", "-nostats", output];

    /// <summary>Runs one attempt. The output is deleted when it fails or is cancelled.</summary>
    public static async Task RunAsync(
        ToolRunner runner, string ffmpeg, string source, IEnumerable<string> arguments, string output,
        double duration, ProgressGate gate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = new ToolInvocation(ffmpeg, CommandLine(source, arguments, output)) { Timeout = Timeout(duration) };
        try
        {
            await runner(invocation, line =>
            {
                if (ParseProgress(line, duration) is { } fraction && fraction < 1) gate.Report(fraction);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw ConversionException.Failed($"ffmpeg did not write anything for “{Path.GetFileName(source)}”.");
        }
        catch
        {
            Discard(output);
            throw;
        }
    }

    /// <summary>Tries each set of arguments in turn until one works: a lossless copy first, say, then a re-encode, then the next
    /// encoder. A cancel, a timeout or a missing ffmpeg stops at once. Returns the index of the attempt that worked.</summary>
    public static async Task<int> RunAttemptsAsync(
        ToolRunner runner, string ffmpeg, string source, IReadOnlyList<IReadOnlyList<string>> attempts, string output,
        double duration, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var gate = new ProgressGate(progress);
        try
        {
            Exception? last = null;
            for (var index = 0; index < attempts.Count; index++)
            {
                try
                {
                    await RunAsync(runner, ffmpeg, source, attempts[index], output, duration, gate, cancellationToken).ConfigureAwait(false);
                    gate.Close();
                    progress?.Report(1);
                    return index;
                }
                catch (OperationCanceledException) { throw; }
                catch (MissingToolException) { throw; }
                catch (ProcessFailure failure) when (failure.TimedOut) { throw; }
                catch (Exception e) when (e is ProcessFailure or ConversionException or IOException)
                {
                    last = e;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            throw last ?? ConversionException.Failed("ffmpeg had nothing to do.");
        }
        finally
        {
            gate.Close();
        }
    }

    public static void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
