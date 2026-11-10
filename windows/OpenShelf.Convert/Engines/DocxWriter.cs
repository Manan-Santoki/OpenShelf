using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert;

/// <summary>A picture to put on a Word page: PNG bytes and their pixel size.</summary>
public sealed record DocxImage(byte[] Png, int Width, int Height);

/// <summary>Writes a minimal Word document with one picture per page, assembled by hand as WordprocessingML. Port of the
/// picture path of DocxWriter.swift; the text, style, numbering and table parts arrive with the document engine.</summary>
public static class DocxWriter
{
    /// <summary>7 x 9.5 in of usable space on a US Letter page with 0.75 in margins, in EMU.</summary>
    private const double MaxWidth = 6_400_800;
    private const double MaxHeight = 8_686_800;
    /// <summary>9525 EMU = 1 px at 96 dpi: pictures are never enlarged past their pixel size.</summary>
    private const double EmuPerPixel = 9525;

    public static void Write(IReadOnlyList<DocxImage> images, string path)
    {
        var relationships = new StringBuilder();
        var paragraphs = new List<string>();
        for (var index = 0; index < images.Count; index++)
        {
            var number = index + 1;
            var image = images[index];
            relationships.Append($"<Relationship Id=\"rId{number}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{number}.png\"/>");
            var scale = Math.Min(Math.Min(MaxWidth / image.Width, MaxHeight / image.Height), EmuPerPixel);
            paragraphs.Add(Picture(number, (long)(image.Width * scale), (long)(image.Height * scale)));
        }
        var body = string.Join("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>", paragraphs);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", PackageRelationships);
        Add(zip, "word/_rels/document.xml.rels",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{relationships}</Relationships>");
        Add(zip, "word/document.xml", Document(body));
        for (var index = 0; index < images.Count; index++)
        {
            var entry = zip.CreateEntry($"word/media/image{index + 1}.png", CompressionLevel.NoCompression); // PNG is compressed already
            using var stream = entry.Open();
            stream.Write(images[index].Png);
        }
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static string Picture(int number, long width, long height) =>
        $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{width}\" cy=\"{height}\"/><wp:docPr id=\"{number}\" name=\"Picture {number}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"{number}\" name=\"image{number}.png\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"rId{number}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";

    private static string Document(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1080\" w:right=\"1080\" w:bottom=\"1080\" w:left=\"1080\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";

    private const string PackageRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
}
