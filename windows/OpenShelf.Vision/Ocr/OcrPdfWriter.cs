using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace OpenShelf.Vision;

/// <summary>Builds the searchable PDF: the original pages (or the image) with each recognised line added as invisible text
/// (text render mode 3) stretched over where it appears, so selecting and searching find it in the right place.
/// Text goes in as a Unicode (Identity-H) font with a ToUnicode map, so any script round-trips through copy and search.</summary>
internal sealed class OcrPdfWriter : IDisposable
{
    private const string FontName = "/OSOcr1";

    private readonly PdfDocument _document = new();
    private PdfDocument? _source;
    private PdfDictionary? _font;

    /// <summary>Opens a PDF whose pages are to be copied into the result.</summary>
    /// <exception cref="PdfReaderException">The file is damaged or password-protected.</exception>
    public void OpenSource(string path) => _source = PdfReader.Open(path, PdfDocumentOpenMode.Import);

    public int SourcePageCount => _source?.PageCount ?? 0;

    /// <summary>Copies source page <paramref name="index"/> unchanged and lays <paramref name="lines"/> over it.</summary>
    public void AddSourcePage(int index, IReadOnlyList<OcrLine> lines)
    {
        var original = _source!.Pages[index];
        var page = _document.AddPage(original);
        // A size or rotation the original took from its page tree is not carried over by the import; set them on the page itself.
        // (Reading CropBox from a page that has none writes an empty one, so look at the keys instead.)
        if (!page.Elements.ContainsKey("/MediaBox")) page.MediaBox = original.MediaBox;
        if (!page.Elements.ContainsKey("/CropBox") && original.Elements.ContainsKey("/CropBox")) page.CropBox = original.CropBox;
        if (!page.Elements.ContainsKey("/Rotate") && original.Rotate != 0) page.Rotate = original.Rotate;
        if (lines.Count == 0) return;
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        var rotate = ((page.Rotate % 360) + 360) % 360;
        AppendTextLayer(page, lines, box.X1, box.Y1, box.Width, box.Height, rotate);
    }

    /// <summary>A page the size of <paramref name="image"/> at its own resolution, showing it, with <paramref name="lines"/> over it.</summary>
    public void AddImagePage(PixelImage image, IReadOnlyList<OcrLine> lines)
    {
        var width = image.Width * 72.0 / image.Dpi;
        var height = image.Height * 72.0 / image.Dpi;
        // PDF page sizes are limited (14400 points); scale huge scans down rather than write an invalid page.
        var shrink = Math.Min(1.0, 14000.0 / Math.Max(width, height));
        width *= shrink; height *= shrink;
        var page = _document.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
        using (var stream = new MemoryStream(image.ToJpeg()))
        using (var picture = XImage.FromStream(stream))
        using (var graphics = XGraphics.FromPdfPage(page))
            graphics.DrawImage(picture, 0, 0, width, height);
        AppendTextLayer(page, lines, 0, 0, width, height, 0);
    }

    public void Save(string path)
    {
        _document.Options.CompressContentStreams = true;
        _document.Save(path);
    }

    public void Dispose()
    {
        _source?.Dispose();
        _document.Dispose();
    }

    // ---- Text layer -----------------------------------------------------------------------------------------

