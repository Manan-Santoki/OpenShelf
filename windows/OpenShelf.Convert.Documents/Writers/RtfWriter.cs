using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>Writes the structure model as RTF: sizes for headings, bold, italic, underline, hyperlinks, bullets and numbers,
/// tables and PNG/JPEG pictures.</summary>
public static class RtfWriter
{
    public static string Build(DocumentModel model)
    {
        var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Arial;}}\\viewkind4\\uc1\\pard\\f0\\fs24\n");
        Block? previous = null;
        var number = 0;
        foreach (var block in model.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    var size = h.Level switch { 1 => 40, 2 => 32, 3 => 28, _ => 26 };
                    rtf.Append($"\\pard\\sb240\\sa120\\b\\fs{size} {Runs(h.Runs)}\\b0\\fs24\\par\n");
                    break;
                case ParagraphBlock p:
                    rtf.Append($"\\pard\\sa160 {Runs(p.Runs)}\\par\n");
                    break;
                case ListItemBlock item:
                    number = previous is ListItemBlock before && before.Ordered == item.Ordered ? number + 1 : 1;
                    var marker = item.Ordered ? $"{number}." : "\\bullet";
                    var indent = 360 * (Math.Clamp(item.Level, 0, 8) + 1);
                    rtf.Append($"\\pard\\li{indent}\\fi-360\\sa40 {marker}\\tab {Runs(item.Runs)}\\par\n");
                    break;
                case TableBlock table:
                    Table(table, rtf);
                    break;
                case ImageBlock image:
                    var kind = image.Extension == "jpeg" ? "jpegblip" : image.Extension == "png" ? "pngblip" : null;
                    if (kind is null) break;
                    rtf.Append($"\\pard\\qc{{\\pict\\{kind}\\picwgoal{image.Width * 15}\\pichgoal{image.Height * 15}\n{System.Convert.ToHexString(image.Data)}}}\\par\n");
                    break;
                case PageBreakBlock:
                    rtf.Append("\\page\n");
                    break;
            }
            previous = block;
        }
        return rtf.Append('}').ToString();
    }

    private static void Table(TableBlock table, StringBuilder rtf)
    {
        var columns = Math.Max(table.Rows.Count == 0 ? 1 : table.Rows.Max(row => row.Count), 1);
        var width = 9360 / columns;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            rtf.Append("\\trowd\\trgaph108");
            for (var c = 0; c < columns; c++)
                rtf.Append("\\clbrdrt\\brdrs\\clbrdrl\\brdrs\\clbrdrb\\brdrs\\clbrdrr\\brdrs").Append($"\\cellx{(c + 1) * width}");
            for (var c = 0; c < columns; c++)
            {
                var runs = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                if (table.HasHeader && r == 0) runs = runs.Select(run => run with { Bold = true }).ToList();
                rtf.Append($"\\intbl {Runs(runs)}\\cell");
            }
            rtf.Append("\\row\n");
        }
        rtf.Append("\\pard\\par\n");
    }

    public static string Runs(IEnumerable<TextRun> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            var text = Escape(run.Text);
            if (text.Length == 0) continue;
            var open = (run.Bold ? "\\b " : "") + (run.Italic ? "\\i " : "") + (run.Underline ? "\\ul " : "");
            var styled = open.Length == 0 ? text : $"{{{open}{text}}}";
            if (run.Link is not null)
                styled = $"{{\\field{{\\*\\fldinst{{HYPERLINK \"{Escape(run.Link)}\"}}}}{{\\fldrslt{{\\ul {text}}}}}}}";
            builder.Append(styled);
        }
        return builder.ToString();
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}': builder.Append('\\').Append(c); break;
                case '\n': builder.Append("\\line "); break;
                case '\t': builder.Append("\\tab "); break;
                case '\r': break;
                case < ' ': break;
                case > '~': builder.Append("\\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?'); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>Writes OpenDocument text from the structure model: headings, paragraphs, bold, italic, underline, links, lists
/// and tables. The fallback when Pandoc is missing or fails; pictures are left out.</summary>
public static class OdtWriter
{
    private const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    public static void Write(DocumentModel model, string path)
    {
        var styles = new SortedDictionary<string, string>();
        var body = new StringBuilder();
        for (var index = 0; index < model.Blocks.Count; index++)
        {
            switch (model.Blocks[index])
            {
                case HeadingBlock h:
                    body.Append($"<text:h text:outline-level=\"{Math.Clamp(h.Level, 1, 6)}\">{Runs(h.Runs, styles)}</text:h>");
                    break;
                case ParagraphBlock p:
                    body.Append($"<text:p>{Runs(p.Runs, styles)}</text:p>");
                    break;
                case ListItemBlock:
                    // One list per run of items of the same kind.
                    var ordered = ((ListItemBlock)model.Blocks[index]).Ordered;
                    body.Append(ordered ? "<text:list text:style-name=\"Numbered\">" : "<text:list text:style-name=\"Bullets\">");
                    while (index < model.Blocks.Count && model.Blocks[index] is ListItemBlock item && item.Ordered == ordered)
                    {
                        body.Append($"<text:list-item><text:p>{Runs(item.Runs, styles)}</text:p></text:list-item>");
                        index++;
                    }
                    index--;
                    body.Append("</text:list>");
                    break;
                case TableBlock table:
                    var columns = Math.Max(table.Rows.Count == 0 ? 1 : table.Rows.Max(row => row.Count), 1);
                    body.Append($"<table:table table:name=\"Table{index}\"><table:table-column table:number-columns-repeated=\"{columns}\"/>");
                    foreach (var row in table.Rows)
                    {
                        body.Append("<table:table-row>");
                        for (var c = 0; c < columns; c++)
                            body.Append($"<table:table-cell office:value-type=\"string\"><text:p>{Runs(c < row.Count ? row[c] : [], styles)}</text:p></table:table-cell>");
                        body.Append("</table:table-row>");
                    }
                    body.Append("</table:table>");
                    break;
            }
        }
        var automatic = new StringBuilder("<office:automatic-styles>");
        foreach (var (name, properties) in styles)
            automatic.Append($"<style:style style:name=\"{name}\" style:family=\"text\"><style:text-properties {properties}/></style:style>");
        automatic.Append("</office:automatic-styles>");
        var lists =
            "<text:list-style style:name=\"Bullets\"><text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"•\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"listtab\" text:list-tab-stop-position=\"0.5in\" fo:text-indent=\"-0.25in\" fo:margin-left=\"0.5in\"/></style:list-level-properties></text:list-level-style-bullet></text:list-style>" +
            "<text:list-style style:name=\"Numbered\"><text:list-level-style-number text:level=\"1\" style:num-suffix=\".\" style:num-format=\"1\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"listtab\" text:list-tab-stop-position=\"0.5in\" fo:text-indent=\"-0.25in\" fo:margin-left=\"0.5in\"/></style:list-level-properties></text:list-level-style-number></text:list-style>";
        automatic.Insert(automatic.Length - "</office:automatic-styles>".Length, lists);
        var content = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {Namespaces} office:version=\"1.2\">{automatic}<office:body><office:text>{body}</office:text></office:body></office:document-content>";

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Zips.Add(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        Zips.Add(zip, "META-INF/manifest.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
        Zips.Add(zip, "content.xml", content);
    }

    private static string Runs(IEnumerable<TextRun> runs, SortedDictionary<string, string> styles)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            var text = new StringBuilder();
            foreach (var c in Texts.XmlSafe(run.Text))
            {
                if (c == '\n') text.Append("<text:line-break/>");
                else if (c == '\t') text.Append("<text:tab/>");
                else if (c != '\r') text.Append(Texts.Escape(c.ToString()));
            }
            var content = text.ToString();
            if (content.Length == 0) continue;
            if (run.Bold || run.Italic || run.Underline)
            {
                var name = "T" + (run.Bold ? "b" : "") + (run.Italic ? "i" : "") + (run.Underline ? "u" : "");
                styles[name] = (run.Bold ? "fo:font-weight=\"bold\" " : "") + (run.Italic ? "fo:font-style=\"italic\" " : "") +
                    (run.Underline ? "style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\" " : "");
                content = $"<text:span text:style-name=\"{name}\">{content}</text:span>";
            }
            if (run.Link is not null) content = $"<text:a xlink:type=\"simple\" xlink:href=\"{Texts.Escape(run.Link)}\">{content}</text:a>";
            builder.Append(content);
        }
        return builder.ToString();
    }
}
