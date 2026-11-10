namespace OpenShelf.Convert.Documents;

/// <summary>Converts documents with Pandoc (https://pandoc.org) when it is installed: DOCX, ODT, HTML and RTF to MD, EPUB and ODT, and EPUB books
/// to DOCX, MD, HTML, ODT, TXT and (with a typesetter) PDF. 180-second limit. If Pandoc fails, the built-in conversion runs for documents.
/// Port of PandocConversion.swift. Markdown sources belong to the Markdown engine.</summary>
public sealed class PandocEngine : IConversionEngine
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    private readonly ToolRunner _runner;
    private readonly DocumentEngine _fallback;

    public PandocEngine(ToolRunner? runner = null, DocumentEngine? fallback = null)
    {
        _runner = runner ?? ToolRunners.Default;
        _fallback = fallback ?? new DocumentEngine();
    }

    private static readonly string[] DocumentOutputs = ["md", "epub", "odt"];

    public static string? Writer(OutputFormat format) => format switch
    {
        OutputFormat.Docx => "docx",
        OutputFormat.Html => "html5",
        OutputFormat.Epub => "epub3",
        OutputFormat.Md => "gfm",
        OutputFormat.Rtf => "rtf",
        OutputFormat.Odt => "odt",
        OutputFormat.Txt => "plain",
        // Pandoc picks the writer that suits the typesetter.
        OutputFormat.Pdf => "",
        _ => null,
    };

    /// <summary>The Pandoc reader for a source, or null. Markdown is not Pandoc's job here.</summary>
    public static string? Reader(string extension) =>
        ConversionCatalog.PandocReaders.TryGetValue(extension, out var reader) && reader != "markdown" ? reader : null;

    public bool CanConvert(FileKind kind, OutputFormat format) =>
        kind switch
        {
            FileKind.Document => DocumentOutputs.Contains(format.Id()),
            FileKind.Ebook => format is OutputFormat.Docx or OutputFormat.Md or OutputFormat.Html or OutputFormat.Odt or OutputFormat.Txt or OutputFormat.Pdf,
            _ => false,
        };

    public bool CanConvert(ConversionRequest request)
    {
        var tools = request.ToolsOrDefault;
        if (tools.Pandoc is null) return false;
        var ext = FileKinds.ExtensionOf(FileKinds.ResolveLink(request.SourcePath));
        if (Reader(ext) is null || request.Format.Matches(ext)) return false;
        if (request.Format == OutputFormat.Pdf && tools.PdfEngine is null) return false;
        return CanConvert(FileKinds.Of(request.SourcePath), request.Format);
    }

    /// <summary>The command line for one conversion, or null when Pandoc cannot do it.</summary>
    public static List<string>? Arguments(string source, OutputFormat format, string output, string? pdfEngine)
    {
        var reader = Reader(FileKinds.ExtensionOf(source));
        var writer = Writer(format);
        if (reader is null || writer is null) return null;
        var arguments = new List<string> { source, "--from", reader };
        // A book's chapters come wrapped in HTML that Markdown would carry along as raw <span>s and <div>s; plain Markdown reads better.
        var bookToMarkdown = reader == "epub" && format == OutputFormat.Md;
        if (writer.Length > 0) { arguments.Add("--to"); arguments.Add(bookToMarkdown ? writer + "-raw_html" : writer); }
        // Pictures next to the document are found wherever Pandoc runs.
        arguments.AddRange(["--output", output, "--resource-path", Path.GetDirectoryName(Path.GetFullPath(source)) ?? "."]);
        // A whole document rather than a fragment; Markdown and text have no such thing.
        if (format is not (OutputFormat.Md or OutputFormat.Txt)) arguments.Add("--standalone");
        // A book's pictures are inside it; a web page needs them inside too.
        if (format == OutputFormat.Html && reader == "epub") arguments.Add("--embed-resources");
        if (format is OutputFormat.Html or OutputFormat.Epub)
            arguments.AddRange(["--metadata", $"pagetitle={Path.GetFileNameWithoutExtension(source)}"]);
        switch (format)
        {
            case OutputFormat.Pdf:
                if (pdfEngine is null) return null;
                arguments.AddRange(["--pdf-engine", pdfEngine]);
                var name = Path.GetFileNameWithoutExtension(pdfEngine);
                if (name.EndsWith("latex", StringComparison.OrdinalIgnoreCase) || name.Equals("tectonic", StringComparison.OrdinalIgnoreCase))
                    // LaTeX's default margins are very wide; links are coloured instead of boxed.
                    arguments.AddRange(["--variable", "geometry:margin=1in", "--variable", "colorlinks=true"]);
                break;
            case OutputFormat.Md or OutputFormat.Txt:
                arguments.AddRange(["--wrap", "none"]);
                break;
        }
        return arguments;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tools = request.ToolsOrDefault;
        var pandoc = tools.Pandoc ?? throw new MissingToolException("Pandoc");
        var source = FileKinds.ResolveLink(request.SourcePath);
        var destination = request.OutputPath($"{request.SourceStem}.{request.Format.FileExtension()}");
        var arguments = Arguments(source, request.Format, destination, tools.PdfEngine)
            ?? throw ConversionException.Unsupported($"{FileKinds.ExtensionOf(source).ToUpperInvariant()} to {request.Format.Title()}");
        // A typesetter starts its own helpers, which it finds next to itself.
        Dictionary<string, string>? environment = null;
        if (request.Format == OutputFormat.Pdf && tools.PdfEngine is { } engine && Path.GetDirectoryName(engine) is { } folder)
            environment = new() { ["PATH"] = folder + ";" + ChildEnvironment.SearchPath() };
        try
        {
            await _runner(new ToolInvocation(pandoc, arguments)
            {
                Timeout = Timeout,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(destination)),
                ExtraEnvironment = environment,
            }, null, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                throw ConversionException.Failed($"Pandoc couldn’t convert “{Path.GetFileName(source)}”.");
            progress?.Report(1);
            return [destination];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Zips.DeleteQuietly(destination);
            // Pandoc failed; the built-in conversion may still do a document.
            if (FileKinds.Of(source) == FileKind.Document && _fallback.CanConvert(request))
                return await _fallback.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);
            if (error is ProcessFailure { TimedOut: true })
                throw ConversionException.Failed($"Pandoc took too long to convert “{Path.GetFileName(source)}”.");
            throw;
        }
    }
}
