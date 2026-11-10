namespace OpenShelf.Convert;

/// <summary>Which conversions apply to which files. Port of ConversionCatalog in OutputFormat.swift, with Windows'
/// tools: every audio and video format goes through ffmpeg (there is no AVFoundation), WebP is written by the bundled
/// ImageMagick, and Pages, Numbers and Keynote are never saved (no such apps).
/// <para>Wheels stay at 12 items or fewer; the last is always ZIP. More than seven are shown over two pages in the
/// wheel's own ordering.</para></summary>
public static class ConversionCatalog
{
    /// <summary>The most wedges a wheel shows; the last one is always ZIP.</summary>
    public const int MaxWedges = 12;

    /// <summary>Pandoc's reader for each kind of file it is given.</summary>
    public static readonly IReadOnlyDictionary<string, string> PandocReaders = new Dictionary<string, string>
    {
        ["md"] = "markdown", ["markdown"] = "markdown", ["mdown"] = "markdown", ["mkd"] = "markdown", ["mkdn"] = "markdown",
        ["docx"] = "docx", ["odt"] = "odt", ["html"] = "html", ["htm"] = "html", ["rtf"] = "rtf", ["epub"] = "epub",
    };

    /// <summary>What calibre makes of a book, in wheel order.</summary>
    public static readonly IReadOnlyList<OutputFormat> CalibreFormats = [OutputFormat.Epub, OutputFormat.Mobi, OutputFormat.Azw3, OutputFormat.Pdf];
    /// <summary>What calibre makes of a PDF that nothing else does.</summary>
    public static readonly IReadOnlyList<OutputFormat> CalibreFormatsForPdf = [OutputFormat.Mobi, OutputFormat.Azw3];

    public static readonly IReadOnlyList<OutputFormat> TranscriptAudioFormats = [OutputFormat.Txt, OutputFormat.Srt, OutputFormat.Vtt];
    public static readonly IReadOnlyList<OutputFormat> TranscriptVideoFormats = [OutputFormat.Srt, OutputFormat.Vtt];

    /// <summary>Formats every file in <paramref name="paths"/> can be converted to, in the first file's order.</summary>
    /// <param name="hasAudioTrack">Whether a video has sound (ffprobe, run by the media engine); null or an unknown answer means yes.</param>
    public static IReadOnlyList<OutputFormat> Formats(IReadOnlyList<string> paths, ExternalTools tools, Func<string, bool?>? hasAudioTrack = null)
    {
        if (paths.Count == 0) return [];
        var common = Formats(paths[0], tools, hasAudioTrack).ToList();
        foreach (var path in paths.Skip(1))
        {
            var available = Formats(path, tools, hasAudioTrack).ToHashSet();
            common.RemoveAll(format => !available.Contains(format));
        }
        return common;
    }

