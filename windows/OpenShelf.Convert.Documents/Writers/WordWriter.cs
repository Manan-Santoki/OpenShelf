using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>Writes a Word document from the structure model, assembled by hand as WordprocessingML: Word styles (Heading 1-6,
/// List Paragraph, Hyperlink, Table Grid), bullet and numbered lists, tables, hyperlinks and pictures.</summary>
public static class WordWriter
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const double MaxWidth = 6_400_800;    // 7 in of a Letter page with 0.75 in margins, in EMU
    private const double MaxHeight = 8_686_800;
    private const double EmuPerPixel = 9525;

    private sealed class Package
    {
        public List<string> Relationships = [];
        public List<(string Name, byte[] Data)> Media = [];
        public List<string> Nums = [];
        public int NextNum = 1;
        public int NextImage = 1;
        public int NextRelationship = 10;
    }

    public static void Write(DocumentModel model, string path)
    {
        var package = new Package();
        var body = new StringBuilder();
        Block? previous = null;
        var currentNum = 0;
        foreach (var block in model.Blocks)
        {
            if (block is ListItemBlock item)
            {
                if (previous is not ListItemBlock before || before.Ordered != item.Ordered)
                {
                    currentNum = package.NextNum++;
                    package.Nums.Add($"<w:num w:numId=\"{currentNum}\"><w:abstractNumId w:val=\"{(item.Ordered ? 1 : 0)}\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
                }
                body.Append($"<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"{Math.Clamp(item.Level, 0, 8)}\"/><w:numId w:val=\"{currentNum}\"/></w:numPr></w:pPr>{Runs(item.Runs, package)}</w:p>");
            }
            else Write(block, package, body);
            previous = block;
        }
        if (model.Blocks.Count == 0) body.Append("<w:p/>");

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var media = package.Media.Select(m => Path.GetExtension(m.Name).TrimStart('.')).Distinct().ToList();
        Zips.Add(zip, "[Content_Types].xml", ContentTypes(media));
        Zips.Add(zip, "_rels/.rels",
            Header + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
        Zips.Add(zip, "docProps/core.xml",
            Header + $"<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>{Texts.Escape(model.Title)}</dc:title><dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>");
        Zips.Add(zip, "word/_rels/document.xml.rels",
            Header + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" +
            string.Concat(package.Relationships) + "</Relationships>");
        Zips.Add(zip, "word/document.xml", Document(body.ToString()));
        Zips.Add(zip, "word/styles.xml", Styles);
        Zips.Add(zip, "word/numbering.xml", Numbering(package.Nums));
        foreach (var (name, data) in package.Media) Zips.Add(zip, "word/media/" + name, data, CompressionLevel.NoCompression);
    }

    private static void Write(Block block, Package package, StringBuilder body)
    {
        switch (block)
        {
            case HeadingBlock h:
                body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Heading{Math.Clamp(h.Level, 1, 6)}\"/></w:pPr>{Runs(h.Runs, package)}</w:p>");
                break;
            case ParagraphBlock p:
                body.Append($"<w:p>{Runs(p.Runs, package)}</w:p>");
                break;
            case TableBlock table:
                body.Append(Table(table, package));
                break;
            case ImageBlock image:
                body.Append(Picture(image, package));
                break;
            case PageBreakBlock:
                body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                break;
        }
    }

    private static string Table(TableBlock table, Package package)
    {
        var columns = Math.Max(table.Rows.Count == 0 ? 1 : table.Rows.Max(row => row.Count), 1);
        var width = 9360 / columns;
        var builder = new StringBuilder("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr><w:tblGrid>");
        for (var i = 0; i < columns; i++) builder.Append($"<w:gridCol w:w=\"{width}\"/>");
        builder.Append("</w:tblGrid>");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var isHeader = table.HasHeader && r == 0;
            builder.Append("<w:tr>").Append(isHeader ? "<w:trPr><w:tblHeader/></w:trPr>" : "");
            for (var c = 0; c < columns; c++)
            {
                var runs = c < row.Count ? row[c] : [];
                if (isHeader) runs = runs.Select(run => run with { Bold = true }).ToList();
                builder.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/></w:tcPr><w:p>{Runs(runs, package)}</w:p></w:tc>");
            }
            builder.Append("</w:tr>");
        }
        // Word wants a paragraph after a table.
        return builder.Append("</w:tbl><w:p/>").ToString();
    }

    private static string Runs(IReadOnlyList<TextRun> runs, Package package)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            var properties = new StringBuilder();
            if (run.Link is not null) properties.Append("<w:rStyle w:val=\"Hyperlink\"/>");
            if (run.Bold) properties.Append("<w:b/>");
            if (run.Italic) properties.Append("<w:i/>");
            if (run.Underline && run.Link is null) properties.Append("<w:u w:val=\"single\"/>");
            var rPr = properties.Length > 0 ? $"<w:rPr>{properties}</w:rPr>" : "";
            var content = new StringBuilder();
            var text = new StringBuilder();
            void FlushText()
            {
                if (text.Length == 0) return;
                content.Append($"<w:t xml:space=\"preserve\">{Texts.Escape(text.ToString())}</w:t>");
                text.Clear();
            }
            foreach (var c in Texts.XmlSafe(run.Text))
            {
                if (c == '\n') { FlushText(); content.Append("<w:br/>"); }
                else if (c == '\t') { FlushText(); content.Append("<w:tab/>"); }
                else if (c != '\r') text.Append(c);
            }
            FlushText();
            var element = $"<w:r>{rPr}{content}</w:r>";
            if (run.Link is not null && Uri.TryCreate(run.Link, UriKind.RelativeOrAbsolute, out _))
            {
                var id = $"rId{package.NextRelationship++}";
                package.Relationships.Add($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{Texts.Escape(run.Link)}\" TargetMode=\"External\"/>");
                element = $"<w:hyperlink r:id=\"{id}\" w:history=\"1\">{element}</w:hyperlink>";
            }
            builder.Append(element);
        }
        return builder.ToString();
    }

    private static string Picture(ImageBlock image, Package package)
    {
        var number = package.NextImage++;
        var extension = image.Extension == "jpeg" ? "jpeg" : image.Extension;
        var name = $"image{number}.{extension}";
        package.Media.Add((name, image.Data));
        var id = $"rId{package.NextRelationship++}";
        package.Relationships.Add($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{name}\"/>");
        var scale = Math.Min(Math.Min(MaxWidth / Math.Max(image.Width * EmuPerPixel, 1), MaxHeight / Math.Max(image.Height * EmuPerPixel, 1)), 1);
        var width = (long)(image.Width * EmuPerPixel * scale);
        var height = (long)(image.Height * EmuPerPixel * scale);
        return $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{width}\" cy=\"{height}\"/><wp:docPr id=\"{number}\" name=\"Picture {number}\" descr=\"{Texts.Escape(image.Alt)}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"{number}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    private static string Document(string body) =>
        Header + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
        $"<w:body>{body}<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1080\" w:right=\"1080\" w:bottom=\"1080\" w:left=\"1080\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

    private static string ContentTypes(IEnumerable<string> mediaExtensions)
    {
        var defaults = new StringBuilder();
        foreach (var extension in mediaExtensions)
            defaults.Append($"<Default Extension=\"{extension}\" ContentType=\"image/{extension}\"/>");
        return Header + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" + defaults +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";
    }

    private static string Numbering(IEnumerable<string> nums)
    {
        static string Levels(bool ordered)
        {
            var builder = new StringBuilder();
            for (var level = 0; level < 9; level++)
            {
                var indent = 720 * (level + 1);
                var format = ordered ? "decimal" : "bullet";
                var text = ordered ? $"%{level + 1}." : (level % 2 == 0 ? "•" : "◦");
                builder.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/><w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{indent}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
            }
            return builder.ToString();
        }
        return Header + "<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            $"<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{Levels(false)}</w:abstractNum>" +
            $"<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>{Levels(true)}</w:abstractNum>" +
            string.Concat(nums) + "</w:numbering>";
    }

    private static string HeadingStyle(int level, int size) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"{(level <= 2 ? 360 : 240)}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"{size}\"/></w:rPr></w:style>";

    private static readonly string Styles = Header + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        HeadingStyle(1, 36) + HeadingStyle(2, 30) + HeadingStyle(3, 26) + HeadingStyle(4, 24) + HeadingStyle(5, 22) + HeadingStyle(6, 22) +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"720\"/></w:pPr></w:style>" +
        "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/></w:style>" +
        "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
        "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
        "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/></w:tblBorders></w:tblPr></w:style>" +
        "</w:styles>";
}