    private void AppendTextLayer(PdfPage page, IReadOnlyList<OcrLine> lines, double originX, double originY, double boxWidth, double boxHeight, int rotate)
    {
        var swap = rotate is 90 or 270;
        var displayWidth = swap ? boxHeight : boxWidth;
        var displayHeight = swap ? boxWidth : boxHeight;
        var content = new StringBuilder("Q\nq\nBT\n3 Tr\n");
        var any = false;
        foreach (var line in lines)
        {
            var text = Clean(line.Text);
            var width = line.Width * displayWidth;
            var height = line.Height * displayHeight;
            if (text.Length == 0 || width <= 0 || height <= 0) continue;
            var size = height * 0.8;
            // Each UTF-16 unit is one em wide in the font (DW 1000), so Tz stretches the line to the measured width.
            var scale = 100.0 * width / (text.Length * size);
            var u = line.X * displayWidth;
            var v = line.Y * displayHeight + height * 0.2;
            var (a, b, c, d, e, f) = Matrix(rotate, u, v, boxWidth, boxHeight);
            content.Append(FormattableString.Invariant($"/{FontName[1..]} {size:0.###} Tf {scale:0.###} Tz {a} {b} {c} {d} {e + originX:0.###} {f + originY:0.###} Tm <"));
            foreach (var unit in text) content.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
            content.Append("> Tj\n");
            any = true;
        }
        if (!any) return;
        content.Append("ET\nQ\n");

        var font = Font(page.Owner);
        var resources = page.Resources;
        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }
        fonts.Elements[FontName] = font.Reference;

        // The original content may leave graphics state open; close it first ("Q" is ignored when nothing is open)
        // and put our text in its own saved state. The first stream's "q" balances that for the original's own Q.
        var before = page.Contents.PrependContent();
        before.CreateStream(Encoding.ASCII.GetBytes("q\n"));
        var after = page.Contents.AppendContent();
        after.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
    }

    /// <summary>Text-matrix entries mapping the displayed (rotated) page, origin bottom left, back to the unrotated page space.</summary>
    private static (int a, int b, int c, int d, double e, double f) Matrix(int rotate, double u, double v, double w, double h)
    {
        return rotate switch
        {
            90 => (0, 1, -1, 0, w - v, u),
            180 => (-1, 0, 0, -1, w - u, h - v),
            270 => (0, -1, 1, 0, v, h - u),
            _ => (1, 0, 0, 1, u, v),
        };
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        return builder.ToString().Trim();
    }

    private PdfDictionary Font(PdfDocument owner)
    {
        if (_font is not null) return _font;
        var toUnicode = new PdfDictionary(owner);
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def /CMapType 2 def\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 beginbfrange <0000> <FFFF> <0000> endbfrange\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end\n";
        owner.Internals.AddObject(toUnicode);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(cmap));

        var descriptor = new PdfDictionary(owner);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/ArialMT");
        descriptor.Elements.SetInteger("/Flags", 4);
        descriptor.Elements["/FontBBox"] = Array(owner, 0, -200, 1000, 900);
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", 900);
        descriptor.Elements.SetInteger("/Descent", -200);
        descriptor.Elements.SetInteger("/CapHeight", 700);
        descriptor.Elements.SetInteger("/StemV", 80);
        owner.Internals.AddObject(descriptor);

        var info = new PdfDictionary(owner);
        info.Elements.SetString("/Registry", "Adobe");
        info.Elements.SetString("/Ordering", "Identity");
        info.Elements.SetInteger("/Supplement", 0);

        var descendant = new PdfDictionary(owner);
        descendant.Elements.SetName("/Type", "/Font");
        descendant.Elements.SetName("/Subtype", "/CIDFontType2");
        descendant.Elements.SetName("/BaseFont", "/ArialMT");
        descendant.Elements["/CIDSystemInfo"] = info;
        descendant.Elements.SetInteger("/DW", 1000);
        descendant.Elements.SetName("/CIDToGIDMap", "/Identity");
        descendant.Elements["/FontDescriptor"] = descriptor.Reference;
        owner.Internals.AddObject(descendant);

        var font = new PdfDictionary(owner);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type0");
        font.Elements.SetName("/BaseFont", "/ArialMT");
        font.Elements.SetName("/Encoding", "/Identity-H");
        var descendants = new PdfArray(owner);
        descendants.Elements.Add(descendant.Reference!);
        font.Elements["/DescendantFonts"] = descendants;
        font.Elements["/ToUnicode"] = toUnicode.Reference;
        owner.Internals.AddObject(font);
        return _font = font;
    }

    private static PdfArray Array(PdfDocument owner, params int[] values)
    {
        var array = new PdfArray(owner);
        foreach (var value in values) array.Elements.Add(new PdfInteger(value));
        return array;
    }
}
