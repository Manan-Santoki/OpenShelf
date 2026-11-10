using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Tools.Pdf;

/// <summary>Document-level details shown and edited by the PDF Metadata tool.</summary>
public sealed record PdfMetadataFields
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    /// <summary>Comma-separated, as typed.</summary>
    public string Keywords { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Producer { get; init; } = "";

    public bool IsEmpty => new[] { Title, Author, Subject, Keywords, Creator, Producer }.All(string.IsNullOrWhiteSpace);
}

/// <summary>What a PDF says about itself, plus a few facts about the file.</summary>
public sealed record PdfInfo
{
    public required PdfMetadataFields Fields { get; init; }
    public int PageCount { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public bool IsEncrypted { get; init; }
    /// <summary>The PDF needs a password; nothing else is known about it.</summary>
    public bool IsLocked { get; init; }

    /// <summary>Whether the file says who made it or what it is about. The producer stamp that PDF writers (including this
    /// one) add to every file they save does not count: it describes the save, not the author, and cannot be removed.</summary>
    public bool HasMetadata
    {
        get
        {
            var identifying = IsSystemProducer(Fields.Producer) ? Fields with { Producer = "" } : Fields;
            if (IsSystemProducer(identifying.Creator)) identifying = identifying with { Creator = "" };
            return !identifying.IsEmpty;
        }
    }

    /// <summary>Whether <paramref name="producer"/> is a stamp a writer adds on its own.</summary>
    public static bool IsSystemProducer(string producer) =>
        producer.StartsWith("PDFsharp", StringComparison.OrdinalIgnoreCase) || producer.EndsWith("Quartz PDFContext", StringComparison.Ordinal) ||
        producer.StartsWith("OpenShelf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads, edits and removes PDF document information.</summary>
public static class PdfMetadata
{
    /// <summary>Suffix of a copy with edited info: "Report Metadata.pdf" (distinct from the Pages editor's "Edited").</summary>
    public const string EditedSuffix = "Metadata";

    /// <summary>Suffix of a copy with all info removed: "Report Clean.pdf".</summary>
    public const string RemovedSuffix = "Clean";

    /// <summary>Reads the document info. A password-protected PDF gives <see cref="PdfInfo.IsLocked"/> rather than an exception.</summary>
    /// <exception cref="PdfToolException">The file is missing or damaged.</exception>
    public static Task<PdfInfo> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path), cancellationToken);

    /// <summary>Synchronous <see cref="ReadAsync"/>, cheap enough for a window's constructor.</summary>
    public static PdfInfo Read(string path)
    {
        var size = PdfFiles.Size(path);
        try
        {
            using var document = PdfFiles.Open(path, PdfDocumentOpenMode.Import);
            return new PdfInfo
            {
                Fields = FieldsOf(document),
                PageCount = document.PageCount,
                FileSize = size,
                Created = Date(document.Info.CreationDate),
                Modified = Date(document.Info.ModificationDate),
                IsEncrypted = document.SecuritySettings.IsEncrypted,
            };
        }
        catch (PdfToolException e) when (e.Kind == PdfToolErrorKind.Locked)
        {
            return new PdfInfo { Fields = new PdfMetadataFields(), FileSize = size, IsEncrypted = true, IsLocked = true };
        }
    }

    private static DateTimeOffset? Date(DateTime value) => value == default ? null : new DateTimeOffset(value);

    private static PdfMetadataFields FieldsOf(PdfDocument document) => new()
    {
        Title = (document.Info.Title ?? "").Trim(),
        Author = (document.Info.Author ?? "").Trim(),
        Subject = (document.Info.Subject ?? "").Trim(),
        Keywords = (document.Info.Keywords ?? "").Trim(),
        Creator = (document.Info.Creator ?? "").Trim(),
        Producer = (document.Info.Producer ?? "").Trim(),
    };

    /// <summary>Writes a copy with <paramref name="fields"/> as the document info ("Name Metadata.pdf"). Empty fields are
    /// removed; keywords are tidied to "a, b, c". If every field is empty this is the same as <see cref="RemoveAllAsync(string, CancellationToken)"/>.</summary>
    /// <returns>The full path of the copy.</returns>
    public static Task<string> SaveAsync(string path, PdfMetadataFields fields, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (fields.IsEmpty) return RemoveNow(path, cancellationToken);
            using var document = PdfFiles.Open(path, PdfDocumentOpenMode.Modify);
            Set(document.Info, "/Title", fields.Title);
            Set(document.Info, "/Author", fields.Author);
            Set(document.Info, "/Subject", fields.Subject);
            Set(document.Info, "/Creator", fields.Creator);
            Set(document.Info, "/Producer", fields.Producer);
            var keywords = string.Join(", ", fields.Keywords.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0));
            Set(document.Info, "/Keywords", keywords);
            document.Info.ModificationDate = DateTime.Now;
            // The XMP packet would still say the old things.
            document.Internals.Catalog.Elements.Remove("/Metadata");
            return PdfFiles.SaveSuffixed(document, path, EditedSuffix, cancellationToken, blankStamp: true);
        }, cancellationToken);

    private static void Set(PdfDocumentInformation info, string key, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) info.Elements.Remove(key);
        else info.Elements.SetString(key, trimmed);
    }

    /// <summary>Writes a clean copy with all document info and the XMP packet removed ("Name Clean.pdf").</summary>
    /// <returns>The full path of the copy.</returns>
    public static Task<string> RemoveAllAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => RemoveNow(path, cancellationToken), cancellationToken);

    /// <summary>Writes clean copies of the PDFs that carry metadata; the rest are left alone and counted, since a copy would be
    /// identical. A PDF that cannot be read goes through, so its error is reported.</summary>
    public static Task<PdfOutcome> RemoveAllAsync(IReadOnlyList<string> paths, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var saved = new List<string>();
            var unchanged = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfInfo? info = null;
                try { info = Read(paths[i]); } catch (PdfToolException) { /* reported by the save below */ }
                if (info is { IsLocked: false } && !info.HasMetadata) unchanged++;
                else saved.Add(RemoveNow(paths[i], cancellationToken));
                progress?.Report((i + 1.0) / paths.Count);
            }
            return new PdfOutcome { Saved = saved, Unchanged = unchanged, SavedLabel = "cleaned", UnchangedLabel = "already clean" };
        }, cancellationToken);

    private static string RemoveNow(string path, CancellationToken ct)
    {
        using var document = PdfFiles.Open(path, PdfDocumentOpenMode.Modify);
        document.Info.Elements.Clear();
        document.Internals.Catalog.Elements.Remove("/Metadata");
        return PdfFiles.SaveSuffixed(document, path, RemovedSuffix, ct, blankStamp: true);
    }
}
