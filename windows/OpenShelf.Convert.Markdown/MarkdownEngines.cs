namespace OpenShelf.Convert.Markdown;

/// <summary>Converts Markdown files with the built-in parser and renderers: PDF (through Edge), DOCX, TXT, EPUB, HTML and RTF.
/// The fallback behind <see cref="PandocMarkdownEngine"/>.</summary>
public sealed class MarkdownEngine(HtmlPdfPrinter? printer = null) : IConversionEngine
{
    /// <summary>Kind and format alone cannot tell Markdown from other documents; the request's extension does.</summary>
    public bool CanConvert(FileKind kind, OutputFormat format) => false;

    public bool CanConvert(ConversionRequest request) =>
        MarkdownConversion.IsMarkdown(request.SourcePath) && MarkdownConversion.Outputs.Contains(request.Format);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (request.Format.FileExtension() is not { } extension)
            throw ConversionException.Unsupported($"Markdown to {request.Format.Title()}");
        var destination = request.OutputPath($"{request.SourceStem}.{extension}");
        progress?.Report(0.05);
        try
        {
            await Task.Run(() => MarkdownConversion.ConvertAsync(request.SourcePath, request.Format, destination, printer, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        progress?.Report(1);
        return [destination];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Converts Markdown with Pandoc when it is installed: DOCX, HTML, EPUB and RTF, and PDF when Pandoc also has a
/// typesetter. Pandoc gets 180 seconds; if it fails or runs out of time the built-in conversion runs instead (SPEC 2.3
/// footnote §). Registered ahead of <see cref="MarkdownEngine"/>.</summary>
public sealed class PandocMarkdownEngine : IConversionEngine
{
    /// <summary>How long Pandoc gets. A LaTeX PDF can take a while the first time fonts are set up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    public static readonly IReadOnlyList<OutputFormat> Outputs =
        [OutputFormat.Docx, OutputFormat.Html, OutputFormat.Epub, OutputFormat.Rtf, OutputFormat.Pdf];

    private readonly ToolRunner _runner;
    private readonly MarkdownEngine _fallback;

    public PandocMarkdownEngine(ToolRunner? runner = null, MarkdownEngine? fallback = null)
    {
        _runner = runner ?? ToolRunners.Default;
        _fallback = fallback ?? new MarkdownEngine();
    }

    public bool CanConvert(FileKind kind, OutputFormat format) => false;

    public bool CanConvert(ConversionRequest request)
    {
        var tools = request.ToolsOrDefault;
        // Pandoc makes PDFs only through a typesetter.
        return tools.Pandoc is not null
            && MarkdownConversion.IsMarkdown(request.SourcePath)
            && Outputs.Contains(request.Format)
            && (request.Format != OutputFormat.Pdf || tools.PdfEngine is not null);
    }

    /// <summary>Pandoc's writer for a format; empty for PDF (Pandoc picks the writer that suits the typesetter).</summary>
    public static string? Writer(OutputFormat format) => format switch
    {
        OutputFormat.Docx => "docx",
        OutputFormat.Html => "html5",
        OutputFormat.Epub => "epub3",
        OutputFormat.Rtf => "rtf",
        OutputFormat.Odt => "odt",
        OutputFormat.Md => "gfm",
        OutputFormat.Txt => "plain",
        OutputFormat.Pdf => "",
        _ => null,
    };

    /// <summary>The command line for one conversion, or null when Pandoc cannot do it.</summary>
    public static List<string>? Arguments(string source, OutputFormat format, string output, string? pdfEngine)
    {
        if (Writer(format) is not { } writer) return null;
        var arguments = new List<string> { Path.GetFullPath(source), "--from", "markdown" };
        if (writer.Length > 0) arguments.AddRange(["--to", writer]);
        // Pictures next to the document are found wherever Pandoc runs.
        arguments.AddRange(["--output", output, "--resource-path", Path.GetDirectoryName(Path.GetFullPath(source)) ?? "."]);
        // A whole document rather than a fragment.
        if (format is not (OutputFormat.Md or OutputFormat.Txt)) arguments.Add("--standalone");
        if (format is OutputFormat.Html or OutputFormat.Epub)
            // The page's title, without adding a heading to it.
            arguments.AddRange(["--metadata", $"pagetitle={Path.GetFileNameWithoutExtension(source)}"]);
        if (format == OutputFormat.Pdf)
        {
            if (pdfEngine is null) return null;
            arguments.AddRange(["--pdf-engine", pdfEngine]);
            var engine = Path.GetFileNameWithoutExtension(pdfEngine);
            if (engine.EndsWith("latex", StringComparison.OrdinalIgnoreCase) || engine.Equals("tectonic", StringComparison.OrdinalIgnoreCase))
                // LaTeX's default margins are very wide; links are coloured instead of boxed.
                arguments.AddRange(["--variable", "geometry:margin=1in", "--variable", "colorlinks=true"]);
        }
        return arguments;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tools = request.ToolsOrDefault;
        var extension = request.Format.FileExtension();
        if (tools.Pandoc is not { } pandoc || extension is null) return await _fallback.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);

        var destination = request.OutputPath($"{request.SourceStem}.{extension}");
        var arguments = Arguments(request.SourcePath, request.Format, destination, tools.PdfEngine);
        if (arguments is null) return await _fallback.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(0.05);
        try
        {
            // A typesetter starts its own helpers, which it finds next to itself.
            Dictionary<string, string>? environment = null;
            if (request.Format == OutputFormat.Pdf && Path.GetDirectoryName(tools.PdfEngine) is { Length: > 0 } engineFolder)
                environment = new Dictionary<string, string> { ["PATH"] = engineFolder + ";" + ChildEnvironment.SearchPath() };
            var invocation = new ToolInvocation(pandoc, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(destination),
                ExtraEnvironment = environment,
                Timeout = Timeout,
            };
            await _runner(invocation, null, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                throw ConversionException.Failed($"Pandoc couldn’t convert “{Path.GetFileName(request.SourcePath)}”.");
            progress?.Report(1);
            return [destination];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // If Pandoc fails, the built-in conversion runs instead.
            TryDelete(destination);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await _fallback.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Registers the Markdown engines. The app's composition root calls <see cref="Register"/> once at start-up.</summary>
public static class MarkdownEngines
{
    /// <summary>Pandoc is asked first (when found), then the built-in conversion.</summary>
    public const int PandocPriority = 20;
    public const int BuiltInPriority = 0;

    public static void Register(EngineRegistry registry)
    {
        var builtIn = new MarkdownEngine();
        registry.Register(new PandocMarkdownEngine(fallback: builtIn), PandocPriority);
        registry.Register(builtIn, BuiltInPriority);
    }
}
