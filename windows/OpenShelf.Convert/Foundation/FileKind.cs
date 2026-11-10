using System.IO.Compression;

namespace OpenShelf.Convert;

/// <summary>Broad category of a file, used to decide which conversions and tools apply. Port of FileKind.swift.</summary>
public enum FileKind
{
    Image,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Audio,
    Video,
    Subtitle,
    /// <summary>EPUB and Kindle books, which calibre (and, for EPUB, Pandoc) reads.</summary>
    Ebook,
    Archive,
    Folder,
    Other,
}

/// <summary>Classifies paths: by extension first, then by looking inside when the name does not say.</summary>
public static class FileKinds
{
    public static readonly IReadOnlySet<string> BookExtensions = new HashSet<string> { "epub", "mobi", "azw3", "azw" };

    /// <summary>Video containers only ffmpeg reads (MediaFormats.ffmpegOnlyVideoExtensions).</summary>
    public static readonly IReadOnlySet<string> FFmpegOnlyVideoExtensions = new HashSet<string>
        { "mkv", "webm", "avi", "wmv", "flv", "asf", "ogv", "mpg", "mpeg", "m2ts", "mts", "ts", "vob", "rm", "rmvb" };

    /// <summary>Audio containers only ffmpeg reads (MediaFormats.ffmpegOnlyAudioExtensions).</summary>
    public static readonly IReadOnlySet<string> FFmpegOnlyAudioExtensions = new HashSet<string>
        { "ogg", "oga", "opus", "wma", "mka", "ape", "ac3", "wv", "spx" };

    /// <summary>Every extension classified before looking inside the file. On macOS the system type
    /// database does most of this; on Windows the table is the whole story.</summary>
    public static readonly IReadOnlyDictionary<string, FileKind> ByExtension = BuildTable();

    private static Dictionary<string, FileKind> BuildTable()
    {
        var kinds = new Dictionary<string, FileKind>();
        void Add(FileKind kind, params string[] extensions)
        {
            foreach (var extension in extensions) kinds[extension] = kind;
        }
        Add(FileKind.Image, "jpg", "jpeg", "jpe", "jfif", "png", "gif", "tif", "tiff", "bmp", "dib", "heic", "heif", "webp", "avif", "svg", "ico", "jp2", "tga", "psd");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Document, "txt", "text", "rtf", "rtfd", "html", "htm", "xhtml", "docx", "doc", "odt", "md", "markdown", "mdown", "mkd", "mkdn", "pages");
        Add(FileKind.Subtitle, "srt", "vtt");
        Add(FileKind.Spreadsheet, "csv", "tsv", "xlsx", "xls", "ods", "numbers");
        Add(FileKind.Presentation, "ppt", "pptx", "odp", "key");
        Add(FileKind.Audio, "mp3", "m4a", "m4b", "aac", "wav", "aif", "aiff", "aifc", "flac", "caf", "amr");
        Add(FileKind.Video, "mp4", "m4v", "mov", "qt", "3gp", "3g2");
        Add(FileKind.Archive, "zip", "gz", "tgz", "tar", "bz2", "tbz", "tbz2", "xz", "txz", "7z", "rar");
        foreach (var extension in BookExtensions) kinds[extension] = FileKind.Ebook;
        foreach (var extension in FFmpegOnlyVideoExtensions) kinds[extension] = FileKind.Video;
        foreach (var extension in FFmpegOnlyAudioExtensions) kinds[extension] = FileKind.Audio;
        return kinds;
    }

    /// <summary>The kind of <paramref name="path"/>. Classifies what a link points to, not the link.</summary>
    public static FileKind Of(string path)
    {
        path = ResolveLink(path);
        var extension = ExtensionOf(path);
        // ".ts" is an MPEG transport stream or TypeScript source; only the bytes tell.
        var isTypeScript = extension == "ts" && IsTransportStream(path) == false;
        // Pages, Numbers and Keynote packages, and RTFD, can be folders on disk but are documents to people.
        if (!isTypeScript && ByExtension.TryGetValue(extension, out var known)) return known;
        if (Directory.Exists(path)) return FileKind.Folder;
        if (isTypeScript) return FileKind.Other;
        // Without a telling extension, look inside.
        return FileSniffer.KindOf(path) ?? FileKind.Other;
    }

    /// <summary>Lower-case extension without the dot ("tar.gz" is "gz").</summary>
    public static string ExtensionOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>The target of a symbolic link or junction, or the path itself.</summary>
    public static string ResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) return target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>Whether the file starts like an MPEG transport stream: 0x47 sync bytes 188 bytes apart.
    /// Null when the file cannot be read.</summary>
    public static bool? IsTransportStream(string path)
    {
        var header = FileSniffer.ReadHeader(path, 189);
        return header is null ? null : IsTransportStream(header);
    }

    public static bool IsTransportStream(byte[] header)
    {
        if (header.Length == 0 || header[0] != 0x47) return false;
        // A second packet, when the file is long enough to have one.
        return header.Length < 189 || header[188] == 0x47;
    }
}

