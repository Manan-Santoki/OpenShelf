using OpenShelf.Convert;
using static OpenShelf.Convert.OutputFormat;

namespace OpenShelf.Convert.Tests;

/// <summary>An engine for tests: turns .txt into whatever format, with scripted behaviour per file name.</summary>
internal sealed class ScriptedEngine(Func<ConversionRequest, IProgress<double>?, CancellationToken, Task<IReadOnlyList<string>>> run) : IConversionEngine
{
    public bool CanConvert(FileKind kind, OutputFormat format) => kind == FileKind.Document && format == Pdf;
    public Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken ct) => run(request, progress, ct);
}

public class EngineRegistryTests
{
    private static Converter ConverterWith(params IConversionEngine[] engines)
    {
        var registry = new EngineRegistry();
        foreach (var engine in engines) registry.Register(engine);
        return new Converter(registry, new ExternalToolsProvider(() => Fakes.None));
    }

    [Fact]
    public async Task UnsupportedConversionsSayWhat()
    {
        using var temp = new TempFolder();
        var error = await Assert.ThrowsAsync<ConversionException>(() => ConverterWith().ConvertAsync(new ConversionRequest(temp.File("a.txt"), Pdf)));
        Assert.Equal("This conversion isn’t supported: TXT to PDF.", error.Message);
    }

    [Fact]
    public async Task HigherPriorityEnginesAreAskedFirst()
    {
        using var temp = new TempFolder();
        var registry = new EngineRegistry();
        registry.Register(new ScriptedEngine((r, _, _) => Task.FromResult<IReadOnlyList<string>>(["low"])));
        registry.Register(new ScriptedEngine((r, _, _) => Task.FromResult<IReadOnlyList<string>>(["high"])), priority: 10);
        var result = await new Converter(registry, new ExternalToolsProvider(() => Fakes.None)).ConvertAsync(new ConversionRequest(temp.File("a.txt"), Pdf));
        Assert.Equal(["high"], result);
    }

    [Fact]
    public async Task EveryEngineSeesTheConvertersTools()
    {
        using var temp = new TempFolder();
        ExternalTools? seen = null;
        var converter = new Converter(new EngineRegistry(), new ExternalToolsProvider(() => ExternalTools.None with { Pandoc = "fake-pandoc" }));
        converter.Registry.Register(new ScriptedEngine((r, _, _) => { seen = r.Tools; return Task.FromResult<IReadOnlyList<string>>([]); }));
        await converter.ConvertAsync(new ConversionRequest(temp.File("a.txt"), Pdf));
        Assert.Equal("fake-pandoc", seen!.Pandoc);
    }

    [Fact]
    public void DefaultRegistryCoversTheFirstWave()
    {
        var registry = EngineRegistry.Default;
        Assert.True(registry.CanConvert(FileKind.Image, Heic));
        Assert.True(registry.CanConvert(FileKind.Folder, Tgz));
        Assert.True(registry.CanConvert(FileKind.Archive, Extract));
        Assert.True(registry.CanConvert(FileKind.Subtitle, Vtt));
        Assert.True(registry.CanConvert(FileKind.Spreadsheet, Xlsx));
        Assert.False(registry.CanConvert(FileKind.Video, Mp4));
    }
}

