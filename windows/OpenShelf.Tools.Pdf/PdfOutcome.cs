namespace OpenShelf.Tools.Pdf;

/// <summary>What a tool run over several files produced: the files written and the ones left alone.
/// Port of SaveOutcome.swift, so the closing toast reads the same.</summary>
public sealed record PdfOutcome
{
    /// <summary>Files written, in order.</summary>
    public IReadOnlyList<string> Saved { get; init; } = [];

    /// <summary>Files left alone because saving them would change nothing.</summary>
    public int Unchanged { get; init; }

    /// <summary>Past tense for a saved file, e.g. "edited".</summary>
    public string SavedLabel { get; init; } = "saved";

    /// <summary>Why a file was left alone, e.g. "not split".</summary>
    public string UnchangedLabel { get; init; } = "unchanged";

    public bool SavedAnything => Saved.Count > 0;

    /// <summary>Toast title: "Saved", "Saved 3 files", "2 cleaned · 1 already clean", or "Nothing saved".</summary>
    public string Title
    {
        get
        {
            if (Saved.Count == 0) return "Nothing saved";
            if (Unchanged > 0) return $"{Saved.Count} {SavedLabel} · {Unchanged} {UnchangedLabel}";
            return Saved.Count == 1 ? "Saved" : $"Saved {Saved.Count} files";
        }
    }

    /// <summary>Toast subtitle: the file name when one file was written, or why nothing was.</summary>
    public string? Subtitle
    {
        get
        {
            if (Saved.Count == 0)
            {
                if (Unchanged == 0) return null;
                var reason = char.ToUpperInvariant(UnchangedLabel[0]) + UnchangedLabel[1..];
                return Unchanged == 1 ? reason : $"All {Unchanged} {UnchangedLabel}";
            }
            return Saved.Count == 1 ? System.IO.Path.GetFileName(Saved[0]) : null;
        }
    }
}
