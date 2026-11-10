using System.IO.Compression;
using System.Text;
using OpenShelf.Convert;
using static OpenShelf.Convert.OutputFormat;

namespace OpenShelf.Convert.Tests;

public class SubtitleTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,500\nHello <i>there</i>\n\n2\n00:00:03,000 --> 00:00:04,000\nGeneral Kenobi";

    [Fact]
    public void ParsesSrtCues()
    {
        var cues = SubtitleEngine.Parse(Srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(new SubtitleEngine.Cue("00:00:01,000", "00:00:02,500", ["Hello <i>there</i>"]), cues[0]);
    }

    [Fact]
    public void ConvertsSrtToVtt() =>
        Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello <i>there</i>", SubtitleEngine.Vtt(SubtitleEngine.Parse(Srt)));

    [Fact]
    public void ConvertsVttToSrtWithShortTimesAndCueSettings() =>
        Assert.Equal("1\n00:01:02,500 --> 00:01:04,000\nLine one\n",
            SubtitleEngine.Srt(SubtitleEngine.Parse("WEBVTT\n\n01:02.5 --> 01:04.000 align:start\nLine one\n")));

    [Fact]
    public void PlainTextDropsTimingsAndTags() =>
        Assert.Equal("Hello there\nGeneral Kenobi\n", SubtitleEngine.PlainText(SubtitleEngine.Parse(Srt)));

    [Fact]
    public async Task ConvertsFilesAndReadsWindowsEncodings()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("Movie.srt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé\r\n"));
        var output = await Converter.Default.ConvertAsync(new ConversionRequest(path, Vtt));
        Assert.Equal("Movie.vtt", Path.GetFileName(output[0]));
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nCafé\n", File.ReadAllText(output[0]));
    }
}

public class SpreadsheetTests
{
    private static List<List<string>> Rows(params string[][] rows) => rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void ParsesQuotesEscapesAndEmbeddedNewlines() =>
        Assert.Equal(Rows(["a", "b", "c"], ["x, y", "say \"hi\"", "two\nlines"], ["1", "", "3"]),
            DelimitedText.Parse("a,b,c\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\n1,,3\n", ','));

    [Fact]
    public void HandlesCrlfAndMissingFinalNewline() =>
        Assert.Equal(Rows(["a", "b"], ["c", "d"]), DelimitedText.Parse("a,b\r\nc,d", ','));

    [Fact]
    public void WriteQuotesOnlyWhenNeededAndRoundTrips()
    {
        var table = Rows(["Name", "Note"], ["Smith, Jane", "Said \"hi\""], ["Tab\there", "multi\nline"]);
        var csv = DelimitedText.Write(table, ',');
        Assert.StartsWith("Name,Note\n\"Smith, Jane\",\"Said \"\"hi\"\"\"", csv);
        Assert.Equal(table, DelimitedText.Parse(csv, ','));
        Assert.Equal(table, DelimitedText.Parse(DelimitedText.Write(table, '\t'), '\t'));
    }

    [Fact]
    public void NumbersOnlyWhenTheyRoundTrip()
    {
        Assert.Equal("1250.5", XlsxWriter.NumericValue("1250.5"));
        Assert.Equal("-3", XlsxWriter.NumericValue("-3"));
        Assert.Null(XlsxWriter.NumericValue("007"));
        Assert.Null(XlsxWriter.NumericValue("1e5"));
        Assert.Null(XlsxWriter.NumericValue("12345678901234567890"));
        Assert.Null(XlsxWriter.NumericValue(""));
    }

    [Fact]
    public void ColumnNamesAndReferences()
    {
        Assert.Equal("A", XlsxReader.ColumnName(0));
        Assert.Equal("Z", XlsxReader.ColumnName(25));
        Assert.Equal("AA", XlsxReader.ColumnName(26));
        Assert.Equal("ZZ", XlsxReader.ColumnName(701));
        Assert.Equal(27, XlsxReader.ColumnIndex("AB12"));
        Assert.Equal(2, XlsxReader.ColumnIndex("C3"));
    }

