using System.IO;
using OpenShelf.Core;

namespace OpenShelf.Tour;

/// <summary>The steps of the guided tour, in order (SPEC §6): Welcome, eight practice steps, You're set.</summary>
public enum TourStep { Welcome, Convert, Markdown, Tools, Pdf, Video, Shelf, Keyboard, Options, Finish }

/// <summary>A file the tour gives people to practise on.</summary>
public enum PracticeFile { Photo, Notes, Report, Clip, Letter }

/// <summary>What each step is, and the rules for when the person has done it.</summary>
public static class TourSteps
{
    public static readonly IReadOnlyList<TourStep> All = Enum.GetValues<TourStep>();
    public static readonly IReadOnlyList<TourStep> Practice = All.Where(IsPractice).ToArray();

    public static bool IsPractice(this TourStep step) => step is not (TourStep.Welcome or TourStep.Finish);

    public static string Title(this TourStep step) => step switch
    {
        TourStep.Welcome => "Welcome",
        TourStep.Convert => "Convert a photo",
        TourStep.Markdown => "Markdown to PDF",
        TourStep.Tools => "Edit with Tools",
        TourStep.Pdf => "Work with PDFs",
        TourStep.Video => "Trim a video",
        TourStep.Shelf => "Keep files on a shelf",
        TourStep.Keyboard => "No drag needed",
        TourStep.Options => "Choose the settings",
        _ => "You’re set",
    };

    /// <summary>One line on why this matters, under the title.</summary>
    public static string Summary(this TourStep step) => step switch
    {
        TourStep.Welcome => "Learn OpenShelf by doing it, with files made for practice.",
        TourStep.Convert => "Any file becomes the format you need, saved right next to it.",
        TourStep.Markdown => "Notes become a typeset document: headings, lists, tables and code.",
        TourStep.Tools => "Crop, annotate, redact, resize and more, without opening an editor app.",
        TourStep.Pdf => "Split, reorder, combine, compress and read text from PDFs.",
        TourStep.Video => "Trim, crop, rotate, mute or turn clips into GIFs.",
        TourStep.Shelf => "A shelf holds files while you gather them, then drags them anywhere.",
        TourStep.Keyboard => "Select files in Explorer or on a shelf and open a wheel with a shortcut.",
        TourStep.Options => "Hold Alt as you drop to pick the quality, size or codec for one conversion.",
        _ => "That’s everything you need. The rest is in Settings.",
    };

    /// <summary>A Segoe Fluent / MDL2 glyph for the step's badge.</summary>
    public static string Glyph(this TourStep step) => step switch
    {
        TourStep.Welcome => "",    // home
        TourStep.Convert => "",    // sync
        TourStep.Markdown => "",   // document
        TourStep.Tools => "",      // crop
        TourStep.Pdf => "",
        TourStep.Video => "",      // video
        TourStep.Shelf => "",      // library
        TourStep.Keyboard => "",   // keyboard
        TourStep.Options => "",    // settings
        _ => "",                   // check
    };

    /// <summary>The practice file the step is done with.</summary>
    public static PracticeFile? File(this TourStep step) => step switch
    {
        TourStep.Convert or TourStep.Tools => PracticeFile.Photo,
        TourStep.Markdown or TourStep.Shelf => PracticeFile.Notes,
        TourStep.Pdf => PracticeFile.Report,
        TourStep.Video or TourStep.Options => PracticeFile.Clip,
        TourStep.Keyboard => PracticeFile.Letter,
        _ => null,
    };

    /// <summary>The wheel the step teaches, whose keys it shows.</summary>
    public static WheelKind? Wheel(this TourStep step) => step switch
    {
        TourStep.Convert or TourStep.Markdown or TourStep.Options => WheelKind.Convert,
        TourStep.Tools or TourStep.Pdf or TourStep.Video => WheelKind.Tools,
        TourStep.Shelf => WheelKind.Actions,
        _ => null,
    };

    /// <summary>The wedge to drop on, as the instructions name it.</summary>
    public static string Target(this TourStep step) => step switch
    {
        TourStep.Convert => "PNG",
        TourStep.Markdown => "PDF",
        TourStep.Tools => "Crop",
        TourStep.Pdf => "Pages or Split",
        TourStep.Video => "Trim",
        TourStep.Shelf => "Shelf",
        TourStep.Options => "MP4 or GIF",
        _ => "",
    };

    public static string WheelTitle(WheelKind kind) => kind switch
    {
        WheelKind.Convert => "Convert", WheelKind.Tools => "Tools", _ => "Actions",
    };

    /// <summary>
    /// Whether <paramref name="e"/> completes the step. Any file of the right kind counts, so practising with one's
    /// own files works too. Wedge ids are "convert.&lt;format&gt;", "tool.&lt;name&gt;" and "action.&lt;name&gt;".
    /// </summary>
    public static bool IsCompleted(this TourStep step, WheelEvent e)
    {
        var id = e.EntryId;
        var ran = id is not null;
        bool Convert() => ran && e.Kind == WheelKind.Convert && id!.StartsWith("convert.", StringComparison.Ordinal);
        bool Tool() => ran && e.Kind == WheelKind.Tools && id!.StartsWith("tool.", StringComparison.Ordinal);
        bool Has(FileKind kind) => e.Paths.Any(p => FileKinds.Of(p) == kind);

        return step switch
        {
            TourStep.Convert => Convert() && Has(FileKind.Image),
            TourStep.Markdown => ran && e.Kind == WheelKind.Convert && id == "convert.pdf" && Has(FileKind.Markdown),
            TourStep.Tools => Tool() && Has(FileKind.Image),
            TourStep.Pdf => Tool() && Has(FileKind.Pdf),
            TourStep.Video => (Tool() || (ran && e.Kind == WheelKind.Convert && id == "convert.gif")) && (Has(FileKind.Video) || Has(FileKind.Audio)),
            TourStep.Shelf => ran && id is "action.shelf" or "action.addtoshelf",
            TourStep.Keyboard => !ran && e.Keyboard,
            TourStep.Options => Convert() && e.OptionsRequested,
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="e"/> is the step under way: its wheel opened mid-drag.</summary>
    public static bool IsUnderWay(this TourStep step, WheelEvent e) =>
        e.EntryId is null && !e.Keyboard && step.Wheel() is { } wheel && wheel == e.Kind;
}

public enum FileKind { Other, Image, Video, Audio, Pdf, Markdown }

/// <summary>Just enough file-type knowledge for the tour's completion rules.</summary>
public static class FileKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".avif", ".jxl" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".mpg", ".mpeg", ".flv", ".3gp", ".ts" };
    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".caf" };

    public static FileKind Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audios.Contains(ext)) return FileKind.Audio;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdown", StringComparison.OrdinalIgnoreCase)) return FileKind.Markdown;
        return FileKind.Other;
    }
}
