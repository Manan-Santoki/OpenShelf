using OpenShelf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Tools.Pdf;

/// <summary>How Split divides a PDF.</summary>
public enum PdfSplitMode
{
    /// <summary>One file per page.</summary>
    EveryPage,
    /// <summary>One file per run of N pages; the last may be shorter.</summary>
    Chunks,
    /// <summary>One file per comma-separated range, e.g. "1-3, 5, 8-".</summary>
    Ranges,
}

/// <summary>A Split rule: a mode plus its setting. Port of PDFSplitRule.</summary>
public sealed record PdfSplitRule
{
    public PdfSplitMode Mode { get; init; } = PdfSplitMode.EveryPage;

    /// <summary>Pages per file in <see cref="PdfSplitMode.Chunks"/> mode.</summary>
    public int ChunkSize { get; init; } = 2;

    /// <summary>The text of <see cref="PdfSplitMode.Ranges"/> mode, e.g. "1-3, 4, 7-".</summary>
    public string RangesText { get; init; } = "";

    /// <summary>Page groups (0-based) for a PDF of <paramref name="pageCount"/> pages. With a
    /// <paramref name="rangeLimit"/> (the page count of the longest of several PDFs) ranges are read up to that page and cut
    /// to this PDF: "4-9" is pages 4-6 of a 6-page PDF and nothing of a 3-page one.</summary>
    /// <returns>The groups, or the first problem with the ranges as a message.</returns>
    public PdfRangeResult Groups(int pageCount, int? rangeLimit = null)
    {
        switch (Mode)
        {
            case PdfSplitMode.EveryPage: return PdfRangeResult.Ok(PdfSplitter.EveryPage(pageCount));
            case PdfSplitMode.Chunks: return PdfRangeResult.Ok(PdfSplitter.Chunks(pageCount, ChunkSize));
            default:
                var parsed = PdfSplitter.ParseRanges(RangesText, Math.Max(rangeLimit ?? pageCount, pageCount));
                if (!parsed.IsValid) return parsed;
                var cut = parsed.Groups.Select(g => (IReadOnlyList<int>)g.Where(p => p < pageCount).ToList())
                    .Where(g => g.Count > 0).ToList();
                return PdfRangeResult.Ok(cut);
        }
    }
}

/// <summary>Either page groups (0-based) or the reason the ranges were rejected.</summary>
public sealed record PdfRangeResult
{
    public bool IsValid => Error is null;
    public IReadOnlyList<IReadOnlyList<int>> Groups { get; init; } = [];
    /// <summary>Plain-language problem, e.g. “9” is outside pages 1–5.</summary>
    public string? Error { get; init; }

    public static PdfRangeResult Ok(IReadOnlyList<IReadOnlyList<int>> groups) => new() { Groups = groups };
    public static PdfRangeResult Fail(string message) => new() { Error = message };
}

/// <summary>One PDF to split and the rule for it.</summary>
public sealed record PdfSplitJob(string Path, PdfSplitRule Rule);

/// <summary>One file the split would make.</summary>
/// <param name="Pages">0-based pages of the source, in order.</param>
/// <param name="Label">"Page 3" or "Pages 1-3" (1-based), the suffix of the file name.</param>
/// <param name="FileName">The name the file would get now (it is chosen again when saving, never overwriting).</param>
public sealed record PdfSplitPart(IReadOnlyList<int> Pages, string Label, string FileName);

/// <summary>What splitting one PDF would do, for the preview before saving.</summary>
public sealed record PdfSplitPlan
{
    public required string Path { get; init; }
    public int PageCount { get; init; }
    public bool IsLocked { get; init; }
    /// <summary>The file could not be opened (damaged, missing); <see cref="Message"/> says so.</summary>
    public bool IsUnreadable { get; init; }
    /// <summary>The problem with the ranges, a lock or damage; null when the plan is fine.</summary>
    public string? Message { get; init; }
    public IReadOnlyList<PdfSplitPart> Parts { get; init; } = [];

    /// <summary>Whether running the plan makes something other than one copy of the whole PDF.</summary>
    public bool WillSplit => Message is null && Parts.Count > 0 && !(Parts.Count == 1 && Parts[0].Pages.Count == PageCount);
}

/// <summary>Splits PDFs by page, by chunk or by range.</summary>
public static class PdfSplitter
{
    /// <summary>Page groups (0-based) for one file per page.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> EveryPage(int pageCount) =>
        Enumerable.Range(0, Math.Max(pageCount, 0)).Select(i => (IReadOnlyList<int>)[i]).ToList();

