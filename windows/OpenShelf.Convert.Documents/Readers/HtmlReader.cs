using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace OpenShelf.Convert.Documents;

/// <summary>A tolerant HTML reader (AngleSharp) into the structure model: headings, paragraphs, lists, tables, links, bold,
/// italic, underline and pictures that are embedded or sit next to the file.</summary>
public static class HtmlReader
{
    private sealed record Style(bool Bold, bool Italic, bool Underline, string? Link);

    private sealed class State
    {
        public List<Block> Blocks = [];
        public List<TextRun> Runs = [];
        public string? BaseFolder;
        public Stack<bool> Lists = new();
        public bool PendingItem;
        public bool PreformattedParagraph;
    }

    public static DocumentModel Read(string path)
    {
        var html = TextDecoding.Read(path);
        return Parse(html, Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    public static DocumentModel Parse(string html, string fallbackTitle, string? baseFolder = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        var state = new State { BaseFolder = baseFolder };
        if (document.Body is { } body) Walk(body, new Style(false, false, false, null), state, false);
        FlushParagraph(state);
        var title = document.Title?.Trim();
        return new DocumentModel(string.IsNullOrEmpty(title) ? fallbackTitle : title, state.Blocks);
    }

    private static readonly HashSet<string> Skipped = ["script", "style", "head", "template", "noscript", "svg", "iframe", "object", "canvas", "button", "select", "textarea"];
    private static readonly HashSet<string> BlockTags =
        ["p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "ul", "ol", "li", "table", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6",
         "blockquote", "pre", "figure", "figcaption", "form", "fieldset", "dl", "dt", "dd", "hr", "address", "details", "summary", "center", "tbody", "thead", "tfoot"];

    private static void Walk(INode node, Style style, State state, bool preformatted)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                AddText(state, text.Data, style, preformatted);
                continue;
            }
            if (child is not IElement element) continue;
            var tag = element.LocalName;
            if (Skipped.Contains(tag)) continue;
            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    FlushParagraph(state);
                    Walk(element, style, state, false);
                    FlushParagraph(state, heading: tag[1] - '0');
                    break;
                case "br":
                    state.Runs.Add(new TextRun("\n"));
                    break;
                case "hr":
                    FlushParagraph(state);
                    break;
                case "img":
                    FlushParagraph(state);
                    if (LoadImage(element, state) is { } image) state.Blocks.Add(image);
                    break;
                case "ul" or "ol":
                    FlushParagraph(state);
                    state.Lists.Push(tag == "ol");
                    Walk(element, style, state, false);
                    FlushParagraph(state);
                    state.Lists.Pop();
                    break;
                case "li":
                    FlushParagraph(state);
                    state.PendingItem = true;
                    Walk(element, style, state, false);
                    FlushParagraph(state);
                    break;
                case "table":
                    FlushParagraph(state);
                    state.Blocks.Add(ReadTable(element, style, state));
                    break;
                case "pre":
                    FlushParagraph(state);
                    Walk(element, style, state, true);
                    FlushParagraph(state);
                    break;
                case "a":
                    var href = element.GetAttribute("href");
                    var linked = style with { Link = string.IsNullOrWhiteSpace(href) || href.StartsWith('#') ? style.Link : href };
                    Walk(element, linked, state, preformatted);
                    break;
                case "b" or "strong":
                    Walk(element, style with { Bold = true }, state, preformatted);
                    break;
                case "i" or "em" or "cite" or "dfn" or "var":
                    Walk(element, style with { Italic = true }, state, preformatted);
                    break;
                case "u" or "ins":
                    Walk(element, style with { Underline = true }, state, preformatted);
                    break;
                default:
                    if (BlockTags.Contains(tag))
                    {
                        FlushParagraph(state);
                        Walk(element, style, state, preformatted);
                        FlushParagraph(state);
                    }
                    else Walk(element, InlineStyle(element, style), state, preformatted);
                    break;
            }
        }
    }

    private static Style InlineStyle(IElement element, Style style)
    {
        var css = element.GetAttribute("style")?.ToLowerInvariant();
        if (css is null) return style;
        if (Regex.IsMatch(css, @"font-weight\s*:\s*(bold|[6-9]00)")) style = style with { Bold = true };
        if (Regex.IsMatch(css, @"font-style\s*:\s*italic")) style = style with { Italic = true };
        if (Regex.IsMatch(css, @"text-decoration[^;]*underline")) style = style with { Underline = true };
        return style;
    }

    private static void AddText(State state, string data, Style style, bool preformatted)
    {
        var text = data.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!preformatted)
        {
            text = Regex.Replace(text, @"[ \t\n\f]+", " ");
            // Leading space at the start of a block means nothing.
            if (state.Runs.Count == 0 || Texts.Of(state.Runs).EndsWith('\n') || Texts.Of(state.Runs).EndsWith(' ')) text = text.TrimStart(' ');
        }
        else state.PreformattedParagraph = true;
        if (text.Length == 0) return;
        state.Runs.Add(new TextRun(text, style.Bold, style.Italic, style.Underline, style.Link));
    }

    private static void FlushParagraph(State state, int? heading = null)
    {
        var runs = state.Runs;
        state.Runs = [];
        state.PendingItem = false;
        var preformatted = state.PreformattedParagraph;
        state.PreformattedParagraph = false;
        // Trim blank edges.
        while (runs.Count > 0 && runs[^1].Text.TrimEnd(' ', '\n').Length == 0) runs.RemoveAt(runs.Count - 1);
        if (runs.Count > 0) runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd(' ', preformatted ? '\n' : '\0') };
        if (runs.Count > 0 && !preformatted) runs[0] = runs[0] with { Text = runs[0].Text.TrimStart(' ') };
        if (runs.Count == 0 || string.IsNullOrWhiteSpace(Texts.Of(runs)))
            return;
        if (heading is { } level) state.Blocks.Add(new HeadingBlock(level, runs.Select(r => r with { Bold = false, Italic = false }).ToList()));
        else if (state.Lists.Count > 0)
            state.Blocks.Add(new ListItemBlock(state.Lists.Peek(), state.Lists.Count - 1, runs));
        else state.Blocks.Add(new ParagraphBlock(runs));
    }

    private static TableBlock ReadTable(IElement table, Style style, State outer)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<TextRun>>>();
        var header = false;
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            // Rows of nested tables belong to those tables.
            if (row.Closest("table") != table) continue;
            var cells = new List<IReadOnlyList<TextRun>>();
            var isHeader = true;
            foreach (var cell in row.Children.Where(c => c.LocalName is "td" or "th"))
            {
                var inner = new State { BaseFolder = outer.BaseFolder };
                Walk(cell, style, inner, false);
                FlushParagraph(inner);
                var runs = new List<TextRun>();
                foreach (var block in inner.Blocks)
                {
                    var blockRuns = Texts.RunsOf(block);
                    if (blockRuns is null) continue;
                    if (runs.Count > 0) runs.Add(new TextRun("\n"));
                    runs.AddRange(blockRuns);
                }
                cells.Add(runs);
                if (cell.LocalName != "th") isHeader = false;
            }
            if (rows.Count == 0) header = cells.Count > 0 && isHeader || (row.Parent as IElement)?.LocalName == "thead";
            rows.Add(cells);
        }
        return new TableBlock(rows, header);
    }

    private static ImageBlock? LoadImage(IElement element, State state)
    {
        var source = element.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(source)) return null;
        var alt = element.GetAttribute("alt") ?? "";
        try
        {
            byte[]? data = null;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var header = source[..comma];
                var payload = source[(comma + 1)..];
                data = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? System.Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
            else if (state.BaseFolder is not null && !source.Contains("://"))
            {
                var path = Path.GetFullPath(Path.Combine(state.BaseFolder, Uri.UnescapeDataString(source.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(path)) data = File.ReadAllBytes(path);
            }
            if (data is null) return null;
            int? width = int.TryParse(element.GetAttribute("width"), out var w) && w > 0 ? w : null;
            int? height = int.TryParse(element.GetAttribute("height"), out var h) && h > 0 ? h : null;
            var image = Pictures.Block(data, alt);
            if (image is null) return null;
            if (width is not null && height is null) height = (int)Math.Max(1, image.Height * (double)width.Value / Math.Max(image.Width, 1));
            if (height is not null && width is null) width = (int)Math.Max(1, image.Width * (double)height.Value / Math.Max(image.Height, 1));
            return image with { Width = width ?? image.Width, Height = height ?? image.Height };
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
