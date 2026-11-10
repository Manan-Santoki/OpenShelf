namespace OpenShelf.Convert;

/// <summary>Optional tools found on this PC: ffmpeg and ffprobe, cwebp, LibreOffice, Pandoc with a program it can make PDFs
/// with, calibre's ebook-convert, and whisper.cpp. Paths are full paths to the executables, null when not found.
/// Port of ExternalTools.swift.</summary>
public sealed record ExternalTools
{
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
    public string? Cwebp { get; init; }
    /// <summary>LibreOffice's soffice.exe.</summary>
    public string? LibreOffice { get; init; }
    public string? Pandoc { get; init; }
    /// <summary>A typesetter Pandoc can make PDFs with (see <see cref="ToolNames.PdfEngines"/>).</summary>
    public string? PdfEngine { get; init; }
    /// <summary>calibre's book converter.</summary>
    public string? EbookConvert { get; init; }
    /// <summary>whisper.cpp's command-line program, for transcripts and subtitles.</summary>
    public string? Whisper { get; init; }
    public string? WhisperModel { get; init; }
    /// <summary>Always empty on Windows (Pages, Numbers and Keynote do not exist); kept so the catalog ports one to one.</summary>
    public IReadOnlyCollection<IWorkFormat> IWorkApps { get; init; } = [];

    /// <summary>WebP is written in-process by the bundled ImageMagick, so it is normally always on the wheel.
    /// Tests (and a future switch) can turn it off.</summary>
    public bool CanEncodeWebP { get; init; } = true;

    /// <summary>HEIC and AVIF are written by the same bundled library when its build includes the encoders;
    /// <see cref="Detect"/> asks it, and the wheel hides the format when it cannot.</summary>
    public bool CanEncodeHeic { get; init; } = true;
    public bool CanEncodeAvif { get; init; } = true;

    /// <summary>whisper.cpp does nothing without a model, so both are needed.</summary>
    public bool CanTranscribe => Whisper is not null && WhisperModel is not null;

    /// <summary>Nothing installed.</summary>
    public static ExternalTools None { get; } = new();

    /// <summary>Looks for every tool in <paramref name="locations"/>: OpenShelf's own copies, then PATH, then the usual
    /// install places. Never runs a tool.</summary>
    public static ExternalTools Detect(ToolLocations locations)
    {
        var ignored = locations.IgnoredTools;
        string? Find(string name, string ignoredAs) => locations.FindCommand(name, ownOnly: ignored.Contains(ignoredAs));

        var pandoc = Find("pandoc", "pandoc");
        string? pdfEngine = null;
        if (pandoc is not null)
        {
            foreach (var engine in ToolNames.PdfEngines)
            {
                pdfEngine = locations.FindCommand(engine, ownOnly: ignored.Contains("pandoc"), extraDirectories: locations.TexDirectories);
                if (pdfEngine is not null) break;
            }
        }
        var whisper = Find("whisper-cli", "whisper") ?? Find("whisper-cpp", "whisper");
        return new ExternalTools
        {
            Ffmpeg = Find("ffmpeg", "ffmpeg"),
            Ffprobe = Find("ffprobe", "ffmpeg"),
            Cwebp = Find("cwebp", "cwebp"),
            CanEncodeWebP = ImageEngine.CanWrite(OutputFormat.Webp),
            CanEncodeHeic = ImageEngine.CanWrite(OutputFormat.Heic),
            CanEncodeAvif = ImageEngine.CanWrite(OutputFormat.Avif),
            LibreOffice = locations.FindLibreOffice(ownOnly: ignored.Contains("libreoffice")),
            Pandoc = pandoc,
            PdfEngine = pdfEngine,
            EbookConvert = locations.FindCalibre(ownOnly: ignored.Contains("calibre")),
            Whisper = whisper,
            WhisperModel = whisper is null ? null : locations.FindWhisperModel(),
        };
    }
}

/// <summary>Names of the programs OpenShelf looks for.</summary>
public static class ToolNames
{
    /// <summary>Typesetters Pandoc makes PDFs with, in order of preference.</summary>
    public static readonly IReadOnlyList<string> PdfEngines = ["xelatex", "lualatex", "tectonic", "typst", "pdflatex", "weasyprint", "wkhtmltopdf"];
}

