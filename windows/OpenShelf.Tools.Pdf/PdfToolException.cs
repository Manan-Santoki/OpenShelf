namespace OpenShelf.Tools.Pdf;

/// <summary>Why a PDF tool could not do its job.</summary>
public enum PdfToolErrorKind
{
    /// <summary>The file is missing, damaged or not a PDF (or not an image the tool can read).</summary>
    Unreadable,
    /// <summary>The PDF needs a password to open.</summary>
    Locked,
    /// <summary>The result could not be written or moved into place.</summary>
    Unwritable,
    /// <summary>There is nothing to save (no pages, no files).</summary>
    NothingToSave,
}

/// <summary>A PDF tool failed. The message is plain language fit for a toast; no partial output is left behind.</summary>
public sealed class PdfToolException : Exception
{
    public PdfToolErrorKind Kind { get; }

    /// <summary>The file the problem is about, when there is one.</summary>
    public string? Path { get; }

    public PdfToolException(PdfToolErrorKind kind, string message, string? path = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Path = path;
    }

    private static string Name(string path) => System.IO.Path.GetFileName(path);

    public static PdfToolException Unreadable(string path, Exception? inner = null) =>
        new(PdfToolErrorKind.Unreadable, $"“{Name(path)}” couldn’t be opened as a PDF.", path, inner);

    public static PdfToolException UnreadableImage(string path, Exception? inner = null) =>
        new(PdfToolErrorKind.Unreadable, $"“{Name(path)}” couldn’t be read as a PDF or an image.", path, inner);

    public static PdfToolException Locked(string path, Exception? inner = null) =>
        new(PdfToolErrorKind.Locked, $"“{Name(path)}” is password-protected.", path, inner);

    public static PdfToolException Unwritable(string path, Exception? inner = null) =>
        new(PdfToolErrorKind.Unwritable, $"“{Name(path)}” couldn’t be saved.", path, inner);

    public static PdfToolException NothingToSave() =>
        new(PdfToolErrorKind.NothingToSave, "There are no pages to save.");
}
