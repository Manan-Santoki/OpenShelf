namespace OpenShelf.Convert.Documents;

/// <summary>Books through calibre's <c>ebook-convert</c> (https://calibre-ebook.com): EPUB, MOBI, AZW3 and AZW to each other and to PDF;
/// PDFs to MOBI and AZW3. Port of EbookConversion.swift.</summary>
public sealed class CalibreEngine : IConversionEngine
{
    /// <summary>calibre crashes now and then on Chromium's main thread while rendering a PDF; the same book converts on the next try.
    /// A crash, never an ordinary failure, gets one more go.</summary>
    public const int AttemptsAfterCrash = 2;

    /// <summary>calibre makes PDFs with Qt WebEngine (Chromium); a PDF needs no GPU.</summary>
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string> { ["QTWEBENGINE_CHROMIUM_FLAGS"] = "--disable-gpu" };

    private readonly ToolRunner _runner;

    public CalibreEngine(ToolRunner? runner = null) => _runner = runner ?? ToolRunners.Default;

    public bool CanConvert(FileKind kind, OutputFormat format) => kind switch
    {
        FileKind.Ebook => ConversionCatalog.CalibreFormats.Contains(format),
        FileKind.Pdf => ConversionCatalog.CalibreFormatsForPdf.Contains(format),
        _ => false,
    };

    public bool CanConvert(ConversionRequest request)
    {
        if (request.ToolsOrDefault.EbookConvert is null) return false;
        var ext = FileKinds.ExtensionOf(FileKinds.ResolveLink(request.SourcePath));
        if (request.Format.Matches(ext)) return false;
        return FileKinds.Of(request.SourcePath) switch
        {
            FileKind.Ebook => FileKinds.BookExtensions.Contains(ext) && ConversionCatalog.CalibreFormats.Contains(request.Format),
            FileKind.Pdf => ConversionCatalog.CalibreFormatsForPdf.Contains(request.Format),
            _ => false,
        };
    }

    /// <summary>ebook-convert prints "34% Running transforms on e-book..." as it goes.</summary>
    public static double? FractionFromProgressLine(string line)
    {
        var digits = new string(line.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0 || digits.Length >= line.Length || line[digits.Length] != '%') return null;
        return int.TryParse(digits, out var percent) ? Math.Min(percent / 100.0, 1) : null;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ebookConvert = request.ToolsOrDefault.EbookConvert ?? throw new MissingToolException("calibre");
        var source = Path.GetFullPath(FileKinds.ResolveLink(request.SourcePath));
        var extension = request.Format.FileExtension() ?? throw ConversionException.Unsupported(request.Format.Title());
        // calibre writes temporary files beside its output, so it works in a folder of its own until the book is complete.
        var workspace = Path.Combine(Path.GetTempPath(), "openshelf-ebook-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workspace);
        var output = Path.Combine(workspace, "converted." + extension);
        var destination = request.OutputPath($"{request.SourceStem}.{extension}");
        try
        {
            var invocation = new ToolInvocation(ebookConvert, [source, output]) { WorkingDirectory = workspace, ExtraEnvironment = Environment };
            for (var attempt = 1; attempt <= AttemptsAfterCrash; attempt++)
            {
                try
                {
                    await _runner(invocation, line =>
                    {
                        if (FractionFromProgressLine(line) is { } fraction) progress?.Report(fraction);
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (ProcessFailure failure) when (failure.Crashed && attempt < AttemptsAfterCrash)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Zips.DeleteQuietly(output);
                }
                catch (ProcessFailure failure) when (failure.TimedOut)
                {
                    throw ConversionException.Failed($"calibre took too long to convert “{Path.GetFileName(source)}”.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(output))
                throw ConversionException.Failed($"calibre couldn’t convert “{Path.GetFileName(source)}”.");
            File.Move(output, destination);
            progress?.Report(1);
            return [destination];
        }
        catch
        {
            Zips.DeleteQuietly(destination);
            throw;
        }
        finally
        {
            Zips.DeleteFolderQuietly(workspace);
        }
    }
}

/// <summary>Registers the document, PDF, office, iWork and e-book engines.</summary>
public static class DocumentEngines
{
    /// <summary>Adds the engines to <paramref name="registry"/>. Tool engines (iWork/LibreOffice, calibre, Pandoc) are asked ahead of the
    /// built-in ones and fall back to them when the tool fails or is missing.</summary>
    public static void Register(EngineRegistry registry)
    {
        Register(registry, null, null);
    }

    /// <summary>For tests: engines that run their tools through <paramref name="runner"/> and print through <paramref name="edge"/>.</summary>
    public static void Register(EngineRegistry registry, ToolRunner? runner, EdgePrinter? edge)
    {
        var documents = new DocumentEngine(edge);
        registry.Register(new IWorkEngine(runner), 100);
        registry.Register(new CalibreEngine(runner), 90);
        registry.Register(new LibreOfficeEngine(runner, documents), 80);
        registry.Register(new PandocEngine(runner, documents), 70);
        registry.Register(new PdfEngine(), 0);
        registry.Register(new TablePdfEngine(edge), 0);
        registry.Register(documents, 0);
    }
}
