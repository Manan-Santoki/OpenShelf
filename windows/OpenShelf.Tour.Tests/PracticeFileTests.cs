using System.Buffers.Binary;
using System.Text;
using OpenShelf.Core;
using static OpenShelf.Tour.Tests.Events;

namespace OpenShelf.Tour.Tests;

public class PracticeFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "os-tour-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    [Fact]
    public void Prepare_makes_all_five_files_with_the_right_formats()
    {
        Sta.Run(() => PracticeFiles.Prepare(_folder));

        var names = Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Field Report.pdf", "Letter.txt", "Mountain Lake.jpg", "Sunset.avi", "Trip Notes.md" }, names);

        var jpg = File.ReadAllBytes(Path.Combine(_folder, "Mountain Lake.jpg"));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, jpg[..3]);
        Assert.True(jpg.Length > 20_000);

        Assert.Contains("| Day | Walk | Distance |", File.ReadAllText(Path.Combine(_folder, "Trip Notes.md")));
        Assert.StartsWith("Dear Sam,", File.ReadAllText(Path.Combine(_folder, "Letter.txt")));
        Assert.Empty(Directory.GetFiles(_folder, "*.part"));
    }

    [Fact]
    public void Practice_file_kinds_match_what_the_steps_need()
    {
        foreach (var file in Enum.GetValues<PracticeFile>())
        {
            var kind = FileKinds.Of(TourInstructions.FileName(file));
            Assert.Equal(file switch
            {
                PracticeFile.Photo => FileKind.Image,
                PracticeFile.Notes => FileKind.Markdown,
                PracticeFile.Report => FileKind.Pdf,
                PracticeFile.Clip => FileKind.Video,
                _ => FileKind.Other,
            }, kind);
        }
        // And each step's own file satisfies that step's rule.
        Assert.True(TourStep.Convert.IsCompleted(Ran(WheelKind.Convert, "convert.png", TourInstructions.FileName(TourStep.Convert.File()!.Value))));
        Assert.True(TourStep.Markdown.IsCompleted(Ran(WheelKind.Convert, "convert.pdf", TourInstructions.FileName(TourStep.Markdown.File()!.Value))));
        Assert.True(TourStep.Tools.IsCompleted(Ran(WheelKind.Tools, "tool.crop", TourInstructions.FileName(TourStep.Tools.File()!.Value))));
        Assert.True(TourStep.Pdf.IsCompleted(Ran(WheelKind.Tools, "tool.split", TourInstructions.FileName(TourStep.Pdf.File()!.Value))));
        Assert.True(TourStep.Video.IsCompleted(Ran(WheelKind.Tools, "tool.trim", TourInstructions.FileName(TourStep.Video.File()!.Value))));
        Assert.True(TourStep.Options.IsCompleted(Ran(WheelKind.Convert, "convert.gif", TourInstructions.FileName(TourStep.Options.File()!.Value), options: true)));
    }

    [Fact]
    public void Prepare_keeps_existing_files_and_remakes_missing_ones()
    {
        Sta.Run(() => PracticeFiles.Prepare(_folder));
        var letter = Path.Combine(_folder, "Letter.txt");
        File.WriteAllText(letter, "edited");
        File.Delete(Path.Combine(_folder, "Trip Notes.md"));
        Sta.Run(() => PracticeFiles.Prepare(_folder));
        Assert.Equal("edited", File.ReadAllText(letter));
        Assert.True(File.Exists(Path.Combine(_folder, "Trip Notes.md")));
    }

    [Fact]
    public void Results_lists_only_what_was_made_newest_first()
    {
        Sta.Run(() => PracticeFiles.Prepare(_folder));
        Assert.Empty(PracticeFiles.Results(_folder));
        var older = Path.Combine(_folder, "Mountain Lake.png");
        var newer = Path.Combine(_folder, "Field Report Page 1.pdf");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(_folder, "half.part"), "x");
        Assert.Equal(new[] { newer, older }, PracticeFiles.Results(_folder));
        Assert.Empty(PracticeFiles.Results(Path.Combine(_folder, "nope")));
    }

    [Fact]
    public void The_pdf_has_three_pages_and_a_valid_cross_reference_table()
    {
        var bytes = PdfWriter.Build(PracticeFiles.ReportPages);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page ").Count);
        Assert.Contains("/Count 3", text);

        var start = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", text[start..]);
        var lines = text[start..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1]);
        for (var i = 1; i < count; i++)
        {
            var offset = int.Parse(lines[2 + i].Split(' ')[0]);
            Assert.StartsWith($"{i} 0 obj", text[offset..]);
        }
        // Each content stream's /Length is exact.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"/Length (\d+) >>\nstream\n"))
        {
            var from = m.Index + m.Length;
            var length = int.Parse(m.Groups[1].Value);
            Assert.StartsWith("\nendstream", text[(from + length)..]);
        }
    }

    [Fact]
    public void Pdf_text_is_escaped_and_wrapped()
    {
        Assert.Equal(new[] { "aaa bbb", "ccc" }, PdfWriter.Wrap("aaa bbb ccc", 8));
        var bytes = PdfWriter.Build([new PdfPage("A (b) \\ c", "body", (1, 2, 3))]);
        Assert.Contains("(A \\(b\\) \\\\ c) Tj", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void The_clip_is_a_forty_frame_motion_jpeg_avi_with_a_matching_index()
    {
        byte[] Jpeg(int n) => new byte[] { 0xFF, 0xD8, (byte)n, 0xFF, 0xD9, 0x00, 0x00 }.Take(5 + n % 2).ToArray();
        var frames = Enumerable.Range(0, 40).Select(Jpeg).ToList();
        var avi = AviWriter.Build(frames, 320, 180, 10);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(avi, 0, 4));
        Assert.Equal(avi.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(4)));
        Assert.Equal("AVI ", Encoding.ASCII.GetString(avi, 8, 4));

        // Walk the top-level chunks.
        var moviStart = -1; var idxStart = -1; var idxSize = 0; var avih = -1;
        var pos = 12;
        while (pos + 8 <= avi.Length)
        {
            var id = Encoding.ASCII.GetString(avi, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(pos + 4));
            if (id == "LIST")
            {
                var type = Encoding.ASCII.GetString(avi, pos + 8, 4);
                if (type == "movi") moviStart = pos + 8;
                if (type == "hdrl") avih = pos + 12;
            }
            else if (id == "idx1") { idxStart = pos + 8; idxSize = size; }
            pos += 8 + size + (size % 2);
        }
        Assert.Equal(avi.Length, pos);
        Assert.True(moviStart > 0 && idxStart > 0 && avih > 0);

        Assert.Equal("avih", Encoding.ASCII.GetString(avi, avih, 4));
        Assert.Equal(100_000, BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(avih + 8)));            // 10 fps
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(avih + 8 + 16)));            // total frames
        Assert.Equal(320, BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(avih + 8 + 32)));

        Assert.Equal(40 * 16, idxSize);
        for (var i = 0; i < 40; i++)
        {
            var entry = idxStart + i * 16;
            Assert.Equal("00dc", Encoding.ASCII.GetString(avi, entry, 4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(entry + 8));
            var size = BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(entry + 12));
            var chunk = moviStart + offset;                     // offsets count from the 'movi' fourcc
            Assert.Equal("00dc", Encoding.ASCII.GetString(avi, chunk, 4));
            Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(chunk + 4)));
            Assert.Equal(frames[i], avi.AsSpan(chunk + 8, size).ToArray());
        }
    }

    [Fact]
    public void The_real_clip_is_four_seconds_of_decodable_frames()
    {
        Sta.Run(() =>
        {
            PracticeFiles.Prepare(_folder);
            var avi = File.ReadAllBytes(Path.Combine(_folder, "Sunset.avi"));
            // First frame: the first 00dc chunk after 'movi' must be a JPEG that WPF can decode at the clip size.
            var movi = Encoding.ASCII.GetString(avi).IndexOf("movi", StringComparison.Ordinal);
            var size = BinaryPrimitives.ReadInt32LittleEndian(avi.AsSpan(movi + 8));
            var jpeg = avi.AsSpan(movi + 12, size).ToArray();
            using var stream = new MemoryStream(jpeg);
            var decoder = new System.Windows.Media.Imaging.JpegBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(320, decoder.Frames[0].PixelWidth);
            Assert.Equal(180, decoder.Frames[0].PixelHeight);
        });
    }
}
