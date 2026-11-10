using System.Text;

namespace OpenShelf.Convert.Markdown;

/// <summary>Writes a Markdown document as a Word document with Word's own styles: headings that appear in the navigation pane,
/// real numbered and bulleted lists, tables, footnotes, links and pictures. The package is assembled by hand as
/// WordprocessingML. Port of MarkdownDocx.swift.</summary>
public sealed class MarkdownDocx
{
    private readonly string? _baseFolder;
    private readonly PaperSize _paper;
    private readonly List<string> _documentRelationships = [];
    private readonly List<string> _footnoteRelationships = [];
    private int _nextRelationship = 10;
    private readonly List<string> _numberingInstances = [];
    private int _nextNumberingId = 2;
    private readonly List<string> _footnoteParts = [];
    private readonly List<(string Name, byte[] Data)> _media = [];
    private int _nextDrawingId = 1;
    private int _nextBookmarkId;
    private bool _inFootnote;
    /// <summary>Runs put at the start of the next paragraph: a footnote's own number.</summary>
    private string? _pendingPrefix;

    private const int BulletNumberingId = 1;

    // Usable text width between 1 in margins, in twips and EMU.
    private int TextWidthTwips => _paper == PaperSize.Letter ? 9360 : 9026;
    private int PageWidth => _paper == PaperSize.Letter ? 12240 : 11906;
    private int PageHeight => _paper == PaperSize.Letter ? 15840 : 16838;
    private long TextWidthEmu => TextWidthTwips * 635L;

    private MarkdownDocx(string? baseFolder, PaperSize paper)
    {
        _baseFolder = baseFolder;
        _paper = paper;
    }

    /// <param name="baseFolder">The Markdown file's folder, where relative image paths start.</param>
    public static void Write(MarkdownDocument document, string? baseFolder, string path, string? fallbackTitle = null, PaperSize? paper = null) =>
        new MarkdownDocx(baseFolder, paper ?? PaperSizes.ForCurrentRegion()).WriteDocument(document, path, fallbackTitle ?? Path.GetFileNameWithoutExtension(path));

    private sealed record Context(int Indent = 0, string? Style = null, int ListLevel = -1);

