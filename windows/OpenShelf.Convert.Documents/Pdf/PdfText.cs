using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OpenShelf.Convert.Documents;

/// <summary>One line of a PDF page's text, with the look of its first word.</summary>
public sealed record PdfLine(string Text, string RowText, double FontSize, bool Bold, double Top, double Bottom);

/// <summary>A page's text lines in reading order. An empty line stands for a paragraph gap.</summary>
public sealed record PdfPageText(IReadOnlyList<PdfLine> Lines)
{
    public string Text => string.Join("\n", Lines.Select(line => line.Text));
    public bool IsBlank => Lines.All(line => string.IsNullOrWhiteSpace(line.Text));
}

/// <summary>Text from PDF pages (PdfPig) and the Mac's PDFText rules for turning it into paragraphs and table rows.</summary>
public static partial class PdfText
{
    /// <summary>The text of every page. Throws the "password-protected" failure for locked files.</summary>
    public static List<PdfPageText> Pages(string path, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var pages = new List<PdfPageText>();
            var count = Math.Max(document.NumberOfPages, 1);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(PageText(document.GetPage(number)));
                progress?.Report((double)number / count);
            }
            return pages;
        }
        catch (Exception e) when (e is not OperationCanceledException and not ConversionException)
        {
            if (IsPasswordProblem(e)) throw PasswordProtected(path);
            throw ConversionException.Unreadable(path);
        }
    }

    public static bool IsPasswordProblem(Exception e) =>
        e.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

    public static ConversionException PasswordProtected(string path) =>
        ConversionException.Failed($"“{Path.GetFileName(path)}” is password-protected. Remove the password first.");

    public static ConversionException NoText(string path) =>
        ConversionException.Failed($"“{Path.GetFileName(path)}” has no text to extract (it looks like a scan).");

    public static PdfPageText PageText(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return new PdfPageText([]);
        // Top to bottom, then cluster words whose middles are within half a line of each other.
        var ordered = words.OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2).ToList();
        var rows = new List<List<Word>>();
        foreach (var word in ordered)
        {
            var middle = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row is not null)
            {
                var rowMiddle = row.Average(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2);
                var tolerance = Math.Max(row.Max(w => w.BoundingBox.Height), word.BoundingBox.Height) * 0.5;
                if (Math.Abs(rowMiddle - middle) <= tolerance) { row.Add(word); continue; }
            }
            rows.Add([word]);
        }

        var lines = new List<PdfLine>();
        PdfLine? previous = null;
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(w => w.BoundingBox.Left).ToList();
            var size = SizeOf(sorted[0]);
            var text = new StringBuilder();
            var rowText = new StringBuilder();
            for (var i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    var gap = sorted[i].BoundingBox.Left - sorted[i - 1].BoundingBox.Right;
                    text.Append(' ');
                    rowText.Append(gap > size * 1.2 ? '\t' : ' ');
                }
                text.Append(sorted[i].Text);
                rowText.Append(sorted[i].Text);
            }
            var top = row.Max(w => w.BoundingBox.Top);
            var bottom = row.Min(w => w.BoundingBox.Bottom);
            var bold = sorted.All(w => w.Letters.All(l => l.FontDetails?.IsBold == true || (l.FontName ?? "").Contains("Bold", StringComparison.OrdinalIgnoreCase)));
            // A gap clearly taller than the leading is a paragraph break.
            if (previous is not null && previous.Bottom - top > size * 0.6)
                lines.Add(new PdfLine("", "", size, false, previous.Bottom, top));
            previous = new PdfLine(text.ToString(), rowText.ToString(), size, bold, top, bottom);
            lines.Add(previous);
        }
        return new PdfPageText(lines);
    }

    private static double SizeOf(Word word) =>
        word.Letters.Count == 0 ? word.BoundingBox.Height : Math.Round(word.Letters.Max(l => l.PointSize), 1);

    // ----- The Mac's rules -----

    /// <summary>Joins wrapped lines into paragraphs. A line much shorter than the page's longest line usually ends a paragraph;
    /// blank lines always do. Pages of short lines (lists, records, verse) keep every line apart.</summary>
    public static List<string> Paragraphs(string pageText) =>
        Group(Lines(pageText).Select(line => (line, (PdfLine?)null)).ToList()).Select(p => p.Text).ToList();

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.Trim(' ', '\t')).ToList();

    /// <summary>Paragraphs of a page's lines, each with the line it started with.</summary>
    public static List<(string Text, PdfLine? First, int LineCount)> Group(List<(string Line, PdfLine? Info)> lines)
    {
        var longest = lines.Select(l => l.Line.Length).DefaultIfEmpty(0).Max();
        var result = new List<(string, PdfLine?, int)>();
        if (longest < 60)
        {
            foreach (var (line, info) in lines)
                if (line.Length > 0) result.Add((line, info, 1));
            return result;
        }
        var current = "";
        PdfLine? first = null;
        var count = 0;
        foreach (var (line, info) in lines)
        {
            if (line.Length == 0)
            {
                if (current.Length > 0) result.Add((current, first, count));
                current = ""; first = null; count = 0;
                continue;
            }
            if (current.Length == 0) { current = line; first = info; count = 1; }
            // Re-join a word hyphenated across lines.
            else if (current.EndsWith('-')) { current = current[..^1] + line; count++; }
            else { current += " " + line; count++; }
            if (line.Length < longest * 0.6)
            {
                result.Add((current, first, count));
                current = ""; first = null; count = 0;
            }
        }
        if (current.Length > 0) result.Add((current, first, count));
        return result;
    }

    [GeneratedRegex(@"\t| {2,}")] private static partial Regex CellSplit();

    /// <summary>One row per line, cells split at tabs or runs of two or more spaces.</summary>
    public static List<List<string>> Rows(string pageText) =>
        pageText.Replace("\r\n", "\n").Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => CellSplit().Split(line.Trim()).Select(cell => cell.Trim()).Where(cell => cell.Length > 0).ToList())
            .ToList();

    /// <summary>Rows from a page's lines, with cells split where the gap between words is wide.</summary>
    public static List<List<string>> Rows(PdfPageText page) =>
        page.Lines.Where(line => !string.IsNullOrWhiteSpace(line.RowText))
            .Select(line => CellSplit().Split(line.RowText.Trim()).Select(cell => cell.Trim()).Where(cell => cell.Length > 0).ToList()).ToList();

    // ----- Structure -----

    /// <summary>Headings, lists and paragraphs of a page's text. A single short line set noticeably larger than the body text is a
    /// heading (DocumentStructure.headingLevel); typed bullets and numbers make list items.</summary>
    public static List<Block> Blocks(PdfPageText page, double? bodySize)
    {
        var blocks = new List<Block>();
        foreach (var (text, first, lineCount) in Group(page.Lines.Select(line => (line.Text.Trim(), (PdfLine?)line)).ToList()))
        {
            if (DocumentStructure.ListMarkerKind(text) is { } ordered)
            {
                blocks.Add(new ListItemBlock(ordered, 0, DocumentStructure.StripListMarker([new TextRun(text)])));
                continue;
            }
            if (lineCount == 1 && first is not null && bodySize is { } body &&
                DocumentStructure.HeadingLevel(first.FontSize, body, first.Bold, text.Length) is { } level)
                blocks.Add(new HeadingBlock(level, [new TextRun(text)]));
            else
                blocks.Add(new ParagraphBlock([new TextRun(text)]));
        }
        return blocks;
    }

    /// <summary>The size most characters are set in.</summary>
    public static double? BodySize(IEnumerable<PdfPageText> pages)
    {
        var weights = new Dictionary<double, int>();
        foreach (var line in pages.SelectMany(p => p.Lines))
            if (line.Text.Length > 0) weights[line.FontSize] = weights.GetValueOrDefault(line.FontSize) + line.Text.Length;
        return weights.Count == 0 ? null : weights.MaxBy(pair => pair.Value).Key;
    }
}
