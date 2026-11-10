using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenShelf.Tour;

/// <summary>
/// Makes the practice files in a folder of their own under the temp directory, drawn and encoded here since the app
/// carries no sample files. Anything the person makes from them is saved beside them, so the folder shows their
/// progress.
/// </summary>
public static class PracticeFiles
{
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "OpenShelf Practice");

    public static string PathOf(PracticeFile file, string folder) => Path.Combine(folder, TourInstructions.FileName(file));

    /// <summary>Creates whichever practice files are missing from <paramref name="folder"/>. Rendering needs a WPF thread: call it from the UI thread.</summary>
    public static void Prepare(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Enum.GetValues<PracticeFile>())
        {
            var path = PathOf(file, folder);
            if (File.Exists(path)) continue;
            var temp = path + ".part";
            switch (file)
            {
                case PracticeFile.Photo: File.WriteAllBytes(temp, PhotoJpeg()); break;
                case PracticeFile.Notes: File.WriteAllText(temp, Notes, new UTF8Encoding(false)); break;
                case PracticeFile.Report: File.WriteAllBytes(temp, PdfWriter.Build(ReportPages)); break;
                case PracticeFile.Clip: File.WriteAllBytes(temp, AviWriter.Build(ClipFrames(), ClipWidth, ClipHeight, ClipFps)); break;
                default: File.WriteAllText(temp, Letter, new UTF8Encoding(false)); break;
            }
            File.Move(temp, path, overwrite: true);   // never leave a half-written file a drag could pick up
        }
    }

    /// <summary>Everything in the folder that isn't one of the practice files: what the person made, newest first.</summary>
    public static IReadOnlyList<string> Results(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var originals = Enum.GetValues<PracticeFile>().Select(TourInstructions.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !originals.Contains(f.Name) && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Deletes the folder and everything made in it (to the Recycle Bin).</summary>
    public static void Remove(string folder)
    {
        if (!Directory.Exists(folder)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    // ------------------------------------------------------------ text

    public const string Notes = """
        # Mountain Lake

        Three days, one lake, and *far* too many photos.

        ## Packing list

        - Tent and sleeping bags
        - Stove, pot and **plenty** of coffee
        - Rain jackets, just in case

        ## The plan

        | Day | Walk | Distance |
        |-----|------|---------:|
        | 1 | Around the lake | 11 km |
        | 2 | Up to the ridge | 7 km |
        | 3 | Back to the car | 4 km |

        > The best view is from the ridge, just before sunset.

        Turn this file into a PDF with the Convert wheel: the headings, the list
        and the table come out properly typeset.
        """;

    public const string Letter = """
        Dear Sam,

        We're back from Mountain Lake! The weather held, the trail was steep,
        and the sunsets were unreal. I've put the photos and a short clip in
        a folder for you.

        See you soon,
        Alex
        """;

    internal static readonly IReadOnlyList<PdfPage> ReportPages =
    [
        new("Field Report", "Notes from three days at Mountain Lake: the weather, the trail and what we saw. Try splitting this PDF into pages, or moving page 3 to the front.", (0x4A, 0x70, 0xDB)),
        new("The Trail", "Eleven kilometres around the lake, with one steep climb to the ridge. The view from the top is the photo in this folder.", (0x2E, 0x9E, 0x78)),
        new("The Weather", "Clear mornings, wind after noon, and a sunset every evening worth staying up for. The clip in this folder is one of them.", (0xDB, 0x73, 0x54)),
    ];

    // ------------------------------------------------------------ the photo

    /// <summary>A landscape at dusk: sky, sun, mountains and their reflection.</summary>
    internal static byte[] PhotoJpeg(int width = 1600, int height = 1066) => EncodeJpeg(DrawScene(width, height, sunProgress: 0), 88);

    private static readonly (double X, double Y)[] Far = [(0, .12), (.14, .26), (.28, .16), (.45, .33), (.6, .18), (.78, .29), (1, .14)];
    private static readonly (double X, double Y)[] Near = [(0, .06), (.18, .17), (.34, .08), (.52, .2), (.7, .07), (.88, .15), (1, .05)];

    private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

    /// <summary>Draws the scene; <paramref name="sunProgress"/> 0 is the high sun of the photo, 1 has the sun on the water line.</summary>
    internal static BitmapSource DrawScene(int width, int height, double sunProgress)
    {
        double w = width, h = height;
        var horizon = h * 0.58;   // y grows downwards here; the Mac drew with y up at 0.42

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Sky, warm at the horizon.
            dc.DrawRectangle(new LinearGradientBrush(
                [new GradientStop(Rgb(.99, .62, .42), 0), new GradientStop(Rgb(.86, .45, .55), .45), new GradientStop(Rgb(.26, .24, .52), 1)],
                new Point(0, 1), new Point(0, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                null, new Rect(0, 0, w, horizon));
            dc.DrawRectangle(new LinearGradientBrush(
                [new GradientStop(Rgb(.9, .55, .45), 0), new GradientStop(Rgb(.55, .36, .48), .5), new GradientStop(Rgb(.22, .2, .42), 1)],
                new Point(0, 0), new Point(0, 1)), null, new Rect(0, horizon, w, h - horizon));

            // The sun, low over the ridge.
            var sunSize = h * 0.16;
            var sunY = horizon - h * 0.05 - sunSize - (h * 0.14) * (1 - sunProgress);
            var sun = new Rect(w * 0.62, sunY, sunSize, sunSize);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, horizon)));
            dc.DrawEllipse(new SolidColorBrush(Rgb(1, .88, .62)), null, new Point(sun.X + sunSize / 2, sun.Y + sunSize / 2), sunSize / 2, sunSize / 2);
            dc.Pop();

            // Two ranges of mountains, then their reflection in the lake.
            void Ridge(IEnumerable<(double X, double Y)> points, Color color, bool mirrored)
            {
                var figure = new PathFigure { StartPoint = new Point(0, horizon), IsClosed = true };
                foreach (var (x, y) in points)
                    figure.Segments.Add(new LineSegment(new Point(w * x, mirrored ? horizon + h * y * 0.8 : horizon - h * y), true));
                figure.Segments.Add(new LineSegment(new Point(w, horizon), true));
                var brush = new SolidColorBrush(color) { Opacity = mirrored ? 0.55 : 1 };
                dc.DrawGeometry(brush, null, new PathGeometry([figure]));
            }
            dc.PushClip(new RectangleGeometry(new Rect(0, horizon, w, h - horizon)));
            Ridge(Far, Rgb(.36, .28, .5), true);
            Ridge(Near, Rgb(.2, .17, .34), true);
            dc.Pop();
            Ridge(Far, Rgb(.36, .28, .5), false);
            Ridge(Near, Rgb(.2, .17, .34), false);

            // Ripples across the water.
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 230, 190)), Math.Max(1, h * 0.003));
            for (var i = 0; i < 7; i++)
            {
                var y = horizon + (h - horizon) * (0.12 + i * 0.12);
                var x = w * (0.58 + (i % 3) * 0.03);
                var half = w * (0.03 + 0.012 * i);
                dc.DrawLine(pen, new Point(x - half, y), new Point(x + half, y));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static byte[] EncodeJpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ------------------------------------------------------------ the clip

    internal const int ClipWidth = 320, ClipHeight = 180, ClipFps = 10, ClipSeconds = 4;

    /// <summary>Four seconds of the sun going down, one JPEG per frame (Motion JPEG in an AVI, so no encoder is needed).</summary>
    internal static IEnumerable<byte[]> ClipFrames()
    {
        var frames = ClipFps * ClipSeconds;
        for (var i = 0; i < frames; i++)
            yield return EncodeJpeg(DrawScene(ClipWidth, ClipHeight, sunProgress: (double)i / (frames - 1)), 85);
    }
}

internal sealed record PdfPage(string Title, string Body, (int R, int G, int B) Color);

/// <summary>A small PDF writer: Letter pages, a coloured band with the title, a big page number, wrapped body text.</summary>
internal static class PdfWriter
{
    public static byte[] Build(IReadOnlyList<PdfPage> pages)
    {
        var objects = new List<string>();   // 1-based object numbers = index + 1
        // 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, then a page and a content stream per page.
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", pages.Select((_, i) => $"{5 + i * 2} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var contentNumber = 5 + i * 2 + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentNumber} 0 R >>");
            var content = Content(pages[i], i + 1, pages.Count);
            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        using var stream = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); stream.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static string Content(PdfPage page, int number, int total)
    {
        string C(int v) => (v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
        var (r, g, b) = page.Color;
        var sb = new StringBuilder();
        // The big page number, faint, then the band and title.
        sb.Append($"BT /F2 260 Tf {Tint(r)} {Tint(g)} {Tint(b)} rg 150 320 Td ({number}) Tj ET\n");
        sb.Append($"{C(r)} {C(g)} {C(b)} rg 0 692 612 100 re f\n");
        sb.Append($"BT /F2 30 Tf 1 1 1 rg 54 730 Td ({Escape(page.Title)}) Tj ET\n");
        var y = 640;
        foreach (var line in Wrap(page.Body, 62))
        {
            sb.Append($"BT /F1 15 Tf 0.15 0.15 0.15 rg 54 {y} Td ({Escape(line)}) Tj ET\n");
            y -= 24;
        }
        sb.Append($"BT /F1 10 Tf 0.5 0.5 0.5 rg 54 40 Td (Page {number} of {total}) Tj ET");
        return sb.ToString();

        static string Tint(int v) => (0.88 + 0.12 * v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}

/// <summary>Writes Motion JPEG video in an AVI container: every frame is a JPEG, so Windows, ffmpeg and most players open it.</summary>
internal static class AviWriter
{
    public static byte[] Build(IEnumerable<byte[]> jpegFrames, int width, int height, int fps)
    {
        var frames = jpegFrames.ToList();
        var maxFrame = frames.Max(f => f.Length);

        using var movi = new MemoryStream();
        var index = new List<(int Offset, int Size)>();
        movi.Write("movi"u8);   // offsets in idx1 count from this fourcc
        foreach (var frame in frames)
        {
            index.Add(((int)movi.Position, frame.Length));
            movi.Write("00dc"u8);
            WriteInt(movi, frame.Length);
            movi.Write(frame);
            if (frame.Length % 2 == 1) movi.WriteByte(0);
        }

        using var hdrl = new MemoryStream();
        hdrl.Write("hdrl"u8);
        Chunk(hdrl, "avih", w =>
        {
            WriteInt(w, 1_000_000 / fps);              // microseconds per frame
            WriteInt(w, maxFrame * fps);               // max bytes per second
            WriteInt(w, 0);                            // padding granularity
            WriteInt(w, 0x10);                         // AVIF_HASINDEX
            WriteInt(w, frames.Count);                 // total frames
            WriteInt(w, 0);                            // initial frames
            WriteInt(w, 1);                            // streams
            WriteInt(w, maxFrame);                     // suggested buffer
            WriteInt(w, width); WriteInt(w, height);
            for (var i = 0; i < 4; i++) WriteInt(w, 0);
        });
        using var strl = new MemoryStream();
        strl.Write("strl"u8);
        Chunk(strl, "strh", w =>
        {
            w.Write("vids"u8); w.Write("MJPG"u8);
            WriteInt(w, 0);                            // flags
            WriteShort(w, 0); WriteShort(w, 0);        // priority, language
            WriteInt(w, 0);                            // initial frames
            WriteInt(w, 1); WriteInt(w, fps);          // scale, rate
            WriteInt(w, 0);                            // start
            WriteInt(w, frames.Count);                 // length
            WriteInt(w, maxFrame);                     // suggested buffer
            WriteInt(w, -1);                           // quality
            WriteInt(w, 0);                            // sample size
            WriteShort(w, 0); WriteShort(w, 0); WriteShort(w, (short)width); WriteShort(w, (short)height);
        });
        Chunk(strl, "strf", w =>
        {
            WriteInt(w, 40); WriteInt(w, width); WriteInt(w, height);
            WriteShort(w, 1); WriteShort(w, 24);
            w.Write("MJPG"u8);
            WriteInt(w, width * height * 3);
            WriteInt(w, 0); WriteInt(w, 0); WriteInt(w, 0); WriteInt(w, 0);
        });
        List(hdrl, strl);

        using var idx1 = new MemoryStream();
        foreach (var (offset, size) in index)
        {
            idx1.Write("00dc"u8);
            WriteInt(idx1, 0x10);                      // AVIIF_KEYFRAME
            WriteInt(idx1, offset);
            WriteInt(idx1, size);
        }

        using var body = new MemoryStream();
        body.Write("AVI "u8);
        List(body, hdrl);
        List(body, movi);
        body.Write("idx1"u8);
        WriteInt(body, (int)idx1.Length);
        idx1.WriteTo(body);

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        WriteInt(file, (int)body.Length);
        body.WriteTo(file);
        return file.ToArray();
    }

    /// <summary>A LIST chunk; <paramref name="content"/> already starts with the list type.</summary>
    private static void List(Stream target, MemoryStream content)
    {
        target.Write("LIST"u8);
        WriteInt(target, (int)content.Length);
        content.WriteTo(target);
    }

    private static void Chunk(Stream target, string id, Action<Stream> write)
    {
        using var data = new MemoryStream();
        write(data);
        target.Write(Encoding.ASCII.GetBytes(id));
        WriteInt(target, (int)data.Length);
        data.WriteTo(target);
        if (data.Length % 2 == 1) target.WriteByte(0);
    }

    private static void WriteInt(Stream s, int value) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, value); s.Write(b); }
    private static void WriteShort(Stream s, short value) { Span<byte> b = stackalloc byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b, value); s.Write(b); }
}
