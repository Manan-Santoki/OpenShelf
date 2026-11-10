using System.Globalization;
using System.Text;

namespace OpenShelf.Convert.Markdown;

/// <summary>Markdown files, rendered: headings, lists, tables, code, footnotes and pictures become the real thing in each
/// format, instead of the Markdown source shown as plain text. Port of MarkdownConversion.swift; PDF goes through
/// <see cref="HtmlPdfPrinter"/> and RTF through <see cref="MarkdownRtf"/>.</summary>
public static class MarkdownConversion
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "md", "markdown", "mdown", "mkd", "mkdn" };

    /// <summary>The formats written from Markdown by the built-in conversion.</summary>
    public static readonly IReadOnlyList<OutputFormat> Outputs =
        [OutputFormat.Pdf, OutputFormat.Docx, OutputFormat.Txt, OutputFormat.Epub, OutputFormat.Html, OutputFormat.Rtf];

    public static bool IsMarkdown(string path) => Extensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>Reads and parses a Markdown file.</summary>
    public static MarkdownDocument Load(string source)
    {
        var text = TextDecoding.Read(source) ?? throw ConversionException.Unreadable(source);
        return MarkdownDocument.Parse(text);
    }

    public static async Task ConvertAsync(string source, OutputFormat format, string destination, HtmlPdfPrinter? printer = null, CancellationToken cancellationToken = default)
    {
        var document = Load(source);
        var folder = Path.GetDirectoryName(Path.GetFullPath(source));
        var name = Path.GetFileNameWithoutExtension(source);
        try
        {
            switch (format)
            {
                case OutputFormat.Html:
                    File.WriteAllText(destination, MarkdownHtml.Page(document, name), new UTF8Encoding(false));
                    break;
                case OutputFormat.Txt:
                    File.WriteAllText(destination, MarkdownPlainText.Text(document), new UTF8Encoding(false));
                    break;
                case OutputFormat.Docx:
                    MarkdownDocx.Write(document, folder, destination, name);
                    break;
                case OutputFormat.Epub:
                    WriteEpub(document, document.Title ?? name, folder, destination);
                    break;
                case OutputFormat.Rtf:
                    MarkdownRtf.Write(document, folder, destination);
                    break;
                case OutputFormat.Pdf:
                    await WritePdfAsync(document, name, folder, destination, printer ?? HtmlPdfPrinter.Default, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw ConversionException.Unsupported($"Markdown to {format.Title()}");
            }
        }
        catch
        {
            try { File.Delete(destination); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>One chapter, with the document's own pictures packed in.</summary>
    private static void WriteEpub(MarkdownDocument document, string title, string? baseFolder, string destination)
    {
        var images = new Dictionary<string, byte[]>();
        var names = new Dictionary<string, string>();
        foreach (var source in MarkdownImages.Sources(document))
        {
            if (names.ContainsKey(source)) continue;
            if (MarkdownImages.LocalPath(source, baseFolder) is not { } path || Pictures.Jpeg(path) is not { } jpeg) continue;
            var name = $"image{images.Count + 1}.jpg";
            images[name] = jpeg;
            names[source] = $"images/{name}";
        }
        var body = new MarkdownHtml(source => names.TryGetValue(source, out var packed) ? packed : source).Body(document);
        EpubWriter.Write(title, [new EpubChapter(title, body)], images, destination);
    }

    /// <summary>Prints through Edge: the page lives in a private temporary folder and may only load local pictures.</summary>
    private static async Task WritePdfAsync(MarkdownDocument document, string name, string? baseFolder, string destination, HtmlPdfPrinter printer, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "openshelf-md-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        try
        {
            var page = MarkdownHtml.PrintPage(document, name, PaperSizes.ForCurrentRegion(), source =>
                MarkdownImages.LocalPath(source, baseFolder) is { } path ? new Uri(path).AbsoluteUri : null);
            var htmlPath = Path.Combine(work, "document.html");
            await File.WriteAllTextAsync(htmlPath, page, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await printer.PrintAsync(htmlPath, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Markdown as plain text that reads well on its own: no asterisks or pound signs, tables lined up in columns, link
/// addresses after their text. Port of MarkdownPlainText.</summary>
public static class MarkdownPlainText
{
    public static string Text(MarkdownDocument document)
    {
        var lines = new List<string>();
        if (document.Title is { } title)
        {
            lines.Add(title);
            lines.Add(new string('=', title.Length));
            lines.Add("");
        }
        lines.AddRange(Blocks(document.Blocks));
        if (document.Footnotes.Count > 0)
        {
            lines.Add("");
            lines.Add("---");
            lines.Add("");
            for (var index = 0; index < document.Footnotes.Count; index++)
                lines.AddRange(Prefixed(Blocks(document.Footnotes[index]), $"[{index + 1}] ", "    "));
        }
        return string.Join("\n", lines).Trim('\n', '\r') + "\n";
    }

    private static List<string> Blocks(List<MarkdownBlock<MarkdownText>> blocks, bool tight = false)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (lines.Count > 0 && !tight) lines.Add("");
            lines.AddRange(Block(block));
        }
        return lines;
    }

    private static List<string> Block(MarkdownBlock<MarkdownText> block)
    {
        switch (block)
        {
            case MarkdownBlock<MarkdownText>.Heading h:
            {
                var title = Inline(h.Text);
                return h.Level switch
                {
                    1 => [title, new string('=', title.Length)],
                    2 => [title, new string('-', title.Length)],
                    _ => [title],
                };
            }
            case MarkdownBlock<MarkdownText>.Paragraph p:
                return Inline(p.Text).Split('\n').ToList();
            case MarkdownBlock<MarkdownText>.Code c:
                return c.Source.Split('\n').Select(l => l.Length == 0 ? "" : "    " + l).ToList();
            case MarkdownBlock<MarkdownText>.Math m:
                return ["    " + MarkdownMath.Unicode(m.Tex)];
            case MarkdownBlock<MarkdownText>.Quote q:
                return Blocks(q.Blocks).Select(l => l.Length == 0 ? ">" : "> " + l).ToList();
            case MarkdownBlock<MarkdownText>.ListBlock l:
            {
                var list = l.List;
                var lines = new List<string>();
                var width = list.Ordered ? $"{list.Start + list.Items.Count - 1}. ".Length : 2;
                for (var index = 0; index < list.Items.Count; index++)
                {
                    if (!list.Tight && index > 0) lines.Add("");
                    var marker = list.Ordered ? $"{list.Start + index}. " : "• ";
                    lines.AddRange(Prefixed(Blocks(list.Items[index], list.Tight), marker.PadRight(width)[..width], new string(' ', width)));
                }
                return lines;
            }
            case MarkdownBlock<MarkdownText>.Table t:
                return Table(t.Value);
            case MarkdownBlock<MarkdownText>.Definitions d:
            {
                var lines = new List<string>();
                for (var index = 0; index < d.Items.Count; index++)
                {
                    if (index > 0) lines.Add("");
                    lines.Add(Inline(d.Items[index].Term));
                    foreach (var definition in d.Items[index].Definitions)
                        lines.AddRange(Blocks(definition).Select(l => l.Length == 0 ? "" : "    " + l));
                }
                return lines;
            }
            case MarkdownBlock<MarkdownText>.Lines lines:
                return lines.Items.Select(l => Inline(l).Replace(' ', ' ')).ToList();
            default:
                return ["* * *"];
        }
    }

    private static List<string> Table(MarkdownTable<MarkdownText> table)
    {
        var header = table.Header?.Select(Cell).ToList();
        var rows = table.Rows.Select(r => r.Select(Cell).ToList()).ToList();
        var widths = new int[table.ColumnCount];
        var measured = new List<List<string>>();
        if (header is not null) measured.Add(header);
        measured.AddRange(rows);
        foreach (var row in measured)
            for (var column = 0; column < row.Count && column < widths.Length; column++)
                widths[column] = Math.Max(widths[column], row[column].Length);

        string Line(List<string> cells) => string.Join("  ", cells.Select((text, column) =>
        {
            var padding = new string(' ', Math.Max(widths[column] - text.Length, 0));
            return table.Alignments[column] == MarkdownAlignment.Right ? padding + text : text + padding;
        })).TrimSpaces();

        var lines = new List<string>();
        if (table.Caption is { } caption)
        {
            lines.Add(Inline(caption));
            lines.Add("");
        }
        if (header is not null && header.Any(c => c.Length > 0))
        {
            lines.Add(Line(header));
            lines.Add(string.Join("  ", widths.Select(w => new string('-', Math.Max(w, 1)))));
        }
        lines.AddRange(rows.Select(Line));
        return lines;
    }

    private static string Cell(MarkdownText text) => Inline(text).Replace("\n", " ");

    private static List<string> Prefixed(List<string> lines, string first, string rest) =>
        lines.Select((line, index) => index == 0 ? first + line : line.Length == 0 ? "" : rest + line).ToList();

    private static string Inline(MarkdownText text)
    {
        var builder = new StringBuilder();
        foreach (var inline in text)
        {
            switch (inline)
            {
                case MarkdownInline.Text t: builder.Append(t.Value); break;
                case MarkdownInline.Code c: builder.Append(c.Value); break;
                case MarkdownInline.Math m: builder.Append(MarkdownMath.Unicode(m.Tex)); break;
                case MarkdownInline.Emphasis e: builder.Append(Inline(e.Content)); break;
                case MarkdownInline.Strong s: builder.Append(Inline(s.Content)); break;
                case MarkdownInline.Strikethrough s: builder.Append(Inline(s.Content)); break;
                case MarkdownInline.Link l: builder.Append(Linked(Inline(l.Content), l.Url)); break;
                case MarkdownInline.Image i:
                {
                    var alt = Inline(i.Alt);
                    builder.Append($"[{(alt.Length == 0 ? "image" : alt)}]");
                    break;
                }
                case MarkdownInline.Footnote f: builder.Append($"[{f.Number}]"); break;
                case MarkdownInline.LineBreak: builder.Append('\n'); break;
                case MarkdownInline.SoftBreak: builder.Append(' '); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>"a website (http://foo.bar)", unless the text already is the address.</summary>
    private static string Linked(string text, string url)
    {
        var bare = url.Replace("mailto:", "");
        if (url.StartsWith('#') || text == url || text == bare) return text;
        return $"{text} ({bare})";
    }
}
