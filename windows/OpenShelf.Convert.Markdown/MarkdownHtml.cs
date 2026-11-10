using System.Text;

namespace OpenShelf.Convert.Markdown;

/// <summary>Escaping for text placed in XML and XHTML (PackageZip.swift's <c>xmlEscaped</c>).</summary>
internal static class Xml
{
    /// <summary>Escapes &amp; &lt; &gt; and quotes, and drops the control characters XML 1.0 cannot hold.</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\t' or '\n' or '\r': builder.Append(c); break;
                case < ' ': break;
                case '￾' or '￿': break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>Writes a Markdown document as HTML. The markup is well-formed XHTML, so the same body serves a standalone page and
/// an EPUB chapter. Port of MarkdownHTML.swift.</summary>
public sealed class MarkdownHtml
{
    private readonly Func<string, string?> _imageSource;
    private readonly HashSet<int> _referencedFootnotes = [];

    /// <param name="imageSource">Rewrites an image's address, e.g. to the copy packed into an EPUB. Null drops the picture
    /// (its description is shown instead).</param>
    public MarkdownHtml(Func<string, string?>? imageSource = null) => _imageSource = imageSource ?? (s => s);

    /// <summary>A complete page with its own stylesheet, light and dark.</summary>
    public static string Page(MarkdownDocument document, string title)
    {
        var body = new MarkdownHtml().Body(document);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <title>{Xml.Escape(document.Title ?? title)}</title>
            <style>
            {Stylesheet}
            </style>
            </head>
            <body>
            {body}
            </body>
            </html>

            """;
    }

    /// <summary>A page for printing: the same markup with a print stylesheet (paper size, margins, page-break rules) and no
    /// access to the network. Pictures are loaded through <paramref name="imageSource"/>.</summary>
    public static string PrintPage(MarkdownDocument document, string title, PaperSize paper, Func<string, string?> imageSource, string? baseHref = null)
    {
        var body = new MarkdownHtml(imageSource).Body(document);
        var baseTag = baseHref is null ? "" : $"<base href=\"{Xml.Escape(baseHref)}\"/>\n";
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src file: data:; style-src 'unsafe-inline'"/>
            {baseTag}<title>{Xml.Escape(document.Title ?? title)}</title>
            <style>
            {PrintStylesheet(paper)}
            </style>
            </head>
            <body>
            {body}
            </body>
            </html>

            """;
    }

    /// <summary>The <c>&lt;body&gt;</c> contents: the title, the blocks, then the footnotes.</summary>
    public string Body(MarkdownDocument document)
    {
        var html = new List<string>();
        if (document.Title is { } title) html.Add($"<h1 class=\"title\">{Xml.Escape(title)}</h1>");
        html.Add(Blocks(document.Blocks, tight: false));
        if (document.Footnotes.Count > 0)
        {
            var notes = new List<string> { "<section class=\"footnotes\" role=\"doc-endnotes\">", "<hr/>", "<ol>" };
            for (var index = 0; index < document.Footnotes.Count; index++)
            {
                var number = index + 1;
                var back = $" <a href=\"#fnref{number}\" class=\"footnote-back\" role=\"doc-backlink\">↩︎</a>";
                notes.Add($"<li id=\"fn{number}\">{AppendingToLastParagraph(back, Blocks(document.Footnotes[index], tight: false))}</li>");
            }
            notes.Add("</ol>");
            notes.Add("</section>");
            html.Add(string.Join("\n", notes));
        }
        return string.Join("\n", html.Where(h => h.Length > 0));
    }

    /// <summary>Puts the back link inside the note's last paragraph, where it reads naturally.</summary>
    private static string AppendingToLastParagraph(string link, string html) =>
        html.EndsWith("</p>", StringComparison.Ordinal) ? html[..^4] + link + "</p>" : html + link;

    // MARK: Blocks

    private string Blocks(List<MarkdownBlock<MarkdownText>> blocks, bool tight) =>
        string.Join("\n", blocks.Select(b => Block(b, tight)));