    public static IReadOnlyList<OutputFormat> Formats(string path, ExternalTools tools, Func<string, bool?>? hasAudioTrack = null)
    {
        var ext = FileKinds.ExtensionOf(FileKinds.ResolveLink(path));
        var hasFFmpeg = tools.Ffmpeg is not null;
        var hasOffice = tools.LibreOffice is not null;
        var formats = new List<OutputFormat>();

        // Pages, Numbers and Keynote files, whether zip files or folders.
        if (IWorkFormats.FromPath(path) is { } iWorkFormat)
        {
            var iWork = hasOffice ? iWorkFormat.LibreOfficeOutputs() : IWorkPreview.Find(path)?.Outputs ?? [];
            return iWork.Count == 0 ? [OutputFormat.Zip] : [.. iWork.Take(MaxWedges - 1), OutputFormat.Zip];
        }

        switch (FileKinds.Of(path))
        {
            case FileKind.Image:
                formats.AddRange([OutputFormat.Png, OutputFormat.Jpg, OutputFormat.Heic, OutputFormat.Tiff, OutputFormat.Avif, OutputFormat.Bmp, OutputFormat.Pdf, OutputFormat.Docx]);
                if (tools.CanEncodeWebP) formats.Insert(2, OutputFormat.Webp);
                if (!tools.CanEncodeHeic) formats.Remove(OutputFormat.Heic);
                if (!tools.CanEncodeAvif) formats.Remove(OutputFormat.Avif);
                break;
            case FileKind.Pdf:
                formats.AddRange([OutputFormat.Docx, OutputFormat.Jpg, OutputFormat.Png, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Pptx, OutputFormat.Xlsx]);
                if (tools.EbookConvert is not null) formats.AddRange(CalibreFormatsForPdf);
                break;
            case FileKind.Document:
                formats.AddRange([OutputFormat.Pdf, OutputFormat.Docx, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Html, OutputFormat.Rtf]);
                // Only Pandoc writes OpenDocument text well.
                if (tools.Pandoc is not null && PandocReaders.ContainsKey(ext)) formats.Add(OutputFormat.Odt);
                break;
            case FileKind.Spreadsheet:
                formats.AddRange(SpreadsheetFormats(ext, hasOffice));
                break;
            case FileKind.Presentation:
                // Slides need LibreOffice to render.
                if (hasOffice) formats.AddRange([OutputFormat.Pdf, OutputFormat.Jpg, OutputFormat.Png]);
                break;
            case FileKind.Audio:
                // Windows has no built-in transcoder in the engine: ffmpeg decodes and encodes everything.
                if (hasFFmpeg)
                    formats.AddRange([OutputFormat.Mp3, OutputFormat.M4a, OutputFormat.Wav, OutputFormat.Aiff, OutputFormat.Flac, OutputFormat.Ogg, OutputFormat.Opus, OutputFormat.Wma]);
                // Speech to text, once there is a way to read the sound.
                if (tools.CanTranscribe && formats.Count > 0) formats.AddRange(TranscriptAudioFormats);
                break;
            case FileKind.Video:
                if (hasFFmpeg)
                    formats.AddRange([OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.M4a, OutputFormat.Gif, OutputFormat.Mp3, OutputFormat.Mkv, OutputFormat.Webm, OutputFormat.Avi, OutputFormat.Wmv]);
                if (tools.CanTranscribe && formats.Count > 0) formats.AddRange(TranscriptVideoFormats);
                // Silent videos have nothing to extract as audio, or to transcribe.
                if (formats.Count > 0 && hasAudioTrack?.Invoke(path) == false)
                    formats.RemoveAll(format => format.IsAudioOnly() || TranscriptAudioFormats.Contains(format));
                break;
            case FileKind.Subtitle:
                formats.AddRange([OutputFormat.Txt, OutputFormat.Srt, OutputFormat.Vtt]);
                break;
            case FileKind.Ebook:
                // Without a tool that reads books, a book is just a file, as before.
                formats.AddRange(EbookFormats(path, ext, tools));
                if (formats.Count == 0) return [OutputFormat.Zip, OutputFormat.Tar, OutputFormat.Tgz];
                break;
            case FileKind.Archive:
                return [OutputFormat.Extract];
            default:
                return [OutputFormat.Zip, OutputFormat.Tar, OutputFormat.Tgz];
        }
        // Pages, Numbers and Keynote save what they open as their own documents.
        if (IWorkFormats.Importing(ext) is { } importing && tools.IWorkApps.Contains(importing))
            formats.Add(importing.ToOutputFormat());
        formats.RemoveAll(format => format.Matches(ext));
        return [.. formats.Take(MaxWedges - 1), OutputFormat.Zip];
    }

    private static IEnumerable<OutputFormat> SpreadsheetFormats(string ext, bool hasOffice)
    {
        switch (ext)
        {
            case "csv" or "tsv" or "xlsx":
                // Built in: CSV, TSV and XLSX to each other, and a table PDF; ODS needs LibreOffice.
                return hasOffice
                    ? [OutputFormat.Xlsx, OutputFormat.Csv, OutputFormat.Tsv, OutputFormat.Pdf, OutputFormat.Ods]
                    : [OutputFormat.Xlsx, OutputFormat.Csv, OutputFormat.Tsv, OutputFormat.Pdf];
            default:
                // XLS and ODS can only be opened by LibreOffice.
                return hasOffice ? [OutputFormat.Xlsx, OutputFormat.Csv, OutputFormat.Pdf, OutputFormat.Ods] : [];
        }
    }

    /// <summary>Everything a book can become with what is installed: calibre's formats, then Pandoc's.</summary>
    private static IEnumerable<OutputFormat> EbookFormats(string path, string ext, ExternalTools tools)
    {
        var calibre = tools.EbookConvert is not null && FileKinds.BookExtensions.Contains(ext) ? CalibreFormats : [];
        var pandoc = tools.Pandoc is not null && PandocReaders.ContainsKey(ext)
            ? new List<OutputFormat> { OutputFormat.Docx, OutputFormat.Md, OutputFormat.Html, OutputFormat.Odt, OutputFormat.Txt }
                .Concat(tools.PdfEngine is not null ? [OutputFormat.Pdf] : [])
                .Where(format => !calibre.Contains(format))
            : [];
        return calibre.Concat(pandoc);
    }
}
