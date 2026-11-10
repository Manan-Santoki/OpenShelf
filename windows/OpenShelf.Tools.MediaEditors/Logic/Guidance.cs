using OpenShelf.Convert;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>What the media tools say when ffmpeg is missing or a file will not open, worded like the Convert wheel
/// ("MKV needs ffmpeg", then <see cref="MissingToolAdvice.InstallHint"/>).</summary>
internal static class Guidance
{
    /// <summary>"Trim needs ffmpeg · Install it in Settings → Extra Formats": one line for a toast.</summary>
    public static string NeedsFfmpeg(string tool) => $"{tool} needs ffmpeg · {MissingToolAdvice.InstallHint}";

    /// <summary>What an editor says when a file will not load: the tool's own words when it is a missing tool or a
    /// readable problem, else <paramref name="fallback"/>.</summary>
    public static string LoadError(Exception error, string fallback) => error switch
    {
        MissingToolException m => $"{m.Name} isn’t installed. {MissingToolAdvice.InstallHint}.",
        ConversionException c => c.Message,
        _ => fallback,
    };

    /// <summary>Turns an export failure into one <see cref="SaveException"/> the editor window can show; cancellation passes through.</summary>
    public static Exception ForSave(Exception error, string source)
    {
        switch (error)
        {
            case OperationCanceledException:
            case SaveException:
                return error;
            case MissingToolException m:
                return new SaveException(SaveException.Reason.Failed, $"{m.Name} isn’t installed any more. {MissingToolAdvice.InstallHint}.", error);
            case ConversionException c:
                return new SaveException(SaveException.Reason.Failed, c.Message, error);
            case UnauthorizedAccessException or IOException:
                return SaveException.From(error, source);
            default:
                return new SaveException(SaveException.Reason.Failed, FailureMessage.Describe(error, source).Message, error);
        }
    }
}

/// <summary>Runs an export over several files the way the Mac's batch export does: one after another, overall progress,
/// files that save nothing counted as unchanged, and a failure that still names what was written before it.</summary>
internal static class ExportRunner
{
    /// <param name="jobs">The files to export.</param>
    /// <param name="outcome">Already counts the files left alone; each job adds to it.</param>
    /// <param name="sourceOf">The file a job works on (for error messages).</param>
    /// <param name="export">Exports a job and returns the files it wrote (empty: nothing needed saving). Its progress is 0 to 1.</param>
    /// <param name="report">Overall progress 0 to 1, and the index of the job running.</param>
    public static async Task<SaveOutcome> RunAsync<T>(IReadOnlyList<T> jobs, SaveOutcome outcome, Func<T, string> sourceOf,
        Func<T, int, IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> export, Action<double, int>? report, CancellationToken ct)
    {
        for (var index = 0; index < jobs.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var i = index;
            report?.Invoke(Overall(0, i, jobs.Count), i);
            var progress = new Direct(f => report?.Invoke(Overall(f, i, jobs.Count), i));
            try
            {
                var written = await export(jobs[index], index, progress, ct).ConfigureAwait(false);
                if (written.Count == 0) outcome.Unchanged++;
                else outcome.Saved.AddRange(written);
            }
            catch (Exception e)
            {
                var wrapped = Guidance.ForSave(e, sourceOf(jobs[index]));
                if (wrapped is OperationCanceledException) throw;
                var save = (SaveException)wrapped;
                throw new SaveException(save.Kind, save.Message, save) { SavedBefore = outcome.Saved.ToList() };
            }
        }
        report?.Invoke(1, Math.Max(jobs.Count - 1, 0));
        return outcome;
    }

    public static double Overall(double fraction, int index, int count) =>
        count <= 0 ? 1 : (index + Math.Clamp(fraction, 0, 1)) / count;

    private sealed class Direct(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
