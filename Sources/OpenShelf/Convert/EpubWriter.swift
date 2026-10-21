import Foundation

/// One chapter of an EPUB: a title for the table of contents and the body of
/// its XHTML page.
struct EpubChapter {
    var title: String
    var bodyXHTML: String
}

/// Writes a minimal, valid EPUB 3 book.
enum EpubWriter {
    /// - Parameter images: JPEG files referenced from chapters as `images/<name>`.
    static func write(title: String, chapters: [EpubChapter], images: [String: Data] = [:], to url: URL) throws {
        let root = try PackageZip.makeWorkspace("epub")
        defer { try? FileManager.default.removeItem(at: root) }

        try PackageZip.write("application/epub+zip", to: "mimetype", in: root)
        try PackageZip.write(container, to: "META-INF/container.xml", in: root)
        try PackageZip.write(stylesheet, to: "OEBPS/style.css", in: root)

        var manifest = [
            #"<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>"#,
            #"<item id="css" href="style.css" media-type="text/css"/>"#,
        ]
        var spine: [String] = []
        var toc: [String] = []
        for (index, chapter) in chapters.enumerated() {
            let file = "chapter\(index + 1).xhtml"
            try PackageZip.write(page(title: chapter.title, body: chapter.bodyXHTML), to: "OEBPS/\(file)", in: root)
            manifest.append(#"<item id="c\#(index + 1)" href="\#(file)" media-type="application/xhtml+xml"/>"#)
            spine.append(#"<itemref idref="c\#(index + 1)"/>"#)
            toc.append(#"<li><a href="\#(file)">\#(chapter.title.xmlEscaped)</a></li>"#)
        }
        for (index, (name, data)) in images.sorted(by: { $0.key < $1.key }).enumerated() {
            try PackageZip.write(data, to: "OEBPS/images/\(name)", in: root)
            manifest.append(#"<item id="img\#(index + 1)" href="images/\#(name.xmlEscaped)" media-type="image/jpeg"/>"#)
        }

        try PackageZip.write(navigation(title: title, entries: toc), to: "OEBPS/nav.xhtml", in: root)
        try PackageZip.write(packageDocument(title: title, manifest: manifest, spine: spine), to: "OEBPS/content.opf", in: root)
        try PackageZip.zip(root, to: url, storedFirst: ["mimetype"])
    }

    private static let container = #"""
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """#

    private static let stylesheet = """
        body { font-family: serif; line-height: 1.5; margin: 1em; }
        h1, h2, h3, h4, h5, h6 { font-family: sans-serif; line-height: 1.2; }
        img { max-width: 100%; height: auto; display: block; margin: 0 auto; }
        """

    private static func page(title: String, body: String) -> String {
        #"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en" xml:lang="en">
        <head><meta charset="UTF-8"/><title>\#(title.xmlEscaped)</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
        \#(body)
        </body>
        </html>
        """#
    }

    private static func navigation(title: String, entries: [String]) -> String {
        page(title: title, body: #"<nav epub:type="toc" id="toc"><h1>Contents</h1><ol>\#(entries.joined())</ol></nav>"#)
    }

    private static func packageDocument(title: String, manifest: [String], spine: [String]) -> String {
        let modified = ISO8601DateFormatter().string(from: Date())
        return #"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="en">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">urn:uuid:\#(UUID().uuidString.lowercased())</dc:identifier>
                <dc:title>\#(title.xmlEscaped)</dc:title>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">\#(modified)</meta>
              </metadata>
              <manifest>
                \#(manifest.joined(separator: "\n    "))
              </manifest>
              <spine>
                \#(spine.joined(separator: "\n    "))
              </spine>
            </package>
            """#
    }
}
