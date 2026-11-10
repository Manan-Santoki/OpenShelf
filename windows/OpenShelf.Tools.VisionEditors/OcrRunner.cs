using OpenShelf.Convert;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>Where a text-recognition run is: the whole batch as a fraction, and a line such as "Reading 2 of 3…".</summary>
public sealed record OcrProgress(double Fraction, string? Detail);

/// <summary>What a run did to each file.</summary>
public sealed record OcrRunResult(IReadOnlyList<string> Saved, IReadOnlyList<(string Path, string Error)> Failed);

/// <summary>
/// The OCR window's logic, free of any window (SPEC §2.4): reads each file in turn, saves "Name OCR.pdf" or "Name OCR.txt" beside it, and
/// turns the result into the closing toast. A file that fails does not stop the rest; cancelling stops between files and between pages;
/// a missing OCR language stops everything with the Windows Settings path.
/// </summary>
public sealed class OcrRunner
{
    /// <summary>Recognises one file and returns the path it saved.</summary>
    public delegate Task<string> Recognise(string path, OcrOutput output, IProgress<double> progress, CancellationToken cancel);

    private readonly Recognise _recognise;

    public OcrRunner(Recognise? recognise = null)
    {
        _recognise = recognise ?? ((path, output, progress, cancel) => new OcrService().RunAsync(path, output, progress, cancel));
    }

    /// <summary>The line under the window's title: the file name, or "3 files".</summary>
    public static string Title(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";

    /// <summary>The Mac's detail line for several files: "Reading 2 of 3…". Nothing for one file.</summary>
    public static string? Detail(int index, int count) => count > 1 ? $"Reading {index + 1} of {count}…" : null;

    public async Task<OcrRunResult> RunAsync(IReadOnlyList<string> paths, OcrOutput output, Action<OcrProgress>? report = null, CancellationToken cancel = default)
    {
        var saved = new List<string>();
        var failed = new List<(string, string)>();
        for (var i = 0; i < paths.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var index = i;
            var detail = Detail(i, paths.Count);
            report?.Invoke(new OcrProgress((double)i / paths.Count, detail));
            var inner = new Progress<double>(fraction => report?.Invoke(new OcrProgress((index + Math.Clamp(fraction, 0, 1)) / paths.Count, detail)));
            try
            {
                saved.Add(await _recognise(paths[i], output, inner, cancel).ConfigureAwait(false));
            }
            catch (OcrNoLanguageException)
            {
                throw;   // nothing can be read at all
            }
            catch (Exception e) when (e is ConversionException or VisionException)
            {
                failed.Add((paths[i], e.Message));
            }
        }
        report?.Invoke(new OcrProgress(1, null));
        return new OcrRunResult(saved, failed);
    }

    /// <summary>The outcome the editor hands back: the files written, and how many could not be read ("2 saved · 1 failed").</summary>
    public static SaveOutcome ToOutcome(OcrRunResult result, OcrOutput output) => new(result.Saved)
    {
        Unchanged = result.Failed.Count,
        SavedLabel = "saved",
        UnchangedLabel = "failed",
        Detail = output == OcrOutput.Text ? "as text" : "as searchable PDFs",
    };

    /// <summary>The message when nothing at all could be saved: the reason for one file, "“a.png”: reason (and 2 more files failed)" for several.</summary>
    public static string FailureMessage(OcrRunResult result)
    {
        if (result.Failed.Count == 0) return "Nothing could be read.";
        var (path, error) = result.Failed[0];
        if (result.Failed.Count == 1 && result.Saved.Count == 0) return error;
        var more = result.Failed.Count - 1;
        return $"“{Path.GetFileName(path)}”: {error}" + (more > 0 ? $" (and {more} more {(more == 1 ? "file" : "files")} failed)" : "");
    }

    /// <summary>Whether the OCR tool takes these files: images and PDFs, alone or mixed.</summary>
    public static bool Applies(IReadOnlyList<string> paths) => ToolsCatalog.AllOfKinds(paths, FileKind.Image, FileKind.Pdf);
}
