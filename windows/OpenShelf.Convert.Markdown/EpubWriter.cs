using System.Globalization;

namespace OpenShelf.Convert.Markdown;

/// <summary>One chapter of an EPUB: a title for the table of contents and the body of its XHTML page.</summary>
public sealed record EpubChapter(string Title, string BodyXhtml);

/// <summary>Writes a minimal, valid EPUB 3 book. Port of EpubWriter.swift.</summary>
public static class EpubWriter
{
    /// <param name="images">JPEG files referenced from chapters as <c>images/&lt;name&gt;</c>.</param>
    public static void Write(string title, IReadOnlyList<EpubChapter> chapters, IReadOnlyDictionary<string, byte[]>? images, string path)
    {
        images ??= new Dictionary<string, byte[]>();
        try
        {
            using var zip = new PackageZip(path);
            // The mimetype entry must come first and be stored.
            zip.Write("mimetype", "application/epub+zip", stored: true);
            zip.Write("META-INF/container.xml", Container);
            zip.Write("OEBPS/style.css", Stylesheet);

            var manifest = new List<string>
            {
                "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>",
                "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>",
            };
            var spine = new List<string>();
            var toc = new List<string>();
            for (var index = 0; index < chapters.Count; index++)
            {
                var chapter = chapters[index];
                var file = $"chapter{index + 1}.xhtml";
                zip.Write($"OEBPS/{file}", Page(chapter.Title, chapter.BodyXhtml));
                manifest.Add($"<item id=\"c{index + 1}\" href=\"{file}\" media-type=\"application/xhtml+xml\"/>");
                spine.Add($"<itemref idref=\"c{index + 1}\"/>");
                toc.Add($"<li><a href=\"{file}\">{Xml.Escape(chapter.Title)}</a></li>");
            }
            var number = 0;
            foreach (var (name, data) in images.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                number++;
                zip.Write($"OEBPS/images/{name}", data, stored: true);
                manifest.Add($"<item id=\"img{number}\" href=\"images/{Xml.Escape(name)}\" media-type=\"image/jpeg\"/>");
            }

            zip.Write("OEBPS/nav.xhtml", Navigation(title, toc));
            zip.Write("OEBPS/content.opf", PackageDocument(title, manifest, spine));
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    private const string Container = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private const string Stylesheet = """
        body { font-family: serif; line-height: 1.5; margin: 1em; }
        h1, h2, h3, h4, h5, h6 { font-family: sans-serif; line-height: 1.2; }
        img { max-width: 100%; height: auto; display: block; margin: 0 auto; }
        """;

    private static string Page(string title, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en" xml:lang="en">
        <head><meta charset="UTF-8"/><title>{Xml.Escape(title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
        {body}
        </body>
        </html>
        """;

    private static string Navigation(string title, IEnumerable<string> entries) =>
        Page(title, $"<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>{string.Concat(entries)}</ol></nav>");

    private static string PackageDocument(string title, IEnumerable<string> manifest, IEnumerable<string> spine)
    {
        var modified = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="en">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">urn:uuid:{Guid.NewGuid().ToString().ToLowerInvariant()}</dc:identifier>
                <dc:title>{Xml.Escape(title)}</dc:title>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">{modified}</meta>
              </metadata>
              <manifest>
                {string.Join("\n    ", manifest)}
              </manifest>
              <spine>
                {string.Join("\n    ", spine)}
              </spine>
            </package>
            """;
    }
}
