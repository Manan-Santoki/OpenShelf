using System.IO;
using OpenShelf.Core;

namespace OpenShelf.Convert;

/// <summary>Where a batch is: <paramref name="Completed"/> files are done (saved or failed), the current one is
/// <paramref name="Fraction"/> (0..1, null when the engine cannot measure it) of the way.</summary>
public sealed record BatchProgress(int Completed, int Total, double? Fraction, string? CurrentFile);

/// <summary>What one job did: its inputs, the files written, or the message that says why not.</summary>
public sealed record JobResult(IReadOnlyList<string> Sources, IReadOnlyList<string> Outputs, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record BatchResult(IReadOnlyList<JobResult> Jobs, bool Cancelled)
{
    /// <summary>Everything written, in order.</summary>
    public IReadOnlyList<string> Saved => Jobs.SelectMany(job => job.Outputs).ToList();

    /// <summary>"Photo.png: message" for each failed job, like the Mac's failure toast.</summary>
    public IReadOnlyList<string> Failures => Jobs.Where(job => job.Error is not null)
        .Select(job => $"{Path.GetFileName(job.Sources[0].TrimEnd('\\', '/'))}: {job.Error}").ToList();
}

/// <summary>Runs file jobs one batch at a time. Port of ConversionRunner.swift without the toast: it reports progress and
/// returns results, and the caller (the wheel, through <see cref="IProgressService"/>) shows them.
/// <para>Rules, all from the Mac: jobs run one after another; a failing job does not stop the rest; cancelling stops after
/// the current job is abandoned and keeps what was already saved; several files going into ZIP, TAR or TAR.GZ make
/// one archive, everything else converts per file; batches queue behind each other.</para>
/// <para>Never overwrites, and never leaves half a file: each job's engine writes into a hidden folder next to the
/// source; only when it has finished are the results moved out under names from <see cref="OutputNaming"/>.</para></summary>
public sealed class ConversionRunner(Converter converter)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversionRunner() : this(Converter.Default) { }

    public static ConversionRunner Default { get; } = new();

    /// <summary>The progress card's title: "Converting to JPG", "Compressing to ZIP", "Extracting".</summary>
    public static string Title(OutputFormat format) => format switch
    {
        OutputFormat.Zip or OutputFormat.Tar or OutputFormat.Tgz => $"Compressing to {format.Title()}",
        OutputFormat.Extract => "Extracting",
        _ => $"Converting to {format.Title()}",
    };

    /// <summary>The jobs a batch splits into: one for all files when making an archive, otherwise one per file.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Jobs(IReadOnlyList<string> paths, OutputFormat format) =>
        format.IsArchive() ? (paths.Count == 0 ? [] : [paths]) : paths.Select(path => (IReadOnlyList<string>)[path]).ToList();

    /// <summary>Converts <paramref name="paths"/> to <paramref name="format"/>, saving beside each original.</summary>
    /// <param name="options">Output settings; the caller reads them once when the batch starts so a change mid-batch does not mix two kinds of output.</param>
    /// <param name="onJobSaved">Called with each job's files as soon as they are written, before the rest finish.</param>
    public Task<BatchResult> ConvertAsync(
        IReadOnlyList<string> paths,
        OutputFormat format,
        ConversionOptions? options = null,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<string>>? onJobSaved = null)
    {
        options ??= ConversionOptions.Automatic;
        return RunJobsAsync(Jobs(paths, format), async (sources, stagingDirectory, jobProgress, ct) =>
        {
            var request = new ConversionRequest(sources[0], format, options)
            {
                Sources = sources.Count > 1 ? sources : null,
                OutputDirectory = stagingDirectory,
            };
            return await converter.ConvertAsync(request, jobProgress, ct).ConfigureAwait(false);
        }, progress, cancellationToken, onJobSaved);
    }

    /// <summary>The general runner behind <see cref="ConvertAsync"/>, for one-click tools too: runs
    /// <paramref name="operation"/> for each job with a staging folder to write into.</summary>
    /// <param name="operation">Receives the job's sources, the hidden staging folder, a 0..1 progress sink and the token; returns the files it wrote there.</param>
    public async Task<BatchResult> RunJobsAsync(
        IReadOnlyList<IReadOnlyList<string>> jobs,
        Func<IReadOnlyList<string>, string, IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> operation,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<string>>? onJobSaved = null)
    {
        if (jobs.Count == 0) return new BatchResult([], false);
        // Batches queue behind each other so their cards do not collide.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<JobResult>();
            var cancelled = false;
            progress?.Report(new BatchProgress(0, jobs.Count, 0, Path.GetFileName(jobs[0][0].TrimEnd('\\', '/'))));
            for (var index = 0; index < jobs.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
                var sources = jobs[index];
                var name = Path.GetFileName(sources[0].TrimEnd('\\', '/'));
                var current = index;
                var jobProgress = new SyncProgress(fraction =>
                    progress?.Report(new BatchProgress(current, jobs.Count, Math.Clamp(fraction, 0, 1), name)));
                try
                {
                    var outputs = await RunJobAsync(sources, operation, jobProgress, cancellationToken).ConfigureAwait(false);
                    results.Add(new JobResult(sources, outputs, null));
                    onJobSaved?.Invoke(outputs);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception error)
                {
                    results.Add(new JobResult(sources, [], FailureMessage.Describe(error, sources[0]).Message));
                }
                progress?.Report(new BatchProgress(index + 1, jobs.Count, index + 1 < jobs.Count ? 0 : 1,
                    index + 1 < jobs.Count ? Path.GetFileName(jobs[index + 1][0].TrimEnd('\\', '/')) : null));
            }
            return new BatchResult(results, cancelled);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IReadOnlyList<string>> RunJobAsync(
        IReadOnlyList<string> sources,
        Func<IReadOnlyList<string>, string, IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> operation,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetDirectoryName(Path.GetFullPath(sources[0].TrimEnd('\\', '/'))) ?? ".";
        var staging = Path.Combine(destination, ".os-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            File.SetAttributes(staging, FileAttributes.Directory | FileAttributes.Hidden);
        }
        catch (UnauthorizedAccessException)
        {
            throw ConversionException.Failed(FailureMessage.PermissionMessage(destination));
        }
        try
        {
            var written = await operation(sources, staging, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return MoveIntoPlace(written, staging, destination);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>Moves what an engine wrote in <paramref name="staging"/> into <paramref name="destination"/>, under names that
    /// do not exist yet. Files outside the staging folder are left where they are.</summary>
    public static IReadOnlyList<string> MoveIntoPlace(IReadOnlyList<string> written, string staging, string destination)
    {
        var prefix = Path.GetFullPath(staging).TrimEnd('\\') + Path.DirectorySeparatorChar;
        var finals = new List<string>();
        foreach (var path in written)
        {
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                finals.Add(path);
                continue;
            }
            var isFolder = Directory.Exists(path);
            var (stem, extension) = isFolder ? (Path.GetFileName(path), null) : OutputFiles.SplitName(Path.GetFileName(path));
            for (var attempt = 0; ; attempt++)
            {
                var target = OutputNaming.UniquePath(destination, stem, extension);
                try
                {
                    if (isFolder) Directory.Move(path, target);
                    else File.Move(path, target, overwrite: false);
                    finals.Add(target);
                    break;
                }
                catch (IOException) when (attempt < 5 && (File.Exists(target) || Directory.Exists(target)))
                {
                    // Something took the name since we looked; pick the next one.
                }
            }
        }
        return finals;
    }

    private static void DeleteQuietly(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>An <see cref="IProgress{T}"/> that calls back on the reporting thread, so order is preserved.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
