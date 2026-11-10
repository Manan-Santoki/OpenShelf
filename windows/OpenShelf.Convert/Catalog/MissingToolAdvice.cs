namespace OpenShelf.Convert;

/// <summary>A free tool that adds formats OpenShelf cannot make itself, as presented in Settings and in messages that point there.</summary>
public enum ExtraTool { Ffmpeg, LibreOffice, Pandoc }

public static class ExtraTools
{
    public static IReadOnlyList<ExtraTool> All { get; } = [ExtraTool.Ffmpeg, ExtraTool.LibreOffice, ExtraTool.Pandoc];

    public static string Name(this ExtraTool tool) => tool switch
    {
        ExtraTool.Ffmpeg => "ffmpeg",
        ExtraTool.LibreOffice => "LibreOffice",
        _ => "Pandoc",
    };

    public static string? ExecutablePath(this ExtraTool tool, ExternalTools tools) => tool switch
    {
        ExtraTool.Ffmpeg => tools.Ffmpeg,
        ExtraTool.LibreOffice => tools.LibreOffice,
        _ => tools.Pandoc,
    };

    public static bool IsInstalled(this ExtraTool tool, ExternalTools tools) => tool.ExecutablePath(tools) is not null;

    /// <summary>The tools as if this one were installed, to ask the catalog what it would add. The placeholder is never run.</summary>
    public static ExternalTools AssumedInstalled(this ExtraTool tool, ExternalTools tools) => tool switch
    {
        ExtraTool.Ffmpeg => tools with { Ffmpeg = @"C:\nonexistent\ffmpeg.exe" },
        ExtraTool.LibreOffice => tools with { LibreOffice = @"C:\nonexistent\soffice.exe" },
        _ => tools with { Pandoc = @"C:\nonexistent\pandoc.exe" },
    };
}

/// <summary>What to say when a missing tool is the reason a wheel has little to offer. Pure decisions, so they can be tested
/// without a drag. Port of MissingToolAdvice in ExtraFormats.swift.</summary>
public static class MissingToolAdvice
{
    /// <summary>The second line of every "needs ..." message.</summary>
    public const string InstallHint = "Install it in Settings → Extra Formats";

    /// <summary>The tool whose absence leaves these files with nothing but ZIP on the Convert wheel, when installing it would
    /// offer real conversions. Null for folders, unknown files and mixes that share no format anyway: their ZIP wheel is all there is.</summary>
    public static ExtraTool? BlockingTool(IReadOnlyList<string> paths, IReadOnlyList<OutputFormat> formats, ExternalTools tools)
    {
        if (paths.Count == 0 || formats.Count != 1 || formats[0] != OutputFormat.Zip) return null;
        foreach (var tool in ExtraTools.All)
        {
            if (tool.IsInstalled(tools)) continue;
            if (ConversionCatalog.Formats(paths, tool.AssumedInstalled(tools)).Any(format => format != OutputFormat.Zip)) return tool;
        }
        return null;
    }

    /// <summary>"MKV needs ffmpeg", shown instead of a ZIP-only wheel. Names only the files that need the tool (not the MP4
    /// dragged along with an MKV). One line, like every toast; the hint is <see cref="InstallHint"/>.</summary>
    public static string Message(IReadOnlyList<string> paths, ExtraTool needing, ExternalTools tools)
    {
        var blocked = paths.Where(path => ConversionCatalog.Formats(path, tools).SequenceEqual([OutputFormat.Zip])).ToList();
        var names = UniqueNames(blocked.Count == 0 ? paths : blocked);
        var name = needing.Name();
        var title = $"These files need {name}";
        if (!names.Contains(""))
        {
            if (names.Count == 1) title = $"{names[0]} needs {name}";
            else if (names.Count == 2) title = $"{names[0]} and {names[1]} need {name}";
        }
        return title.Length > 28 ? $"These files need {name}" : title;
    }

    /// <summary>The message a wheel shows instead of an empty one, or null when there is nothing to say.</summary>
    public static string? EmptyMessage(IReadOnlyList<string> paths, IReadOnlyList<OutputFormat> formats, ExternalTools tools) =>
        BlockingTool(paths, formats, tools) is { } tool ? Message(paths, tool, tools) : null;

    /// <summary>How a format is known to people: "PowerPoint", "WebM", "MKV"; empty for a file without an extension.</summary>
    public static string DisplayName(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "ppt" or "pptx" => "PowerPoint",
        "key" => "Keynote",
        "pages" => "Pages",
        "numbers" => "Numbers",
        "webm" => "WebM",
        "opus" => "Opus",
        "oga" => "OGG",
        var other => other.ToUpperInvariant(),
    };

    private static List<string> UniqueNames(IEnumerable<string> paths)
    {
        var names = new List<string>();
        foreach (var path in paths)
        {
            var name = DisplayName(FileKinds.ExtensionOf(FileKinds.ResolveLink(path)));
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }
}
