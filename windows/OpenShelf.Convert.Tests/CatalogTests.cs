using OpenShelf.Convert;
using static OpenShelf.Convert.OutputFormat;

namespace OpenShelf.Convert.Tests;

public class FileKindTests
{
    [Theory]
    [InlineData("a.jpg", FileKind.Image), InlineData("a.HEIC", FileKind.Image), InlineData("a.svg", FileKind.Image),
     InlineData("a.pdf", FileKind.Pdf), InlineData("a.docx", FileKind.Document), InlineData("a.md", FileKind.Document),
     InlineData("a.rtfd", FileKind.Document), InlineData("a.pages", FileKind.Document),
     InlineData("a.mp3", FileKind.Audio), InlineData("a.ogg", FileKind.Audio), InlineData("a.opus", FileKind.Audio), InlineData("a.wma", FileKind.Audio),
     InlineData("a.mov", FileKind.Video), InlineData("a.mkv", FileKind.Video), InlineData("a.webm", FileKind.Video), InlineData("a.avi", FileKind.Video), InlineData("a.wmv", FileKind.Video),
     InlineData("a.srt", FileKind.Subtitle), InlineData("a.zip", FileKind.Archive), InlineData("a.tar.gz", FileKind.Archive), InlineData("a.7z", FileKind.Archive),
     InlineData("a.csv", FileKind.Spreadsheet), InlineData("a.xlsx", FileKind.Spreadsheet), InlineData("a.ods", FileKind.Spreadsheet), InlineData("a.numbers", FileKind.Spreadsheet),
     InlineData("a.pptx", FileKind.Presentation), InlineData("a.key", FileKind.Presentation),
     InlineData("a.epub", FileKind.Ebook), InlineData("a.mobi", FileKind.Ebook), InlineData("a.xyz123", FileKind.Other)]
    public void ClassifiesByExtension(string name, FileKind kind) => Assert.Equal(kind, FileKinds.Of(@"C:\nonexistent\" + name));

    [Fact]
    public void FoldersAndPackages()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("Plain"));
        Directory.CreateDirectory(temp.Combine("Deck.key"));
        Assert.Equal(FileKind.Folder, FileKinds.Of(temp.Combine("Plain")));
        Assert.Equal(FileKind.Presentation, FileKinds.Of(temp.Combine("Deck.key")));
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public void SniffsHeaders(byte[] bytes, FileKind kind) => Assert.Equal(kind, FileSniffer.KindOfHeader(bytes));

    public static IEnumerable<object[]> Headers() =>
    [
        [new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FileKind.Image],
        [new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, FileKind.Image],
        [System.Text.Encoding.ASCII.GetBytes("%PDF-1.7"), FileKind.Pdf],
        [new byte[] { 0x50, 0x4B, 0x03, 0x04 }, FileKind.Archive],
        [new byte[] { 0x1F, 0x8B, 0x08 }, FileKind.Archive],
        [new byte[] { 0, 0, 0, 0x18 }.Concat(System.Text.Encoding.ASCII.GetBytes("ftypheic")).ToArray(), FileKind.Image],
        [new byte[] { 0, 0, 0, 0x14 }.Concat(System.Text.Encoding.ASCII.GetBytes("ftypqt  ")).ToArray(), FileKind.Video],
    ];

    [Fact]
    public void IgnoresText()
    {
        Assert.Null(FileSniffer.KindOfHeader(System.Text.Encoding.ASCII.GetBytes("BMW service notes")));
        Assert.Null(FileSniffer.KindOfHeader(System.Text.Encoding.ASCII.GetBytes("hello")));
    }

    [Fact]
    public void ClassifiesFilesWithoutExtensions()
    {
        using var temp = new TempFolder();
        var photo = temp.Combine("NoExtension");
        File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]);
        Assert.Equal(FileKind.Image, FileKinds.Of(photo));
        var link = temp.Combine("Link to photo");
        try { File.CreateSymbolicLink(link, photo); }
        catch (UnauthorizedAccessException) { return; } // symlinks need developer mode
        catch (IOException) { return; }
        Assert.Equal(FileKind.Image, FileKinds.Of(link));
    }

    [Fact]
    public void TypeScriptIsNotAVideo()
    {
        using var temp = new TempFolder();
        Assert.Equal(FileKind.Other, FileKinds.Of(temp.File("app.ts", "export const x = 1;")));
        var stream = new byte[376];
        stream[0] = 0x47; stream[188] = 0x47;
        var video = temp.Combine("clip.ts");
        File.WriteAllBytes(video, stream);
        Assert.Equal(FileKind.Video, FileKinds.Of(video));
    }

    [Fact]
    public void IWorkPreviewIsFoundInPackages()
    {
        Assert.Equal("x/QuickLook/Preview.pdf", IWorkPreview.Find(["x/QuickLook/Preview.pdf", "preview.jpg"])?.Path);
        Assert.Equal("Deck/preview.jpg", IWorkPreview.Find(["Deck/preview.jpg"])?.Path);
        Assert.Null(IWorkPreview.Find(["a/b/preview.jpg"]));
    }
}

