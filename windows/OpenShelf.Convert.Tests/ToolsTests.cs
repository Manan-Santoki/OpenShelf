using System.Diagnostics;
using OpenShelf.Convert;

namespace OpenShelf.Convert.Tests;

public class ExternalToolsTests
{
    private static ToolLocations Locations(HashSet<string> existing, Dictionary<string, string>? env = null)
    {
        env ??= new()
        {
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["USERPROFILE"] = @"C:\Users\me", ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["ProgramData"] = @"C:\ProgramData",
            ["PATH"] = @"C:\Windows\System32;C:\Tools\bin",
        };
        return ToolLocations.ForEnvironment(name => env.GetValueOrDefault(name), existing.Contains);
    }

    [Fact]
    public void NothingInstalled()
    {
        var tools = ExternalTools.Detect(Locations([]));
        Assert.Null(tools.Ffmpeg);
        Assert.Null(tools.LibreOffice);
        Assert.Null(tools.EbookConvert);
        Assert.Null(tools.PdfEngine);
    }

    [Fact]
    public void OwnCopyBeatsPathBeatsUsualPlaces()
    {
        var own = @"C:\Users\me\AppData\Local\OpenShelf\tools\ffmpeg\ffmpeg.exe";
        var onPath = @"C:\Tools\bin\ffmpeg.exe";
        var scoop = @"C:\Users\me\scoop\shims\ffmpeg.exe";
        Assert.Equal(own, ExternalTools.Detect(Locations([own, onPath, scoop])).Ffmpeg);
        Assert.Equal(onPath, ExternalTools.Detect(Locations([onPath, scoop])).Ffmpeg);
        Assert.Equal(scoop, ExternalTools.Detect(Locations([scoop])).Ffmpeg);
    }

    [Fact]
    public void FindsTheUsualInstallLocations()
    {
        var soffice = @"C:\Program Files\LibreOffice\program\soffice.exe";
        var calibre = @"C:\Program Files\Calibre2\ebook-convert.exe";
        var pandoc = @"C:\Users\me\AppData\Local\Pandoc\pandoc.exe";
        var winget = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe";
        var tools = ExternalTools.Detect(Locations([soffice, calibre, pandoc, winget]));
        Assert.Equal(soffice, tools.LibreOffice);
        Assert.Equal(calibre, tools.EbookConvert);
        Assert.Equal(pandoc, tools.Pandoc);
        Assert.Equal(winget, tools.Ffprobe);
    }

    [Fact]
    public void PdfEngineNeedsPandocAndFollowsThePreferredOrder()
    {
        var pandoc = @"C:\Tools\bin\pandoc.exe";
        var typst = @"C:\Tools\bin\typst.exe";
        var xelatex = @"C:\Program Files\MiKTeX\miktex\bin\x64\xelatex.exe";
        Assert.Null(ExternalTools.Detect(Locations([typst])).PdfEngine);
        Assert.Equal(typst, ExternalTools.Detect(Locations([pandoc, typst])).PdfEngine);
        Assert.Equal(xelatex, ExternalTools.Detect(Locations([pandoc, typst, xelatex])).PdfEngine);
    }

    [Fact]
    public void IgnoredToolsOnlyCountAsOpenShelfsOwnCopies()
    {
        var env = new Dictionary<string, string> { ["LOCALAPPDATA"] = @"C:\L", ["PATH"] = @"C:\Tools", ["OPENSHELF_IGNORED_TOOLS"] = "ffmpeg, pandoc" };
        var onPath = @"C:\Tools\ffmpeg.exe";
        Assert.Null(ExternalTools.Detect(Locations([onPath], env)).Ffmpeg);
        var own = @"C:\L\OpenShelf\tools\ffmpeg\bin\ffmpeg.exe";
        Assert.Equal(own, ExternalTools.Detect(Locations([onPath, own], env)).Ffmpeg);
    }

