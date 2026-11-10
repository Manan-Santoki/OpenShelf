using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The PDF Metadata editor without the window: one PDF's fields to edit, or several to strip.</summary>
public sealed class MetadataModel
{
    public MetadataModel(IReadOnlyList<PdfFileState> files)
    {
        Files = files;
        Original = IsSingle ? files[0].Info?.Fields ?? new PdfMetadataFields() : new PdfMetadataFields();
        Fields = Original;
    }

    public IReadOnlyList<PdfFileState> Files { get; }
    public bool IsSingle => Files.Count == 1;
    public PdfMetadataFields Original { get; }

    /// <summary>The fields as typed (single PDF only).</summary>
    public PdfMetadataFields Fields { get; set; }

    public PdfInfo? SingleInfo => IsSingle ? Files[0].Info : null;

    /// <summary>A locked or damaged PDF among them: nothing can be saved.</summary>
    public bool HasUnusableFile => Files.Any(f => !f.IsUsable);

    /// <summary>Whether the fields can be shown and edited at all.</summary>
    public bool ShowsForm => IsSingle && Files[0].IsUsable;

    public bool HasLockedFile => Files.Any(f => f.IsLocked);

    /// <summary>The note above the form when a PDF cannot be changed (the Mac's wording), or its own message when damaged.</summary>
    public string? Warning
    {
        get
        {
            if (HasLockedFile) return "Password-protected PDFs can’t be changed.";
            return Files.FirstOrDefault(f => f.IsUnreadable)?.Problem;
        }
    }

    /// <summary>Saving is only offered when the copy would differ from the original.</summary>
    public bool CanSave
    {
        get
        {
            if (HasUnusableFile) return false;
            if (IsSingle) return Fields != Original || (Fields.IsEmpty && Files[0].HasMetadata);
            return Files.Any(f => f.HasMetadata);
        }
    }

    public string PrimaryTitle => IsSingle ? "Save" : "Remove Metadata";

    public string WindowTitle => "PDF Metadata";

    public string Title => IsSingle ? Files[0].Name : $"{Files.Count} PDFs";

    public string Detail
    {
        get
        {
            if (IsSingle && Files[0].IsLocked) return $"Password-protected · {PdfText.Bytes(Files[0].Size)}";
            if (IsSingle && Files[0].IsUnreadable) return "Can’t be opened";
            if (IsSingle && Files[0].Info is { } info) return $"{PdfText.Pages(info.PageCount)} · {PdfText.Bytes(info.FileSize)}";
            var withMetadata = Files.Count(f => f.HasMetadata);
            return $"{withMetadata} of {Files.Count} have document info";
        }
    }

    /// <summary>The state of one PDF in the several-PDF list.</summary>
    public static (string Text, bool Warn) Status(PdfFileState file) =>
        file.IsLocked ? ("Locked", true) : file.IsUnreadable ? ("Unreadable", true) : file.HasMetadata ? ("Has document info", false) : ("Clean", false);

    public void RemoveAll() => Fields = new PdfMetadataFields();
    public void Reset() => Fields = Original;

    /// <summary>One PDF: writes the edited copy, or a clean one when every field was cleared. Several: strips those that have any.</summary>
    public SaveOutcome Save(IProgress<double>? progress, CancellationToken cancel)
    {
        if (IsSingle)
        {
            var path = Files[0].Path;
            var saved = Fields.IsEmpty
                ? Outcomes.Wait(PdfMetadata.RemoveAllAsync(path, cancel))
                : Outcomes.Wait(PdfMetadata.SaveAsync(path, Fields, cancel));
            progress?.Report(1);
            return new SaveOutcome([saved]);
        }
        return Outcomes.From(Outcomes.Wait(PdfMetadata.RemoveAllAsync(Files.Select(f => f.Path).ToList(), progress, cancel)));
    }
}