    private string Block(MarkdownBlock<MarkdownText> block, bool tight)
    {
        switch (block)
        {
            case MarkdownBlock<MarkdownText>.Heading h:
                return $"<h{h.Level} id=\"{Xml.Escape(h.Id)}\">{Inlines(h.Text)}</h{h.Level}>";
            case MarkdownBlock<MarkdownText>.Paragraph p:
                if (p.Text.FigureImage() is { } figure)
                {
                    var alt = figure.Alt.PlainText();
                    var caption = alt.Length == 0 ? "" : $"<figcaption>{Inlines(figure.Alt)}</figcaption>";
                    return $"<figure>{Image(figure.Source, figure.Title, alt)}{caption}</figure>";
                }
                return tight ? Inlines(p.Text) : $"<p>{Inlines(p.Text)}</p>";
            case MarkdownBlock<MarkdownText>.Code c:
                var attribute = c.Language is { } language ? $" class=\"language-{Xml.Escape(language)}\"" : "";
                return $"<pre><code{attribute}>{Xml.Escape(c.Source)}</code></pre>";
            case MarkdownBlock<MarkdownText>.Math m:
                return $"<p class=\"math display\">{Xml.Escape(MarkdownMath.Unicode(m.Tex))}</p>";
            case MarkdownBlock<MarkdownText>.Quote q:
                return $"<blockquote>\n{Blocks(q.Blocks, tight: false)}\n</blockquote>";
            case MarkdownBlock<MarkdownText>.ListBlock l:
            {
                var list = l.List;
                var tag = list.Ordered ? "ol" : "ul";
                var start = list.Ordered && list.Start != 1 ? $" start=\"{list.Start}\"" : "";
                var items = list.Items.Select(item => $"<li>{Blocks(item, list.Tight)}</li>");
                return $"<{tag}{start}>\n{string.Join("\n", items)}\n</{tag}>";
            }
            case MarkdownBlock<MarkdownText>.Table t:
                return Table(t.Value);
            case MarkdownBlock<MarkdownText>.Definitions d:
            {
                var html = new List<string> { "<dl>" };
                foreach (var item in d.Items)
                {
                    html.Add($"<dt>{Inlines(item.Term)}</dt>");
                    foreach (var definition in item.Definitions) html.Add($"<dd>{Blocks(definition, definition.Count == 1)}</dd>");
                }
                html.Add("</dl>");
                return string.Join("\n", html);
            }
            case MarkdownBlock<MarkdownText>.Lines lines:
                return $"<div class=\"line-block\">{string.Join("<br/>", lines.Items.Select(Inlines))}</div>";
            default:
                return "<hr/>";
        }
    }

    private string Table(MarkdownTable<MarkdownText> table)
    {
        string Style(int column) => table.Alignments[column] switch
        {
            MarkdownAlignment.Left => " style=\"text-align: left\"",
            MarkdownAlignment.Center => " style=\"text-align: center\"",
            MarkdownAlignment.Right => " style=\"text-align: right\"",
            _ => "",
        };
        var html = new List<string> { "<table>" };
        if (table.Caption is { } caption) html.Add($"<caption>{Inlines(caption)}</caption>");
        if (table.Widths is { } widths)
            html.Add("<colgroup>" + string.Concat(widths.Select(w => $"<col style=\"width: {(int)Math.Round(w * 100, MidpointRounding.AwayFromZero)}%\"/>")) + "</colgroup>");
        if (table.Header is { } header && header.Any(c => c.Count > 0))
        {
            var cells = header.Select((cell, i) => $"<th{Style(i)}>{Inlines(cell)}</th>");
            html.Add($"<thead>\n<tr>{string.Concat(cells)}</tr>\n</thead>");
        }
        html.Add("<tbody>");
        foreach (var row in table.Rows)
        {
            var cells = row.Select((cell, i) => $"<td{Style(i)}>{Inlines(cell)}</td>");
            html.Add($"<tr>{string.Concat(cells)}</tr>");
        }
        html.Add("</tbody>");
        html.Add("</table>");
        return string.Join("\n", html);
    }

    // MARK: Inline content

    private string Inlines(MarkdownText text) => string.Concat(text.Select(Inline));