public class OutputFormatTests
{
    [Fact]
    public void EveryMacFormatExists() => Assert.Equal(46, OutputFormats.All.Count);

    [Fact]
    public void TitlesAndExtensions()
    {
        Assert.Equal("TAR.GZ", Tgz.Title());
        Assert.Equal("tar.gz", Tgz.FileExtension());
        Assert.Equal("UNZIP", Extract.Title());
        Assert.Null(Extract.FileExtension());
        Assert.Equal("JPG", Jpg.Title());
        Assert.Equal("jpg", Jpg.Id());
        Assert.Equal(Heic, OutputFormats.FromId("HEIC"));
        Assert.Null(OutputFormats.FromId("nope"));
    }

    [Fact]
    public void MatchesOwnExtensions()
    {
        Assert.True(Jpg.Matches("jpeg"));
        Assert.True(Tiff.Matches(".TIF"));
        Assert.True(Md.Matches("markdown"));
        Assert.False(Png.Matches("jpg"));
    }

    [Fact]
    public void LossyAndOptionFormats()
    {
        foreach (var f in new[] { Jpg, Heic, Avif, Webp, Mp4, Mov, Mkv, Webm, Avi, Wmv, M4a, Mp3, Ogg, Opus, Wma })
        {
            Assert.True(f.IsLossy());
            Assert.True(f.HasOptions());
        }
        Assert.True(Gif.HasOptions());
        Assert.False(Png.HasOptions());
        Assert.False(Pdf.HasOptions());
    }
}

/// <summary>The Mac's FormatParityTests and ConversionCatalogTests, as the oracle for the wheel's contents.</summary>
public class ConversionCatalogTests
{
    private static string P(string name) => @"C:\nonexistent\" + name;
    private static IReadOnlyList<OutputFormat> Formats(string name, ExternalTools tools) => ConversionCatalog.Formats([P(name)], tools);

    [Fact]
    public void PdfOffersDocumentAndImageFormats() =>
        Assert.Equal([Docx, Jpg, Png, Txt], Formats("Page 001.pdf", Fakes.None).Take(4));

    [Fact]
    public void ImageOmitsItsOwnFormat()
    {
        var formats = Formats("Page 001.jpg", Fakes.None);
        Assert.DoesNotContain(Jpg, formats);
        Assert.Equal([Png, Heic, Tiff, Avif, Bmp, Pdf, Docx, Zip], formats);
    }

    [Fact]
    public void WebPIsOnTheWheelUnlessTheEncoderIsOff()
    {
        Assert.Equal([Png, Jpg, Webp, Heic, Tiff, Avif, Bmp, Pdf, Docx, Zip], Formats("a.gif", ExternalTools.None));
        Assert.DoesNotContain(Webp, Formats("a.png", Fakes.None));
    }

    [Fact]
    public void MixedSelectionsOnlyOfferSharedFormats() =>
        Assert.Equal(new HashSet<OutputFormat> { Jpg, Docx, Zip }, ConversionCatalog.Formats([P("a.png"), P("b.pdf")], Fakes.None).ToHashSet());

    [Fact]
    public void ArchivesOfferExtraction() => Assert.Equal([Extract], Formats("files.zip", Fakes.None));

    [Fact]
    public void FFmpegOnlyFormatsNeedFFmpeg()
    {
        Assert.Equal([Zip], Formats("a.ogg", Fakes.None));
        Assert.Equal([Zip], Formats("a.mkv", Fakes.None));
        Assert.Contains(Webm, Formats("a.mkv", Fakes.All));
        Assert.Contains(Opus, Formats("a.aiff", Fakes.All));
        Assert.DoesNotContain(Ogg, Formats("a.aiff", Fakes.None));
    }