    /// <summary>Page groups (0-based) of <paramref name="size"/> pages each; the last may be shorter.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Chunks(int pageCount, int size)
    {
        if (pageCount <= 0 || size <= 0) return [];
        var groups = new List<IReadOnlyList<int>>();
        for (var start = 0; start < pageCount; start += size)
            groups.Add(Enumerable.Range(start, Math.Min(size, pageCount - start)).ToList());
        return groups;
    }

    /// <summary>Parses "1-3, 4, 7-" into 0-based page groups, one per comma-separated part. "5-" runs to the last page,
    /// "-3" starts at page 1. Returns the first problem as a message when the text is invalid.</summary>
    public static PdfRangeResult ParseRanges(string? text, int pageCount)
    {
        var parts = (text ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return PdfRangeResult.Fail($"Enter page ranges, like 1-3, 4, 5-{Math.Max(pageCount, 5)}.");
        var groups = new List<IReadOnlyList<int>>();
        foreach (var part in parts)
        {
            var bounds = part.Split('-').Select(b => b.Trim()).ToArray();
            int? start = null, end = null;
            if (bounds.Length == 1)
            {
                start = Number(bounds[0]);
                end = start;
            }
            else if (bounds.Length == 2)
            {
                start = bounds[0].Length == 0 ? 1 : Number(bounds[0]);
                end = bounds[1].Length == 0 ? pageCount : Number(bounds[1]);
            }
            if (start is null || end is null) return PdfRangeResult.Fail($"“{part}” isn’t a page or range.");
            if (start < 1 || end > pageCount) return PdfRangeResult.Fail($"“{part}” is outside pages 1–{pageCount}.");
            if (start > end) return PdfRangeResult.Fail($"“{part}” runs backwards.");
            groups.Add(Enumerable.Range(start.Value - 1, end.Value - start.Value + 1).ToList());
        }
        return PdfRangeResult.Ok(groups);

        static int? Number(string s) =>
            s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, out var n) ? n : null;
    }

    /// <summary>Whether <paramref name="groups"/> make something other than one copy of the whole PDF.</summary>
    public static bool Splits(IReadOnlyList<IReadOnlyList<int>> groups, int pageCount) =>
        groups.Count > 0 && !(groups.Count == 1 && groups[0].SequenceEqual(Enumerable.Range(0, pageCount)));

    /// <summary>"Page 3" for one page, "Pages 1-3" for a run (1-based).</summary>
    public static string Label(IReadOnlyList<int> group) =>
        group[0] == group[^1] ? $"Page {group[0] + 1}" : $"Pages {group[0] + 1}-{group[^1] + 1}";

