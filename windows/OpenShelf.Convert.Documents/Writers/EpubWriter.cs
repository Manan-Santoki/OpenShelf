using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>One chapter of an EPUB: a title for the table of contents and the body of its XHTML page.</summary>
public sealed record EpubChapter(string Title, string BodyXhtml);

/// <summary>A picture inside an EPUB, referenced from chapters as <c>images/&lt;name&gt;</c>.</summary>
public sealed record EpubImage(string Name, byte[] Data, string MediaType);

/// <summary>Writes a minimal, valid EPUB 3 book. Port of EpubWriter.swift.</summary>
public static class EpubWriter
{
    public static void Write(string title, IReadOnlyList<EpubChapter> chapters, string path, IReadOnlyList<EpubImage>? images = null)
    {
        images ??= [];
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        // The mimetype comes first and is stored, not compressed.
        Zips.Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Zips.Add(zip, "META-INF/container.xml", Container);
        Zips.Add(zip, "OEBPS/style.css", Stylesheet);

        var manifest = new List<string>
        {
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>",
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>",
        };
        var spine = new List<string>();
        var toc = new List<string>();
        for (var index = 0; index < chapters.Count; index++)
        {
            var file1 = $"chapter{index + 1}.xhtml";
            Zips.Add(zip, $"OEBPS/{file1}", Page(chapters[index].Title, chapters[index].BodyXhtml));
            manifest.Add($"<item id=\"c{index + 1}\" href=\"{file1}\" media-type=\"application/xhtml+xml\"/>");
            spine.Add($"<itemref idref=\"c{index + 1}\"/>");
            toc.Add($"<li><a href=\"{file1}\">{Texts.Escape(chapters[index].Title)}</a></li>");
        }
        for (var index = 0; index < images.Count; index++)
        {
            Zips.Add(zip, $"OEBPS/images/{images[index].Name}", images[index].Data, CompressionLevel.NoCompression);
            manifest.Add($"<item id=\"img{index + 1}\" href=\"images/{Texts.Escape(images[index].Name)}\" media-type=\"{images[index].MediaType}\"/>");
        }
        Zips.Add(zip, "OEBPS/nav.xhtml", Page(title, $"<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>{string.Concat(toc)}</ol></nav>"));
        Zips.Add(zip, "OEBPS/content.opf", PackageDocument(title, manifest, spine));
    }

    /// <summary>Body XHTML for a model, with its pictures collected as EPUB images.</summary>
    public static (string Body, List<EpubImage> Images) BodyFor(IReadOnlyList<Block> blocks)
    {
        var images = new List<EpubImage>();
        var body = DocumentStructure.Xhtml(blocks, image =>
        {
            var name = $"image{images.Count + 1}.{(image.Extension == "jpeg" ? "jpg" : image.Extension)}";
            images.Add(new EpubImage(name, image.Data, image.MediaType));
            return "images/" + name;
        });
        return (body, images);
    }

    private const string Container =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n  <rootfiles>\n    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n  </rootfiles>\n</container>";

    private const string Stylesheet =
        "body { font-family: serif; line-height: 1.5; margin: 1em; }\nh1, h2, h3, h4, h5, h6 { font-family: sans-serif; line-height: 1.2; }\nimg { max-width: 100%; height: auto; display: block; margin: 0 auto; }\ntable { border-collapse: collapse; }\nth, td { border: 1px solid #888; padding: 0.25em 0.5em; }";

    private static string Page(string title, string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"en\" xml:lang=\"en\">\n" +
        $"<head><meta charset=\"UTF-8\"/><title>{Texts.Escape(title)}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head>\n<body>\n{body}\n</body>\n</html>";

    private static string PackageDocument(string title, List<string> manifest, List<string> spine) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"en\">\n  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
        $"    <dc:identifier id=\"bookid\">urn:uuid:{Guid.NewGuid()}</dc:identifier>\n    <dc:title>{Texts.Escape(title)}</dc:title>\n    <dc:language>en</dc:language>\n    <meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>\n  </metadata>\n" +
        $"  <manifest>\n    {string.Join("\n    ", manifest)}\n  </manifest>\n  <spine>\n    {string.Join("\n    ", spine)}\n  </spine>\n</package>";
}