/// <summary>Where tools are looked for, in order of preference: OpenShelf's own copies under
/// <c>%LOCALAPPDATA%\OpenShelf\tools\&lt;tool&gt;\</c>, then PATH, then the usual install locations.
/// Everything that touches the machine is injectable, so tests can fake a PC.</summary>
public sealed class ToolLocations
{
    /// <summary>%LOCALAPPDATA%\OpenShelf\tools: one folder per tool inside ("ffmpeg", "cwebp", "pandoc", "libreoffice", "calibre").</summary>
    public string OwnToolsRoot { get; init; } = "";
    /// <summary>The directories on PATH, in order.</summary>
    public IReadOnlyList<string> PathDirectories { get; init; } = [];
    /// <summary>Command folders checked after PATH (scoop shims, winget links, Chocolatey, %LOCALAPPDATA%\Pandoc, ...).</summary>
    public IReadOnlyList<string> CommandDirectories { get; init; } = [];
    /// <summary>Full paths of soffice.exe in the usual places, best first.</summary>
    public IReadOnlyList<string> LibreOfficeCandidates { get; init; } = [];
    /// <summary>Full paths of ebook-convert.exe in the usual places, best first.</summary>
    public IReadOnlyList<string> CalibreCandidates { get; init; } = [];
    /// <summary>MiKTeX and TeX Live bin folders, for Pandoc's PDFs.</summary>
    public IReadOnlyList<string> TexDirectories { get; init; } = [];
    /// <summary>Where whisper.cpp's speech models live.</summary>
    public string? ModelsDirectory { get; init; }
    /// <summary>Tools whose PATH and installed copies are ignored (only OpenShelf's own copies count), to try OpenShelf as
    /// someone without them sees it: "ffmpeg" (covers ffprobe), "cwebp", "pandoc", "libreoffice", "calibre", "whisper".
    /// From the OPENSHELF_IGNORED_TOOLS environment variable, comma separated.</summary>
    public IReadOnlySet<string> IgnoredTools { get; init; } = new HashSet<string>();
    /// <summary>Whether a file exists. Fake it in tests.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>The real machine.</summary>
    public static ToolLocations Standard => ForEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Locations derived from environment variables read through <paramref name="getEnvironmentVariable"/>.</summary>
    public static ToolLocations ForEnvironment(Func<string, string?> getEnvironmentVariable, Func<string, bool>? fileExists = null)
    {
        string Var(string name) => getEnvironmentVariable(name) ?? "";
        var localAppData = Var("LOCALAPPDATA");
        var userProfile = Var("USERPROFILE");
        var programFiles = Var("ProgramFiles");
        var programFilesX86 = Var("ProgramFiles(x86)");
        var programData = Var("ProgramData");
        var scoop = Var("SCOOP") is { Length: > 0 } custom ? custom : Path.Combine(userProfile, "scoop");

        static IReadOnlyList<string> Existing(params string[] paths) => paths.Where(p => p.Length > 0).ToList();
        string Under(string root, params string[] parts) => root.Length == 0 ? "" : Path.Combine([root, .. parts]);

        var ignored = (getEnvironmentVariable("OPENSHELF_IGNORED_TOOLS") ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant()).ToHashSet();

        return new ToolLocations
        {
            OwnToolsRoot = Under(localAppData, "OpenShelf", "tools"),
            PathDirectories = Var("PATH").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Trim('"')).ToList(),
            CommandDirectories = Existing(
                Under(scoop, "shims"),
                Under(localAppData, "Microsoft", "WinGet", "Links"),
                Under(programData, "chocolatey", "bin"),
                Under(localAppData, "Pandoc"),
                Under(programFiles, "Pandoc"),
                Under(programFiles, "ffmpeg", "bin"),
                Under(scoop, "apps", "ffmpeg", "current", "bin"),
                Under(userProfile, "ffmpeg", "bin"),
                @"C:\ffmpeg\bin",
                Under(programFiles, "wkhtmltopdf", "bin"),
                Under(programFiles, "Typst"),
                Under(localAppData, "Programs", "Typst")),
            LibreOfficeCandidates = Existing(
                Under(programFiles, "LibreOffice", "program", "soffice.exe"),
                Under(programFilesX86, "LibreOffice", "program", "soffice.exe"),
                Under(localAppData, "Programs", "LibreOffice", "program", "soffice.exe"),
                Under(scoop, "apps", "libreoffice", "current", "LibreOffice", "program", "soffice.exe"),
                Under(scoop, "apps", "libreoffice", "current", "program", "soffice.exe")),
            CalibreCandidates = Existing(
                Under(programFiles, "Calibre2", "ebook-convert.exe"),
                Under(programFilesX86, "Calibre2", "ebook-convert.exe"),
                Under(localAppData, "Programs", "Calibre2", "ebook-convert.exe"),
                Under(scoop, "apps", "calibre", "current", "ebook-convert.exe")),
            TexDirectories = Existing(
                Under(programFiles, "MiKTeX", "miktex", "bin", "x64"),
                Under(localAppData, "Programs", "MiKTeX", "miktex", "bin", "x64"),
                @"C:\texlive\2025\bin\windows", @"C:\texlive\2024\bin\windows"),
            ModelsDirectory = localAppData.Length == 0 ? null : Under(localAppData, "OpenShelf", "models"),
            IgnoredTools = ignored,
            FileExists = fileExists ?? File.Exists,
        };
    }

    private IEnumerable<string> OwnDirectories(string tool)
    {
        if (OwnToolsRoot.Length == 0) yield break;
        var root = Path.Combine(OwnToolsRoot, tool);
        yield return root;
        yield return Path.Combine(root, "bin");
    }

    /// <summary>Finds <paramref name="name"/>.exe: OpenShelf's own copy, then PATH, then the usual places.</summary>
    /// <param name="ownOnly">Only OpenShelf's own copy counts (see <see cref="IgnoredTools"/>).</param>
    public string? FindCommand(string name, bool ownOnly = false, IEnumerable<string>? extraDirectories = null)
    {
        var file = name + ".exe";
        // The folder OpenShelf downloads a tool into is named for the tool; ffprobe lives with ffmpeg.
        var folder = name == "ffprobe" ? "ffmpeg" : name;
        var directories = OwnDirectories(folder).ToList();
        if (!ownOnly) directories.AddRange(PathDirectories.Concat(CommandDirectories).Concat(extraDirectories ?? []));
        foreach (var directory in directories)
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, file);
            if (FileExists(candidate)) return candidate;
        }
        return null;
    }

    public string? FindLibreOffice(bool ownOnly = false)
    {
        var own = new[] { "program", Path.Combine("App", "libreoffice", "program") }
            .Select(sub => Path.Combine(OwnToolsRoot, "libreoffice", sub, "soffice.exe"));
        foreach (var candidate in own.Where(_ => OwnToolsRoot.Length > 0))
            if (FileExists(candidate)) return candidate;
        if (ownOnly) return null;
        return FindCommand("soffice") ?? LibreOfficeCandidates.FirstOrDefault(FileExists);
    }

    public string? FindCalibre(bool ownOnly = false)
    {
        if (OwnToolsRoot.Length > 0)
        {
            var own = Path.Combine(OwnToolsRoot, "calibre", "ebook-convert.exe");
            if (FileExists(own)) return own;
        }
        if (ownOnly) return null;
        return FindCommand("ebook-convert") ?? CalibreCandidates.FirstOrDefault(FileExists);
    }

    /// <summary>The most capable speech model in <see cref="ModelsDirectory"/> (ggml-*.bin), skipping tiny test models.</summary>
    public string? FindWhisperModel()
    {
        if (ModelsDirectory is null || !Directory.Exists(ModelsDirectory)) return null;
        var models = Directory.EnumerateFiles(ModelsDirectory, "ggml-*.bin").ToList();
        foreach (var preferred in new[] { "large", "medium", "small", "base" })
        {
            var match = models.FirstOrDefault(m => Path.GetFileName(m).Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }
}

/// <summary>Detects tools and caches the answer until it is a few seconds old or <see cref="Refresh"/> is called.
/// Detection is a handful of file checks and never runs a tool, so repeating it is cheap: an install shows up on the
/// next wheel, while a drag and the conversion it starts see the same answer.
/// Tests build a provider around a fake: <c>new ExternalToolsProvider(() =&gt; new ExternalTools { Ffmpeg = "x" })</c>.</summary>
public sealed class ExternalToolsProvider
{
    private readonly Func<ExternalTools> _detect;
    private readonly TimeSpan _maxAge;
    private readonly object _lock = new();
    private ExternalTools? _cached;
    private long _detectedAt;

    public ExternalToolsProvider(Func<ExternalTools>? detect = null, TimeSpan? maxAge = null)
    {
        _detect = detect ?? (() => ExternalTools.Detect(ToolLocations.Standard));
        _maxAge = maxAge ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The app-wide provider. Replace it (once, at startup) to fake tools for everything built on <see cref="Converter.Default"/>.</summary>
    public static ExternalToolsProvider Default { get; set; } = new();

    public ExternalTools Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is not null && Environment.TickCount64 - _detectedAt < _maxAge.TotalMilliseconds) return _cached;
                return Detect();
            }
        }
    }

    /// <summary>Detects again now: when Settings opens, after an install or removal, or when a tool turns out to be gone.</summary>
    public ExternalTools Refresh()
    {
        lock (_lock) return Detect();
    }

    private ExternalTools Detect()
    {
        _cached = _detect();
        _detectedAt = Environment.TickCount64;
        return _cached;
    }
}
