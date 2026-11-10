using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>Text documents (TXT, RTF, RTFD, DOCX, DOC, ODT, HTML) to PDF, DOCX, TXT, MD, EPUB, HTML and RTF with no external tool
/// (ODT too, as the fallback for Pandoc). DOCX, ODT, RTF and HTML are read into the structure model; PDFs are printed by Edge.
/// DOC needs LibreOffice. Port of DocumentConversion.convertBuiltIn.</summary>
public sealed class DocumentEngine : IConversionEngine
{
    public static readonly IReadOnlyList<OutputFormat> Formats =
        [OutputFormat.Pdf, OutputFormat.Docx, OutputFormat.Txt, OutputFormat.Md, OutputFormat.Epub, OutputFormat.Html, OutputFormat.Rtf, OutputFormat.Odt];

    /// <summary>Extensions read here. Markdown has its own engine, and iWork files their own.</summary>
    public static readonly IReadOnlySet<string> Sources =
        new HashSet<string> { "txt", "text", "rtf", "rtfd", "docx", "doc", "odt", "html", "htm", "xhtml" };

    private readonly EdgePrinter _edge;

    public DocumentEngine(EdgePrinter? edge = null) => _edge = edge ?? EdgePrinter.Default;

    public bool CanConvert(FileKind kind, OutputFormat format) => kind == FileKind.Document && Formats.Contains(format);

    public bool CanConvert(ConversionRequest request) =>
        Formats.Contains(request.Format) && Sources.Contains(FileKinds.ExtensionOf(FileKinds.ResolveLink(request.SourcePath))) &&
        !request.Format.Matches(FileKinds.ExtensionOf(request.SourcePath));

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var destination = request.OutputPath($"{request.SourceStem}.{request.Format.FileExtension()}");
        try
        {
            await ConvertToAsync(FileKinds.ResolveLink(request.SourcePath), request.Format, request.SourceStem, destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Zips.DeleteQuietly(destination);
            throw;
        }
        progress?.Report(1);
        return [destination];
    }

    public async Task ConvertToAsync(string source, OutputFormat format, string title, string destination, CancellationToken ct)
    {
        var extension = FileKinds.ExtensionOf(source);
        if (extension is "txt" or "text")
        {
            string text;
            try { text = TextDecoding.Read(source); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ConversionException.Unreadable(source); }
            switch (format)
            {
                case OutputFormat.Pdf:
                    await _edge.PrintHtmlAsync(HtmlWriter.PlainTextPage(title, text), destination, ct).ConfigureAwait(false);
                    return;
                case OutputFormat.Md:
                    // Plain text is already valid Markdown.
                    File.WriteAllText(destination, text, new UTF8Encoding(false));
                    return;
            }
            await WriteAsync(new DocumentModel(title, DocumentStructure.BlocksFromPlainText(text)), format, destination, ct).ConfigureAwait(false);
            return;
        }
        var model = Read(source, extension) with { Title = title };
        ct.ThrowIfCancellationRequested();
        await WriteAsync(model, format, destination, ct).ConfigureAwait(false);
    }

    /// <summary>The structure of a document file.</summary>
    public static DocumentModel Read(string source, string? extension = null)
    {
        extension ??= FileKinds.ExtensionOf(source);
        try
        {
            return extension switch
            {
                "docx" => DocxReader.Read(source),
                "odt" => OdtReader.Read(source),
                "rtf" or "rtfd" => RtfReader.Read(source),
                "html" or "htm" or "xhtml" => HtmlReader.Read(source),
                "doc" => throw ConversionException.Failed($"“{Path.GetFileName(source)}” is an old Word file; reading it needs LibreOffice. Install it in Settings → Extra Formats."),
                _ => throw ConversionException.Unsupported($"{extension.ToUpperInvariant()} documents"),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ConversionException.Unreadable(source);
        }
    }

    public async Task WriteAsync(DocumentModel model, OutputFormat format, string destination, CancellationToken ct)
    {
        switch (format)
        {
            case OutputFormat.Pdf:
                await _edge.PrintHtmlAsync(HtmlWriter.PrintPage(model), destination, ct).ConfigureAwait(false);
                break;
            case OutputFormat.Docx:
                WordWriter.Write(model, destination);
                break;
            case OutputFormat.Txt:
                File.WriteAllText(destination, DocumentStructure.PlainText(model.Blocks), new UTF8Encoding(false));
                break;
            case OutputFormat.Md:
                File.WriteAllText(destination, DocumentStructure.Markdown(model.Blocks), new UTF8Encoding(false));
                break;
            case OutputFormat.Epub:
                var (body, images) = EpubWriter.BodyFor(model.Blocks);
                EpubWriter.Write(model.Title, [new EpubChapter(model.Title, body)], destination, images);
                break;
            case OutputFormat.Html:
                HtmlWriter.Save(destination, HtmlWriter.StandalonePage(model));
                break;
            case OutputFormat.Rtf:
                File.WriteAllText(destination, RtfWriter.Build(model), Encoding.ASCII);
                break;
            case OutputFormat.Odt:
                OdtWriter.Write(model, destination);
                break;
            default:
                throw ConversionException.Unsupported($"document to {format.Title()}");
        }
    }
}
