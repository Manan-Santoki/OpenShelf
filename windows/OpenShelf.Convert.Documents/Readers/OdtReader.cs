using System.IO.Compression;
using System.Xml.Linq;

namespace OpenShelf.Convert.Documents;

/// <summary>Reads OpenDocument text (content.xml) into the structure model: headings, paragraphs, lists, tables, spans with
/// bold, italic and underline, links and pictures.</summary>
public static class OdtReader
{
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    private sealed record Format(bool Bold, bool Italic, bool Underline);

    private sealed class Context
    {
        public ZipArchive Zip = null!;
        public Dictionary<string, Format> TextStyles = new();
        public Dictionary<string, Format> ParagraphStyles = new();
        public HashSet<string> OrderedLists = [];
    }

    public static DocumentModel Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var content = Zips.ReadEntry(zip, "content.xml") ?? throw new InvalidDataException("no content.xml");
            var document = XDocument.Load(new MemoryStream(content));
            var context = new Context { Zip = zip };
            LoadStyles(document, context);
            if (Zips.ReadEntry(zip, "styles.xml") is { } styles) LoadStyles(XDocument.Load(new MemoryStream(styles)), context);
            var blocks = new List<Block>();
            var body = document.Root?.Element(Office + "body")?.Element(Office + "text");
            if (body is not null) ReadBlocks(body, context, blocks, 0, false);
            return new DocumentModel(Path.GetFileNameWithoutExtension(path), DocumentStructure.Normalize(blocks));
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    private static void LoadStyles(XDocument document, Context context)
    {
        foreach (var style in document.Descendants(Style + "style"))
        {
            var name = (string?)style.Attribute(Style + "name");
            var properties = style.Element(Style + "text-properties");
            if (name is null || properties is null) continue;
            var format = new Format(
                ((string?)properties.Attribute(Fo + "font-weight") ?? "") is "bold" or "700" or "800" or "900",
                ((string?)properties.Attribute(Fo + "font-style") ?? "") is "italic" or "oblique",
                (string?)properties.Attribute(Style + "text-underline-style") is { } u && u != "none");
            var family = (string?)style.Attribute(Style + "family");
            if (family == "text") context.TextStyles[name] = format;
            else if (family == "paragraph") context.ParagraphStyles[name] = format;
        }
        foreach (var list in document.Descendants(Text + "list-style"))
        {
            var name = (string?)list.Attribute(Style + "name");
            if (name is not null && list.Elements().FirstOrDefault()?.Name == Text + "list-level-style-number") context.OrderedLists.Add(name);
        }
    }

    private static void ReadBlocks(XElement container, Context context, List<Block> blocks, int level, bool ordered, string? listStyle = null)
    {
        foreach (var element in container.Elements())
        {
            var name = element.Name;
            if (name == Text + "h")
            {
                var runs = ReadRuns(element, context, new Format(false, false, false), null, blocks);
                var outline = int.TryParse((string?)element.Attribute(Text + "outline-level"), out var n) ? n : 1;
                if (runs.Count > 0) blocks.Add(new HeadingBlock(Math.Clamp(outline, 1, 6), runs.Select(r => r with { Bold = false, Italic = false }).ToList()));
            }
            else if (name == Text + "p")
            {
                var style = (string?)element.Attribute(Text + "style-name");
                var baseFormat = style is not null && context.ParagraphStyles.TryGetValue(style, out var f) ? f : new Format(false, false, false);
                var runs = ReadRuns(element, context, baseFormat, null, blocks);
                if (runs.Count == 0) continue;
                if (listStyle is not null || level > 0) blocks.Add(new ListItemBlock(ordered, Math.Max(level - 1, 0), runs));
                else blocks.Add(new ParagraphBlock(runs));
            }
            else if (name == Text + "list")
            {
                var style = (string?)element.Attribute(Text + "style-name") ?? listStyle;
                var isOrdered = style is not null && context.OrderedLists.Contains(style);
                foreach (var item in element.Elements(Text + "list-item").Concat(element.Elements(Text + "list-header")))
                    ReadBlocks(item, context, blocks, level + 1, isOrdered, style ?? "");
            }
            else if (name == Table + "table")
            {
                blocks.Add(ReadTable(element, context));
            }
            else if (name == Text + "section" || name == Text + "index-body" || name == Text + "table-of-content" || name == Text + "index-title")
            {
                ReadBlocks(element, context, blocks, level, ordered, listStyle);
            }
        }
    }

