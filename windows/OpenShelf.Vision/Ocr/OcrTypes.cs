namespace OpenShelf.Vision;

/// <summary>What OCR saves.</summary>
public enum OcrOutput
{
    /// <summary>"Name OCR.pdf": the pages with an invisible text layer over each recognised line.</summary>
    SearchablePdf,
    /// <summary>"Name OCR.txt": plain UTF-8 text, pages separated by a blank line.</summary>
    Text,
}

/// <summary>A recognised line and where it sits, as fractions of the page (0 to 1) with the origin at the bottom left,
/// like the Mac's Vision boxes. <see cref="Width"/> and <see cref="Height"/> are fractions too.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height);

/// <summary>A recognition language installed on this PC.</summary>
public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>The outcome for one file of a batch: exactly one of <see cref="OutputPath"/> and <see cref="Error"/> is set.</summary>
public sealed record OcrFileResult(string SourcePath, string? OutputPath, string? Error)
{
    public bool Succeeded => OutputPath is not null;
}

/// <summary>Every file of a batch, in the order given. A file that fails does not stop the others.</summary>
public sealed record OcrBatchResult(IReadOnlyList<OcrFileResult> Files)
{
    public IReadOnlyList<string> Saved => Files.Where(f => f.Succeeded).Select(f => f.OutputPath!).ToList();
    public IReadOnlyList<OcrFileResult> Failed => Files.Where(f => !f.Succeeded).ToList();
}

/// <summary>Settings for <see cref="OcrService"/>.</summary>
public sealed class OcrOptions
{
    /// <summary>BCP-47 tag of the language to read ("en-US"). Null uses the user's profile languages, then any installed one.</summary>
    public string? LanguageTag { get; init; }

    /// <summary>Resolution scanned PDF pages are rendered at for recognition. Default 300 like the Mac.</summary>
    public int RenderDpi { get; init; } = 300;
}
