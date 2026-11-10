namespace OpenShelf.Tools;

/// <summary>What a save did across the files an editor was given. Files that would not change (an image already
/// smaller than the target, a file with no metadata to remove) are left alone, and the confirmation says so:
/// "1 resized · 2 already smaller". Port of SaveOutcome.swift.</summary>
public sealed class SaveOutcome
{
    public SaveOutcome() { }
    public SaveOutcome(IEnumerable<string> saved) => Saved.AddRange(saved);

    /// <summary>Files written, in order.</summary>
    public List<string> Saved { get; } = [];
    /// <summary>Files left alone because saving them would change nothing.</summary>
    public int Unchanged { get; set; }
    /// <summary>Past tense for a saved file, e.g. "resized".</summary>
    public string SavedLabel { get; init; } = "saved";
    /// <summary>Why a file was left alone, e.g. "already smaller".</summary>
    public string UnchangedLabel { get; init; } = "unchanged";
    /// <summary>Shown after a plain "Saved 3 files", e.g. "as PNG".</summary>
    public string? Detail { get; init; }

    /// <summary>Whether anything was written, so the editor can close.</summary>
    public bool SavedAnything => Saved.Count > 0;

    public string Title
    {
        get
        {
            if (Saved.Count == 0) return "Nothing saved";
            if (Unchanged > 0) return $"{Saved.Count} {SavedLabel} · {Unchanged} {UnchangedLabel}";
            return Saved.Count == 1 ? "Saved" : $"Saved {Saved.Count} files";
        }
    }

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
            if (Saved.Count == 1) return Path.GetFileName(Saved[0]);
            return Unchanged > 0 ? null : Detail;
        }
    }

    /// <summary>The one-line text for the toast.</summary>
    public string Message => Subtitle is null ? Title : $"{Title} · {Subtitle}";
}