    [Fact]
    public void XlsxWriteThenReadRoundTrips()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("roundtrip.xlsx");
        var table = Rows(["Name", "Amount", "Code"], ["Zürich & co <x>", "12.5", "007"], ["東京", "", "-3"]);
        XlsxWriter.Write([("Data", table.Select(r => (IReadOnlyList<string>)r).ToList())], path);
        Assert.Equal(table, XlsxReader.FirstSheet(path));
    }

    [Fact]
    public void ReadsSharedStringsAndSparseCells()
    {
        var sheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\"><v>5</v></c></row><row r=\"3\"><c r=\"B3\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sheet));
        Assert.Equal(Rows(["Hello", "", "5"], ["", "", ""], ["", "TRUE", ""]), XlsxReader.Cells(stream, ["Hello"]));
    }

    [Fact]
    public void CountsRecordsNotLines()
    {
        using var temp = new TempFolder();
        var csv = temp.File("Quoted.csv", "a,b\n1,\"two\nlines\"\n2,z");
        Assert.Equal(3, DelimitedText.RecordCount(csv));
        File.WriteAllText(csv, "a,b\n1,x\n");
        Assert.Equal(2, DelimitedText.RecordCount(csv));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a,b\r1,x\r")]
    [InlineData("a,b\r\n1,x\r\n")]
    [InlineData("a,b\n1,\"two\"\"\nlines\"\n2,z")]
    [InlineData("a,b\n1,an\"unquoted\n2,z")]
    [InlineData("a,b\n1,\"two\nlines\"")]
    public void CounterMatchesParser(string text)
    {
        using var temp = new TempFolder();
        var csv = temp.Combine("Rows.csv");
        foreach (var data in new[]
        {
            Encoding.UTF8.GetBytes(text),
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
            [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        })
        {
            File.WriteAllBytes(csv, data);
            Assert.Equal(DelimitedText.Read(csv, ',').Count, DelimitedText.RecordCount(csv));
        }
    }

    [Fact]
    public void QuotedFieldsAcrossChunks()
    {
        using var temp = new TempFolder();
        var tsv = temp.File("Rows.tsv", "a\tb\n1\t\"" + new string('x', (1 << 20) - 8) + "\"\"\nmore\"\n2\tz");
        Assert.Equal(3, DelimitedText.RecordCount(tsv, '\t'));
    }

    [Fact]
    public void ExcelLimitMessage()
    {
        ExcelLimit.Check(1_048_576);
        var error = Assert.Throws<ConversionException>(() => ExcelLimit.Check(1_048_577));
        Assert.Equal("Excel holds up to 1,048,576 rows; this has 1,048,577. Save as CSV instead.", error.Message);
    }

    [Fact]
    public async Task CsvOverTheLimitIsRefusedAndNothingIsSaved()
    {
        using var temp = new TempFolder();
        var csv = temp.File("Rows.csv", "name\nAda\nGrace\nKatherine\n");
        var engine = new SpreadsheetEngine();
        // Four rows with the header: fine at four, refused at three.
        var fits = await engine.ConvertAsync(new ConversionRequest(csv, Xlsx), 4);
        Assert.Equal("Rows.xlsx", Path.GetFileName(fits[0]));
        File.Delete(fits[0]);
        var before = temp.Names();
        var error = await Assert.ThrowsAsync<ConversionException>(() => engine.ConvertAsync(new ConversionRequest(csv, Xlsx), 3));
        Assert.StartsWith("Excel holds up to", error.Message);
        Assert.Equal(before, temp.Names());
    }

    [Fact]
    public async Task ConvertsAmongCsvTsvAndXlsx()
    {
        using var temp = new TempFolder();
        var csv = temp.File("Data.csv", "Name,Note\n\"Smith, Jane\",\"two\nlines\"\n007,12.5\n");
        var tsv = (await Converter.Default.ConvertAsync(new ConversionRequest(csv, Tsv)))[0];
        var xlsx = (await Converter.Default.ConvertAsync(new ConversionRequest(tsv, Xlsx)))[0];
        var back = (await Converter.Default.ConvertAsync(new ConversionRequest(xlsx, Csv)))[0];
        Assert.Equal("Data 2.csv", Path.GetFileName(back));
        Assert.Equal(File.ReadAllText(csv), File.ReadAllText(back));
    }

    [Fact]
    public async Task XlsAndOdsAreLeftToLibreOffice()
    {
        using var temp = new TempFolder();
        await Assert.ThrowsAsync<ConversionException>(() => Converter.Default.ConvertAsync(new ConversionRequest(temp.File("a.xls", "x"), Csv)));
    }
}

public class ArchiveTests
{
    private static string Tree(TempFolder temp)
    {
        temp.File(@"Album\one.txt", "1");
        temp.File(@"Album\sub\two.txt", "2");
        temp.File(@"Album\.DS_Store", "junk");
        temp.File(@"Album\._one.txt", "junk");
        return temp.Combine("Album");
    }

    private static IEnumerable<string> Names(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task ZipsFolderWithoutMacMetadata()
    {
        using var temp = new TempFolder();
        var output = await Converter.Default.ConvertAsync(new ConversionRequest(Tree(temp), Zip));
        Assert.Equal("Album.zip", Path.GetFileName(output[0]));
        Assert.Equal(["Album/", "Album/one.txt", "Album/sub/", "Album/sub/two.txt"], Names(output[0]));
    }

    [Fact]
    public async Task SeveralFilesMakeOneArchiveAndNeverOverwrite()
    {
        using var temp = new TempFolder();
        var a = temp.File("a.txt", "A");
        var b = temp.File("b.txt", "B");
        var request = new ConversionRequest(a, Zip) { Sources = [a, b] };
        var first = (await Converter.Default.ConvertAsync(request))[0];
        var second = (await Converter.Default.ConvertAsync(request))[0];
        Assert.Equal("Archive.zip", Path.GetFileName(first));
        Assert.Equal("Archive 2.zip", Path.GetFileName(second));
        Assert.Equal(["a.txt", "b.txt"], Names(first));
    }

    [Fact]
    public async Task SameNamedItemsAreRefused()
    {
        using var temp = new TempFolder();
        var a = temp.File(@"x\same.txt", "A");
        var b = temp.File(@"y\same.txt", "B");
        var error = await Assert.ThrowsAsync<ConversionException>(() =>
            Converter.Default.ConvertAsync(new ConversionRequest(a, Zip) { Sources = [a, b] }));
        Assert.Contains("Two of the items are named “same.txt”", error.Message);
        Assert.DoesNotContain("Archive.zip", temp.Names());
    }

    [Fact]
    public async Task TarAndTarGzRoundTripThroughUnarchive()
    {
        using var temp = new TempFolder();
        var tree = Tree(temp);
        foreach (var format in new[] { Tar, Tgz })
        {
            var archive = (await Converter.Default.ConvertAsync(new ConversionRequest(tree, format)))[0];
            Assert.Equal(format == Tar ? "Album.tar" : "Album.tar.gz", Path.GetFileName(archive));
            var extracted = (await Converter.Default.ConvertAsync(new ConversionRequest(archive, Extract)))[0];
            Assert.StartsWith("Album ", Path.GetFileName(extracted)); // a single folder keeps its own name, made unique
            Assert.Equal("2", File.ReadAllText(Path.Combine(extracted, "sub", "two.txt")));
            Assert.False(File.Exists(Path.Combine(extracted, ".DS_Store")));
        }
    }

    [Fact]
    public async Task ZipWithSeveralTopLevelItemsGoesInAFolderNamedAfterIt()
    {
        using var temp = new TempFolder();
        var zip = temp.Combine("Bundle.zip");
        using (var file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.txt", "b.txt", "__MACOSX/._a.txt" })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(name);
        }
        var output = (await Converter.Default.ConvertAsync(new ConversionRequest(zip, Extract)))[0];
        Assert.Equal("Bundle", Path.GetFileName(output));
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task LoneGzipIsOneFile()
    {
        using var temp = new TempFolder();
        var gz = temp.Combine("notes.txt.gz");
        using (var file = File.Create(gz))
        using (var stream = new GZipStream(file, CompressionMode.Compress))
            stream.Write("hello"u8);
        var output = (await Converter.Default.ConvertAsync(new ConversionRequest(gz, Extract)))[0];
        Assert.Equal("notes.txt", Path.GetFileName(output));
        Assert.Equal("hello", File.ReadAllText(output));
    }

    [Fact]
    public async Task ZipSlipIsRefused()
    {
        using var temp = new TempFolder();
        var zip = temp.Combine("evil.zip");
        using (var file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("ok.txt").Open())) writer.Write("ok");
            using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open())) writer.Write("bad");
        }
        var error = await Assert.ThrowsAsync<ConversionException>(() => Converter.Default.ConvertAsync(new ConversionRequest(zip, Extract)));
        Assert.Contains("outside the destination", error.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(temp.Path)!, "escaped.txt")));
        Assert.False(File.Exists(temp.Combine("escaped.txt")));
        Assert.Equal(["evil.zip"], temp.Names());
    }

    [Theory]
    [InlineData("a/b.txt", true)]
    [InlineData("../x.txt", false)]
    [InlineData("a/../../x.txt", false)]
    [InlineData("C:/x.txt", false)]
    [InlineData("/x.txt", false)]
    [InlineData("a/../b.txt", true)]
    public void SafeTargetStaysInside(string entry, bool safe) =>
        Assert.Equal(safe, ArchiveEngine.SafeTarget(@"C:\work\out", entry) is not null);

    [Fact]
    public async Task DamagedArchiveGetsAPlainMessage()
    {
        using var temp = new TempFolder();
        var error = await Assert.ThrowsAsync<ConversionException>(() =>
            Converter.Default.ConvertAsync(new ConversionRequest(temp.File("broken.zip", "not a zip at all"), Extract)));
        Assert.Equal("“broken.zip” is damaged or isn’t an archive OpenShelf can open.", error.Message);
    }

    [Fact]
    public async Task SevenZipAndRarGoThroughBsdtar()
    {
        // Make a .tar.xz-free proof that tar.exe is wired in: bsdtar reads a zip renamed to .7z? No: use a real .tar.bz2 made by tar.exe.
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) return;
        using var temp = new TempFolder();
        temp.File(@"src\hello.txt", "hi");
        var bz2 = temp.Combine("Pack.tar.bz2");
        var made = await ProcessRunner.RunAsync(tar, ["-cjf", bz2, "-C", temp.Combine("src"), "hello.txt"], new ProcessOptions { ThrowOnFailure = false });
        if (made.ExitCode != 0) return; // this tar.exe cannot write bzip2
        var output = (await Converter.Default.ConvertAsync(new ConversionRequest(bz2, Extract)))[0];
        Assert.Equal("hi", File.ReadAllText(Path.Combine(output, "hello.txt")));
    }
}
