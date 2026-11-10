using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>Whole XHTML pages: the document a browser prints to PDF and the HTML a person gets.</summary>
public static class HtmlWriter
{
    private const string DocumentCss =
        "body { font-family: 'Segoe UI', Calibri, Arial, sans-serif; font-size: 11pt; line-height: 1.45; color: #111; margin: 0; }\n" +
        "h1, h2, h3, h4, h5, h6 { line-height: 1.2; margin: 1.1em 0 0.4em; page-break-after: avoid; }\n" +
        "h1 { font-size: 22pt; } h2 { font-size: 17pt; } h3 { font-size: 14pt; } h4, h5, h6 { font-size: 12pt; }\n" +
        "p { margin: 0 0 0.7em; } a { color: #0563c1; }\n" +
        "table { border-collapse: collapse; margin: 0.6em 0; max-width: 100%; } th, td { border: 1px solid #888; padding: 3px 8px; vertical-align: top; text-align: left; } th { background: #eee; }\n" +
        "tr { page-break-inside: avoid; } img { max-width: 100%; height: auto; } p.img { text-align: center; }\n";

    /// <summary>A page for printing: US Letter, 0.75 in margins, as the Mac prints rich text.</summary>
    public static string PrintPage(DocumentModel model) =>
        Page(model.Title, DocumentStructure.Xhtml(model.Blocks), "@page { size: 8.5in 11in; margin: 0.75in; }\n" + DocumentCss);

    /// <summary>A page of preformatted text, paginated as it is (plain text to PDF).</summary>
    public static string PlainTextPage(string title, string text) =>
        Page(title, $"<div class=\"plain\">{Texts.Escape(text)}</div>",
            "@page { size: 8.5in 11in; margin: 0.75in; }\nbody { margin: 0; } .plain { font-family: 'Segoe UI', Arial, sans-serif; font-size: 12pt; line-height: 1.3; white-space: pre-wrap; overflow-wrap: anywhere; }\n");

    /// <summary>A page to save as an HTML file: self-contained, pictures embedded.</summary>
    public static string StandalonePage(DocumentModel model) =>
        Page(model.Title, DocumentStructure.Xhtml(model.Blocks), "body { max-width: 46em; margin: 2em auto; padding: 0 1em; }\n" + DocumentCss.Replace("margin: 0;", "", StringComparison.Ordinal));

    public static string Page(string title, string body, string css) =>
        "<!DOCTYPE html>\n<html lang=\"en\">\n" +
        $"<head><meta charset=\"UTF-8\"/><title>{Texts.Escape(title)}</title><style>\n{css}</style></head>\n<body>\n{body}\n</body>\n</html>\n";

    public static void Save(string path, string html) => File.WriteAllText(path, html, new UTF8Encoding(false));
}