    private string Inline(MarkdownInline inline)
    {
        switch (inline)
        {
            case MarkdownInline.Text t: return Xml.Escape(t.Value);
            case MarkdownInline.Emphasis e: return $"<em>{Inlines(e.Content)}</em>";
            case MarkdownInline.Strong s: return $"<strong>{Inlines(s.Content)}</strong>";
            case MarkdownInline.Strikethrough s: return $"<del>{Inlines(s.Content)}</del>";
            case MarkdownInline.Code c: return $"<code>{Xml.Escape(c.Value)}</code>";
            case MarkdownInline.Math m: return $"<span class=\"math\">{Xml.Escape(MarkdownMath.Unicode(m.Tex))}</span>";
            case MarkdownInline.Link l:
            {
                if (!IsSafe(l.Url)) return Inlines(l.Content);
                var title = l.Title is { } t ? $" title=\"{Xml.Escape(t)}\"" : "";
                return $"<a href=\"{Xml.Escape(l.Url)}\"{title}>{Inlines(l.Content)}</a>";
            }
            case MarkdownInline.Image i: return Image(i.Source, i.Title, i.Alt.PlainText());
            case MarkdownInline.Footnote f:
            {
                // Only the first reference can be the note's way back.
                var id = _referencedFootnotes.Add(f.Number) ? $" id=\"fnref{f.Number}\"" : "";
                return $"<sup><a href=\"#fn{f.Number}\"{id} class=\"footnote-ref\" role=\"doc-noteref\">{f.Number}</a></sup>";
            }
            case MarkdownInline.LineBreak: return "<br/>";
            default: return "\n";
        }
    }

    private string Image(string source, string? title, string alt)
    {
        if (!IsSafe(source)) return Xml.Escape(alt);
        var rewritten = _imageSource(source);
        if (rewritten is null) return Xml.Escape(alt);
        var titleAttribute = title is { } t ? $" title=\"{Xml.Escape(t)}\"" : "";
        return $"<img src=\"{Xml.Escape(rewritten)}\" alt=\"{Xml.Escape(alt)}\"{titleAttribute}/>";
    }

    /// <summary>Script links are dropped; the document is opened by people who did not write it.</summary>
    public static bool IsSafe(string url)
    {
        var length = 0;
        while (length < url.Length && url[length] is not (':' or '/' or '#' or '?')) length++;
        var scheme = url[..length].ToLowerInvariant();
        if (!(length < url.Length && url[length] == ':')) return true;
        return !(scheme.Trim() is "javascript" or "vbscript" or "data" or "file");
    }

    // MARK: Style

    public const string Stylesheet = """
        :root { color-scheme: light dark; --text: #1d1d1f; --muted: #6e6e73; --rule: #d2d2d7; --code: #f5f5f7; --link: #0066cc; }
        @media (prefers-color-scheme: dark) { :root { --text: #f5f5f7; --muted: #a1a1a6; --rule: #424245; --code: #2c2c2e; --link: #2997ff; } }
        body { font: 16px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", "Helvetica Neue", Helvetica, Arial, sans-serif; color: var(--text); max-width: 46em; margin: 0 auto; padding: 2em 1.25em 4em; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.6em 0 0.6em; }
        h1 { font-size: 2em; } h2 { font-size: 1.5em; } h3 { font-size: 1.25em; } h4 { font-size: 1.05em; }
        h1.title { margin-top: 0; text-align: center; }
        a { color: var(--link); }
        p, ul, ol, dl, pre, blockquote, table, figure { margin: 0 0 1em; }
        code, pre { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 0.875em; }
        code { background: var(--code); border-radius: 4px; padding: 0.1em 0.35em; }
        pre { background: var(--code); border-radius: 8px; padding: 0.85em 1em; overflow-x: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 1em; }
        blockquote { border-left: 4px solid var(--rule); color: var(--muted); margin-left: 0; padding: 0 1em; }
        table { border-collapse: collapse; margin-left: auto; margin-right: auto; }
        caption { font-style: italic; color: var(--muted); padding-bottom: 0.4em; }
        th, td { border-bottom: 1px solid var(--rule); padding: 0.35em 0.8em; vertical-align: top; text-align: left; }
        thead th { border-bottom: 2px solid var(--text); }
        hr { border: none; border-top: 1px solid var(--rule); margin: 2em 0; }
        img { max-width: 100%; height: auto; }
        figure { text-align: center; } figcaption { font-style: italic; color: var(--muted); margin-top: 0.4em; }
        dt { font-weight: 600; } dd { margin: 0 0 0.6em 1.5em; }
        .line-block { margin: 0 0 1em; }
        .math { font-family: "Times New Roman", Times, serif; font-style: italic; }
        .math.display { text-align: center; font-size: 1.15em; }
        .footnotes { font-size: 0.9em; color: var(--muted); margin-top: 3em; }
        .footnote-ref { text-decoration: none; }
        """;