    [Fact]
    public void ProviderCachesUntilRefreshed()
    {
        var calls = 0;
        var provider = new ExternalToolsProvider(() => ExternalTools.None with { Ffmpeg = "run" + ++calls }, TimeSpan.FromHours(1));
        Assert.Equal("run1", provider.Current.Ffmpeg);
        Assert.Equal("run1", provider.Current.Ffmpeg);
        Assert.Equal("run2", provider.Refresh().Ffmpeg);
        Assert.Equal("run2", provider.Current.Ffmpeg);
        Assert.Equal(2, calls);
    }
}

public class ChildEnvironmentTests
{
    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["AWS_SECRET_ACCESS_KEY"] = "a", ["AWS_REGION"] = "b", ["GITHUB_TOKEN"] = "c", ["OP_SERVICE_ACCOUNT_TOKEN"] = "d",
        ["SSH_AUTH_SOCK"] = "/tmp/agent", ["PGPASSWORD"] = "e", ["MY_API_KEY"] = "f", ["SOME_SECRET"] = "g", ["LC_SECRET_TOKEN"] = "test",
    };

    private static readonly Dictionary<string, string> Kept = new()
    {
        ["SystemRoot"] = @"C:\Windows", ["TEMP"] = @"C:\T", ["USERPROFILE"] = @"C:\Users\x", ["LOCALAPPDATA"] = @"C:\Users\x\AppData\Local",
        ["USERNAME"] = "x", ["LANG"] = "en_GB.UTF-8", ["LC_ALL"] = "en_GB.UTF-8",
    };

    [Fact]
    public void KeepsOnlyTheAllowlist()
    {
        var input = Secrets.Concat(Kept).Append(new("PATH", @"C:\evil;C:\Windows")).Append(new("Path2", "x")).ToDictionary(p => p.Key, p => p.Value);
        var environment = ChildEnvironment.Minimal(input);
        Assert.Equal(Kept.Keys.Append("PATH").Order(), environment.Keys.Order());
        Assert.DoesNotContain("evil", environment["PATH"]);
        Assert.StartsWith(@"C:\Windows\System32", environment["PATH"]);
    }

    [Fact]
    public void CredentialNames()
    {
        foreach (var name in Secrets.Keys) Assert.True(ChildEnvironment.IsCredential(name), name);
        foreach (var name in new[] { "SystemRoot", "LOCALAPPDATA", "TEMP", "LANG", "PATHEXT" }) Assert.False(ChildEnvironment.IsCredential(name), name);
    }

    [Fact]
    public async Task ChildrenNeverSeeTheParentsSecrets()
    {
        var name = "OPENSHELF_TEST_" + Guid.NewGuid().ToString("N")[..8] + "_TOKEN";
        Environment.SetEnvironmentVariable(name, "leaked");
        try
        {
            var result = await ProcessRunner.RunAsync(Cmd, ["/c", "set"]);
            Assert.DoesNotContain(name, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("USERPROFILE=", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SystemRoot=", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    internal static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
}

public class ProcessRunnerTests
{
    private static string Cmd => ChildEnvironmentTests.Cmd;

    [Fact]
    public async Task CapturesOutputAndLines()
    {
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(Cmd, ["/c", "echo one&& echo two"], new ProcessOptions { OnOutputLine = l => { lock (lines) lines.Add(l.Trim()); } });
        Assert.Equal(["one", "two"], lines);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>The tool's last words are never lost, even run many times in parallel.</summary>
    [Fact]
    public async Task ErrorTextIsAlwaysCaught()
    {
        var missed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (_, ct) =>
        {
            try { await ProcessRunner.RunAsync(Cmd, ["/c", "echo Couldn't open input file 1>&2& exit 3"], cancellationToken: ct); Interlocked.Increment(ref missed); }
            catch (ProcessFailure failure) when (failure.Detail.Contains("Couldn't open input file") && failure.ExitCode == 3) { }
            catch { Interlocked.Increment(ref missed); }
        });
        Assert.Equal(0, missed);
    }

    [Fact]
    public async Task OutputIsBounded()
    {
        var result = await ProcessRunner.RunAsync(Cmd, ["/c", "for /L %i in (1,1,500) do @echo line%i"], new ProcessOptions { MaxCapturedLines = 10 });
        var lines = result.Output.Split('\n');
        Assert.Equal(10, lines.Length);
        Assert.Contains("line500", lines[^1]);
    }

    [Fact]
    public async Task NonZeroExitCanBeReturned()
    {
        var result = await ProcessRunner.RunAsync(Cmd, ["/c", "exit 7"], new ProcessOptions { ThrowOnFailure = false });
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task MissingToolIsNamed()
    {
        var error = await Assert.ThrowsAsync<MissingToolException>(() => ProcessRunner.RunAsync(@"C:\nonexistent\soffice.exe", []));
        Assert.Equal("LibreOffice", error.Name);
    }

    [Fact]
    public async Task CancellationKillsTheWholeTree()
    {
        using var cts = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        // cmd starts ping, which keeps running (and keeps the output pipe open) unless the tree is killed.
        var run = ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 40 127.0.0.1"], cancellationToken: cts.Token);
        await Task.Delay(600);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task TimeoutKillsAndSaysSo()
    {
        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ProcessFailure>(() =>
            ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 40 127.0.0.1"], new ProcessOptions { Timeout = TimeSpan.FromMilliseconds(500) }));
        Assert.True(failure.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ChromiumLogLinesAreRecognised()
    {
        Assert.True(ProcessRunner.IsChromiumLog("[60878:259:1101/181024.142716:ERROR:service_utils.cc(339)] x"));
        Assert.False(ProcessRunner.IsChromiumLog("Error: nope"));
    }
}

public class FailureMessageTests
{
    private const string Folder = @"C:\Users\me\Read Only";

    [Fact]
    public void PermissionProblemsNameTheFolder() =>
        Assert.Equal("You don’t have permission to save in “Read Only”.",
            FailureMessage.Message("tar: x.zip: Permission denied", Folder + @"\Photo.jpg", FileKind.Image));

    [Fact]
    public void ArchivesExplainPasswordsAndDamage()
    {
        var zip = Folder + @"\Secret.zip";
        Assert.Equal("“Secret.zip” is password-protected; OpenShelf can’t open encrypted archives.",
            FailureMessage.Message("No password was provided", zip, FileKind.Archive));
        Assert.Equal("“Secret.zip” is damaged or isn’t an archive OpenShelf can open.",
            FailureMessage.Message("End of Central Directory record could not be found.", zip, FileKind.Archive));
    }

    [Fact]
    public void DamagedMediaIsExplained()
    {
        var clip = Folder + @"\Clip.mp4";
        Assert.Equal("“Clip.mp4” is damaged or in a format OpenShelf can’t read.", FailureMessage.Message("clip.mp4: Invalid data found when processing input", clip, FileKind.Video));
        Assert.Null(FailureMessage.Message("something unexpected", clip, FileKind.Video));
    }

    [Fact]
    public void SanitizingRemovesTemporaryPaths()
    {
        var sanitized = FailureMessage.Sanitized(@"Error! Cannot open output file 'C:\Users\me\AppData\Local\Temp\1234.webp'");
        Assert.DoesNotContain("AppData", sanitized);
        Assert.Contains("a temporary file", sanitized);
        Assert.Equal("the tool reported an error.", FailureMessage.Sanitized("tool:  "));
    }

    [Fact]
    public void CancellationAndUserFacingErrorsPassThrough()
    {
        var cancelled = new OperationCanceledException();
        Assert.Same(cancelled, FailureMessage.Describe(cancelled, @"C:\x\a.png"));
        var own = ConversionException.Failed("x");
        Assert.Same(own, FailureMessage.Describe(own, @"C:\x\a.png"));
    }
}

public class TextDecodingTests
{
    [Fact]
    public void DecodesUtf8AndEmpty()
    {
        Assert.Equal("Hello 👋", TextDecoding.Decode(System.Text.Encoding.UTF8.GetBytes("Hello 👋")));
        Assert.Equal("", TextDecoding.Decode([]));
    }

    [Fact]
    public void DecodesUtf16WithByteOrderMark() =>
        Assert.Equal("Grüße", TextDecoding.Decode([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("Grüße")]));

    [Fact]
    public void FallsBackToWindowsLatin() => Assert.Equal("café", TextDecoding.Decode([0x63, 0x61, 0x66, 0xE9]));
}
