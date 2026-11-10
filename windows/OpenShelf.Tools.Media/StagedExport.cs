using OpenShelf.Convert;

namespace OpenShelf.Tools.Media;

/// <summary>Runs ffmpeg into a hidden file beside the destination and moves it into place only when it is complete, so a cancelled or
/// failed export never leaves a partial file where the user can see it. Reuses the progress parsing, gate and timeout of
/// <see cref="FFmpegJob"/> (which cannot be used directly: it takes one input and no seek before it).</summary>
internal static class StagedExport
{
    /// <summary>A hidden file beside <paramref name="destination"/> to write into before it is complete; same extension, so ffmpeg
    /// picks the right muxer.</summary>
    public static string PartialFor(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? ".";
        return Path.Combine(directory, $".openshelf-{Guid.NewGuid():N}"[..19] + "-" + Path.GetFileName(destination));
    }

    /// <summary>Tries each set of arguments in turn (a lossless copy first, say, then a re-encode) until one works. The arguments
    /// are everything between <c>-nostdin -loglevel error</c> and <c>-progress</c>: inputs, maps, codecs.</summary>
    /// <param name="keep">Asked with the finished file's size before it is moved into place; false discards it and the result is null.</param>
    /// <returns>The path written, which is <paramref name="destination"/> unless another file took that name meanwhile; null when
    /// <paramref name="keep"/> declined.</returns>
    public static async Task<string?> RunAsync(
        ToolRunner runner, string ffmpeg, IReadOnlyList<IReadOnlyList<string>> attempts, string destination, double duration,
        IProgress<double>? progress, CancellationToken cancellationToken, Func<long, bool>? keep = null)
    {
        if (attempts.Count == 0) throw ConversionException.Failed("This copy of ffmpeg can’t make that (an encoder is missing). A full ffmpeg build can.");
        var partial = PartialFor(destination);
        var gate = new ProgressGate(progress);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? last = null;
            var done = false;
            foreach (var attempt in attempts)
            {
                try
                {
                    await RunOnceAsync(runner, ffmpeg, attempt, partial, duration, gate, cancellationToken).ConfigureAwait(false);
                    done = true;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (MissingToolException) { throw; }
                catch (ProcessFailure failure) when (failure.TimedOut) { throw; }
                catch (Exception e) when (e is ProcessFailure or ConversionException or IOException)
                {
                    last = e;
                    FFmpegJob.Discard(partial);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            if (!done) throw last ?? ConversionException.Failed("ffmpeg had nothing to do.");

            cancellationToken.ThrowIfCancellationRequested();
            if (keep is not null && !keep(new FileInfo(partial).Length))
            {
                FFmpegJob.Discard(partial);
                return null;
            }
            var final = MoveIntoPlace(partial, destination);
            gate.Close();
            progress?.Report(1);
            return final;
        }
        catch
        {
            gate.Close();
            FFmpegJob.Discard(partial);
            throw;
        }
        finally
        {
            gate.Close();
        }
    }

    private static async Task RunOnceAsync(
        ToolRunner runner, string ffmpeg, IReadOnlyList<string> attempt, string partial, double duration, ProgressGate gate, CancellationToken ct)
    {
        IReadOnlyList<string> arguments = ["-y", "-nostdin", "-loglevel", "error", .. attempt, "-progress", "pipe:1", "-nostats", partial];
        var invocation = new ToolInvocation(ffmpeg, arguments) { Timeout = FFmpegJob.Timeout(duration) };

        // Hide the file as soon as ffmpeg creates it (a leading dot hides nothing on Windows).
        using var stopHiding = new CancellationTokenSource();
        var hider = HideWhenCreatedAsync(partial, stopHiding.Token);
        try
        {
            await runner(invocation, line =>
            {
                if (FFmpegJob.ParseProgress(line, duration) is { } fraction && fraction < 1) gate.Report(fraction);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            stopHiding.Cancel();
            await hider.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
            throw ConversionException.Failed("ffmpeg did not write anything.");
    }

    private static async Task HideWhenCreatedAsync(string path, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                        return;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try again */ }
                await Task.Delay(10, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Moves the finished file to where the user will see it, never over another file: if the name was taken meanwhile the
    /// next free one is used.</summary>
    public static string MoveIntoPlace(string partial, string destination)
    {
        var attributes = File.GetAttributes(partial);
        if ((attributes & FileAttributes.Hidden) != 0) File.SetAttributes(partial, attributes & ~FileAttributes.Hidden);
        var target = destination;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(partial, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target) && attempt < 50)
            {
                var directory = Path.GetDirectoryName(destination) ?? ".";
                var extension = Path.GetExtension(destination).TrimStart('.');
                target = OpenShelf.Core.OutputNaming.UniquePath(directory, Path.GetFileNameWithoutExtension(destination), extension.Length == 0 ? null : extension);
            }
        }
    }
}