    /// <summary>Works out what each PDF would split into, for a preview. With several jobs, ranges are read against the
    /// longest PDF and cut to each one. Unreadable and locked PDFs get a plan with a message, not an exception.</summary>
    public static Task<IReadOnlyList<PdfSplitPlan>> PlanAsync(IReadOnlyList<PdfSplitJob> jobs, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PdfSplitPlan>>(() =>
        {
            var counts = new List<(int Count, bool Locked, string? Error)>();
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = PdfFiles.Open(job.Path, PdfDocumentOpenMode.Import);
                    counts.Add((document.PageCount, false, null));
                }
                catch (PdfToolException e)
                {
                    counts.Add((0, e.Kind == PdfToolErrorKind.Locked, e.Message));
                }
            }
            var limit = jobs.Count > 1 ? counts.Max(c => c.Count) : (int?)null;
            var plans = new List<PdfSplitPlan>();
            for (var i = 0; i < jobs.Count; i++)
            {
                var (count, locked, error) = counts[i];
                if (error is not null)
                {
                    plans.Add(new PdfSplitPlan { Path = jobs[i].Path, IsLocked = locked, IsUnreadable = !locked, Message = error });
                    continue;
                }
                var result = jobs[i].Rule.Groups(count, limit);
                if (!result.IsValid)
                {
                    plans.Add(new PdfSplitPlan { Path = jobs[i].Path, PageCount = count, Message = result.Error });
                    continue;
                }
                var path = jobs[i].Path;
                var parts = result.Groups.Select(g =>
                {
                    var label = Label(g);
                    return new PdfSplitPart(g, label, System.IO.Path.GetFileName(OutputNaming.Destination(path, label, "pdf")));
                }).ToList();
                plans.Add(new PdfSplitPlan { Path = path, PageCount = count, Parts = parts });
            }
            return plans;
        }, cancellationToken);

    /// <summary>Writes one PDF per group next to the original ("Name Page 3.pdf", "Name Pages 1-3.pdf").</summary>
    /// <returns>The new files, in order. Every part is built before any is written; if saving fails or is cancelled
    /// the parts already moved into place are taken back, so there is never a partial set.</returns>
    public static Task<IReadOnlyList<string>> SplitAsync(string path, IReadOnlyList<IReadOnlyList<int>> groups,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => SplitNow(path, groups, progress, 0, 1, cancellationToken), cancellationToken);

    /// <summary>Splits each PDF by its rule. A PDF whose rule would not split it (too few pages, the whole document as
    /// one range) is left alone and counted. With several jobs a locked, damaged or badly-ranged PDF is counted too;
    /// with one job it throws.</summary>
    /// <exception cref="PdfToolException">One job that is locked, damaged or has invalid ranges.</exception>
    public static Task<PdfOutcome> SplitAsync(IReadOnlyList<PdfSplitJob> jobs, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var plans = await PlanAsync(jobs, cancellationToken).ConfigureAwait(false);
            if (plans.Count == 1 && plans[0].Message is { } problem)
            {
                throw plans[0].IsLocked ? PdfToolException.Locked(plans[0].Path)
                    : plans[0].IsUnreadable ? PdfToolException.Unreadable(plans[0].Path)
                    : new PdfToolException(PdfToolErrorKind.NothingToSave, problem, plans[0].Path);
            }
            var saved = new List<string>();
            var toSplit = plans.Where(p => p.WillSplit).ToList();
            for (var i = 0; i < toSplit.Count; i++)
                saved.AddRange(SplitNow(toSplit[i].Path, toSplit[i].Parts.Select(p => p.Pages).ToList(), progress, i, toSplit.Count, cancellationToken));
            progress?.Report(1);
            return new PdfOutcome { Saved = saved, Unchanged = plans.Count - toSplit.Count, SavedLabel = "split", UnchangedLabel = "not split" };
        }, cancellationToken);

    private static IReadOnlyList<string> SplitNow(string path, IReadOnlyList<IReadOnlyList<int>> groups, IProgress<double>? progress,
        int fileIndex, int fileCount, CancellationToken ct)
    {
        if (groups.Count == 0 || groups.Any(g => g.Count == 0)) throw PdfToolException.NothingToSave();
        using var source = PdfFiles.Open(path, PdfDocumentOpenMode.Import);
        foreach (var page in groups.SelectMany(g => g))
        {
            if (page < 0 || page >= source.PageCount)
                throw new PdfToolException(PdfToolErrorKind.NothingToSave, $"Page {page + 1} doesn’t exist in “{System.IO.Path.GetFileName(path)}”.", path);
        }

        var built = new List<(string Label, byte[] Bytes)>();
        for (var g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            using var output = new PdfDocument();
            foreach (var index in groups[g]) output.AddPage(source.Pages[index]);
            CopyInfo(source, output);
            built.Add((Label(groups[g]), PdfFiles.ToBytes(output)));
            progress?.Report((fileIndex + 0.5 * (g + 1) / groups.Count) / fileCount);
        }
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var written = new List<string>();
        try
        {
            for (var g = 0; g < built.Count; g++)
            {
                var (label, bytes) = built[g];
                written.Add(PdfFiles.WriteNew(bytes, directory, () => OutputNaming.Destination(path, label, "pdf"), ct));
                progress?.Report((fileIndex + 0.5 + 0.5 * (g + 1) / built.Count) / fileCount);
            }
        }
        catch
        {
            foreach (var file in written) { try { File.Delete(file); } catch { /* best effort */ } }
            throw;
        }
        return written;
    }

    internal static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        var info = from.Info;
        if (!string.IsNullOrEmpty(info.Title)) to.Info.Title = info.Title;
        if (!string.IsNullOrEmpty(info.Author)) to.Info.Author = info.Author;
        if (!string.IsNullOrEmpty(info.Subject)) to.Info.Subject = info.Subject;
        if (!string.IsNullOrEmpty(info.Keywords)) to.Info.Keywords = info.Keywords;
    }
}
