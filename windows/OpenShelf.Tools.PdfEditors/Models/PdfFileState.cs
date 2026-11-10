using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>What an editor knows about one PDF it was given: its size, page count, and whether it could be opened.
/// Locked and damaged files never throw here; they carry the wording to show (the Mac app's).</summary>
public sealed record PdfFileState
{
    public required string Path { get; init; }
    public int PageCount { get; init; }
    public long Size { get; init; }
    public PdfInfo? Info { get; init; }
    public bool IsLocked { get; init; }

    /// <summary>Why the file cannot be worked on: “name” is password-protected. / couldn’t be opened as a PDF.</summary>
    public string? Problem { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsUnreadable => Problem is not null && !IsLocked;
    public bool IsUsable => Problem is null;
    public bool HasMetadata => Info?.HasMetadata == true;

    public static PdfFileState Read(string path)
    {
        try
        {
            var info = PdfMetadata.Read(path);
            if (info.IsLocked)
                return new PdfFileState { Path = path, Size = info.FileSize, Info = info, IsLocked = true, Problem = PdfToolException.Locked(path).Message };
            return new PdfFileState { Path = path, PageCount = info.PageCount, Size = info.FileSize, Info = info };
        }
        catch (PdfToolException e)
        {
            return new PdfFileState { Path = path, Problem = e.Message };
        }
        catch (Exception)
        {
            return new PdfFileState { Path = path, Problem = PdfToolException.Unreadable(path).Message };
        }
    }

    public static IReadOnlyList<PdfFileState> ReadAll(IEnumerable<string> paths) => paths.Select(Read).ToList();
}

/// <summary>Text helpers shared by the PDF editors.</summary>
public static class PdfText
{
    /// <summary>File sizes the way the Mac app prints them: "4.2 MB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }

    public static string Pages(int count) => count == 1 ? "1 page" : $"{count} pages";

    /// <summary>"Save", "Save PDFs", "Save 1 PDF", "Save 3 PDFs".</summary>
    public static string Counted(string verb, int editedCount) => editedCount switch
    {
        0 => $"{verb} PDFs",
        1 => $"{verb} 1 PDF",
        var n => $"{verb} {n} PDFs",
    };
}

/// <summary>Turns the headless PDF results into the framework's types.</summary>
internal static class Outcomes
{
    public static SaveOutcome From(PdfOutcome outcome) => new(outcome.Saved)
    {
        Unchanged = outcome.Unchanged,
        SavedLabel = outcome.SavedLabel,
        UnchangedLabel = outcome.UnchangedLabel,
    };

    /// <summary>Waits for a headless task on a worker thread; its failures become messages a person can act on.</summary>
    public static T Wait<T>(Task<T> task)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch (PdfToolException e) { throw new SaveException(SaveException.Reason.Failed, e.Message, e); }
    }
}