/// <summary>Recognises common formats from their first bytes, for files whose name does not say what they are.</summary>
public static class FileSniffer
{
    public static byte[]? ReadHeader(string path, int count)
    {
        try
        {
            if (Directory.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            return buffer[..read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static FileKind? KindOf(string path)
    {
        var header = ReadHeader(path, 16);
        return header is null ? null : KindOfHeader(header);
    }

    public static FileKind? KindOfHeader(ReadOnlySpan<byte> bytes)
    {
        static bool Starts(ReadOnlySpan<byte> all, ReadOnlySpan<byte> prefix, int offset = 0) =>
            all.Length >= offset + prefix.Length && all.Slice(offset, prefix.Length).SequenceEqual(prefix);
        static bool Ascii(ReadOnlySpan<byte> all, string text, int offset = 0) =>
            Starts(all, System.Text.Encoding.ASCII.GetBytes(text), offset);

        if (Starts(bytes, [0xFF, 0xD8, 0xFF])) return FileKind.Image;                       // JPEG
        if (Starts(bytes, [0x89, 0x50, 0x4E, 0x47])) return FileKind.Image;                 // PNG
        if (Ascii(bytes, "GIF8")) return FileKind.Image;
        if (Ascii(bytes, "%PDF")) return FileKind.Pdf;
        if (Starts(bytes, [0x49, 0x49, 0x2A, 0x00]) || Starts(bytes, [0x4D, 0x4D, 0x00, 0x2A])) return FileKind.Image; // TIFF
        // "BM" alone is too common in text; also require the zeroed reserved field.
        if (Ascii(bytes, "BM") && Starts(bytes, [0, 0, 0, 0], 6)) return FileKind.Image;
        if (Ascii(bytes, "RIFF") && Ascii(bytes, "WEBP", 8)) return FileKind.Image;
        if (Ascii(bytes, "RIFF") && Ascii(bytes, "WAVE", 8)) return FileKind.Audio;
        if (Ascii(bytes, "FORM") && (Ascii(bytes, "AIFF", 8) || Ascii(bytes, "AIFC", 8))) return FileKind.Audio;
        if (Ascii(bytes, "ID3") || Starts(bytes, [0xFF, 0xFB])) return FileKind.Audio;     // MP3
        if (Ascii(bytes, "fLaC") || Ascii(bytes, "OggS")) return FileKind.Audio;
        // Matroska and WebM (EBML), AVI, ASF (WMV/WMA) and Flash video.
        if (Starts(bytes, [0x1A, 0x45, 0xDF, 0xA3])) return FileKind.Video;
        if (Ascii(bytes, "RIFF") && Ascii(bytes, "AVI ", 8)) return FileKind.Video;
        if (Starts(bytes, [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11])) return FileKind.Video;
        if (Ascii(bytes, "FLV")) return FileKind.Video;
        if (Starts(bytes, [0x50, 0x4B, 0x03, 0x04])) return FileKind.Archive;               // ZIP
        if (Starts(bytes, [0x1F, 0x8B])) return FileKind.Archive;                           // gzip
        if (Ascii(bytes, "ftyp", 4) && bytes.Length >= 12)
        {
            var brand = System.Text.Encoding.ASCII.GetString(bytes.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "heim" or "heis" or "mif1" or "msf1" or "avif" or "avis" => FileKind.Image,
                "M4A " or "M4B " => FileKind.Audio,
                _ => FileKind.Video,
            };
        }
        return null;
    }
}

/// <summary>Pages, Numbers and Keynote documents. They are zip files, or folder packages in older and synced copies.</summary>
public enum IWorkFormat { Pages, Numbers, Keynote }

public static class IWorkFormats
{
    public static IReadOnlyList<IWorkFormat> All { get; } = [IWorkFormat.Pages, IWorkFormat.Numbers, IWorkFormat.Keynote];

    public static string FileExtension(this IWorkFormat format) => format switch
    {
        IWorkFormat.Pages => "pages",
        IWorkFormat.Numbers => "numbers",
        _ => "key",
    };

    public static string AppName(this IWorkFormat format) => format switch
    {
        IWorkFormat.Pages => "Pages",
        IWorkFormat.Numbers => "Numbers",
        _ => "Keynote",
    };

    public static OutputFormat ToOutputFormat(this IWorkFormat format) => format switch
    {
        IWorkFormat.Pages => OutputFormat.Pages,
        IWorkFormat.Numbers => OutputFormat.Numbers,
        _ => OutputFormat.Key,
    };

    /// <summary>What LibreOffice makes of the format, in wheel order.</summary>
    public static IReadOnlyList<OutputFormat> LibreOfficeOutputs(this IWorkFormat format) => format switch
    {
        IWorkFormat.Pages => [OutputFormat.Pdf, OutputFormat.Docx, OutputFormat.Rtf, OutputFormat.Txt, OutputFormat.Jpg, OutputFormat.Png],
        IWorkFormat.Numbers => [OutputFormat.Pdf, OutputFormat.Xlsx, OutputFormat.Csv, OutputFormat.Ods, OutputFormat.Jpg, OutputFormat.Png],
        _ => [OutputFormat.Pdf, OutputFormat.Pptx, OutputFormat.Jpg, OutputFormat.Png],
    };

    /// <summary>Files the app opens and can save as one of its own documents.</summary>
    public static IReadOnlySet<string> ImportableExtensions(this IWorkFormat format) => format switch
    {
        IWorkFormat.Pages => new HashSet<string> { "docx", "doc", "rtf", "rtfd", "txt", "text" },
        IWorkFormat.Numbers => new HashSet<string> { "xlsx", "xls", "csv", "tsv" },
        _ => new HashSet<string> { "pptx", "ppt" },
    };

    public static IWorkFormat? FromExtension(string extension)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        foreach (var format in All)
            if (format.FileExtension() == extension) return format;
        return null;
    }

    /// <summary>The format of the file <paramref name="path"/> is, or points to.</summary>
    public static IWorkFormat? FromPath(string path) => FromExtension(FileKinds.ExtensionOf(FileKinds.ResolveLink(path)));

    /// <summary>The app that saves a file with this extension in its own format, if any.</summary>
    public static IWorkFormat? Importing(string extension)
    {
        extension = extension.TrimStart('.').ToLowerInvariant();
        foreach (var format in All)
            if (format.ImportableExtensions().Contains(extension)) return format;
        return null;
    }
}

/// <summary>The picture of the document that Pages, Numbers and Keynote save inside it: a PDF of every page in
/// iWork '09 files, a JPEG of the first page in newer ones. Turns these documents into PDFs and images without LibreOffice.</summary>
public sealed record IWorkPreview(string Path, bool IsPdf)
{
    /// <summary>Previews in order of preference; the best one found is used.</summary>
    public static readonly IReadOnlyList<(string Path, bool IsPdf)> Candidates =
        [("QuickLook/Preview.pdf", true), ("preview.jpg", false), ("QuickLook/Thumbnail.jpg", false)];

    /// <summary>What a preview alone converts to.</summary>
    public IReadOnlyList<OutputFormat> Outputs => IsPdf
        ? [OutputFormat.Pdf, OutputFormat.Jpg, OutputFormat.Png]
        : [OutputFormat.Jpg, OutputFormat.Png];

    /// <summary>The best preview in the package at <paramref name="path"/>, or null.</summary>
    public static IWorkPreview? Find(string path)
    {
        path = FileKinds.ResolveLink(path);
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var candidate in Candidates)
                    if (File.Exists(System.IO.Path.Combine(path, candidate.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))))
                        return new IWorkPreview(candidate.Path, candidate.IsPdf);
                return null;
            }
            if (!File.Exists(path)) return null;
            using var zip = ZipFile.OpenRead(path);
            return Find(zip.Entries.Select(entry => entry.FullName).ToList());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The best preview among a zip's entry names. Packages zipped by hand have their
    /// contents inside a folder named after them.</summary>
    public static IWorkPreview? Find(IReadOnlyList<string> names)
    {
        foreach (var candidate in Candidates)
        {
            var match = names.FirstOrDefault(name =>
            {
                if (name == candidate.Path) return true;
                if (!name.EndsWith("/" + candidate.Path, StringComparison.Ordinal)) return false;
                var folder = name[..^(candidate.Path.Length + 1)];
                return folder.Length > 0 && !folder.Contains('/');
            });
            if (match is not null) return new IWorkPreview(match, candidate.IsPdf);
        }
        return null;
    }
}
