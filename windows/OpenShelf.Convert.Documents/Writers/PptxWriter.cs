using System.IO.Compression;
using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>A slide's picture: JPEG bytes and pixel size.</summary>
public sealed record SlidePicture(byte[] Jpeg, int Width, int Height);

/// <summary>Writes a PowerPoint file with one picture per slide, assembled by hand from the smallest set of PresentationML parts
/// PowerPoint, Keynote and LibreOffice accept. Port of PptxWriter.swift.</summary>
public static class PptxWriter
{
    private const double EmuPerPoint = 12_700;
    private const string Namespaces = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string RelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <param name="slides">Each slide's picture, produced as the file is written.</param>
    /// <param name="slideWidthPoints">Slide size in points; pictures are fitted inside it.</param>
    public static void Write(IEnumerable<SlidePicture> slides, double slideWidthPoints, double slideHeightPoints, string path)
    {
        // PowerPoint accepts slides between 1 and 56 inches on a side.
        var width = (int)(Math.Min(Math.Max(slideWidthPoints, 72), 4032) * EmuPerPoint);
        var height = (int)(Math.Min(Math.Max(slideHeightPoints, 72), 4032) * EmuPerPoint);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var slideIds = new List<string>();
        var presentationRelationships = new List<string>
        {
            Relationship("rId1", "slideMaster", "slideMasters/slideMaster1.xml"),
            Relationship("rId2", "theme", "theme/theme1.xml"),
            Relationship("rId3", "presProps", "presProps.xml"),
            Relationship("rId4", "tableStyles", "tableStyles.xml"),
        };
        var overrides = new List<string>();
        var number = 0;
        foreach (var picture in slides)
        {
            number++;
            Zips.Add(zip, $"ppt/media/image{number}.jpeg", picture.Jpeg, CompressionLevel.NoCompression);
            Zips.Add(zip, $"ppt/slides/slide{number}.xml", Slide(number, picture, width, height));
            Zips.Add(zip, $"ppt/slides/_rels/slide{number}.xml.rels", Relationships(
                [Relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), Relationship("rId2", "image", $"../media/image{number}.jpeg")]));
            var id = $"rId{number + 4}";
            presentationRelationships.Add(Relationship(id, "slide", $"slides/slide{number}.xml"));
            slideIds.Add($"<p:sldId id=\"{255 + number}\" r:id=\"{id}\"/>");
            overrides.Add(Override($"/ppt/slides/slide{number}.xml", "presentationml.slide+xml"));
        }

        Zips.Add(zip, "[Content_Types].xml", ContentTypes(overrides));
        Zips.Add(zip, "_rels/.rels", Relationships([Relationship("rId1", "officeDocument", "ppt/presentation.xml")]));
        Zips.Add(zip, "ppt/presentation.xml", Xml($"<p:presentation {Namespaces} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>{string.Concat(slideIds)}</p:sldIdLst><p:sldSz cx=\"{width}\" cy=\"{height}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>"));
        Zips.Add(zip, "ppt/_rels/presentation.xml.rels", Relationships(presentationRelationships));
        Zips.Add(zip, "ppt/presProps.xml", Xml($"<p:presentationPr {Namespaces}/>"));
        Zips.Add(zip, "ppt/tableStyles.xml", Xml("<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>"));
        Zips.Add(zip, "ppt/slideMasters/slideMaster1.xml", Master);
        Zips.Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Relationships(
            [Relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), Relationship("rId2", "theme", "../theme/theme1.xml")]));
        Zips.Add(zip, "ppt/slideLayouts/slideLayout1.xml", Layout);
        Zips.Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Relationships([Relationship("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")]));
        Zips.Add(zip, "ppt/theme/theme1.xml", Theme);
    }

    private static string Slide(int number, SlidePicture picture, int slideWidth, int slideHeight)
    {
        // Fit the picture inside the slide, centred, keeping its proportions.
        var scale = Math.Min((double)slideWidth / picture.Width, (double)slideHeight / picture.Height);
        var width = (int)(picture.Width * scale);
        var height = (int)(picture.Height * scale);
        var x = (slideWidth - width) / 2;
        var y = (slideHeight - height) / 2;
        return Xml($"<p:sld {Namespaces}><p:cSld><p:spTree>{GroupProperties}<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {number}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
    }

    private static string Xml(string body) => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" + body;
    private static string Relationship(string id, string type, string target) => $"<Relationship Id=\"{id}\" Type=\"{RelationshipType}/{type}\" Target=\"{target}\"/>";
    private static string Relationships(IEnumerable<string> items) =>
        Xml($"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{string.Concat(items)}</Relationships>");
    private static string Override(string part, string type) => $"<Override PartName=\"{part}\" ContentType=\"application/vnd.openxmlformats-officedocument.{type}\"/>";

    private static string ContentTypes(IEnumerable<string> slideOverrides)
    {
        var fixedParts = new[]
        {
            Override("/ppt/presentation.xml", "presentationml.presentation.main+xml"),
            Override("/ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster+xml"),
            Override("/ppt/slideLayouts/slideLayout1.xml", "presentationml.slideLayout+xml"),
            Override("/ppt/theme/theme1.xml", "theme+xml"),
            Override("/ppt/presProps.xml", "presentationml.presProps+xml"),
            Override("/ppt/tableStyles.xml", "presentationml.tableStyles+xml"),
        };
        return Xml($"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>{string.Concat(fixedParts.Concat(slideOverrides))}</Types>");
    }

    private const string GroupProperties = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static readonly string Master = Xml($"<p:sldMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{GroupProperties}</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");

    private static readonly string Layout = Xml($"<p:sldLayout {Namespaces} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{GroupProperties}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");

    private static readonly string Theme = BuildTheme();

    private static string BuildTheme()
    {
        static string Colour(string name, string hex) => $"<a:{name}><a:srgbClr val=\"{hex}\"/></a:{name}>";
        var colours = "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            Colour("dk2", "1F2328") + Colour("lt2", "EEEEEE") + Colour("accent1", "4472C4") + Colour("accent2", "ED7D31") + Colour("accent3", "A5A5A5") +
            Colour("accent4", "FFC000") + Colour("accent5", "5B9BD5") + Colour("accent6", "70AD47") + Colour("hlink", "0563C1") + Colour("folHlink", "954F72");
        const string font = "<a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>";
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var line = $"<a:ln w=\"6350\">{fill}</a:ln>";
        const string effect = "<a:effectStyle><a:effectLst/></a:effectStyle>";
        string Triple(string item) => string.Concat(Enumerable.Repeat(item, 3));
        return Xml($"<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"OpenShelf\"><a:themeElements><a:clrScheme name=\"OpenShelf\">{colours}</a:clrScheme><a:fontScheme name=\"OpenShelf\"><a:majorFont>{font}</a:majorFont><a:minorFont>{font}</a:minorFont></a:fontScheme><a:fmtScheme name=\"OpenShelf\"><a:fillStyleLst>{Triple(fill)}</a:fillStyleLst><a:lnStyleLst>{Triple(line)}</a:lnStyleLst><a:effectStyleLst>{Triple(effect)}</a:effectStyleLst><a:bgFillStyleLst>{Triple(fill)}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>");
    }
}
