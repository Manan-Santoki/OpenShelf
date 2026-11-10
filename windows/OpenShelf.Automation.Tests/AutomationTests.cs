using Microsoft.Win32;
using OpenShelf.Automation;
using OpenShelf.Convert;

namespace OpenShelf.Automation.Tests;

public class ArrivalTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static ArrivalTracker.Entry E(string name, ulong? id = null, long size = 10, bool busy = false) => new(name, id, size, true, busy);

    [Fact]
    public void ExistingFilesAreNeverReported()
    {
        var t = new ArrivalTracker([E("a.png", 1)]);
        Assert.Empty(t.Observe([E("a.png", 1)], T0));
        Assert.Empty(t.Observe([E("a.png", 1)], T0.AddSeconds(10)));
    }

    [Fact]
    public void NewFileIsReportedAfterSizeIsStableForOneSecond()
    {
        var t = new ArrivalTracker([]);
        Assert.Empty(t.Observe([E("b.png", 2)], T0));
        Assert.True(t.HasPending);
        Assert.Empty(t.Observe([E("b.png", 2)], T0.AddMilliseconds(500)));
        Assert.Equal(["b.png"], t.Observe([E("b.png", 2)], T0.AddMilliseconds(1000)));
        Assert.False(t.HasPending);
        Assert.Empty(t.Observe([E("b.png", 2)], T0.AddSeconds(5)));
    }

    [Fact]
    public void GrowingFileRestartsTheWait()
    {
        var t = new ArrivalTracker([]);
        t.Observe([E("c.zip", 3, 10)], T0);
        t.Observe([E("c.zip", 3, 20)], T0.AddMilliseconds(900));
        Assert.Empty(t.Observe([E("c.zip", 3, 20)], T0.AddMilliseconds(1500)));
        Assert.Equal(["c.zip"], t.Observe([E("c.zip", 3, 20)], T0.AddMilliseconds(1900)));
    }

    [Fact]
    public void EmptyFileWaitsFiveSeconds()
    {
        var t = new ArrivalTracker([]);
        t.Observe([E("d.txt", 4, 0)], T0);
        Assert.Empty(t.Observe([E("d.txt", 4, 0)], T0.AddSeconds(4)));
        Assert.Equal(["d.txt"], t.Observe([E("d.txt", 4, 0)], T0.AddSeconds(5)));
    }

    [Fact]
    public void BusyFileIsNeverReportedUntilFree()
    {
        var t = new ArrivalTracker([]);
        t.Observe([E("e.mp4", 5, 10, busy: true)], T0);
        Assert.Empty(t.Observe([E("e.mp4", 5, 10, busy: true)], T0.AddSeconds(30)));
        t.Observe([E("e.mp4", 5, 10)], T0.AddSeconds(31));
        Assert.Equal(["e.mp4"], t.Observe([E("e.mp4", 5, 10)], T0.AddSeconds(32)));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("~$Report.docx")]
    [InlineData("movie.mp4.crdownload")]
    [InlineData("a.PART")]
    [InlineData("x.download")]
    [InlineData("x.opdownload")]
    [InlineData("x.tmp")]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    public void TemporaryNamesAreIgnored(string name)
    {
        Assert.True(ArrivalFilter.IsIgnored(name));
        var t = new ArrivalTracker([]);
        t.Observe([E(name, 9)], T0);
        Assert.Empty(t.Observe([E(name, 9)], T0.AddSeconds(10)));
    }

    [Fact]
    public void FinishedFileThatReplacesAPartialCountsAsNew()
    {
        var t = new ArrivalTracker([]);
        t.Observe([E("pic.jpg.crdownload", 6)], T0);
        t.Observe([E("pic.jpg", 6)], T0.AddSeconds(1));
        Assert.Equal(["pic.jpg"], t.Observe([E("pic.jpg", 6)], T0.AddSeconds(2.5)));
    }

    [Fact]
    public void RenamedFileIsNotNew()
    {
        var t = new ArrivalTracker([E("old.png", 7)]);
        t.Observe([E("new.png", 7)], T0);
        Assert.Empty(t.Observe([E("new.png", 7)], T0.AddSeconds(10)));
        Assert.False(t.HasPending);
    }

    [Fact]
    public void SavedOverInPlaceIsNotNew()
    {
        var t = new ArrivalTracker([E("a.png", 1)]);
        Assert.Empty(t.Observe([E("a.png", 99, 500)], T0));
        Assert.Empty(t.Observe([E("a.png", 99, 500)], T0.AddSeconds(10)));
    }

    [Fact]
    public void DeletedThenRecreatedNameCountsAsNew()
    {
        var t = new ArrivalTracker([E("a.png", 1)]);
        t.Observe([], T0);
        t.Observe([E("a.png", 50)], T0.AddSeconds(1));
        Assert.Equal(["a.png"], t.Observe([E("a.png", 50)], T0.AddSeconds(2.5)));
    }

    [Fact]
    public void ReadyFilesAreInNaturalOrder()
    {
        var t = new ArrivalTracker([]);
        var names = new[] { "shot 10.png", "shot 2.png", "Shot 1.png" };
        t.Observe(names.Select((n, i) => E(n, (ulong)(20 + i))), T0);
        Assert.Equal(["Shot 1.png", "shot 2.png", "shot 10.png"], t.Observe(names.Select((n, i) => E(n, (ulong)(20 + i))), T0.AddSeconds(2)));
    }

    [Fact]
    public void DirectoriesAreNotFiles()
    {
        var t = new ArrivalTracker([]);
        t.Observe([new ArrivalTracker.Entry("sub", 1, 0, IsRegularFile: false)], T0);
        Assert.False(t.HasPending);
    }
}

