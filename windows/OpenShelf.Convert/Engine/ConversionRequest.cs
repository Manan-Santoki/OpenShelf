using OpenShelf.Core;

namespace OpenShelf.Convert;

public enum VideoCodec { Automatic, H264, Hevc }
public enum VideoQuality { Automatic, Best, Balanced, Smaller, Custom }
public enum VideoSize { Original, UltraHD4K, FullHD1080, HD720, SD480 }
public enum GifWidth { W480, W720, W1080, Original }

/// <summary>Video output settings (SPEC 2.3.1). Placeholders until the media engine lands: all automatic.</summary>
public sealed record VideoOptions
{
    public VideoCodec Codec { get; init; } = VideoCodec.Automatic;
    public VideoQuality Quality { get; init; } = VideoQuality.Automatic;
    /// <summary>Megabits per second when <see cref="Quality"/> is Custom.</summary>
    public double? CustomMbps { get; init; }
    public VideoSize LargestSize { get; init; } = VideoSize.Original;
    public bool IsAutomatic => Codec == VideoCodec.Automatic && Quality == VideoQuality.Automatic && LargestSize == VideoSize.Original;
}

/// <summary>Audio output settings. Placeholder: automatic.</summary>
public sealed record AudioOptions
{
    /// <summary>96..320 kb/s, or null for each format's usual rate.</summary>
    public int? BitrateKbps { get; init; }
}

/// <summary>GIF made from video. Placeholder: 720 px wide at 12 fps, the Mac's defaults.</summary>
public sealed record GifOptions
{
    public GifWidth Width { get; init; } = GifWidth.W720;
    public int FramesPerSecond { get; init; } = 12;
}

/// <summary>The output-settings values an engine needs (SPEC 2.3.1), independent of the preferences store. The defaults
/// are "Automatic" everywhere, which converts exactly as the Mac does with default settings.</summary>
public sealed record ConversionOptions
{
    /// <summary>0..1, or null for each format's usual: JPEG, HEIC and AVIF 0.9, WebP 88 (0.88), PDF pages 0.92.</summary>
    public double? ImageQuality { get; init; }
    /// <summary>Copy the source's metadata (camera, date, location) into the copy; off strips it, keeping colour profiles.</summary>
    public bool KeepMetadata { get; init; } = true;
    public VideoOptions Video { get; init; } = new();
    public AudioOptions Audio { get; init; } = new();
    public GifOptions Gif { get; init; } = new();

    public static ConversionOptions Automatic { get; } = new();

    /// <summary>The quality to write with, given the format's usual one.</summary>
    public double Quality(double usual) => Math.Clamp(ImageQuality ?? usual, 0, 1);
}

/// <summary>One conversion: <see cref="SourcePath"/> to <see cref="Format"/>.</summary>
/// <param name="SourcePath">The file or folder to convert; for archives made of several items, the first.</param>
public sealed record ConversionRequest(string SourcePath, OutputFormat Format, ConversionOptions Options)
{
    public ConversionRequest(string sourcePath, OutputFormat format) : this(sourcePath, format, ConversionOptions.Automatic) { }

    /// <summary>Every item going into one archive (ZIP, TAR, TAR.GZ); null means just <see cref="SourcePath"/>.</summary>
    public IReadOnlyList<string>? Sources { get; init; }

    public IReadOnlyList<string> AllSources => Sources is { Count: > 0 } ? Sources : [SourcePath];

    /// <summary>The tools in effect. Null means whatever <see cref="ExternalToolsProvider.Default"/> finds; the
    /// <see cref="Converter"/> fills it in so every engine of one conversion sees the same answer.</summary>
    public ExternalTools? Tools { get; init; }

    /// <summary>The folder an engine writes its results into. The <see cref="ConversionRunner"/> gives each job a hidden
    /// staging folder and moves the results out when they are complete. Null (an engine called directly) means the
    /// source's own folder, and names are made unique at once.</summary>
    public string? OutputDirectory { get; init; }

    public ExternalTools ToolsOrDefault => Tools ?? ExternalToolsProvider.Default.Current;

    /// <summary>Where an engine writes a file called <paramref name="fileName"/> ("Photo.jpg", "Archive.tar.gz").
    /// Inside a staging folder the name is used as is; otherwise it is made unique next to the source, so nothing is
    /// ever overwritten.</summary>
    public string OutputPath(string fileName)
    {
        if (OutputDirectory is not null) return Path.Combine(OutputDirectory, fileName);
        var (stem, extension) = OutputFiles.SplitName(fileName);
        return OutputNaming.UniquePath(SourceDirectory, stem, extension);
    }

    /// <summary>Where an engine writes a folder called <paramref name="name"/> (the extension, if any, is part of the name).</summary>
    public string OutputFolderPath(string name) =>
        OutputDirectory is not null ? Path.Combine(OutputDirectory, name) : OutputNaming.UniquePath(SourceDirectory, name, null);

    /// <summary>The folder results end up in: the first source's.</summary>
    public string SourceDirectory => Path.GetDirectoryName(Path.GetFullPath(SourcePath)) ?? ".";

    /// <summary>The source's name without its extension ("Photo" for "Photo.png").</summary>
    public string SourceStem
    {
        get
        {
            var name = Path.GetFileName(SourcePath.TrimEnd('\\', '/'));
            var stem = Directory.Exists(SourcePath) && !FileKinds.ByExtension.ContainsKey(FileKinds.ExtensionOf(SourcePath)) ? name : Path.GetFileNameWithoutExtension(name);
            return stem.Length == 0 ? name : stem;
        }
    }
}

/// <summary>File-name helpers shared by engines and the runner.</summary>
public static class OutputFiles
{
    /// <summary>Splits "Archive.tar.gz" into ("Archive", "tar.gz") and "Photo.jpg" into ("Photo", "jpg"); no extension gives null.</summary>
    public static (string Stem, string? Extension) SplitName(string fileName)
    {
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) && fileName.Length > ".tar.gz".Length)
            return (fileName[..^".tar.gz".Length], "tar.gz");
        var extension = Path.GetExtension(fileName);
        if (extension.Length <= 1 || extension.Length == fileName.Length) return (fileName, null);
        return (fileName[..^extension.Length], extension[1..]);
    }
}
