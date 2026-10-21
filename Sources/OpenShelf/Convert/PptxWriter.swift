import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Writes a PowerPoint file with one picture per slide, assembled by hand from
/// the smallest set of PresentationML parts PowerPoint, Keynote and
/// LibreOffice accept.
enum PptxWriter {
    private static let emuPerPoint = 12_700.0
    private static let namespaces = #"xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main""#
    private static let relationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

    /// - Parameters:
    ///   - slides: Each slide's picture.
    ///   - slideSize: Slide size in points; pictures are fitted inside it.
    static func write(slides: [CGImage], slideSize: CGSize, to url: URL) throws {
        let root = try PackageZip.makeWorkspace("pptx")
        defer { try? FileManager.default.removeItem(at: root) }
        // PowerPoint accepts slides between 1 and 56 inches on a side.
        let width = Int(min(max(slideSize.width, 72), 4032) * emuPerPoint)
        let height = Int(min(max(slideSize.height, 72), 4032) * emuPerPoint)

        var slideIDs: [String] = []
        var presentationRelationships = [
            relationship("rId1", "slideMaster", "slideMasters/slideMaster1.xml"),
            relationship("rId2", "theme", "theme/theme1.xml"),
            relationship("rId3", "presProps", "presProps.xml"),
            relationship("rId4", "tableStyles", "tableStyles.xml"),
        ]
        var overrides: [String] = []

        for (index, image) in slides.enumerated() {
            let number = index + 1
            guard let jpeg = jpegData(image) else { throw EditableImageError.unwritable(url) }
            try PackageZip.write(jpeg, to: "ppt/media/image\(number).jpeg", in: root)
            try PackageZip.write(slide(number: number, image: image, slideWidth: width, slideHeight: height), to: "ppt/slides/slide\(number).xml", in: root)
            try PackageZip.write(
                relationships([
                    relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
                    relationship("rId2", "image", "../media/image\(number).jpeg"),
                ]),
                to: "ppt/slides/_rels/slide\(number).xml.rels",
                in: root
            )
            let id = "rId\(number + 4)"
            presentationRelationships.append(relationship(id, "slide", "slides/slide\(number).xml"))
            slideIDs.append(#"<p:sldId id="\#(255 + number)" r:id="\#(id)"/>"#)
            overrides.append(override("/ppt/slides/slide\(number).xml", "presentationml.slide+xml"))
        }

        try PackageZip.write(contentTypes(slideOverrides: overrides), to: "[Content_Types].xml", in: root)
        try PackageZip.write(relationships([relationship("rId1", "officeDocument", "ppt/presentation.xml")]), to: "_rels/.rels", in: root)
        try PackageZip.write(
            xml(#"<p:presentation \#(namespaces) saveSubsetFonts="1"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>\#(slideIDs.joined())</p:sldIdLst><p:sldSz cx="\#(width)" cy="\#(height)"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>"#),
            to: "ppt/presentation.xml",
            in: root
        )
        try PackageZip.write(relationships(presentationRelationships), to: "ppt/_rels/presentation.xml.rels", in: root)
        try PackageZip.write(xml(#"<p:presentationPr \#(namespaces)/>"#), to: "ppt/presProps.xml", in: root)
        try PackageZip.write(
            xml(#"<a:tblStyleLst xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" def="{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}"/>"#),
            to: "ppt/tableStyles.xml",
            in: root
        )
        try PackageZip.write(master, to: "ppt/slideMasters/slideMaster1.xml", in: root)
        try PackageZip.write(
            relationships([
                relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
                relationship("rId2", "theme", "../theme/theme1.xml"),
            ]),
            to: "ppt/slideMasters/_rels/slideMaster1.xml.rels",
            in: root
        )
        try PackageZip.write(layout, to: "ppt/slideLayouts/slideLayout1.xml", in: root)
        try PackageZip.write(
            relationships([relationship("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")]),
            to: "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
            in: root
        )
        try PackageZip.write(theme, to: "ppt/theme/theme1.xml", in: root)
        try PackageZip.zip(root, to: url)
    }

    private static func slide(number: Int, image: CGImage, slideWidth: Int, slideHeight: Int) -> String {
        // Fit the picture inside the slide, centred, keeping its proportions.
        let scale = min(Double(slideWidth) / Double(image.width), Double(slideHeight) / Double(image.height))
        let width = Int(Double(image.width) * scale)
        let height = Int(Double(image.height) * scale)
        let x = (slideWidth - width) / 2
        let y = (slideHeight - height) / 2
        return xml(#"<p:sld \#(namespaces)><p:cSld><p:spTree>\#(groupProperties)<p:pic><p:nvPicPr><p:cNvPr id="2" name="Page \#(number)"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rId2"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="\#(x)" y="\#(y)"/><a:ext cx="\#(width)" cy="\#(height)"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>"#)
    }

    private static func jpegData(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, EditableImage.flattened(image) ?? image, [kCGImageDestinationLossyCompressionQuality: 0.85] as CFDictionary)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    // MARK: Parts

    private static func xml(_ body: String) -> String {
        #"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"# + "\n" + body
    }

    private static func relationship(_ id: String, _ type: String, _ target: String) -> String {
        #"<Relationship Id="\#(id)" Type="\#(relationshipType)/\#(type)" Target="\#(target)"/>"#
    }

    private static func relationships(_ items: [String]) -> String {
        xml(#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">\#(items.joined())</Relationships>"#)
    }

    private static func override(_ part: String, _ type: String) -> String {
        #"<Override PartName="\#(part)" ContentType="application/vnd.openxmlformats-officedocument.\#(type)"/>"#
    }

    private static func contentTypes(slideOverrides: [String]) -> String {
        let fixed = [
            override("/ppt/presentation.xml", "presentationml.presentation.main+xml"),
            override("/ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster+xml"),
            override("/ppt/slideLayouts/slideLayout1.xml", "presentationml.slideLayout+xml"),
            override("/ppt/theme/theme1.xml", "theme+xml"),
            override("/ppt/presProps.xml", "presentationml.presProps+xml"),
            override("/ppt/tableStyles.xml", "presentationml.tableStyles+xml"),
        ]
        return xml(#"<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpeg" ContentType="image/jpeg"/>\#((fixed + slideOverrides).joined())</Types>"#)
    }

    private static let groupProperties = #"<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>"#

    private static let master = xml(#"<p:sldMaster \#(namespaces)><p:cSld><p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg><p:spTree>\#(groupProperties)</p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>"#)

    private static let layout = xml(#"<p:sldLayout \#(namespaces) type="blank" preserve="1"><p:cSld name="Blank"><p:spTree>\#(groupProperties)</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>"#)

    private static let theme: String = {
        func colour(_ name: String, _ hex: String) -> String { "<a:\(name)><a:srgbClr val=\"\(hex)\"/></a:\(name)>" }
        let colours = [
            #"<a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>"#,
            colour("dk2", "1F2328"), colour("lt2", "EEEEEE"),
            colour("accent1", "4472C4"), colour("accent2", "ED7D31"), colour("accent3", "A5A5A5"),
            colour("accent4", "FFC000"), colour("accent5", "5B9BD5"), colour("accent6", "70AD47"),
            colour("hlink", "0563C1"), colour("folHlink", "954F72"),
        ].joined()
        let font = #"<a:latin typeface="Helvetica Neue"/><a:ea typeface=""/><a:cs typeface=""/>"#
        let fill = #"<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>"#
        let line = #"<a:ln w="6350">\#(fill)</a:ln>"#
        let effect = "<a:effectStyle><a:effectLst/></a:effectStyle>"
        let triple = { (item: String) in String(repeating: item, count: 3) }
        return xml(#"<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="OpenShelf"><a:themeElements><a:clrScheme name="OpenShelf">\#(colours)</a:clrScheme><a:fontScheme name="OpenShelf"><a:majorFont>\#(font)</a:majorFont><a:minorFont>\#(font)</a:minorFont></a:fontScheme><a:fmtScheme name="OpenShelf"><a:fillStyleLst>\#(triple(fill))</a:fillStyleLst><a:lnStyleLst>\#(triple(line))</a:lnStyleLst><a:effectStyleLst>\#(triple(effect))</a:effectStyleLst><a:bgFillStyleLst>\#(triple(fill))</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>"#)
    }()
}
