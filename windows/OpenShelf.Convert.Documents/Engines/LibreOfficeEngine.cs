namespace OpenShelf.Convert.Documents;

/// <summary>Runs LibreOffice headless (soffice) with a private profile and a 120-second limit. Port of OfficeConversion.swift.</summary>
public sealed class OfficeConversion(ToolRunner? runner = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly ToolRunner _runner = runner ?? ToolRunners.Default;

    /// <summary>LibreOffice's <c>--convert-to</c> value for a format.</summary>
    public static string? Filter(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => "pdf",
        OutputFormat.Docx => "docx",
        OutputFormat.Xlsx => "xlsx",
        OutputFormat.Ods => "ods",
        OutputFormat.Pptx => "pptx",
        OutputFormat.Rtf => "rtf",
        OutputFormat.Txt => "txt:Text (encoded):UTF8",
        // Comma-separated, double quotes, UTF-8 (76), first sheet.
        OutputFormat.Csv => "csv:Text - txt - csv (StarCalc):44,34,76,1",
        _ => null,
    };

    /// <summary>The command line for one conversion. A private profile keeps it from clashing with a LibreOffice already open.</summary>
    public static List<string> Arguments(string source, string filter, string profile, string output) =>
    [
        $"-env:UserInstallation={new Uri(Path.GetFullPath(profile) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')}",
        "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
        "--convert-to", filter,
        "--outdir", output,
        Path.GetFullPath(source),
    ];

    /// <param name="pageLabel">How numbered images are labelled ("Slide 2", "Page 2").</param>
    public async Task<IReadOnlyList<string>> ConvertAsync(
        string soffice, string source, OutputFormat format, string stem, Func<string, string> outputPath,
        string pageLabel = "Slide", double? imageQuality = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openshelf-office-" + Guid.NewGuid().ToString("N")[..12]);
        var output = Path.Combine(workspace, "out");
        Directory.CreateDirectory(output);
        try
        {
            // Slides (and pages) become images by way of a PDF, one picture each.
            if (format is OutputFormat.Jpg or OutputFormat.Png)
            {
                var pdf = await RunAsync(soffice, source, "pdf", "pdf", workspace, output, ct).ConfigureAwait(false);
                var named = Path.Combine(workspace, Path.GetFileNameWithoutExtension(source) + ".pdf");
                File.Move(pdf, named);
                return await Task.Run(() => PdfConversion.Convert(named, format, stem, outputPath, pageLabel, imageQuality, progress, ct), ct).ConfigureAwait(false);
            }
            var extension = format.FileExtension();
            var filter = Filter(format);
            if (extension is null || filter is null)
                throw ConversionException.Unsupported($"{FileKinds.ExtensionOf(source).ToUpperInvariant()} to {format.Title()}");
            var converted = await RunAsync(soffice, source, filter, extension, workspace, output, ct).ConfigureAwait(false);
            var destination = outputPath($"{stem}.{extension}");
            File.Move(converted, destination);
            progress?.Report(1);
            return [destination];
        }
        finally
        {
            Zips.DeleteFolderQuietly(workspace);
        }
    }

    private async Task<string> RunAsync(string soffice, string source, string filter, string extension, string workspace, string output, CancellationToken ct)
    {
        var profile = Path.Combine(workspace, "profile");
        try
        {
            await _runner(new ToolInvocation(soffice, Arguments(source, filter, profile, output)) { Timeout = Timeout, WorkingDirectory = workspace }, null, ct).ConfigureAwait(false);
        }
        catch (ProcessFailure failure) when (failure.TimedOut)
        {
            throw ConversionException.Failed($"LibreOffice took too long to convert “{Path.GetFileName(source)}”.");
        }
        var expected = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + "." + extension);
        if (File.Exists(expected)) return expected;
        // LibreOffice exits successfully even when it couldn't read the file.
        var produced = Directory.EnumerateFiles(output).FirstOrDefault();
        return produced ?? throw ConversionException.Failed($"LibreOffice couldn’t convert “{Path.GetFileName(source)}”.");
    }
}

/// <summary>Word, spreadsheet and slide files through LibreOffice: DOCX/DOC/ODT/RTF to PDF, XLS/ODS to XLSX/CSV/PDF/ODS, CSV/XLSX to ODS,
/// and PPTX/PPT/ODP to PDF and page images. Asked before the built-in engines; when LibreOffice fails a document is converted
/// by the built-in way instead.</summary>
public sealed class LibreOfficeEngine : IConversionEngine
{
    private readonly OfficeConversion _office;
    private readonly DocumentEngine _fallback;

    public LibreOfficeEngine(ToolRunner? runner = null, DocumentEngine? fallback = null)
    {
        _office = new OfficeConversion(runner);
        _fallback = fallback ?? new DocumentEngine();
    }

    private static readonly string[] WordExtensions = ["docx", "doc", "odt", "rtf"];
    private static readonly string[] SlideExtensions = ["pptx", "ppt", "odp"];

    public bool CanConvert(FileKind kind, OutputFormat format) => kind switch
    {
        FileKind.Document => format == OutputFormat.Pdf,
        FileKind.Spreadsheet => format is OutputFormat.Xlsx or OutputFormat.Csv or OutputFormat.Pdf or OutputFormat.Ods,
        FileKind.Presentation => format is OutputFormat.Pdf or OutputFormat.Jpg or OutputFormat.Png,
        _ => false,
    };

    public bool CanConvert(ConversionRequest request)
    {
        if (request.ToolsOrDefault.LibreOffice is null) return false;
        var path = FileKinds.ResolveLink(request.SourcePath);
        if (IWorkFormats.FromPath(path) is not null) return false;     // the iWork engine decides
        var ext = FileKinds.ExtensionOf(path);
        var format = request.Format;
        if (format.Matches(ext)) return false;
        return FileKinds.Of(path) switch
        {
            FileKind.Document => format == OutputFormat.Pdf && WordExtensions.Contains(ext),
            FileKind.Spreadsheet => ext switch
            {
                "xls" or "ods" => format is OutputFormat.Xlsx or OutputFormat.Csv or OutputFormat.Pdf or OutputFormat.Ods,
                "csv" or "tsv" or "xlsx" => format == OutputFormat.Ods,
                _ => false,
            },
            FileKind.Presentation => SlideExtensions.Contains(ext) && format is OutputFormat.Pdf or OutputFormat.Jpg or OutputFormat.Png,
            _ => false,
        };
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var soffice = request.ToolsOrDefault.LibreOffice ?? throw new MissingToolException("LibreOffice");
        var source = FileKinds.ResolveLink(request.SourcePath);
        var label = FileKinds.Of(source) == FileKind.Presentation ? "Slide" : "Page";
        try
        {
            return await _office.ConvertAsync(soffice, source, request.Format, request.SourceStem, request.OutputPath, label,
                request.Options.ImageQuality, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && FileKinds.Of(source) == FileKind.Document && _fallback.CanConvert(request))
        {
            _ = error;
            // LibreOffice could not read it; the built-in conversion may.
            return await _fallback.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
    }
}