public class WatchedFolderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osauto-" + Guid.NewGuid().ToString("N"));
    private static readonly ArrivalTimings Fast = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(80));

    public WatchedFolderServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static async Task<FolderArrival?> Next(System.Collections.Concurrent.BlockingCollection<FolderArrival> q, int ms = 6000) =>
        await Task.Run(() => q.TryTake(out var a, ms) ? a : null);

    [Fact]
    public async Task ReportsNewFileNotExistingOnesAndIgnoresPartials()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "w")).FullName;
        File.WriteAllText(Path.Combine(folder, "old.txt"), "old");
        var q = new System.Collections.Concurrent.BlockingCollection<FolderArrival>();
        using var service = new WatchedFolderService(() => [new WatchedFolderSpec(folder, true, "convert.jpg")], q.Add, Fast);
        service.Start();

        File.WriteAllText(Path.Combine(folder, "movie.mp4.crdownload"), "partial");
        File.WriteAllText(Path.Combine(folder, ".hidden"), "x");
        File.WriteAllText(Path.Combine(folder, "new.png"), "data");

        var a = await Next(q);
        Assert.NotNull(a);
        Assert.Equal("convert.jpg", a!.Action);
        Assert.Equal([Path.Combine(folder, "new.png")], a.Files);

        // The partial download finishes by being renamed: now it counts.
        File.Move(Path.Combine(folder, "movie.mp4.crdownload"), Path.Combine(folder, "movie.mp4"));
        var b = await Next(q);
        Assert.Equal([Path.Combine(folder, "movie.mp4")], b!.Files);
        Assert.Null(await Next(q, 800));
    }

    [Fact]
    public async Task FileHeldOpenForWritingWaitsUntilClosed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "busy")).FullName;
        var q = new System.Collections.Concurrent.BlockingCollection<FolderArrival>();
        using var service = new WatchedFolderService(() => [new WatchedFolderSpec(folder)], q.Add, Fast);
        service.Start();
        var path = Path.Combine(folder, "big.bin");
        using (var w = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            w.Write(new byte[100]);
            w.Flush();
            Assert.Null(await Next(q, 1500));
        }
        var a = await Next(q);
        Assert.Equal([path], a!.Files);
    }

    [Fact]
    public async Task OutputsOpenShelfWroteAreIgnored()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var q = new System.Collections.Concurrent.BlockingCollection<FolderArrival>();
        using var service = new WatchedFolderService(() => [new WatchedFolderSpec(folder)], q.Add, Fast);
        service.Start();
        var mine = Path.Combine(folder, "a.jpg");
        service.NoteOutputs([mine]);
        File.WriteAllText(mine, "converted");
        File.WriteAllText(Path.Combine(folder, "b.jpg"), "download");
        var a = await Next(q);
        Assert.Equal([Path.Combine(folder, "b.jpg")], a!.Files);
        Assert.Null(await Next(q, 600));
    }

    [Fact]
    public void MissingFolderDoesNotThrowAndIsReportedUnavailable()
    {
        var missing = Path.Combine(_root, "nope");
        using var service = new WatchedFolderService(() => [new WatchedFolderSpec(missing), new WatchedFolderSpec("Z:\\definitely\\not\\here"), new WatchedFolderSpec("")], _ => { }, Fast);
        service.Start();
        Assert.Contains(service.Unavailable, p => string.Equals(p, missing, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FolderAppearingLaterIsPickedUpOnReconcile()
    {
        var later = Path.Combine(_root, "later");
        var q = new System.Collections.Concurrent.BlockingCollection<FolderArrival>();
        using var service = new WatchedFolderService(() => [new WatchedFolderSpec(later)], q.Add, Fast);
        service.Start();
        Assert.Single(service.Unavailable);
        Directory.CreateDirectory(later);
        service.Reconcile();
        Assert.Empty(service.Unavailable);
        File.WriteAllText(Path.Combine(later, "x.txt"), "x");
        Assert.NotNull(await Next(q));
    }

    [Fact]
    public async Task ReconcileAppliesSettingsChangesLive()
    {
        var f1 = Directory.CreateDirectory(Path.Combine(_root, "f1")).FullName;
        var f2 = Directory.CreateDirectory(Path.Combine(_root, "f2")).FullName;
        var specs = new List<WatchedFolderSpec> { new(f1, true, null) };
        var q = new System.Collections.Concurrent.BlockingCollection<FolderArrival>();
        using var service = new WatchedFolderService(() => specs.ToList(), q.Add, Fast);
        service.Start();

        specs[0] = new WatchedFolderSpec(f1, true, "tool.compress");   // action change, same monitor
        specs.Add(new WatchedFolderSpec(f2, true, "convert.png"));      // folder added
        service.Reconcile();
        File.WriteAllText(Path.Combine(f1, "1.txt"), "1");
        File.WriteAllText(Path.Combine(f2, "2.txt"), "2");
        var got = new[] { await Next(q), await Next(q) }.Select(a => (a!.Action, Path.GetFileName(a.Files[0]))).OrderBy(x => x.Item2).ToArray();
        Assert.Equal([("tool.compress", "1.txt"), ("convert.png", "2.txt")], got);

        specs.Clear(); // all removed: nothing more arrives
        service.Reconcile();
        File.WriteAllText(Path.Combine(f1, "3.txt"), "3");
        Assert.Null(await Next(q, 800));
    }

    [Fact]
    public void DecodesTheSettingsJson()
    {
        var list = WatchedFolderJson.Decode("[{\"path\":\"C:\\\\a\",\"enabled\":false,\"action\":\"convert.jpg\"},{\"path\":\"c:\\\\A\\\\\"},{\"nope\":1},{\"path\":\"C:\\\\b\",\"action\":null}]");
        Assert.Equal(2, list.Count);
        Assert.False(list[0].Enabled);
        Assert.Equal("convert.jpg", list[0].Action);
        Assert.True(list[1].Enabled);
        Assert.Null(list[1].Action);
        Assert.Empty(WatchedFolderJson.Decode("garbage"));
        Assert.Empty(WatchedFolderJson.Decode(null));
    }
}

public class CommandLineTests
{
    private static readonly string Cwd = @"C:\work";
    private static bool Exists(string p) => !p.Contains("missing");
    private static ParseResult P(params string[] args) => CommandLine.Parse(args, Cwd, Exists);

    [Fact]
    public void NoArgumentsMeansNormalStart() => Assert.Equal(ParseOutcome.None, P().Outcome);

    [Theory]
    [InlineData("--help")] [InlineData("-h")] [InlineData("/?")] [InlineData("help")]
    public void HelpForms(string flag)
    {
        var r = P(flag);
        Assert.Equal(ParseOutcome.Help, r.Outcome);
        Assert.Contains("convert --to", r.Message);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void ConvertParsesFormatAndResolvesPaths()
    {
        var r = P("convert", "--to", "jpg", "a.png", @"D:\x\b.png", "a.png");
        Assert.Equal(ParseOutcome.Command, r.Outcome);
        Assert.Equal(CommandKind.Convert, r.Command!.Kind);
        Assert.Equal(OutputFormat.Jpg, r.Command.Format);
        Assert.Equal([@"C:\work\a.png", @"D:\x\b.png"], r.Command.Paths);
    }

    [Theory]
    [InlineData("--to=jpeg", OutputFormat.Jpg)]
    [InlineData("--to=PNG", OutputFormat.Png)]
    [InlineData("--to=tar.gz", OutputFormat.Tgz)]
    [InlineData("--to=.pdf", OutputFormat.Pdf)]
    public void FormatSpellings(string option, OutputFormat expected) =>
        Assert.Equal(expected, P("convert", option, "a.png").Command!.Format);

    [Fact]
    public void ShortOptionAndDoubleDash()
    {
        var r = P("convert", "-t", "png", "--", "--weird.png");
        Assert.Equal([@"C:\work\--weird.png"], r.Command!.Paths);
    }

    [Fact]
    public void UnknownFormatListsValidOnesAndSuggests()
    {
        var r = P("convert", "--to", "jgp", "a.png");
        Assert.Equal(ParseOutcome.Error, r.Outcome);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("Unknown format 'jgp'", r.Message);
        Assert.Contains("Did you mean 'jpg'", r.Message);
        Assert.Contains("png", r.Message);
        Assert.Contains("mp4", r.Message);
        Assert.DoesNotContain("pages,", r.Message);
    }

    [Fact]
    public void ConvertWithoutFormatOrFilesIsAnError()
    {
        Assert.Contains("--to", P("convert", "a.png").Message);
        Assert.Contains("at least one file", P("convert", "--to", "png").Message);
        Assert.Contains("needs a format", P("convert", "--to").Message);
    }

    [Fact]
    public void ShelfAndWheel()
    {
        Assert.Equal(CommandKind.Shelf, P("shelf", "a.png", "b.png").Command!.Kind);
        var w = P("WHEEL", "a.png").Command!;
        Assert.Equal(CommandKind.Wheel, w.Kind);
        Assert.Null(w.Format);
        Assert.Contains("--help", P("shelf").Message);
    }

    [Fact]
    public void ErrorsForUnknownCommandOptionAndMissingFile()
    {
        Assert.Contains("Unknown command 'frobnicate'", P("frobnicate").Message);
        Assert.Contains("Unknown option '--bogus'", P("shelf", "--bogus", "a").Message);
        Assert.Contains("File not found", P("shelf", "missing.png").Message);
        Assert.Contains("Unknown option '--to'", P("shelf", "--to", "jpg", "a").Message);
    }

    [Fact]
    public void ValidFormatsExcludeIWorkApps()
    {
        Assert.DoesNotContain(OutputFormat.Pages, CommandLine.ValidFormats);
        Assert.Contains(OutputFormat.Tgz, CommandLine.ValidFormats);
    }
}

public class PipeProtocolTests
{
    [Fact]
    public void JsonRoundTrip()
    {
        var cmd = new AutomationCommand(CommandKind.Convert, [@"C:\a b\ü.png", @"D:\x.png"], OutputFormat.Webp);
        Assert.Equal(cmd.Kind, PipeProtocol.FromJson(PipeProtocol.ToJson(cmd))!.Kind);
        var back = PipeProtocol.FromJson(PipeProtocol.ToJson(cmd))!;
        Assert.Equal(cmd.Paths, back.Paths);
        Assert.Equal(OutputFormat.Webp, back.Format);
        var wheel = new AutomationCommand(CommandKind.Wheel, [@"C:\a.png"]);
        Assert.Equal(wheel.Paths, PipeProtocol.FromJson(PipeProtocol.ToJson(wheel))!.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"command\":\"explode\",\"paths\":[\"a\"]}")]
    [InlineData("{\"command\":\"wheel\",\"paths\":[]}")]
    [InlineData("{\"command\":\"convert\",\"paths\":[\"a\"]}")]
    [InlineData("{\"command\":\"convert\",\"format\":\"nope\",\"paths\":[\"a\"]}")]
    [InlineData("{\"command\":\"wheel\",\"paths\":[1]}")]
    public void MalformedJsonIsNull(string json) => Assert.Null(PipeProtocol.FromJson(json));

    [Fact]
    public async Task FramedCommandReadsBack()
    {
        var cmd = new AutomationCommand(CommandKind.Shelf, [@"C:\a.png"]);
        var r = await PipeProtocol.ReadAsync(new MemoryStream(PipeProtocol.Frame(cmd)), CancellationToken.None);
        Assert.Equal(cmd.Paths, Assert.IsType<CommandRequest>(r).Command.Paths);
    }

    [Fact]
    public async Task LegacyByteAndEmptyConnectionMeanShowSettings()
    {
        Assert.IsType<ShowSettingsRequest>(await PipeProtocol.ReadAsync(new MemoryStream([1]), CancellationToken.None));
        Assert.IsType<ShowSettingsRequest>(await PipeProtocol.ReadAsync(new MemoryStream([]), CancellationToken.None));
    }

    [Fact]
    public async Task GarbageAndOversizeAreRejected()
    {
        Assert.Null(await PipeProtocol.ReadAsync(new MemoryStream([9, 9, 9]), CancellationToken.None));
        var huge = new byte[] { 2, 0xFF, 0xFF, 0xFF, 0x7F };
        Assert.Null(await PipeProtocol.ReadAsync(new MemoryStream(huge), CancellationToken.None));
        var bad = new List<byte> { 2, 3, 0, 0, 0, (byte)'x', (byte)'y', (byte)'z' };
        Assert.Null(await PipeProtocol.ReadAsync(new MemoryStream(bad.ToArray()), CancellationToken.None));
    }

    [Fact]
    public async Task RoundTripOverARealPipe()
    {
        var name = "OpenShelf.Test." + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = PipeProtocol.AcceptAsync(name, TimeSpan.FromSeconds(2), cts.Token);
        var cmd = new AutomationCommand(CommandKind.Convert, [@"C:\a.png", @"C:\b.png"], OutputFormat.Pdf);
        Assert.True(await PipeProtocol.SendAsync(cmd, 3000, name));
        var got = Assert.IsType<CommandRequest>(await accept).Command;
        Assert.Equal(cmd.Paths, got.Paths);
        Assert.Equal(OutputFormat.Pdf, got.Format);

        var legacy = PipeProtocol.AcceptAsync(name, TimeSpan.FromSeconds(2), cts.Token);
        Assert.True(await PipeProtocol.SendAsync(null, 3000, name));
        Assert.IsType<ShowSettingsRequest>(await legacy);
    }

    [Fact]
    public async Task SendWithNoServerReturnsFalse() =>
        Assert.False(await PipeProtocol.SendAsync(null, 100, "OpenShelf.Test.NoSuchPipe." + Guid.NewGuid().ToString("N")));
}

public class CoalescerTests
{
    [Fact]
    public async Task MergesCommandsArrivingTogetherPerKindAndFormat()
    {
        var got = new System.Collections.Concurrent.ConcurrentQueue<AutomationCommand>();
        using var c = new CommandCoalescer(got.Enqueue, TimeSpan.FromMilliseconds(150));
        c.Submit(new AutomationCommand(CommandKind.Wheel, [@"C:\1.png"]));
        c.Submit(new AutomationCommand(CommandKind.Wheel, [@"C:\2.png"]));
        c.Submit(new AutomationCommand(CommandKind.Wheel, [@"C:\1.png"]));
        c.Submit(new AutomationCommand(CommandKind.Convert, [@"C:\3.png"], OutputFormat.Jpg));
        c.Submit(new AutomationCommand(CommandKind.Convert, [@"C:\4.png"], OutputFormat.Png));
        await Task.Delay(700);
        Assert.Equal(3, got.Count);
        Assert.Equal([@"C:\1.png", @"C:\2.png"], got.Single(x => x.Kind == CommandKind.Wheel).Paths);
    }

    [Fact]
    public async Task LaterCommandStartsANewBatch()
    {
        var got = new System.Collections.Concurrent.ConcurrentQueue<AutomationCommand>();
        using var c = new CommandCoalescer(got.Enqueue, TimeSpan.FromMilliseconds(100));
        c.Submit(new AutomationCommand(CommandKind.Shelf, [@"C:\1.png"]));
        await Task.Delay(500);
        c.Submit(new AutomationCommand(CommandKind.Shelf, [@"C:\2.png"]));
        await Task.Delay(500);
        Assert.Equal(2, got.Count);
    }
}

public class ExplorerIntegrationTests : IDisposable
{
    private readonly string _root = @"Software\OpenShelfAutomationTests\" + Guid.NewGuid().ToString("N") + @"\Classes";
    private const string Exe = @"C:\Program Files\OpenShelf\OpenShelf.exe";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\OpenShelfAutomationTests", throwOnMissingSubKey: false);

    [Fact]
    public void RegisterWritesTheVerbAndIsIdempotent()
    {
        Assert.False(ExplorerIntegration.IsRegistered(null, _root));
        ExplorerIntegration.Register(Exe, _root);
        ExplorerIntegration.Register(Exe, _root);
        Assert.True(ExplorerIntegration.IsRegistered(Exe, _root));

        using var verb = Registry.CurrentUser.OpenSubKey(_root + @"\*\shell\OpenShelf")!;
        Assert.Equal("Open with OpenShelf", verb.GetValue(""));
        Assert.Equal("Document", verb.GetValue("MultiSelectModel"));
        using var cmd = verb.OpenSubKey("command")!;
        Assert.Equal("\"" + Exe + "\" wheel \"%1\"", cmd.GetValue(""));
    }

    [Fact]
    public void RegisterAgainWithANewPathMovesTheEntry()
    {
        ExplorerIntegration.Register(Exe, _root);
        ExplorerIntegration.Register(@"D:\Apps\OpenShelf.exe", _root);
        Assert.False(ExplorerIntegration.IsRegistered(Exe, _root));
        Assert.True(ExplorerIntegration.IsRegistered(@"D:\Apps\OpenShelf.exe", _root));
    }

    [Fact]
    public void UnregisterRemovesAndIsIdempotent()
    {
        ExplorerIntegration.Unregister(_root); // nothing there yet
        ExplorerIntegration.Register(Exe, _root);
        ExplorerIntegration.Unregister(_root);
        ExplorerIntegration.Unregister(_root);
        Assert.False(ExplorerIntegration.IsRegistered(null, _root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OpenShelf.exe")]
    [InlineData("C:\\a\"b.exe")]
    public void RejectsBadExePaths(string exe) =>
        Assert.Throws<ArgumentException>(() => ExplorerIntegration.Register(exe, _root));

    [Fact]
    public void DefaultRootIsPerUserClasses() => Assert.Equal(@"Software\Classes", ExplorerIntegration.DefaultClassesRoot);
}
