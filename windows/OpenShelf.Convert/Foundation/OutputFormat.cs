namespace OpenShelf.Convert;

/// <summary>A conversion target. Port of OutputFormat.swift; the member order is the Mac enum's.</summary>
public enum OutputFormat
{
    // Images
    Png, Jpg, Webp, Heic, Tiff, Avif, Bmp, Gif,
    // Documents
    Pdf, Docx, Txt, Rtf, Html, Md, Epub, Pptx, Odt,
    // Spreadsheets
    Xlsx, Csv, Tsv, Ods,
    // Audio and video
    Mp4, Mov, M4a, Mp3, Wav, Aiff, Flac, Ogg, Opus, Wma,
    Mkv, Webm, Avi, Wmv,
    // Subtitles
    Srt, Vtt,
    // Kindle books, made by calibre
    Mobi, Azw3,
    // Archives
    Zip, Tar, Tgz, Extract,
    // Apple's own, saved by Pages, Numbers and Keynote. Never offered on Windows (no such apps), kept so ids match the Mac.
    Pages, Numbers, Key,
}

/// <summary>Properties of <see cref="OutputFormat"/>.</summary>
public static class OutputFormats
{
    public static IReadOnlyList<OutputFormat> All { get; } = Enum.GetValues<OutputFormat>();

    /// <summary>Stable lower-case id ("jpg", "tgz", "extract"), the Mac's raw value. Wheel entries use "convert.&lt;id&gt;".</summary>
    public static string Id(this OutputFormat format) => format.ToString().ToLowerInvariant();

    public static OutputFormat? FromId(string id) =>
        Enum.TryParse<OutputFormat>(id, ignoreCase: true, out var format) && Enum.IsDefined(format) ? format : null;

    /// <summary>The label on a wedge: "JPG", "TAR.GZ", "UNZIP".</summary>
    public static string Title(this OutputFormat format) => format switch
    {
        OutputFormat.Extract => "UNZIP",
        OutputFormat.Tgz => "TAR.GZ",
        _ => format.Id().ToUpperInvariant(),
    };

    /// <summary>Extension of the written file without the dot; null for formats that produce a folder.</summary>
    public static string? FileExtension(this OutputFormat format) => format switch
    {
        OutputFormat.Extract => null,
        OutputFormat.Tgz => "tar.gz",
        _ => format.Id(),
    };

    /// <summary>Whether a file with this extension (with or without the dot) is already in this format.</summary>
    public static bool Matches(this OutputFormat format, string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return format switch
        {
            OutputFormat.Jpg => ext is "jpg" or "jpeg" or "jpe",
            OutputFormat.Tiff => ext is "tif" or "tiff",
            OutputFormat.Heic => ext is "heic" or "heif",
            OutputFormat.Html => ext is "html" or "htm",
            OutputFormat.Aiff => ext is "aif" or "aiff",
            OutputFormat.Txt => ext is "txt" or "text",
            OutputFormat.Md => ext is "md" or "markdown",
            OutputFormat.Ogg => ext is "ogg" or "oga",
            OutputFormat.Tgz => ext == "tgz",
            _ => ext == format.Id(),
        };
    }

    /// <summary>Formats only ffmpeg can write from audio or video.</summary>
    public static bool NeedsFFmpeg(this OutputFormat format) =>
        format is OutputFormat.Mp3 or OutputFormat.Ogg or OutputFormat.Opus or OutputFormat.Wma
            or OutputFormat.Mkv or OutputFormat.Webm or OutputFormat.Avi or OutputFormat.Wmv;

    /// <summary>Audio-only outputs, which need a sound track in the source.</summary>
    public static bool IsAudioOnly(this OutputFormat format) =>
        format is OutputFormat.M4a or OutputFormat.Mp3 or OutputFormat.Wav or OutputFormat.Aiff
            or OutputFormat.Flac or OutputFormat.Ogg or OutputFormat.Opus or OutputFormat.Wma;

    /// <summary>Formats with a quality setting: lossy images, video and audio.</summary>
    public static bool IsLossy(this OutputFormat format) =>
        format is OutputFormat.Jpg or OutputFormat.Heic or OutputFormat.Avif or OutputFormat.Webp
            or OutputFormat.Mp4 or OutputFormat.Mov or OutputFormat.Mkv or OutputFormat.Webm or OutputFormat.Avi or OutputFormat.Wmv
            or OutputFormat.M4a or OutputFormat.Mp3 or OutputFormat.Ogg or OutputFormat.Opus or OutputFormat.Wma;

    /// <summary>Whether holding Alt on the wedge opens the options window first (SPEC 2.3.1): the lossy
    /// formats, plus GIF (width and frame rate, when made from video).</summary>
    public static bool HasOptions(this OutputFormat format) => format.IsLossy() || format == OutputFormat.Gif;

    /// <summary>Archive outputs: several files go into one.</summary>
    public static bool IsArchive(this OutputFormat format) => format is OutputFormat.Zip or OutputFormat.Tar or OutputFormat.Tgz;

    /// <summary>The app that saves this format, for PAGES, NUMBERS and KEY.</summary>
    public static IWorkFormat? IWork(this OutputFormat format) => format switch
    {
        OutputFormat.Pages => IWorkFormat.Pages,
        OutputFormat.Numbers => IWorkFormat.Numbers,
        OutputFormat.Key => IWorkFormat.Keynote,
        _ => null,
    };
}