    /// <summary>The look of a printed page: light only, 11 pt serif-free body, margins from the paper size, and rules that keep
    /// headings with what follows them and tables, figures and code from splitting where they can be kept whole.</summary>
    public static string PrintStylesheet(PaperSize paper) => $$"""
        @page { size: {{HtmlPdfPrinter.CssPaperName(paper)}}; margin: {{HtmlPdfPrinter.CssMargin(paper)}}; }
        :root { --text: #111; --muted: #555; --rule: #bbb; --code: #f2f2f2; --link: #0645ad; }
        * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        html { font-size: 11pt; }
        body { font: 11pt/1.5 "Segoe UI", "Helvetica Neue", Arial, sans-serif; color: var(--text); margin: 0; padding: 0; orphans: 3; widows: 3; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.4em 0 0.5em; break-after: avoid; page-break-after: avoid; break-inside: avoid; }
        h1 { font-size: 22pt; } h2 { font-size: 16pt; } h3 { font-size: 13pt; } h4 { font-size: 11.5pt; } h5, h6 { font-size: 11pt; }
        h1.title { margin-top: 0; text-align: center; }
        a { color: var(--link); text-decoration: none; }
        p, ul, ol, dl, pre, blockquote, table, figure { margin: 0 0 0.9em; }
        code, pre { font-family: Consolas, "Cascadia Mono", "Courier New", monospace; font-size: 9.5pt; }
        code { background: var(--code); border-radius: 3px; padding: 0.05em 0.3em; }
        pre { background: var(--code); border: 1px solid var(--rule); border-radius: 4px; padding: 0.6em 0.8em; line-height: 1.4; white-space: pre-wrap; word-wrap: break-word; break-inside: avoid; page-break-inside: avoid; }
        pre code { background: none; padding: 0; font-size: 1em; }
        blockquote { border-left: 3px solid var(--rule); color: var(--muted); margin-left: 0; padding: 0 1em; break-inside: avoid; page-break-inside: avoid; }
        table { border-collapse: collapse; margin-left: auto; margin-right: auto; max-width: 100%; break-inside: avoid; page-break-inside: avoid; }
        tr, th, td, li, figure, img { break-inside: avoid; page-break-inside: avoid; }
        thead { display: table-header-group; }
        caption { font-style: italic; color: var(--muted); padding-bottom: 0.4em; caption-side: top; }
        th, td { border-bottom: 1px solid var(--rule); padding: 0.3em 0.7em; vertical-align: top; text-align: left; }
        thead th { border-bottom: 2px solid var(--text); }
        hr { border: none; border-top: 1px solid var(--rule); margin: 1.6em 0; }
        img { max-width: 100%; max-height: 22cm; height: auto; }
        figure { text-align: center; } figcaption { font-style: italic; color: var(--muted); margin-top: 0.4em; }
        dt { font-weight: 600; break-after: avoid; page-break-after: avoid; } dd { margin: 0 0 0.6em 1.5em; }
        .line-block { margin: 0 0 0.9em; }
        .math { font-family: "Cambria Math", "Times New Roman", Times, serif; font-style: italic; }
        .math.display { text-align: center; font-size: 1.1em; break-inside: avoid; }
        .footnotes { font-size: 9.5pt; color: var(--muted); margin-top: 2em; }
        .footnote-ref { text-decoration: none; }
        .footnote-back { display: none; }
        """;
}
