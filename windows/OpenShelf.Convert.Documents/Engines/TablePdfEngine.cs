using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>CSV, TSV and XLSX to a table PDF: landscape US Letter pages, the header row repeated on each, printed by Edge from an HTML
/// table. Port of TablePDF in Spreadsheet.swift.</summary>
public sealed class TablePdfEngine : IConversionEngine
{
    private readonly EdgePrinter _edge;

    public TablePdfEngine(EdgePrinter? edge = null) => _edge = edge ?? EdgePrinter.Default;

    public bool CanConvert(FileKind kind, OutputFormat format) => kind == FileKind.Spreadsheet && format == OutputFormat.Pdf;

    public bool CanConvert(ConversionRequest request) =>
        request.Format == OutputFormat.Pdf && FileKinds.ExtensionOf(request.SourcePath) is "csv" or "tsv" or "xlsx";

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var table = await Task.Run(() => SpreadsheetEngine.Read(request.SourcePath), cancellationToken).ConfigureAwait(false);
        progress?.Report(0.3);
        var destination = request.OutputPath($"{request.SourceStem}.pdf");
        try
        {
            await _edge.PrintHtmlAsync(Html(request.SourceStem, table), destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Zips.DeleteQuietly(destination);
            throw;
        }
        progress?.Report(1);
        return [destination];
    }

    /// <summary>The page to print: a table that fits the width, sized to its content, header row repeated, every other row shaded.</summary>
    public static string Html(string title, IReadOnlyList<List<string>> table)
    {
        var rows = table.Count == 0 ? [[" "]] : table;
        var columns = rows.Max(row => row.Count);
        // Size columns to their content like the Mac, then let the browser shrink everything to fit.
        var longest = Enumerable.Range(0, Math.Max(columns, 1))
            .Select(column => Math.Clamp(rows.Take(500).Select(row => column < row.Count ? row[column].Length : 0).DefaultIfEmpty(1).Max(), 4, 40)).ToList();
        var total = Math.Max(longest.Sum(), 1);
        var fontSize = total > 110 ? 7 : total > 80 ? 8 : 9;
        var html = new StringBuilder();
        html.Append("<table><colgroup>");
        foreach (var length in longest) html.Append($"<col style=\"width:{length * 100.0 / total:0.##}%\"/>");
        html.Append("</colgroup>\n<thead><tr>");
        foreach (var cell in Pad(rows[0], columns)) html.Append($"<th>{Texts.Escape(cell)}</th>");
        html.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows.Skip(1))
        {
            html.Append("<tr>");
            foreach (var cell in Pad(row, columns)) html.Append($"<td>{Texts.Escape(cell)}</td>");
            html.Append("</tr>\n");
        }
        html.Append("</tbody></table>");
        var css = "@page { size: 11in 8.5in; margin: 0.5in; }\n" +
            $"body {{ font-family: 'Segoe UI', Arial, sans-serif; font-size: {fontSize}pt; margin: 0; color: #111; }}\n" +
            "table { width: 100%; border-collapse: collapse; table-layout: fixed; }\n" +
            "thead { display: table-header-group; } tr { page-break-inside: avoid; }\n" +
            "th, td { padding: 2px 5px; text-align: left; vertical-align: top; overflow-wrap: anywhere; line-height: 1.5; }\n" +
            "th { background: #e6e6e6; font-weight: 700; } tbody tr:nth-child(even) td { background: #f7f7f7; }\n";
        return HtmlWriter.Page(title, html.ToString(), css);
    }

    private static IEnumerable<string> Pad(List<string> row, int columns) =>
        row.Count >= columns ? row : row.Concat(Enumerable.Repeat("", columns - row.Count));
}
