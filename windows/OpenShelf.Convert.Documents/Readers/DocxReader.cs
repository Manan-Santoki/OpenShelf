using System.IO.Compression;
using System.Xml.Linq;

namespace OpenShelf.Convert.Documents;

/// <summary>Reads WordprocessingML into the structure model: paragraph styles (headings, lists), runs with bold, italic and
/// underline, numbered and bulleted lists, tables, hyperlinks and pictures.</summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private sealed record StyleInfo(string Name, string? BasedOn, XElement? Paragraph, XElement? Run);

    private sealed class Context
    {
        public Dictionary<string, StyleInfo> Styles = new();
        public Dictionary<string, string> Relationships = new();
        public Dictionary<string, string> RelationshipTargets = new();
        public Dictionary<string, string> NumToAbstract = new();
        public Dictionary<(string Abstract, int Level), string> LevelFormats = new();
        public ZipArchive Zip = null!;
    }

    public static DocumentModel Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Read(zip, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    public static DocumentModel Read(ZipArchive zip, string title)
    {
        var context = new Context { Zip = zip };
        var main = Zips.ReadEntry(zip, "word/document.xml") ?? throw new InvalidDataException("no document.xml");
        LoadRelationships(context);
        LoadStyles(context);
        LoadNumbering(context);

        var document = XDocument.Load(new MemoryStream(main));
        var blocks = new List<Block>();
        var body = document.Root?.Element(W + "body");
        if (body is not null) ReadBody(body, context, blocks);
        return new DocumentModel(title, DocumentStructure.Normalize(blocks));
    }

    // ----- Parts -----

    private static void LoadRelationships(Context context)
    {
        var data = Zips.ReadEntry(context.Zip, "word/_rels/document.xml.rels");
        if (data is null) return;
        foreach (var relationship in XDocument.Load(new MemoryStream(data)).Descendants(Rel + "Relationship"))
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id is null || target is null) continue;
            context.Relationships[id] = target;
            if ((string?)relationship.Attribute("TargetMode") == "External") context.RelationshipTargets[id] = target;
        }
    }

    private static void LoadStyles(Context context)
    {
        var data = Zips.ReadEntry(context.Zip, "word/styles.xml");
        if (data is null) return;
        foreach (var style in XDocument.Load(new MemoryStream(data)).Descendants(W + "style"))
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id is null) continue;
            context.Styles[id] = new StyleInfo(
                (string?)style.Element(W + "name")?.Attribute(W + "val") ?? id,
                (string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                style.Element(W + "pPr"), style.Element(W + "rPr"));
        }
    }

    private static void LoadNumbering(Context context)
    {
        var data = Zips.ReadEntry(context.Zip, "word/numbering.xml");
        if (data is null) return;
        var root = XDocument.Load(new MemoryStream(data)).Root;
        if (root is null) return;
        foreach (var num in root.Elements(W + "num"))
        {
            var id = (string?)num.Attribute(W + "numId");
            var abstractId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
            if (id is not null && abstractId is not null) context.NumToAbstract[id] = abstractId;
        }
        foreach (var abstractNum in root.Elements(W + "abstractNum"))
        {
            var id = (string?)abstractNum.Attribute(W + "abstractNumId");
            if (id is null) continue;
            foreach (var level in abstractNum.Elements(W + "lvl"))
                if (int.TryParse((string?)level.Attribute(W + "ilvl"), out var index))
                    context.LevelFormats[(id, index)] = (string?)level.Element(W + "numFmt")?.Attribute(W + "val") ?? "decimal";
        }
    }

    // ----- Body -----

    private static void ReadBody(XElement container, Context context, List<Block> blocks)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == W + "p") ReadParagraph(element, context, blocks);
            else if (element.Name == W + "tbl") blocks.Add(ReadTable(element, context));
            else if (element.Name == W + "sdt") { if (element.Element(W + "sdtContent") is { } content) ReadBody(content, context, blocks); }
            else if (element.Name == W + "ins" || element.Name == W + "customXml") ReadBody(element, context, blocks);
        }
    }

    private static TableBlock ReadTable(XElement table, Context context)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<TextRun>>>();
        var header = false;
        foreach (var row in table.Elements(W + "tr"))
        {
            var cells = new List<IReadOnlyList<TextRun>>();
            foreach (var cell in row.Elements(W + "tc"))
            {
                var inner = new List<Block>();
                ReadBody(cell, context, inner);
                var runs = new List<TextRun>();
                foreach (var block in inner)
                {
                    var blockRuns = Texts.RunsOf(block);
                    if (blockRuns is null) continue;
                    if (runs.Count > 0) runs.Add(new TextRun("\n"));
                    runs.AddRange(blockRuns);
                }
                cells.Add(runs.Select(run => run with { Size = null }).ToList());
            }
            if (rows.Count == 0)
                header = row.Element(W + "trPr")?.Element(W + "tblHeader") is not null ||
                    (cells.Count > 0 && cells.All(cell => cell.Count > 0 && cell.All(run => run.Bold || string.IsNullOrWhiteSpace(run.Text))));
            rows.Add(cells);
        }
        // A header row is bold because it is a header; the table already says so.
        if (header && rows.Count > 0)
            rows[0] = rows[0].Select(cell => (IReadOnlyList<TextRun>)cell.Select(run => run with { Bold = false }).ToList()).ToList();
        return new TableBlock(rows, header);
    }

    private sealed class ParagraphState
    {
        public List<TextRun> Runs = [];
        public string? FieldLink;
        public bool InField;
        public bool InFieldResult;
        public string Instruction = "";
    }

    private static void ReadParagraph(XElement paragraph, Context context, List<Block> blocks)
    {
        var properties = paragraph.Element(W + "pPr");
        var styleId = (string?)properties?.Element(W + "pStyle")?.Attribute(W + "val");
        var heading = HeadingLevel(styleId, context);
        var (numId, level) = NumberingOf(properties, styleId, context);

        var state = new ParagraphState();
        void Flush()
        {
            if (state.Runs.Count == 0) return;
            // List items and headings keep their identity; everything else is a paragraph.
            var runs = Merge(state.Runs);
            state.Runs = [];
            if (runs.Count == 0) return;
            if (heading is { } h) blocks.Add(new HeadingBlock(h, runs.Select(r => r with { Bold = false, Italic = false, Size = null }).ToList()));
            else if (numId is not null && numId != "0") blocks.Add(new ListItemBlock(IsOrdered(numId, level, context), level, runs.Select(r => r with { Size = null }).ToList()));
            else blocks.Add(new ParagraphBlock(runs));
        }
        void Emit(Block block)
        {
            Flush();
            blocks.Add(block);
        }

        var paragraphRunStyle = ResolveRun(styleId, context);
        foreach (var child in paragraph.Elements())
            ReadInline(child, context, state, paragraphRunStyle, null, Emit);
        Flush();
    }

    private static void ReadInline(XElement element, Context context, ParagraphState state, RunFormat inherited, string? link, Action<Block> emit)
    {
        if (element.Name == W + "r") ReadRun(element, context, state, inherited, link, emit);
        else if (element.Name == W + "hyperlink")
        {
            var target = (string?)element.Attribute(R + "id") is { } id && context.RelationshipTargets.TryGetValue(id, out var url) ? url : link;
            foreach (var child in element.Elements()) ReadInline(child, context, state, inherited, target, emit);
        }
        else if (element.Name == W + "fldSimple")
        {
            var instruction = (string?)element.Attribute(W + "instr") ?? "";
            var target = HyperlinkIn(instruction) ?? link;
            foreach (var child in element.Elements()) ReadInline(child, context, state, inherited, target, emit);
        }
        else if (element.Name == W + "ins" || element.Name == W + "smartTag" || element.Name == W + "sdtContent" || element.Name == W + "sdt")
        {
            foreach (var child in element.Elements()) ReadInline(child, context, state, inherited, link, emit);
        }
    }

    private static string? HyperlinkIn(string instruction)
    {
        var trimmed = instruction.Trim();
        if (!trimmed.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
        var start = trimmed.IndexOf('"');
        var end = start >= 0 ? trimmed.IndexOf('"', start + 1) : -1;
        return start >= 0 && end > start ? trimmed[(start + 1)..end] : null;
    }

    private readonly record struct RunFormat(bool Bold, bool Italic, bool Underline, double? Size);

    private static RunFormat ResolveRun(string? styleId, Context context)
    {
        var format = new RunFormat(false, false, false, null);
        var chain = new List<StyleInfo>();
        for (var current = styleId; current is not null && context.Styles.TryGetValue(current, out var style) && chain.Count < 10; current = style.BasedOn)
            chain.Add(style);
        chain.Reverse();
        foreach (var style in chain) format = Apply(style.Run, format);
        return format;
    }

    private static RunFormat Apply(XElement? properties, RunFormat format)
    {
        if (properties is null) return format;
        bool? Toggle(string name) =>
            properties.Element(W + name) is { } e ? !((string?)e.Attribute(W + "val") is "0" or "false" or "none") : null;
        var size = properties.Element(W + "sz") is { } sz && double.TryParse((string?)sz.Attribute(W + "val"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var halfPoints) ? halfPoints / 2 : format.Size;
        return new RunFormat(Toggle("b") ?? format.Bold, Toggle("i") ?? format.Italic, Toggle("u") ?? format.Underline, size);
    }

    private static void ReadRun(XElement run, Context context, ParagraphState state, RunFormat inherited, string? link, Action<Block> emit)
    {
        var runStyle = (string?)run.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val");
        var format = Apply(run.Element(W + "rPr"), Apply(context.Styles.TryGetValue(runStyle ?? "", out var rs) ? rs.Run : null, inherited));
        var text = new System.Text.StringBuilder();
        void EmitText()
        {
            if (text.Length == 0) return;
            if (state.InField && !state.InFieldResult) { text.Clear(); return; }
            var target = state.FieldLink ?? link;
            // Links are underlined by their style; that is the link, not emphasis.
            state.Runs.Add(new TextRun(text.ToString(), format.Bold, format.Italic, format.Underline && target is null, target, format.Size));
            text.Clear();
        }
        foreach (var child in run.Elements())
        {
            var name = child.Name;
            if (name == W + "t") text.Append((string)child);
            else if (name == W + "tab") text.Append('\t');
            else if (name == W + "noBreakHyphen") text.Append('-');
            else if (name == W + "br" || name == W + "cr")
            {
                if ((string?)child.Attribute(W + "type") == "page")
                {
                    EmitText();
                    emit(new PageBreakBlock());
                }
                else text.Append('\n');
            }
            else if (name == W + "instrText") state.Instruction += (string)child;
            else if (name == W + "fldChar")
            {
                var kind = (string?)child.Attribute(W + "fldCharType");
                if (kind == "begin") { EmitText(); state.InField = true; state.InFieldResult = false; state.Instruction = ""; state.FieldLink = null; }
                else if (kind == "separate") { state.FieldLink = HyperlinkIn(state.Instruction); state.InFieldResult = true; }
                else if (kind == "end") { EmitText(); state.InField = false; state.InFieldResult = false; state.FieldLink = null; }
            }
            else if (name == W + "drawing" || name == W + "pict")
            {
                EmitText();
                var image = ReadImage(child, context);
                if (image is not null) emit(image);
            }
        }
        EmitText();
    }

    private static ImageBlock? ReadImage(XElement drawing, Context context)
    {
        var blip = drawing.Descendants(A + "blip").FirstOrDefault();
        var id = (string?)blip?.Attribute(R + "embed");
        if (id is null)
            id = (string?)drawing.Descendants().FirstOrDefault(e => e.Name.LocalName == "imagedata")?.Attribute(R + "id");
        if (id is null || !context.Relationships.TryGetValue(id, out var target)) return null;
        var part = Combine("word/", target);
        var data = Zips.ReadEntry(context.Zip, part);
        if (data is null) return null;
        var extent = drawing.Descendants(Wp + "extent").FirstOrDefault();
        int? width = null, height = null;
        if (extent is not null && long.TryParse((string?)extent.Attribute("cx"), out var cx) && long.TryParse((string?)extent.Attribute("cy"), out var cy) && cx > 0 && cy > 0)
        {
            width = (int)Math.Max(1, cx / 9525);
            height = (int)Math.Max(1, cy / 9525);
        }
        var alt = (string?)drawing.Descendants(Wp + "docPr").FirstOrDefault()?.Attribute("descr") ?? "";
        return Pictures.Block(data, alt, width, height);
    }

    private static string Combine(string folder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(folder.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (piece != ".") parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    private static List<TextRun> Merge(List<TextRun> runs)
    {
        var merged = new List<TextRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1] is { } last && last.Bold == run.Bold && last.Italic == run.Italic && last.Underline == run.Underline && last.Link == run.Link && last.Size == run.Size)
                merged[^1] = last with { Text = last.Text + run.Text };
            else merged.Add(run);
        }
        return merged;
    }

    // ----- Styles and lists -----

    private static int? HeadingLevel(string? styleId, Context context)
    {
        for (var depth = 0; styleId is not null && depth < 10 && context.Styles.TryGetValue(styleId, out var style); depth++, styleId = style.BasedOn)
        {
            var name = style.Name.ToLowerInvariant().Replace(" ", "");
            if (name.StartsWith("heading") && int.TryParse(name["heading".Length..], out var level)) return Math.Clamp(level, 1, 6);
            if (name is "title") return 1;
            if (name is "subtitle") return 2;
        }
        // Styles missing from styles.xml still carry their usual ids.
        if (styleId is not null && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(styleId["Heading".Length..], out var fallback))
            return Math.Clamp(fallback, 1, 6);
        return null;
    }

    private static (string? NumId, int Level) NumberingOf(XElement? properties, string? styleId, Context context)
    {
        var numPr = properties?.Element(W + "numPr");
        var numId = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val");
        var level = int.TryParse((string?)numPr?.Element(W + "ilvl")?.Attribute(W + "val"), out var direct) ? direct : 0;
        for (var depth = 0; numId is null && styleId is not null && depth < 10 && context.Styles.TryGetValue(styleId, out var style); depth++, styleId = style.BasedOn)
        {
            var stylePr = style.Paragraph?.Element(W + "numPr");
            numId = (string?)stylePr?.Element(W + "numId")?.Attribute(W + "val");
            if (numId is not null && int.TryParse((string?)stylePr?.Element(W + "ilvl")?.Attribute(W + "val"), out var fromStyle)) level = fromStyle;
        }
        return (numId, level);
    }

    private static bool IsOrdered(string numId, int level, Context context)
    {
        if (!context.NumToAbstract.TryGetValue(numId, out var abstractId)) return false;
        if (!context.LevelFormats.TryGetValue((abstractId, level), out var format)) return false;
        return format != "bullet" && format != "none";
    }
}