    private void WriteDocument(MarkdownDocument document, string path, string fallbackTitle)
    {
        var body = new List<string>();
        if (document.Title is { } title)
            body.Add($"<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr>{Run(title, new RunFormat())}</w:p>");
        body.AddRange(Blocks(document.Blocks, new Context()));

        _inFootnote = true;
        for (var index = 0; index < document.Footnotes.Count; index++)
        {
            var number = index + 1;
            _pendingPrefix = "<w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> </w:t></w:r>";
            var paragraphs = Blocks(document.Footnotes[index], new Context(Style: "FootnoteText"));
            if (_pendingPrefix is { } prefix)
            {
                paragraphs.Insert(0, $"<w:p><w:pPr><w:pStyle w:val=\"FootnoteText\"/></w:pPr>{prefix}</w:p>");
                _pendingPrefix = null;
            }
            _footnoteParts.Add($"<w:footnote w:id=\"{number}\">{string.Concat(paragraphs)}</w:footnote>");
        }
        _inFootnote = false;

        var hasFootnotes = _footnoteParts.Count > 0;
        try
        {
            using var zip = new PackageZip(path);
            zip.Write("[Content_Types].xml", ContentTypes(hasFootnotes));
            zip.Write("_rels/.rels", PackageRelationships);
            zip.Write("docProps/core.xml", Core(document.Title ?? fallbackTitle));
            zip.Write("word/_rels/document.xml.rels", Relationships(DocumentPartRelationships(hasFootnotes)));
            zip.Write("word/document.xml", DocumentXml(string.Concat(body)));
            zip.Write("word/styles.xml", Styles);
            zip.Write("word/numbering.xml", Numbering(_numberingInstances));
            zip.Write("word/settings.xml", Settings(hasFootnotes));
            if (hasFootnotes)
            {
                zip.Write("word/footnotes.xml", Footnotes(_footnoteParts));
                zip.Write("word/_rels/footnotes.xml.rels", Relationships(_footnoteRelationships));
            }
            foreach (var item in _media) zip.Write($"word/media/{item.Name}", item.Data);
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    // MARK: Blocks

    private List<string> Blocks(List<MarkdownBlock<MarkdownText>> blocks, Context context) => blocks.SelectMany(b => Block(b, context)).ToList();

    private List<string> Block(MarkdownBlock<MarkdownText> block, Context context)
    {
        switch (block)
        {
            case MarkdownBlock<MarkdownText>.Heading h:
            {
                var bookmark = _nextBookmarkId++;
                var name = Xml.Escape(h.Id.Length > 40 ? h.Id[..40] : h.Id);
                var content = $"<w:bookmarkStart w:id=\"{bookmark}\" w:name=\"{name}\"/>{Runs(h.Text, new RunFormat())}<w:bookmarkEnd w:id=\"{bookmark}\"/>";
                return [Paragraph(content, context, style: $"Heading{Math.Min(Math.Max(h.Level, 1), 6)}")];
            }
            case MarkdownBlock<MarkdownText>.Paragraph p:
            {
                if (p.Text.FigureImage() is { } figure)
                {
                    var picture = Image(figure.Source, figure.Alt);
                    var paragraphs = new List<string> { Paragraph(picture, context, style: "Figure") };
                    if (figure.Alt.Count > 0) paragraphs.Add(Paragraph(Runs(figure.Alt, new RunFormat()), context, style: "ImageCaption"));
                    return paragraphs;
                }
                return [Paragraph(Runs(p.Text, new RunFormat()), context)];
            }
            case MarkdownBlock<MarkdownText>.Code c:
                return [Paragraph(Run(c.Source.Replace("\t", "    "), new RunFormat { Style = "VerbatimChar" }), context, style: "SourceCode")];
            case MarkdownBlock<MarkdownText>.Math m:
                return [Paragraph(Run(MarkdownMath.Unicode(m.Tex), new RunFormat { Italic = true, Font = "Cambria Math" }), context, justification: "center")];
            case MarkdownBlock<MarkdownText>.Quote q:
                return Blocks(q.Blocks, context with { Indent = context.Indent + 360, Style = "BlockText" });
            case MarkdownBlock<MarkdownText>.ListBlock l:
                return ListBlock(l.List, context);
            case MarkdownBlock<MarkdownText>.Table t:
                return Table(t.Value, context);
            case MarkdownBlock<MarkdownText>.Definitions d:
            {
                var result = new List<string>();
                foreach (var item in d.Items)
                {
                    result.Add(Paragraph(Runs(item.Term, new RunFormat()), context, style: "DefinitionTerm"));
                    foreach (var definition in item.Definitions)
                        result.AddRange(Blocks(definition, context with { Indent = context.Indent + 720, Style = definition.Count == 1 ? "Compact" : context.Style }));
                }
                return result;
            }
            case MarkdownBlock<MarkdownText>.Lines lines:
                return [Paragraph(string.Join("<w:r><w:br/></w:r>", lines.Items.Select(l => Runs(l, new RunFormat()))), context)];
            default:
                return [Paragraph("", context, style: "HorizontalRule")];
        }
    }

    private List<string> ListBlock(MarkdownList<MarkdownText> list, Context context)
    {
        var level = Math.Min(context.ListLevel + 1, 8);
        int numberingId;
        if (list.Ordered)
        {
            // Each numbered list counts from its own start.
            numberingId = _nextNumberingId++;
            _numberingInstances.Add($"<w:num w:numId=\"{numberingId}\"><w:abstractNumId w:val=\"2\"/><w:lvlOverride w:ilvl=\"{level}\"><w:startOverride w:val=\"{list.Start}\"/></w:lvlOverride></w:num>");
        }
        else numberingId = BulletNumberingId;
        var inside = context with { ListLevel = level, Indent = context.Indent + 360 };
        if (list.Tight) inside = inside with { Style = "Compact" };
        else if (inside.Style == "Compact") inside = inside with { Style = null };

        var result = new List<string>();
        foreach (var item in list.Items)
        {
            var rest = item;
            if (item.Count > 0 && item[0] is MarkdownBlock<MarkdownText>.Paragraph first && first.Text.FigureImage() is null)
            {
                result.Add(Paragraph(Runs(first.Text, new RunFormat()), inside, numbering: (numberingId, level)));
                rest = item.Skip(1).ToList();
            }
            else result.Add(Paragraph("", inside, numbering: (numberingId, level)));
            result.AddRange(Blocks(rest, inside));
        }
        return result;
    }

    private List<string> Table(MarkdownTable<MarkdownText> table, Context context)
    {
        var result = new List<string>();
        if (table.Caption is { } caption) result.Add(Paragraph(Runs(caption, new RunFormat()), context, style: "TableCaption"));
        var columns = Math.Max(table.ColumnCount, 1);
        var available = TextWidthTwips - context.Indent;
        var widths = (table.Widths ?? Enumerable.Repeat(1.0 / columns, columns).ToList()).Select(w => (int)(available * w)).ToList();
        var width = table.Widths is null
            ? "<w:tblW w:w=\"0\" w:type=\"auto\"/><w:jc w:val=\"center\"/>"
            : $"<w:tblW w:w=\"{available}\" w:type=\"dxa\"/>";
        var indent = context.Indent > 0 ? $"<w:tblInd w:w=\"{context.Indent}\" w:type=\"dxa\"/>" : "";
        var xml = new StringBuilder($"<w:tbl><w:tblPr><w:tblStyle w:val=\"Table\"/>{width}{indent}<w:tblLook w:val=\"0020\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"0\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"0\"/></w:tblPr><w:tblGrid>");
        xml.Append(string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))).Append("</w:tblGrid>");

        var rows = new List<(List<MarkdownText> Cells, bool IsHeader)>();
        if (table.Header is { } header && header.Any(c => c.Count > 0)) rows.Add((header, true));
        rows.AddRange(table.Rows.Select(r => (r, false)));
        var cellContext = new Context(Style: "Compact", ListLevel: context.ListLevel);
        foreach (var row in rows)
        {
            xml.Append(row.IsHeader ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>");
            for (var column = 0; column < table.ColumnCount; column++)
            {
                var border = row.IsHeader ? "<w:tcBorders><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"0\" w:color=\"auto\"/></w:tcBorders>" : "";
                var cellWidth = table.Widths is null ? "<w:tcW w:w=\"0\" w:type=\"auto\"/>" : $"<w:tcW w:w=\"{widths[column]}\" w:type=\"dxa\"/>";
                var content = column < row.Cells.Count ? Runs(row.Cells[column], new RunFormat { Bold = row.IsHeader }) : "";
                var alignment = table.Alignments[column] switch { MarkdownAlignment.Center => "center", MarkdownAlignment.Right => "right", _ => null };
                xml.Append($"<w:tc><w:tcPr>{cellWidth}{border}</w:tcPr>{Paragraph(content, cellContext, justification: alignment)}</w:tc>");
            }
            xml.Append("</w:tr>");
        }
        xml.Append("</w:tbl>");
        result.Add(xml.ToString());
        // Word has no space below a table; a small empty paragraph makes some.
        result.Add("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"80\"/><w:rPr><w:sz w:val=\"12\"/></w:rPr></w:pPr></w:p>");
        return result;
    }

    private string Paragraph(string content, Context context, string? style = null, (int Id, int Level)? numbering = null, string? justification = null)
    {
        var properties = new StringBuilder();
        if ((style ?? context.Style) is { } resolved) properties.Append($"<w:pStyle w:val=\"{resolved}\"/>");
        if (numbering is { } n) properties.Append($"<w:numPr><w:ilvl w:val=\"{n.Level}\"/><w:numId w:val=\"{n.Id}\"/></w:numPr>");
        if (numbering is not null) properties.Append($"<w:ind w:left=\"{context.Indent}\" w:hanging=\"360\"/>");
        else if (context.Indent > 0) properties.Append($"<w:ind w:left=\"{context.Indent}\"/>");
        if (justification is not null) properties.Append($"<w:jc w:val=\"{justification}\"/>");
        if (_pendingPrefix is { } prefix)
        {
            content = prefix + content;
            _pendingPrefix = null;
        }
        return $"<w:p>{(properties.Length == 0 ? "" : $"<w:pPr>{properties}</w:pPr>")}{content}</w:p>";
    }

    // MARK: Inline content

    private sealed record RunFormat
    {
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Strike { get; init; }
        public string? Style { get; init; }
        public string? Font { get; init; }
    }

    private string Runs(MarkdownText text, RunFormat format) => string.Concat(text.Select(i => Inline(i, format)));

    private string Inline(MarkdownInline inline, RunFormat format)
    {
        switch (inline)
        {
            case MarkdownInline.Text t: return Run(t.Value, format);
            case MarkdownInline.Emphasis e: return Runs(e.Content, format with { Italic = true });
            case MarkdownInline.Strong s: return Runs(s.Content, format with { Bold = true });
            case MarkdownInline.Strikethrough s: return Runs(s.Content, format with { Strike = true });
            case MarkdownInline.Code c: return Run(c.Value, format with { Style = "VerbatimChar" });
            case MarkdownInline.Math m: return Run(MarkdownMath.Unicode(m.Tex), format with { Italic = true, Font = "Cambria Math" });
            case MarkdownInline.Link l:
            {
                var inner = format.Style is null ? format with { Style = "Hyperlink" } : format;
                var text = Runs(l.Content, inner);
                if (l.Url.StartsWith('#'))
                {
                    var anchor = l.Url[1..];
                    return $"<w:hyperlink w:anchor=\"{Xml.Escape(anchor.Length > 40 ? anchor[..40] : anchor)}\">{text}</w:hyperlink>";
                }
                if (!MarkdownHtml.IsSafe(l.Url)) return Runs(l.Content, format);
                var id = Relationship("hyperlink", l.Url, external: true);
                return $"<w:hyperlink r:id=\"{id}\" w:history=\"1\">{text}</w:hyperlink>";
            }
            case MarkdownInline.Image i: return Image(i.Source, i.Alt);
            case MarkdownInline.Footnote f:
                return $"<w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteReference w:id=\"{f.Number}\"/></w:r>";
            case MarkdownInline.LineBreak: return "<w:r><w:br/></w:r>";
            default: return Run(" ", format);
        }
    }

    private static string Run(string text, RunFormat format)
    {
        var properties = new StringBuilder();
        if (format.Style is { } style) properties.Append($"<w:rStyle w:val=\"{style}\"/>");
        if (format.Font is { } font) properties.Append($"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\"/>");
        if (format.Bold) properties.Append("<w:b/><w:bCs/>");
        if (format.Italic) properties.Append("<w:i/><w:iCs/>");
        if (format.Strike) properties.Append("<w:strike/>");
        var lines = text.Split('\n').Select(line =>
            string.Join("<w:tab/>", line.Split('\t').Select(piece => $"<w:t xml:space=\"preserve\">{Xml.Escape(piece)}</w:t>")));
        var content = string.Join("<w:br/>", lines);
        return $"<w:r>{(properties.Length == 0 ? "" : $"<w:rPr>{properties}</w:rPr>")}{content}</w:r>";
    }

    /// <summary>The picture embedded in the document, or its description when it is not on this PC (pictures on the web are
    /// never downloaded).</summary>
    private string Image(string source, MarkdownText alt)
    {
        var description = alt.PlainText();
        EmbeddablePicture? picture = null;
        if (!_inFootnote && MarkdownImages.LocalPath(source, _baseFolder) is { } path) picture = Pictures.Embeddable(path);
        if (picture is null)
            return Run($"[{(description.Length == 0 ? "image" : description)}]", new RunFormat { Italic = true });
        var number = _media.Count + 1;
        var name = $"image{number}.{picture.Extension}";
        _media.Add((name, picture.Data));
        var id = Relationship("image", $"media/{name}", external: false);
        var scale = Math.Min(1, Math.Min(TextWidthEmu / picture.Width, 7_315_200 / picture.Height));
        var width = (long)(picture.Width * scale);
        var height = (long)(picture.Height * scale);
        var drawing = _nextDrawingId++;
        var escaped = Xml.Escape(description);
        return $"<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{width}\" cy=\"{height}\"/><wp:docPr id=\"{drawing}\" name=\"Picture {drawing}\" descr=\"{escaped}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"{drawing}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
    }

    /// <summary>Adds a relationship to the part being written (the document or its footnotes).</summary>
    private string Relationship(string type, string target, bool external)
    {
        var id = $"rId{_nextRelationship++}";
        var mode = external ? " TargetMode=\"External\"" : "";
        var xml = $"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{type}\" Target=\"{Xml.Escape(target)}\"{mode}/>";
        (_inFootnote ? _footnoteRelationships : _documentRelationships).Add(xml);
        return id;
    }

    // MARK: Package parts

    private static string ContentTypes(bool hasFootnotes)
    {
        var footnotes = hasFootnotes
            ? "<Override PartName=\"/word/footnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml\"/>"
            : "";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default Extension=\"gif\" ContentType=\"image/gif\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/><Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>"
            + footnotes
            + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";
    }

    private List<string> DocumentPartRelationships(bool hasFootnotes)
    {
        const string baseType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        var fixedParts = new List<string>
        {
            $"<Relationship Id=\"rId1\" Type=\"{baseType}styles\" Target=\"styles.xml\"/>",
            $"<Relationship Id=\"rId2\" Type=\"{baseType}numbering\" Target=\"numbering.xml\"/>",
            $"<Relationship Id=\"rId3\" Type=\"{baseType}settings\" Target=\"settings.xml\"/>",
        };
        if (hasFootnotes) fixedParts.Add($"<Relationship Id=\"rId4\" Type=\"{baseType}footnotes\" Target=\"footnotes.xml\"/>");
        return fixedParts.Concat(_documentRelationships).ToList();
    }

    private static string Relationships(IEnumerable<string> entries) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{string.Concat(entries)}</Relationships>";

    private const string PackageRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>";

    private static string Core(string title) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>{Xml.Escape(title)}</dc:title></cp:coreProperties>";

    private string DocumentXml(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"{PageWidth}\" w:h=\"{PageHeight}\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

    private static string Footnotes(IEnumerable<string> notes)
    {
        const string separators = "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:footnotes xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">{separators}{string.Concat(notes)}</w:footnotes>";
    }

    private static string Settings(bool hasFootnotes)
    {
        var footnotes = hasFootnotes ? "<w:footnotePr><w:footnote w:id=\"-1\"/><w:footnote w:id=\"0\"/></w:footnotePr>" : "";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:defaultTabStop w:val=\"720\"/><w:characterSpacingControl w:val=\"doNotCompress\"/>{footnotes}<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>";
    }

    /// <summary>Bullets (abstract list 1, instance 1) and numbers (abstract list 2, one instance per list so each starts afresh),
    /// nine levels deep.</summary>
    private static string Numbering(IEnumerable<string> instances)
    {
        string[] bullets = ["•", "◦", "▪"];
        static string Level(int index, string format, string text) =>
            $"<w:lvl w:ilvl=\"{index}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/><w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{360 * (index + 1)}\" w:hanging=\"360\"/></w:pPr></w:lvl>";
        var bulletLevels = string.Concat(Enumerable.Range(0, 9).Select(i => Level(i, "bullet", bullets[i % 3])));
        var numberLevels = string.Concat(Enumerable.Range(0, 9).Select(i => Level(i, "decimal", $"%{i + 1}.")));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{bulletLevels}</w:abstractNum><w:abstractNum w:abstractNumId=\"2\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{numberLevels}</w:abstractNum><w:num w:numId=\"{BulletNumberingId}\"><w:abstractNumId w:val=\"1\"/></w:num>{string.Concat(instances)}</w:numbering>";
    }

    private static string ParagraphStyle(string id, string name, string? basedOn = "Normal", string? next = null, string pPr = "", string rPr = "", bool primary = true)
    {
        var based = basedOn is null ? "" : $"<w:basedOn w:val=\"{basedOn}\"/>";
        var nextStyle = next is null ? "" : $"<w:next w:val=\"{next}\"/>";
        var quick = primary ? "<w:qFormat/>" : "";
        var paragraph = pPr.Length == 0 ? "" : $"<w:pPr>{pPr}</w:pPr>";
        var run = rPr.Length == 0 ? "" : $"<w:rPr>{rPr}</w:rPr>";
        return $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>{based}{nextStyle}{quick}{paragraph}{run}</w:style>";
    }

    public static string Styles { get; } = BuildStyles();

    private static string BuildStyles()
    {
        int[] headingSizes = [36, 30, 26, 24, 22, 22];
        int[] headingSpaceBefore = [360, 300, 240, 200, 200, 200];
        var headings = string.Concat(Enumerable.Range(1, 6).Select(level => ParagraphStyle(
            $"Heading{level}", $"heading {level}", next: "Normal",
            pPr: $"<w:keepNext/><w:keepLines/><w:spacing w:before=\"{headingSpaceBefore[level - 1]}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/>",
            rPr: $"<w:b/><w:bCs/>{(level == 6 ? "<w:i/><w:iCs/>" : "")}<w:color w:val=\"1D1D1F\"/><w:sz w:val=\"{headingSizes[level - 1]}\"/><w:szCs w:val=\"{headingSizes[level - 1]}\"/>")));
        // Courier New comes with Windows and macOS alike.
        const string code = "<w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/>";
        var styles = new[]
        {
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>",
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>",
            "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>",
            "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>",
            ParagraphStyle("Title", "Title", next: "Normal", pPr: "<w:spacing w:after=\"300\"/><w:jc w:val=\"center\"/>", rPr: "<w:b/><w:bCs/><w:sz w:val=\"48\"/><w:szCs w:val=\"48\"/>"),
            headings,
            ParagraphStyle("Compact", "Compact", pPr: "<w:spacing w:before=\"36\" w:after=\"36\"/>"),
            ParagraphStyle("BlockText", "Block Text", pPr: "<w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"D2D2D7\"/></w:pBdr><w:spacing w:before=\"60\" w:after=\"120\"/>", rPr: "<w:color w:val=\"595959\"/>"),
            ParagraphStyle("SourceCode", "Source Code", pPr: "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F4F4F5\"/><w:spacing w:before=\"60\" w:after=\"160\" w:line=\"240\" w:lineRule=\"auto\"/>", rPr: code + "<w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/>"),
            $"<w:style w:type=\"character\" w:styleId=\"VerbatimChar\"><w:name w:val=\"Verbatim Char\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr>{code}<w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F4F4F5\"/></w:rPr></w:style>",
            "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>",
            ParagraphStyle("FootnoteText", "footnote text", pPr: "<w:spacing w:after=\"60\" w:line=\"240\" w:lineRule=\"auto\"/>", rPr: "<w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/>", primary: false),
            "<w:style w:type=\"character\" w:styleId=\"FootnoteReference\"><w:name w:val=\"footnote reference\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr></w:style>",
            ParagraphStyle("TableCaption", "Table Caption", pPr: "<w:keepNext/><w:spacing w:before=\"120\" w:after=\"80\"/><w:jc w:val=\"center\"/>", rPr: "<w:i/><w:iCs/><w:color w:val=\"595959\"/>"),
            ParagraphStyle("Figure", "Figure", pPr: "<w:keepNext/><w:spacing w:after=\"80\"/><w:jc w:val=\"center\"/>"),
            ParagraphStyle("ImageCaption", "Image Caption", pPr: "<w:jc w:val=\"center\"/>", rPr: "<w:i/><w:iCs/><w:color w:val=\"595959\"/>"),
            ParagraphStyle("DefinitionTerm", "Definition Term", next: "Normal", pPr: "<w:keepNext/><w:spacing w:after=\"40\"/>", rPr: "<w:b/><w:bCs/>"),
            ParagraphStyle("HorizontalRule", "Horizontal Rule", pPr: "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr><w:spacing w:before=\"120\" w:after=\"240\"/>", rPr: "<w:sz w:val=\"8\"/><w:szCs w:val=\"8\"/>"),
            "<w:style w:type=\"table\" w:styleId=\"Table\"><w:name w:val=\"Table\"/><w:basedOn w:val=\"TableNormal\"/><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"8\" w:space=\"0\" w:color=\"auto\"/><w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"0\" w:color=\"auto\"/></w:tblBorders><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>",
        };
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">{string.Concat(styles)}</w:styles>";
    }
}