    private static TableBlock ReadTable(XElement table, Context context)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<TextRun>>>();
        var header = table.Element(Table + "table-header-rows") is not null;
        foreach (var row in table.Descendants(Table + "table-row"))
        {
            if (row.Ancestors(Table + "table").First() != table) continue;
            var cells = new List<IReadOnlyList<TextRun>>();
            foreach (var cell in row.Elements(Table + "table-cell"))
            {
                var inner = new List<Block>();
                ReadBlocks(cell, context, inner, 0, false);
                var runs = new List<TextRun>();
                foreach (var block in inner)
                {
                    var blockRuns = Texts.RunsOf(block);
                    if (blockRuns is null) continue;
                    if (runs.Count > 0) runs.Add(new TextRun("\n"));
                    runs.AddRange(blockRuns);
                }
                cells.Add(runs);
            }
            rows.Add(cells);
        }
        return new TableBlock(rows, header);
    }

    private static List<TextRun> ReadRuns(XElement element, Context context, Format format, string? link, List<Block> blocks)
    {
        var runs = new List<TextRun>();
        Collect(element, format, link);
        // Merge neighbours with the same styling.
        var merged = new List<TextRun>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1] is { } last && last.Bold == run.Bold && last.Italic == run.Italic && last.Underline == run.Underline && last.Link == run.Link)
                merged[^1] = last with { Text = last.Text + run.Text };
            else merged.Add(run);
        }
        return merged.Count == 1 && string.IsNullOrWhiteSpace(merged[0].Text) ? [] : merged;

        void Add(string text, Format f, string? l)
        {
            if (text.Length > 0) runs.Add(new TextRun(text, f.Bold, f.Italic, f.Underline, l));
        }

        void Collect(XElement parent, Format f, string? l)
        {
            foreach (var node in parent.Nodes())
            {
                if (node is XText text) { Add(text.Value, f, l); continue; }
                if (node is not XElement child) continue;
                var name = child.Name;
                if (name == Text + "span")
                {
                    var style = (string?)child.Attribute(Text + "style-name");
                    var inner = style is not null && context.TextStyles.TryGetValue(style, out var s)
                        ? new Format(f.Bold || s.Bold, f.Italic || s.Italic, f.Underline || s.Underline) : f;
                    Collect(child, inner, l);
                }
                else if (name == Text + "a") Collect(child, f, (string?)child.Attribute(XLink + "href") ?? l);
                else if (name == Text + "s") Add(new string(' ', int.TryParse((string?)child.Attribute(Text + "c"), out var count) ? count : 1), f, l);
                else if (name == Text + "tab") Add("\t", f, l);
                else if (name == Text + "line-break") Add("\n", f, l);
                else if (name == Draw + "frame")
                {
                    var image = child.Descendants(Draw + "image").FirstOrDefault();
                    var href = (string?)image?.Attribute(XLink + "href");
                    if (href is null || Zips.ReadEntry(context.Zip, href) is not { } data) continue;
                    int? width = Pixels((string?)child.Attribute(Svg + "width")), height = Pixels((string?)child.Attribute(Svg + "height"));
                    if (Pictures.Block(data, "", width, height) is { } picture) blocks.Add(picture);
                }
                else if (name == Text + "note" || name == Text + "bookmark" || name == Text + "bookmark-start") continue;
                else Collect(child, f, l);
            }
        }
    }

    private static int? Pixels(string? length)
    {
        if (length is null) return null;
        var digits = new string(length.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        var unit = length[digits.Length..];
        return unit switch
        {
            "in" => (int)(value * 96),
            "cm" => (int)(value * 96 / 2.54),
            "mm" => (int)(value * 96 / 25.4),
            "pt" => (int)(value * 96 / 72),
            "px" => (int)value,
            _ => null,
        };
    }
}
