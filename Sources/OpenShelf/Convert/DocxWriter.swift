import CoreGraphics
import Foundation

/// Writes a minimal Word document with one image per page. The system's
/// `.officeOpenXML` exporter drops images, so the package is assembled by hand.
enum DocxWriter {
    /// 7 × 9.5 in of usable space on a US Letter page with 0.75 in margins, in EMU.
    private static let maxWidth = 6_400_800.0
    private static let maxHeight = 8_686_800.0

    static func write(images: [CGImage], to url: URL) throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("docx-\(UUID().uuidString)")
        defer { try? fileManager.removeItem(at: root) }
        let media = root.appendingPathComponent("word/media")
        try fileManager.createDirectory(at: media, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: root.appendingPathComponent("_rels"), withIntermediateDirectories: true)
        try fileManager.createDirectory(at: root.appendingPathComponent("word/_rels"), withIntermediateDirectories: true)

        var relationships = ""
        var paragraphs: [String] = []
        for (index, image) in images.enumerated() {
            let number = index + 1
            try EditableImage.write(image, to: media.appendingPathComponent("image\(number).png"), type: .png)
            relationships += #"<Relationship Id="rId\#(number)" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image\#(number).png"/>"#

            let scale = min(maxWidth / Double(image.width), maxHeight / Double(image.height), 9525)  // 9525 EMU = 1 px at 96 dpi
            let width = Int(Double(image.width) * scale)
            let height = Int(Double(image.height) * scale)
            paragraphs.append(picture(number: number, width: width, height: height))
        }

        let body = paragraphs.joined(separator: #"<w:p><w:r><w:br w:type="page"/></w:r></w:p>"#)
        try write(contentTypes, to: root.appendingPathComponent("[Content_Types].xml"))
        try write(packageRelationships, to: root.appendingPathComponent("_rels/.rels"))
        try write(
            #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">\#(relationships)</Relationships>"#,
            to: root.appendingPathComponent("word/_rels/document.xml.rels")
        )
        try write(document(body: body), to: root.appendingPathComponent("word/document.xml"))

        try? fileManager.removeItem(at: url)
        let zip = ChildEnvironment.process(URL(fileURLWithPath: "/usr/bin/zip"), ["-X", "-r", "-q", url.path, "[Content_Types].xml", "_rels", "word"])
        zip.currentDirectoryURL = root
        // Failures surface as an error below; keep zip's own message out of the log.
        zip.standardOutput = FileHandle.nullDevice
        zip.standardError = FileHandle.nullDevice
        try zip.run()
        zip.waitUntilExit()
        guard zip.terminationStatus == 0 else { throw EditableImageError.unwritable(url) }
    }

    private static func write(_ string: String, to url: URL) throws {
        try string.write(to: url, atomically: true, encoding: .utf8)
    }

    private static func picture(number: Int, width: Int, height: Int) -> String {
        #"<w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="\#(width)" cy="\#(height)"/><wp:docPr id="\#(number)" name="Picture \#(number)"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="\#(number)" name="image\#(number).png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rId\#(number)"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="\#(width)" cy="\#(height)"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"#
    }

    private static func document(body: String) -> String {
        #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>\#(body)<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1080" w:right="1080" w:bottom="1080" w:left="1080" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr></w:body></w:document>"#
    }

    private static let contentTypes = #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>"#

    private static let packageRelationships = #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>"#
}