public class ConversionRunnerTests
{
    private static string Srt(TempFolder temp, string name) => temp.File(name, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");

    [Fact]
    public async Task ConvertsEachFileBesideItAndReportsProgress()
    {
        using var temp = new TempFolder();
        var a = Srt(temp, "A.srt");
        var b = Srt(temp, "B.srt");
        var reports = new List<BatchProgress>();
        var result = await new ConversionRunner().ConvertAsync([a, b], Vtt, progress: new Sync(reports));
        Assert.False(result.Cancelled);
        Assert.Equal(["A.vtt", "B.vtt"], result.Saved.Select(Path.GetFileName));
        Assert.Equal(["A.srt", "A.vtt", "B.srt", "B.vtt"], temp.Names());
        Assert.Equal(2, reports[^1].Completed);
        Assert.Equal(2, reports[^1].Total);
        Assert.Contains(reports, r => r.Completed == 1 && r.Fraction is 0);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.Second.Completed >= pair.First.Completed));
    }

    private sealed class Sync(List<BatchProgress> reports) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => reports.Add(value);
    }

    [Fact]
    public async Task NeverOverwritesAndLeavesNoStagingBehind()
    {
        using var temp = new TempFolder();
        var a = Srt(temp, "A.srt");
        temp.File("A.vtt", "mine");
        var result = await new ConversionRunner().ConvertAsync([a], Vtt);
        Assert.Equal("A 2.vtt", Path.GetFileName(result.Saved[0]));
        Assert.Equal("mine", File.ReadAllText(temp.Combine("A.vtt")));
        Assert.Equal(["A 2.vtt", "A.srt", "A.vtt"], temp.Names());
    }

    [Fact]
    public async Task AFailingFileDoesNotStopTheRest()
    {
        using var temp = new TempFolder();
        var good1 = Srt(temp, "Good1.srt");
        var missing = temp.Combine("Missing.srt");
        var good2 = Srt(temp, "Good2.srt");
        var result = await new ConversionRunner().ConvertAsync([good1, missing, good2], Txt);
        Assert.Equal(["Good1.txt", "Good2.txt"], result.Saved.Select(Path.GetFileName));
        Assert.Single(result.Failures);
        Assert.StartsWith("Missing.srt: ", result.Failures[0]);
        Assert.Contains("couldn’t be read", result.Failures[0]);
    }

    [Fact]
    public async Task SeveralFilesIntoAnArchiveMakeOneArchive()
    {
        using var temp = new TempFolder();
        var files = new[] { Srt(temp, "A.srt"), Srt(temp, "B.srt"), Srt(temp, "C.srt") };
        foreach (var format in new[] { Zip, Tar, Tgz })
        {
            var result = await new ConversionRunner().ConvertAsync(files, format);
            Assert.Single(result.Jobs);
            Assert.Single(result.Saved);
            Assert.StartsWith("Archive", Path.GetFileName(result.Saved[0]));
            Assert.EndsWith("." + format.FileExtension(), result.Saved[0]);
        }
        var perFile = await new ConversionRunner().ConvertAsync(files, Extract);
        Assert.Equal(3, perFile.Jobs.Count);
    }

    [Fact]
    public async Task HalfWrittenOutputNeverAppears()
    {
        using var temp = new TempFolder();
        var source = temp.File("Doc.txt", "x");
        string? staged = null;
        var engine = new ScriptedEngine((request, _, _) =>
        {
            staged = request.OutputPath("Doc.pdf");
            File.WriteAllText(staged, "half");
            Assert.True(Directory.Exists(request.OutputDirectory));
            Assert.NotEqual(request.SourceDirectory, Path.GetDirectoryName(staged));
            Assert.True(new DirectoryInfo(request.OutputDirectory!).Attributes.HasFlag(FileAttributes.Hidden));
            throw new IOException("disk went away");
        });
        var registry = new EngineRegistry();
        registry.Register(engine);
        var runner = new ConversionRunner(new Converter(registry, new ExternalToolsProvider(() => Fakes.None)));
        var result = await runner.ConvertAsync([source], Pdf);
        Assert.Empty(result.Saved);
        Assert.Single(result.Failures);
        Assert.Equal(["Doc.txt"], temp.Names());
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task OutputsAppearOnlyWhenTheEngineIsDone()
    {
        using var temp = new TempFolder();
        var source = temp.File("Doc.txt", "x");
        var duringRun = new List<string>();
        var registry = new EngineRegistry();
        registry.Register(new ScriptedEngine((request, progress, _) =>
        {
            var path = request.OutputPath("Doc.pdf");
            File.WriteAllText(path, "done");
            duringRun.AddRange(Directory.GetFileSystemEntries(request.SourceDirectory).Select(p => Path.GetFileName(p)!).Where(n => !n.StartsWith(".os-")));
            progress?.Report(0.5);
            return Task.FromResult<IReadOnlyList<string>>([path]);
        }));
        var reports = new List<BatchProgress>();
        var result = await new ConversionRunner(new Converter(registry, new ExternalToolsProvider(() => Fakes.None)))
            .ConvertAsync([source], Pdf, progress: new Sync(reports));
        Assert.Equal(["Doc.txt"], duringRun);
        Assert.Equal(["Doc.pdf", "Doc.txt"], temp.Names());
        Assert.Equal(result.Saved[0], temp.Combine("Doc.pdf"));
        Assert.Contains(reports, r => r.Fraction is 0.5 && r.Completed == 0);
    }

    [Fact]
    public async Task CancellingKeepsWhatWasSavedAndStops()
    {
        using var temp = new TempFolder();
        var files = new[] { temp.File("A.txt"), temp.File("B.txt"), temp.File("C.txt") };
        using var cts = new CancellationTokenSource();
        var registry = new EngineRegistry();
        registry.Register(new ScriptedEngine(async (request, _, ct) =>
        {
            var path = request.OutputPath(request.SourceStem + ".pdf");
            if (request.SourceStem == "B")
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }
            File.WriteAllText(path, "x");
            return [path];
        }));
        var saved = new List<IReadOnlyList<string>>();
        var result = await new ConversionRunner(new Converter(registry, new ExternalToolsProvider(() => Fakes.None)))
            .ConvertAsync(files, Pdf, cancellationToken: cts.Token, onJobSaved: saved.Add);
        Assert.True(result.Cancelled);
        Assert.Equal(["A.pdf"], result.Saved.Select(Path.GetFileName));
        Assert.Single(saved);
        Assert.Equal(["A.pdf", "A.txt", "B.txt", "C.txt"], temp.Names());
    }

    [Fact]
    public async Task BatchesQueueBehindEachOther()
    {
        using var temp = new TempFolder();
        var running = 0;
        var overlapped = false;
        var registry = new EngineRegistry();
        registry.Register(new ScriptedEngine(async (request, _, _) =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            await Task.Delay(100);
            Interlocked.Decrement(ref running);
            var path = request.OutputPath(request.SourceStem + ".pdf");
            File.WriteAllText(path, "x");
            return [path];
        }));
        var runner = new ConversionRunner(new Converter(registry, new ExternalToolsProvider(() => Fakes.None)));
        var one = runner.ConvertAsync([temp.File("A.txt")], Pdf);
        var two = runner.ConvertAsync([temp.File("B.txt")], Pdf);
        await Task.WhenAll(one, two);
        Assert.False(overlapped);
        Assert.Equal(["A.pdf", "A.txt", "B.pdf", "B.txt"], temp.Names());
    }

    [Fact]
    public async Task ExtractedFoldersAndUnusualNamesMoveIntoPlace()
    {
        using var temp = new TempFolder();
        temp.File(@"Pack\Album.2024\song.txt", "la");
        var zip = (await new ConversionRunner().ConvertAsync([temp.Combine("Pack")], Zip)).Saved[0];
        Directory.Delete(temp.Combine("Pack"), true);
        var extracted = (await new ConversionRunner().ConvertAsync([zip], Extract)).Saved[0];
        Assert.Equal("Pack", Path.GetFileName(extracted));
        Assert.Equal("la", File.ReadAllText(Path.Combine(extracted, "Album.2024", "song.txt")));
    }

    [Fact]
    public void TitlesMatchTheMac()
    {
        Assert.Equal("Converting to JPG", ConversionRunner.Title(Jpg));
        Assert.Equal("Compressing to TAR.GZ", ConversionRunner.Title(Tgz));
        Assert.Equal("Extracting", ConversionRunner.Title(Extract));
    }

    [Fact]
    public void OutputFileNameSplitting()
    {
        Assert.Equal(("Archive", "tar.gz"), OutputFiles.SplitName("Archive.tar.gz"));
        Assert.Equal(("Photo", "jpg"), OutputFiles.SplitName("Photo.jpg"));
        Assert.Equal(("Folder", (string?)null), OutputFiles.SplitName("Folder"));
        Assert.Equal((".hidden", (string?)null), OutputFiles.SplitName(".hidden"));
    }
}
