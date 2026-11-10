using OpenShelf.Core;
using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>How a line under the controls should look.</summary>
public enum Tone { Normal, Muted, Info, Warning }

/// <summary>One line the Split summary shows.</summary>
public sealed record SplitSummary(string Text, Tone Tone);

/// <summary>One part of the live preview: the pages and the file name it would get.</summary>
public sealed record SplitPreviewPart(string Pages, string FileName);

/// <summary>The Split editor without the window (SPEC §2.4): one rule per PDF, "Same for all" on by default, ranges read against the
/// longest PDF and cut to each one, and the summary, preview and button text that follow from them.</summary>
public sealed class SplitModel
{
    public SplitModel(IReadOnlyList<PdfFileState> files)
    {
        Files = files;
        Edits = new FileEdits<PdfSplitRule>(files.Count, _ => new PdfSplitRule(), (i, rule) => WillSplit(i, rule), sameForAll: true);
    }

    public IReadOnlyList<PdfFileState> Files { get; }
    public FileEdits<PdfSplitRule> Edits { get; }
    public bool IsBatch => Files.Count > 1;
    public int Index => Edits.Index;
    public PdfFileState File => Files[Index];
    public PdfSplitRule Rule => Edits.Current;

    /// <summary>Changes the shown PDF's rule; with Same for all on every PDF gets it.</summary>
    public void Update(Func<PdfSplitRule, PdfSplitRule> change) => Edits.Current = change(Edits.Current);

    /// <summary>The most pages a "pages each" count or a range may reach: the longest PDF while one rule splits them all.</summary>
    public int PageLimit => IsBatch && Edits.SameForAll ? Files.Max(f => f.PageCount) : File.PageCount;

    private int? RangeLimit => IsBatch ? Files.Max(f => f.PageCount) : null;

    /// <summary>The page groups the rule makes of PDF <paramref name="index"/>. With several PDFs, ranges are cut to each one's
    /// pages, so a rule kept from Same for all does not stop working when it is turned off.</summary>
    public PdfRangeResult Groups(int index)
    {
        var file = Files[index];
        if (!file.IsUsable) return PdfRangeResult.Fail(file.Problem!);
        return Edits[index].Groups(file.PageCount, RangeLimit);
    }

    private bool WillSplit(int index, PdfSplitRule rule)
    {
        var file = Files[index];
        if (!file.IsUsable) return false;
        var result = rule.Groups(file.PageCount, RangeLimit);
        return result.IsValid && PdfSplitter.Splits(result.Groups, file.PageCount);
    }

    /// <summary>PDFs their rule would split; the rest are left as they are.</summary>
    public IReadOnlyList<bool> Edited => Edits.Edited;

    public int EditedCount => Edits.EditedCount;

    /// <summary>New PDFs made from every PDF that will be split.</summary>
    public int TotalFileCount => Enumerable.Range(0, Files.Count).Where(Edits.IsEdited).Sum(i => Groups(i).Groups.Count);

    public string WindowTitle => IsBatch ? "Split PDFs" : "Split PDF";

    public string PrimaryTitle => IsBatch ? PdfText.Counted("Split", EditedCount) : "Split";

    public bool CanApply => IsBatch ? EditedCount > 0 : Edits.IsEdited(0);

    public string RangesPlaceholder => $"e.g. 1-3, 4, 5-{Math.Max(PageLimit, 5)}";

    /// <summary>The one-line verdict on the shown PDF.</summary>
    public SplitSummary Summary()
    {
        var file = File;
        if (file.IsLocked) return new SplitSummary("This PDF is password-protected.", Tone.Warning);
        if (file.IsUnreadable) return new SplitSummary(file.Problem!, Tone.Warning);
        var result = Groups(Index);
        if (!result.IsValid)
        {
            return string.IsNullOrWhiteSpace(Rule.RangesText)
                ? new SplitSummary("Enter the pages for each new PDF, separated by commas.", Tone.Info)
                : new SplitSummary(result.Error!, Tone.Warning);
        }
        if (!PdfSplitter.Splits(result.Groups, file.PageCount))
            return new SplitSummary($"Not split: it has {PdfText.Pages(file.PageCount)}", Tone.Muted);
        return new SplitSummary(result.Groups.Count == 1 ? "Creates 1 PDF" : $"Creates {result.Groups.Count} PDFs", Tone.Normal);
    }

    /// <summary>"3 PDFs in all", shown beside the summary with several PDFs.</summary>
    public string? TotalText => IsBatch ? (TotalFileCount == 1 ? "1 PDF in all" : $"{TotalFileCount} PDFs in all") : null;

    /// <summary>The parts the shown PDF would be cut into, with the names they would get beside it. Empty when there is nothing
    /// to split or the rule has a problem.</summary>
    public IReadOnlyList<SplitPreviewPart> Preview()
    {
        var file = File;
        if (!file.IsUsable) return [];
        var result = Groups(Index);
        if (!result.IsValid || !PdfSplitter.Splits(result.Groups, file.PageCount)) return [];
        return result.Groups.Select(g =>
        {
            var label = PdfSplitter.Label(g);
            return new SplitPreviewPart(label, System.IO.Path.GetFileName(OutputNaming.Destination(file.Path, label, "pdf")));
        }).ToList();
    }

    public IReadOnlyList<PdfSplitJob> Jobs() => Enumerable.Range(0, Files.Count).Select(i => new PdfSplitJob(Files[i].Path, Edits[i])).ToList();

    /// <summary>Splits. One PDF with a problem throws; several leave the problem ones alone and count them.</summary>
    public SaveOutcome Save(IProgress<double>? progress, CancellationToken cancel) =>
        Outcomes.From(Outcomes.Wait(PdfSplitter.SplitAsync(Jobs(), progress, cancel)));
}
