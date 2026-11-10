using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert.Documents;

/// <summary>Headings, lists and tables from rich text, and Markdown, XHTML and plain text from them. Port of DocumentStructure.swift.</summary>
public static partial class DocumentStructure
{
    // ----- Reading -----

    /// <summary>Applies the Mac's recognisers to what a reader produced: a heading is a short paragraph set noticeably larger than the
    /// body text (or a short all-bold line a touch larger), a list item is a paragraph that starts with a typed marker.
    /// Blocks that readers already classified (styles, tags) are left alone.</summary>
    public static IReadOnlyList<Block> Normalize(IReadOnlyList<Block> blocks)
    {
        var body = MostCommonSize(blocks);
        var result = new List<Block>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block is not ParagraphBlock paragraph) { result.Add(block); continue; }
            var runs = paragraph.Runs.ToList();
            var plain = Texts.Of(runs);
            if (string.IsNullOrWhiteSpace(plain)) continue;
            if (ListMarkerKind(plain) is { } ordered)
            {
                result.Add(new ListItemBlock(ordered, 0, StripListMarker(runs)));
                continue;
            }
            var size = runs.Select(run => run.Size).FirstOrDefault(s => s is not null) ?? body;
            if (body is not null && size is not null && !plain.Contains('\n') &&
                HeadingLevel(size.Value, body.Value, runs.All(run => run.Bold || string.IsNullOrWhiteSpace(run.Text)), plain.Length) is { } level)
                result.Add(new HeadingBlock(level, runs.Select(run => new TextRun(run.Text, Link: run.Link)).ToList()));
            else
                result.Add(paragraph);
        }
        return result;
    }

    private static double? MostCommonSize(IReadOnlyList<Block> blocks)
    {
        var weights = new Dictionary<double, int>();
        foreach (var block in blocks)
            if (block is ParagraphBlock p)
                foreach (var run in p.Runs)
                    if (run.Size is { } size) weights[size] = weights.GetValueOrDefault(size) + run.Text.Length;
        return weights.Count == 0 ? null : weights.MaxBy(pair => pair.Value).Key;
    }

    public static int? HeadingLevel(double size, double bodySize, bool allBold, int length)
    {
        if (length > 200) return null;
        var ratio = size / Math.Max(bodySize, 1);
        if (ratio >= 1.6) return 1;
        if (ratio >= 1.3) return 2;
        if (ratio >= 1.1) return 3;
        // A short, all-bold line set a touch larger reads as a minor heading.
        if (allBold && ratio >= 1.05) return 3;
        return null;
    }

    [GeneratedRegex(@"^[•◦▪‣·]\s+")] private static partial Regex BulletMarker();
    [GeneratedRegex(@"^\d+[.)]\s+")] private static partial Regex NumberMarker();
    [GeneratedRegex(@"^\t\d+[.)]?\t")] private static partial Regex TabbedNumber();
    [GeneratedRegex(@"^\t[-–•◦▪‣·]\t")] private static partial Regex TabbedBullet();
    [GeneratedRegex(@"^\s*(?:[•◦▪‣·\-–]|\d+[.)]?|[a-zA-Z][.)])\s+")] private static partial Regex LeadingMarker();

    /// <summary>Whether a paragraph starts with a typed list marker ("•", "1."): true for numbered, false for bullets, null otherwise.</summary>
    public static bool? ListMarkerKind(string text)
    {
        var trimmed = text.Trim(' ', ' ');
        if (BulletMarker().IsMatch(trimmed)) return false;
        if (NumberMarker().IsMatch(trimmed)) return true;
        if (TabbedNumber().IsMatch(text)) return true;
        if (TabbedBullet().IsMatch(text)) return false;
        return null;
    }

    public static IReadOnlyList<TextRun> StripListMarker(List<TextRun> runs)
    {
        if (runs.Count == 0) return runs;
        var first = runs[0];
        runs[0] = first with { Text = LeadingMarker().Replace(first.Text, "", 1).Trim(' ', '\t') };
        return runs;
    }

    /// <summary>Paragraphs of plain text, split at blank lines, keeping line breaks inside them.</summary>
    public static List<string> Paragraphs(string text) =>
        Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n[ \t]*\n")
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

    public static IReadOnlyList<Block> BlocksFromPlainText(string text) =>
        Paragraphs(text).Select(p => (Block)new ParagraphBlock([new TextRun(p)])).ToList();

    // ----- Markdown -----

    public static string Markdown(IReadOnlyList<Block> blocks)
    {
        var output = new StringBuilder();
        Block? previous = null;
        var numbers = new int[10];
        foreach (var block in blocks)
        {
            if (block is PageBreakBlock) continue;
            var continuesList = block is ListItemBlock current && previous is ListItemBlock before && current.Ordered == before.Ordered;
            // Blocks are separated by a blank line; items of one list sit on consecutive lines.
            if (previous is not null) output.Append(continuesList ? "\n" : "\n\n");
            switch (block)
            {
                case HeadingBlock h:
                    output.Append(new string('#', Math.Clamp(h.Level, 1, 6))).Append(' ').Append(Markdown(h.Runs));
                    break;
                case ParagraphBlock p:
                    output.Append(Markdown(p.Runs));
                    break;
                case ListItemBlock item:
                    var level = Math.Clamp(item.Level, 0, 8);
                    if (!continuesList) Array.Clear(numbers);
                    numbers[level]++;
                    for (var deeper = level + 1; deeper < numbers.Length; deeper++) numbers[deeper] = 0;
                    output.Append(new string(' ', level * 3)).Append(item.Ordered ? $"{numbers[level]}. " : "- ").Append(Markdown(item.Runs));
                    break;
                case TableBlock table:
                    output.Append(MarkdownTable(table));
                    break;
                case ImageBlock image:
                    output.Append("![").Append(EscapeMarkdown(image.Alt)).Append("]()");
                    break;
            }
            previous = block;
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    private static string MarkdownTable(TableBlock table)
    {
        var rows = table.Rows.Select(row => row.Select(cell => Markdown(cell).Replace("\n", " ").Replace("|", "\\|").Trim()).ToList()).ToList();
        if (rows.Count == 0) return "";
        var width = rows.Max(row => row.Count);
        foreach (var row in rows) while (row.Count < width) row.Add("");
        var lines = new List<string> { "| " + string.Join(" | ", rows[0]) + " |", "|" + string.Concat(Enumerable.Repeat(" --- |", width)) };
        lines.AddRange(rows.Skip(1).Select(row => "| " + string.Join(" | ", row) + " |"));
        return string.Join("\n", lines);
    }

    public static string Markdown(IEnumerable<TextRun> runs)
    {
        var output = new StringBuilder();
        foreach (var run in runs)
        {
            // Emphasis markers must hug the text, so keep spaces outside them.
            var core = run.Text.Trim(' ', '\t');
            if (core.Length == 0) { output.Append(run.Text); continue; }
            var leading = run.Text[..(run.Text.Length - run.Text.TrimStart(' ', '\t').Length)];
            var trailing = run.Text[run.Text.TrimEnd(' ', '\t').Length..];
            var text = EscapeMarkdown(core);
            if (run.Bold && run.Italic) text = $"***{text}***";
            else if (run.Bold) text = $"**{text}**";
            else if (run.Italic) text = $"*{text}*";
            if (run.Link is not null) text = $"[{text}]({run.Link})";
            output.Append(leading).Append(text).Append(trailing);
        }
        return output.ToString();
    }

    private static string EscapeMarkdown(string text)
    {
        var builder = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if ("\\`*_[]".Contains(c)) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ----- XHTML -----

    /// <summary>The body contents of an XHTML document for <paramref name="blocks"/>. <paramref name="imageSource"/> gives each picture's src.</summary>
    public static string Xhtml(IReadOnlyList<Block> blocks, Func<ImageBlock, string>? imageSource = null)
    {
        var html = new List<string>();
        var openLists = new Stack<bool>();
        var openItem = new bool[12];
        // Nested lists sit inside the item they belong to, so an item stays open until the next one at its level.
        void CloseLists(int keep = 0)
        {
            while (openLists.Count > keep)
            {
                if (openItem[openLists.Count]) html[^1] += "</li>";
                openItem[openLists.Count] = false;
                html.Add(openLists.Pop() ? "</ol>" : "</ul>");
            }
        }
        foreach (var block in blocks)
        {
            if (block is not ListItemBlock) CloseLists();
            switch (block)
            {
                case HeadingBlock h:
                    var level = Math.Clamp(h.Level, 1, 6);
                    html.Add($"<h{level}>{Xhtml(h.Runs)}</h{level}>");
                    break;
                case ParagraphBlock p:
                    html.Add($"<p>{Xhtml(p.Runs).Replace("\n", "<br/>")}</p>");
                    break;
                case ListItemBlock item:
                    var depth = Math.Clamp(item.Level, 0, 8) + 1;
                    CloseLists(depth);
                    if (openLists.Count == depth && openLists.Peek() != item.Ordered) CloseLists(depth - 1);
                    if (openLists.Count == depth && openItem[depth])
                    {
                        html[^1] += "</li>";
                        openItem[depth] = false;
                    }
                    while (openLists.Count < depth)
                    {
                        openLists.Push(item.Ordered);
                        html.Add(item.Ordered ? "<ol>" : "<ul>");
                    }
                    html.Add($"<li>{Xhtml(item.Runs).Replace("\n", "<br/>")}");
                    openItem[depth] = true;
                    break;
                case TableBlock table:
                    html.Add(XhtmlTable(table));
                    break;
                case ImageBlock image:
                    var src = imageSource?.Invoke(image) ?? DataUri(image);
                    html.Add($"<p class=\"img\"><img src=\"{Texts.Escape(src)}\" alt=\"{Texts.Escape(image.Alt)}\" width=\"{image.Width}\" height=\"{image.Height}\"/></p>");
                    break;
                case PageBreakBlock:
                    html.Add("<div class=\"pagebreak\" style=\"page-break-after: always; break-after: page\"></div>");
                    break;
            }
        }
        CloseLists();
        return string.Join("\n", html);
    }

    public static string DataUri(ImageBlock image) => $"data:{image.MediaType};base64,{System.Convert.ToBase64String(image.Data)}";

    private static string XhtmlTable(TableBlock table)
    {
        var builder = new StringBuilder("<table>\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            builder.Append("<tr>");
            var tag = table.HasHeader && r == 0 ? "th" : "td";
            foreach (var cell in table.Rows[r]) builder.Append($"<{tag}>{Xhtml(cell).Replace("\n", "<br/>")}</{tag}>");
            builder.Append("</tr>\n");
        }
        return builder.Append("</table>").ToString();
    }

    public static string Xhtml(IEnumerable<TextRun> runs)
    {
        var output = new StringBuilder();
        foreach (var run in runs)
        {
            var text = Texts.Escape(run.Text);
            if (run.Underline) text = $"<u>{text}</u>";
            if (run.Italic) text = $"<em>{text}</em>";
            if (run.Bold) text = $"<strong>{text}</strong>";
            if (run.Link is not null) text = $"<a href=\"{Texts.Escape(run.Link)}\">{text}</a>";
            output.Append(text);
        }
        return output.ToString();
    }

    // ----- Plain text -----

    public static string PlainText(IReadOnlyList<Block> blocks)
    {
        var text = new StringBuilder();
        Block? last = null;
        var number = 0;
        foreach (var block in blocks)
        {
            string part;
            switch (block)
            {
                case HeadingBlock h: part = Texts.Of(h.Runs); break;
                case ParagraphBlock p: part = Texts.Of(p.Runs); break;
                case ListItemBlock item:
                    number = last is ListItemBlock before && before.Ordered == item.Ordered ? number + 1 : 1;
                    part = new string(' ', item.Level * 2) + (item.Ordered ? $"{number}. " : "• ") + Texts.Of(item.Runs);
                    break;
                case TableBlock table:
                    part = string.Join("\n", table.Rows.Select(row => string.Join("\t", row.Select(cell => Texts.Of(cell).Replace("\n", " ")))));
                    break;
                default: continue;
            }
            // Items of one list sit on consecutive lines, everything else is a paragraph apart.
            if (last is not null) text.Append(block is ListItemBlock && last is ListItemBlock ? "\n" : "\n\n");
            text.Append(part);
            last = block;
        }
        return text.Append('\n').ToString();
    }
}