    [Fact]
    public void OfficeFormatsNeedLibreOffice()
    {
        Assert.Equal([Zip], Formats("a.pptx", Fakes.None));
        Assert.Equal([Pdf, Jpg, Png, Zip], Formats("a.pptx", Fakes.All));
        Assert.Equal([Pdf, Pptx, Jpg, Png, Zip], Formats("a.key", Fakes.All));
        Assert.Equal([Zip], Formats("a.xls", Fakes.None));
        Assert.Equal([Xlsx, Tsv, Pdf, Zip], Formats("a.csv", Fakes.None));
        Assert.Equal([Xlsx, Tsv, Pdf, Ods, Zip], Formats("a.csv", Fakes.All));
    }

    [Fact]
    public void DocumentsAndPdfsOfferNewFormats()
    {
        Assert.Equal([Docx, Jpg, Png, Txt, Md, Epub, Pptx, Xlsx, Zip], Formats("a.pdf", Fakes.None));
        Assert.Equal([Pdf, Docx, Txt, Epub, Html, Rtf, Zip], Formats("a.md", Fakes.None));
    }

    [Fact]
    public void PandocAddsOdtOnlyForItsReaders()
    {
        var pandoc = ExternalTools.None with { Pandoc = @"C:\fake\pandoc.exe" };
        Assert.Contains(Odt, Formats("a.docx", pandoc));
        Assert.DoesNotContain(Odt, Formats("a.txt", pandoc));
    }

    [Fact]
    public void FoldersAndOtherFilesOfferArchives()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("Folder"));
        Assert.Equal([Zip, Tar, Tgz], ConversionCatalog.Formats([temp.Combine("Folder")], Fakes.None));
        Assert.Equal([Zip, Tar, Tgz], Formats("a.xyz123", Fakes.None));
    }

    [Fact]
    public void EbooksAreJustFilesWithoutATool()
    {
        Assert.Equal([Zip, Tar, Tgz], Formats("a.epub", Fakes.None));
        var calibre = ExternalTools.None with { EbookConvert = @"C:\fake\ebook-convert.exe" };
        Assert.Equal([Mobi, Azw3, Pdf, Zip], Formats("a.epub", calibre));
    }

    [Fact]
    public void SubtitlesAndSilentVideo()
    {
        Assert.Equal([Txt, Vtt, Zip], Formats("a.srt", Fakes.None));
        Assert.Contains(M4a, ConversionCatalog.Formats([P("a.mp4")], Fakes.All, _ => true));
        Assert.DoesNotContain(M4a, ConversionCatalog.Formats([P("a.mp4")], Fakes.All, _ => false));
    }

    [Fact]
    public void WheelsStayWithinTwelveWedges()
    {
        var everything = Fakes.All with { Pandoc = "p", EbookConvert = "e", Whisper = "w", WhisperModel = "m" };
        foreach (var name in new[] { "a.jpg", "a.pdf", "a.docx", "a.mkv", "a.mov", "a.ogg", "a.aiff", "a.csv", "a.xls", "a.pptx", "a.pages", "a.numbers", "a.key" })
        {
            var formats = Formats(name, everything);
            Assert.True(formats.Count <= ConversionCatalog.MaxWedges, name);
            Assert.Equal(Zip, formats[^1]);
        }
    }

    [Fact]
    public void ZipOnlyWheelsNameTheMissingTool()
    {
        var none = Fakes.None;
        Assert.Equal("MKV needs ffmpeg", MissingToolAdvice.EmptyMessage([P("a.mkv")], [Zip], none));
        Assert.Equal("PowerPoint needs LibreOffice", MissingToolAdvice.EmptyMessage([P("a.pptx")], [Zip], none));
        Assert.Equal("MP4 and MKV need ffmpeg", MissingToolAdvice.EmptyMessage([P("a.mp4"), P("b.mkv")], [Zip], none));
        Assert.Null(MissingToolAdvice.EmptyMessage([P("a.xyz")], [Zip, Tar, Tgz], none));
        Assert.Null(MissingToolAdvice.EmptyMessage([P("a.png")], [Png, Zip], none));
    }
}
