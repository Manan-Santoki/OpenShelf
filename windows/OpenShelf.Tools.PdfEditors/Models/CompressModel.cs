using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The PDF Compress editor without the window: the level, the estimated size scaled to the batch, and the wording around it.</summary>
public sealed class CompressModel
{
    public CompressModel(IReadOnlyList<PdfFileState> files)
    {
        Files = files;
        TotalOriginal = files.Sum(f => f.Size);
    }

    public IReadOnlyList<PdfFileState> Files { get; }
    public long TotalOriginal { get; }
    public PdfCompressionLevel Level { get; set; } = PdfCompressionLevel.Balanced;

    /// <summary>The estimated size of all the copies together, once known.</summary>
    public long? Estimate { get; private set; }

    public bool EstimateFailed { get; private set; }

    /// <summary>Why the first PDF cannot be compressed at all (locked, damaged), when that is the reason the estimate failed.</summary>
    public string? Problem => Files[0].Problem;

    public void StartEstimate()
    {
        Estimate = null;
        EstimateFailed = false;
    }

    public void FailEstimate() => EstimateFailed = true;

    /// <summary>The first PDF was compressed in memory to <paramref name="firstCompressedSize"/>; the batch is estimated to shrink alike.</summary>
    public void SetEstimate(long firstCompressedSize) => Estimate = Scale(firstCompressedSize, Files[0].Size, TotalOriginal);

    public static long Scale(long firstCompressedSize, long firstOriginal, long total) =>
        firstOriginal > 0 ? (long)(firstCompressedSize * (double)total / firstOriginal) : firstCompressedSize;

    /// <summary>For a single PDF the estimate is the real result, so a copy that would not be smaller is known before anything is saved.</summary>
    public bool IsKnownNotSmaller => Files.Count == 1 && Estimate is { } e && !PdfCompressor.IsWorthSaving(e, Files[0].Size);

    /// <summary>Whole percent saved by the estimate (truncated, as on the Mac); 0 when it is no smaller.</summary>
    public int SavingPercent => Estimate is { } e && TotalOriginal > 0 ? 100 - (int)(e / (double)TotalOriginal * 100) : 0;

    public string Subtitle => Files.Count == 1 ? PdfText.Bytes(TotalOriginal) : $"{PdfText.Bytes(TotalOriginal)} in total";

    public string Title => Files.Count == 1 ? Files[0].Name : $"{Files.Count} PDFs";

    /// <summary>"≈ 1.2 MB" or "≈ 3.4 MB for 3 PDFs"; "Estimating…" and "Couldn’t estimate the size" before and instead.</summary>
    public string EstimateText
    {
        get
        {
            if (Estimate is not { } e) return EstimateFailed ? "Couldn’t estimate the size" : "Estimating…";
            return Files.Count == 1 ? $"≈ {PdfText.Bytes(e)}" : $"≈ {PdfText.Bytes(e)} for {Files.Count} PDFs";
        }
    }

    /// <summary>The part after the size: "  (35% smaller)" or "  (no smaller — nothing left to squeeze)"; empty while unknown.</summary>
    public string SavingText => Estimate is null ? "" : SavingPercent > 0 ? $"  ({SavingPercent}% smaller)" : "  (no smaller — nothing left to squeeze)";

    /// <summary>What to say when a save wrote nothing because nothing got smaller.</summary>
    public static string NotSmallerMessage(int count) =>
        count == 1 ? "Already as small as it gets — nothing saved." : $"These {count} PDFs are already as small as they get — nothing saved.";

    /// <summary>Compresses every PDF. Files that would not get smaller are left alone and counted; a locked or damaged one throws.</summary>
    public SaveOutcome Save(IProgress<double>? progress, CancellationToken cancel, out string? nothingSaved)
    {
        var batch = Outcomes.Wait(PdfCompressor.CompressAsync(Files.Select(f => f.Path).ToList(), Level, progress, cancel));
        nothingSaved = batch.Outcome.SavedAnything ? null : NotSmallerMessage(batch.Outcome.Unchanged);
        return Outcomes.From(batch.Outcome);
    }
}
