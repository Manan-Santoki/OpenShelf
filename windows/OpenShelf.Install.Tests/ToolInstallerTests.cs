using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenShelf.Convert;
using OpenShelf.Install;

namespace OpenShelf.Install.Tests;

internal sealed class FakeHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requests { get; } = [];

    public void Text(string url, string body) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    public void Bytes(string url, byte[] body) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public void Status(string url, HttpStatusCode code) => Routes[url] = _ => new HttpResponseMessage(code);
    public void Down(string url) => Routes[url] = _ => throw new HttpRequestException("No such host is known.");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests) Requests.Add(url);
        if (!Routes.TryGetValue(url, out var route)) throw new HttpRequestException("No route for " + url);
        var response = route(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

internal sealed class FakeSignatures(SignatureResult result) : ISignatureVerifier
{
    public List<string> Checked { get; } = [];
    public SignatureResult Verify(string path) { Checked.Add(path); return result; }
}

internal sealed class Env : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "openshelf-install-tests-" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Dir, "tools");
    public string External => Path.Combine(Dir, "external");
    public FakeHandler Http { get; } = new();
    public List<ToolInvocation> Invocations { get; } = [];
    public Func<ToolInvocation, CancellationToken, Task> OnRun { get; set; } = (_, _) => Task.CompletedTask;
    public FakeSignatures Signatures { get; set; } = new(new SignatureResult(SignatureState.Valid, "The Document Foundation"));
    public string? Winget { get; set; }
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public Env() { Directory.CreateDirectory(External); }

    public ToolInstaller Create()
    {
        var locations = new ToolLocations { OwnToolsRoot = Root, PathDirectories = [External] };
        return new ToolInstaller(Http, Root,
            async (invocation, _, ct) => { lock (Invocations) Invocations.Add(invocation); await OnRun(invocation, ct); },
            () => Now,
            new ToolInstallerOptions
            {
                Locations = locations,
                Signatures = Signatures,
                FindWinget = () => Winget,
                MsiExecPath = @"C:\Windows\System32\msiexec.exe",
                StallTimeout = TimeSpan.FromSeconds(5),
            });
    }

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return stream.ToArray();
    }

    public static string Sha(byte[] bytes) => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ReleaseJson(string tag, string assetName, string url, string? sha) =>
        $$"""{"tag_name":"{{tag}}","assets":[{"name":"other.txt","browser_download_url":"https://x/other","digest":"sha256:{{new string('0', 64)}}"},{"name":"{{assetName}}","size":1,"browser_download_url":"{{url}}"{{(sha is null ? "" : $$""","digest":"sha256:{{sha}}" """)}}}]}""";

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    /// <summary>Everything left in the tools folder, so tests can assert nothing half-installed remains.</summary>
    public string[] Leftovers() => Directory.Exists(Root) ? Directory.GetFileSystemEntries(Root) : [];

    // ---- Canned servers -----------------------------------------------------------------------------------
    public byte[] ServeGyan(byte[]? zip = null, bool lieAboutHash = false)
    {
        zip ??= Zip(("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "ffmpeg"), ("ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", "ffprobe"), ("ffmpeg-9.0.2-essentials_build/README.txt", "hi"));
        Http.Text(ToolSources.GyanBase + ".sha256", lieAboutHash ? new string('a', 64) : Sha(zip));
        Http.Text("https://www.gyan.dev/ffmpeg/builds/release-version", "9.0.2");
        Http.Bytes(ToolSources.GyanBase, zip);
        return zip;
    }

    public byte[] ServeBtbn()
    {
        var zip = Zip(("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "ffmpeg"), ("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", "ffprobe"));
        Http.Text(ToolSources.BtbnLatestApi, ReleaseJson("latest", ToolSources.BtbnAsset, "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/" + ToolSources.BtbnAsset, Sha(zip)));
        Http.Bytes("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/" + ToolSources.BtbnAsset, zip);
        return zip;
    }

    public byte[] ServePandoc(bool withDigest = true)
    {
        var zip = Zip(("pandoc-3.13/pandoc.exe", "pandoc"), ("pandoc-3.13/COPYRIGHT", "c"));
        const string url = "https://github.com/jgm/pandoc/releases/download/3.13/pandoc-3.13-windows-x86_64.zip";
        Http.Text(ToolSources.PandocLatestApi, ReleaseJson("3.13", "pandoc-3.13-windows-x86_64.zip", url, withDigest ? Sha(zip) : null));
        Http.Bytes(url, zip);
        return zip;
    }

    public byte[] ServeLibreOffice()
    {
        var msi = Encoding.UTF8.GetBytes("pretend this is an MSI");
        Http.Text(ToolSources.LibreOfficeStable, """<a href="25.8.7/">25.8.7/</a> <a href="26.8.2/">26.8.2/</a> <a href="26.8.1/">26.8.1/</a>""");
        var url = ToolSources.LibreOfficeStable + "26.8.2/win/x86_64/LibreOffice_26.8.2_Win_x86-64.msi";
        Http.Text(url + ".sha256", Sha(msi) + "  LibreOffice_26.8.2_Win_x86-64.msi\n");
        Http.Bytes(url, msi);
        OnRun = (invocation, _) =>
        {
            if (invocation.Executable.EndsWith("msiexec.exe"))
                WriteUnpacked(invocation, Path.Combine("LibreOffice", "program", "soffice.exe"), Path.Combine("LibreOffice", "program", "x.dll"));
            return Task.CompletedTask;
        };
        return msi;
    }

    public byte[] ServeCalibre(bool githubDown = false)
    {
        var msi = Encoding.UTF8.GetBytes("pretend calibre MSI");
        const string github = "https://github.com/kovidgoyal/calibre/releases/download/v9.18.0/calibre-64bit-9.18.0.msi";
        Http.Text(ToolSources.CalibreLatestApi, ReleaseJson("v9.18.0", "calibre-64bit-9.18.0.msi", github, Sha(msi)));
        if (githubDown) Http.Down(github); else Http.Bytes(github, msi);
        Http.Bytes(ToolSources.CalibreHost + "9.18.0/calibre-64bit-9.18.0.msi", msi);
        OnRun = (invocation, _) =>
        {
            if (invocation.Executable.EndsWith("msiexec.exe"))
                WriteUnpacked(invocation, Path.Combine("Calibre2", "ebook-convert.exe"), Path.Combine("Calibre2", "calibre.exe"));
            return Task.CompletedTask;
        };
        return msi;
    }

    public static string TargetDir(ToolInvocation invocation) =>
        invocation.Arguments.First(a => a.StartsWith("TARGETDIR=")).Substring("TARGETDIR=".Length);

    public static void WriteUnpacked(ToolInvocation invocation, params string[] relative)
    {
        foreach (var file in relative)
        {
            var path = Path.Combine(TargetDir(invocation), file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }
}

public class ToolInstallerTests
{
    private sealed class Reports : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) { lock (Values) Values.Add(value); }
    }

    [Fact]
    public void RowsListFourToolsNotInstalled()
    {
        using var env = new Env();
        using var installer = env.Create();
        Assert.Equal(["ffmpeg", "libreoffice", "pandoc", "calibre"], installer.Tools.Select(t => t.Id));
        Assert.Equal(["ffmpeg", "LibreOffice", "Pandoc", "calibre"], installer.Tools.Select(t => t.Name));
        Assert.All(installer.Tools, row => { Assert.False(row.Installed); Assert.Null(row.Location); Assert.False(row.CanRemove); });
        Assert.Equal("MP3, OGG, MKV, WebM, AVI, WMV and every audio and video conversion", installer.Tools[0].Description);
    }

    [Fact]
    public void ACopyFoundElsewhereShowsAsInstalledButNotRemovable()
    {
        using var env = new Env();
        File.WriteAllText(Path.Combine(env.External, "pandoc.exe"), "x");
        using var installer = env.Create();
        var row = installer.Tools.Single(t => t.Id == "pandoc");
        Assert.True(row.Installed);
        Assert.Equal(Path.Combine(env.External, "pandoc.exe"), row.Location);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public async Task FfmpegInstallsFromGyanAndIsVerified()
    {
        using var env = new Env();
        env.ServeGyan();
        using var installer = env.Create();
        var changed = 0;
        installer.Changed += () => changed++;
        var progress = new Reports();

        await installer.InstallAsync("ffmpeg", progress, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(env.Root, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(env.Root, "ffmpeg", "bin", "ffprobe.exe")));
        var row = installer.Tools[0];
        Assert.True(row.Installed);
        Assert.True(row.CanRemove);
        Assert.Equal(Path.Combine(env.Root, "ffmpeg", "bin", "ffmpeg.exe"), row.Location);
        Assert.True(changed >= 1);
        Assert.Equal(1, progress.Values[^1]);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
        Assert.DoesNotContain(env.Leftovers(), p => Path.GetFileName(p).StartsWith('.'));
        var record = installer.RecordFor("ffmpeg")!;
        Assert.True(record.ChecksumVerified);
        Assert.Equal("gyan.dev", record.Source);
        Assert.Equal("9.0.2", record.Version);
        Assert.Contains("checked", record.Signature);
        Assert.Equal(Env.Now, record.InstalledAt);
        Assert.Empty(env.Invocations);
    }

    [Fact]
    public async Task PandocInstallsFromTheLatestGitHubRelease()
    {
        using var env = new Env();
        env.ServePandoc();
        using var installer = env.Create();
        await installer.InstallAsync("pandoc", new Reports(), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(env.Root, "pandoc", "pandoc.exe")));
        Assert.True(installer.Tools.Single(t => t.Id == "pandoc").CanRemove);
        Assert.Equal("3.13", installer.RecordFor("pandoc")!.Version);
    }

    [Fact]
    public async Task PandocWithoutAListedDigestIsNotInstalled()
    {
        using var env = new Env();
        env.ServePandoc(withDigest: false);
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("pandoc", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Verification, e.Kind);
        Assert.Contains("checksum", e.Message);
        Assert.False(installer.Tools.Single(t => t.Id == "pandoc").Installed);
    }

    [Fact]
    public async Task LibreOfficeIsUnpackedWithoutInstallingAndItsSignatureChecked()
    {
        using var env = new Env();
        env.ServeLibreOffice();
        using var installer = env.Create();
        await installer.InstallAsync("libreoffice", new Reports(), CancellationToken.None);

        var invocation = Assert.Single(env.Invocations);
        Assert.EndsWith("msiexec.exe", invocation.Executable);
        Assert.Equal("/a", invocation.Arguments[0]);
        Assert.Contains("/qn", invocation.Arguments);
        Assert.StartsWith("TARGETDIR=", invocation.Arguments[^1]);
        var soffice = Path.Combine(env.Root, "libreoffice", "program", "soffice.exe");
        Assert.True(File.Exists(soffice));
        Assert.Equal(soffice, installer.Tools.Single(t => t.Id == "libreoffice").Location);
        Assert.Single(env.Signatures.Checked);
        Assert.EndsWith("soffice.exe", env.Signatures.Checked[0]);
        var record = installer.RecordFor("libreoffice")!;
        Assert.Equal("26.8.2", record.Version);
        Assert.Contains("The Document Foundation", record.Signature);
    }

    [Fact]
    public async Task CalibreIsUnpackedFromTheMsi()
    {
        using var env = new Env();
        env.ServeCalibre();
        using var installer = env.Create();
        await installer.InstallAsync("calibre", new Reports(), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(env.Root, "calibre", "ebook-convert.exe")));
        Assert.True(installer.Tools.Single(t => t.Id == "calibre").CanRemove);
    }

    [Fact]
    public async Task CalibreFallsToTheVendorHostWhenGitHubIsDown()
    {
        using var env = new Env();
        env.ServeCalibre(githubDown: true);
        using var installer = env.Create();
        await installer.InstallAsync("calibre", new Reports(), CancellationToken.None);
        Assert.Equal("download.calibre-ebook.com", installer.RecordFor("calibre")!.Source);
    }

    [Fact]
    public async Task ChecksumMismatchAbortsWithoutTryingAnotherSource()
    {
        using var env = new Env();
        env.ServeGyan(lieAboutHash: true);
        env.ServeBtbn();
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Verification, e.Kind);
        Assert.Contains("damaged or altered", e.Message);
        Assert.DoesNotContain(env.Http.Requests, r => r.Contains("BtbN"));
        Assert.False(installer.Tools[0].Installed);
        Assert.Empty(env.Leftovers());
        Assert.Empty(env.Invocations);
    }

    [Fact]
    public async Task ABadSignatureAbortsAndLeavesNothing()
    {
        using var env = new Env();
        env.ServeLibreOffice();
        env.Signatures = new FakeSignatures(new SignatureResult(SignatureState.Invalid, null, "0x80096010"));
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("libreoffice", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Verification, e.Kind);
        Assert.Contains("isn’t signed by its publisher", e.Message);
        Assert.False(installer.Tools.Single(t => t.Id == "libreoffice").Installed);
        Assert.Empty(env.Leftovers());
    }

    [Fact]
    public async Task AnUnsignedProgramFromASigningVendorIsRecordedNotRejected()
    {
        using var env = new Env();
        env.ServeLibreOffice();
        env.Signatures = new FakeSignatures(new SignatureResult(SignatureState.Unsigned));
        using var installer = env.Create();
        await installer.InstallAsync("libreoffice", new Reports(), CancellationToken.None);
        Assert.Contains("no signature", installer.RecordFor("libreoffice")!.Signature);
    }

    [Fact]
    public async Task ANetworkFailureFallsToTheNextSource()
    {
        using var env = new Env();
        env.Http.Down(ToolSources.GyanBase + ".sha256");
        env.ServeBtbn();
        using var installer = env.Create();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);
        Assert.Equal("BtbN on GitHub", installer.RecordFor("ffmpeg")!.Source);
        Assert.True(installer.Tools[0].Installed);
    }

    [Fact]
    public async Task AServerErrorFallsToTheNextSource()
    {
        using var env = new Env();
        env.Http.Status(ToolSources.GyanBase + ".sha256", HttpStatusCode.BadGateway);
        env.ServeBtbn();
        using var installer = env.Create();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);
        Assert.Equal("BtbN on GitHub", installer.RecordFor("ffmpeg")!.Source);
    }

    [Fact]
    public async Task ADownloadCutShortFallsToTheNextSource()
    {
        using var env = new Env();
        var zip = env.ServeGyan();
        env.Http.Routes[ToolSources.GyanBase] = _ =>
        {
            var content = new StreamContent(new MemoryStream(zip, 0, zip.Length / 2));
            content.Headers.ContentLength = zip.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        env.ServeBtbn();
        using var installer = env.Create();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);
        Assert.Equal("BtbN on GitHub", installer.RecordFor("ffmpeg")!.Source);
    }

    [Fact]
    public async Task WhenEverySourceIsDownWingetIsRunWithUserScope()
    {
        using var env = new Env();
        env.Winget = @"C:\fake\winget.exe";
        env.OnRun = (invocation, _) =>
        {
            if (invocation.Executable == env.Winget) File.WriteAllText(Path.Combine(env.External, "ffmpeg.exe"), "x");
            return Task.CompletedTask;
        };
        using var installer = env.Create();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);

        var winget = Assert.Single(env.Invocations);
        Assert.Equal(["install", "--id", "Gyan.FFmpeg", "-e", "--accept-source-agreements", "--accept-package-agreements", "--scope", "user"], winget.Arguments);
        var row = installer.Tools[0];
        Assert.True(row.Installed);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public async Task WithoutWingetAllSourcesDownGivesAClearMessage()
    {
        using var env = new Env();
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("pandoc", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.SourcesUnavailable, e.Kind);
        Assert.Equal("The Pandoc download server isn’t responding right now.", e.Message);
        Assert.Empty(env.Invocations);
        Assert.Empty(env.Leftovers());
    }

    [Fact]
    public async Task AFailedWingetIsReportedInPlainWords()
    {
        using var env = new Env();
        env.Winget = @"C:\fake\winget.exe";
        env.OnRun = (_, _) => throw new ProcessFailure("winget.exe", "No package found matching input criteria.", 1);
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("pandoc", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Winget, e.Kind);
        Assert.Contains("winget couldn’t install it either", e.Message);
    }

    [Fact]
    public async Task AChecksumFailureNeverFallsBackToWinget()
    {
        using var env = new Env();
        env.Winget = @"C:\fake\winget.exe";
        env.ServeGyan(lieAboutHash: true);
        using var installer = env.Create();
        await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None));
        Assert.Empty(env.Invocations);
    }

    [Fact]
    public async Task CancellingMidDownloadCleansUp()
    {
        using var env = new Env();
        var zip = env.ServeGyan();
        var started = new TaskCompletionSource();
        env.Http.Routes[ToolSources.GyanBase] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(zip, started)) };
        using var installer = env.Create();
        using var cts = new CancellationTokenSource();
        var install = installer.InstallAsync("ffmpeg", new Reports(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.Empty(env.Leftovers());
        Assert.False(installer.Tools[0].Installed);
        // and the tool can be installed again afterwards
        env.ServeGyan();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);
        Assert.True(installer.Tools[0].Installed);
    }

    private sealed class StallingStream(byte[] data, TaskCompletionSource started) : Stream
    {
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == 0)
            {
                _position = 1;
                data.AsSpan(0, 1).CopyTo(buffer.Span);
                return 1;
            }
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CancellingDuringTheUnpackCleansUp()
    {
        using var env = new Env();
        env.ServeLibreOffice();
        var original = env.OnRun;
        env.OnRun = async (invocation, ct) => { Env.WriteUnpacked(invocation, "partial.bin"); await Task.Delay(Timeout.Infinite, ct); };
        using var installer = env.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync("libreoffice", new Reports(), cts.Token));
        Assert.Empty(env.Leftovers());
        Assert.False(installer.Tools.Single(t => t.Id == "libreoffice").Installed);
    }

    [Fact]
    public async Task AnMsiThatCannotBeUnpackedFailsClearly()
    {
        using var env = new Env();
        env.ServeLibreOffice();
        env.OnRun = (_, _) => throw new ProcessFailure("msiexec.exe", "1625", 1625);
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("libreoffice", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Unpack, e.Kind);
        Assert.Equal("LibreOffice couldn’t be unpacked.", e.Message);
        Assert.Empty(env.Leftovers());
    }

    [Fact]
    public async Task AZipWithoutTheProgramIsRejectedAndNothingIsInstalled()
    {
        using var env = new Env();
        env.ServeGyan(Env.Zip(("bin/ffmpeg.exe", "x")));
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Unpack, e.Kind);
        Assert.Contains("ffprobe.exe", e.Message);
        Assert.Empty(env.Leftovers());
    }

    [Fact]
    public async Task AZipEntryEscapingTheFolderIsRejected()
    {
        using var env = new Env();
        env.ServeGyan(Env.Zip(("../../evil.exe", "x")));
        using var installer = env.Create();
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None));
        Assert.Equal(InstallFailureKind.Unpack, e.Kind);
        Assert.False(File.Exists(Path.Combine(env.Dir, "evil.exe")));
    }

    [Fact]
    public async Task ReinstallingReplacesTheOwnCopy()
    {
        using var env = new Env();
        env.ServePandoc();
        using var installer = env.Create();
        await installer.InstallAsync("pandoc", new Reports(), CancellationToken.None);
        File.WriteAllText(Path.Combine(env.Root, "pandoc", "stale.txt"), "old");
        await installer.InstallAsync("pandoc", new Reports(), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(env.Root, "pandoc", "stale.txt")));
        Assert.True(File.Exists(Path.Combine(env.Root, "pandoc", "pandoc.exe")));
        Assert.DoesNotContain(env.Leftovers(), p => Path.GetFileName(p).StartsWith('.'));
    }

    [Fact]
    public async Task OnlyOneInstallPerToolAtATime()
    {
        using var env = new Env();
        var zip = env.ServeGyan();
        var started = new TaskCompletionSource();
        env.Http.Routes[ToolSources.GyanBase] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(zip, started)) };
        using var installer = env.Create();
        using var cts = new CancellationTokenSource();
        var first = installer.InstallAsync("ffmpeg", new Reports(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var e = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None));
        Assert.Contains("already being installed", e.Message);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task RemoveDeletesOnlyOpenShelfsOwnCopy()
    {
        using var env = new Env();
        var external = Path.Combine(env.External, "ffmpeg.exe");
        File.WriteAllText(external, "x");
        env.ServeGyan();
        using var installer = env.Create();
        await installer.InstallAsync("ffmpeg", new Reports(), CancellationToken.None);
        Assert.True(installer.Tools[0].CanRemove);
        var changed = 0;
        installer.Changed += () => changed++;

        await installer.RemoveAsync("ffmpeg");

        Assert.False(Directory.Exists(Path.Combine(env.Root, "ffmpeg")));
        Assert.True(File.Exists(external));
        var row = installer.Tools[0];
        Assert.True(row.Installed);          // the copy elsewhere is still found
        Assert.False(row.CanRemove);
        Assert.Equal(external, row.Location);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task RemovingACopyOpenShelfDidNotInstallDeletesNothing()
    {
        using var env = new Env();
        var external = Path.Combine(env.External, "pandoc.exe");
        File.WriteAllText(external, "x");
        using var installer = env.Create();
        await installer.RemoveAsync("pandoc");
        Assert.True(File.Exists(external));
        Assert.True(installer.Tools.Single(t => t.Id == "pandoc").Installed);
    }

    [Fact]
    public async Task ChangedFiresWhenDetectionChanges()
    {
        using var env = new Env();
        using var installer = env.Create();
        var changed = 0;
        installer.Changed += () => changed++;
        installer.Rescan();
        Assert.Equal(0, changed);
        File.WriteAllText(Path.Combine(env.External, "ffmpeg.exe"), "x");
        installer.Rescan();
        Assert.Equal(1, changed);
        Assert.True(installer.Tools[0].Installed);
        installer.Rescan();
        Assert.Equal(1, changed);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChangedFiresAfterAFailedInstallToo()
    {
        using var env = new Env();
        using var installer = env.Create();
        var changed = 0;
        installer.Changed += () => changed++;
        await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("pandoc", new Reports(), CancellationToken.None));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task UnknownToolIsRejected()
    {
        using var env = new Env();
        using var installer = env.Create();
        await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync("nope", new Reports(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolInstallException>(() => installer.RemoveAsync("nope"));
    }

    [Fact]
    public async Task StaleLeftoversFromACrashAreClearedAtTheNextInstall()
    {
        using var env = new Env();
        var stale = Path.Combine(env.Root, ".staging-dead");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, Env.Now.UtcDateTime.AddDays(-2));
        env.ServePandoc();
        using var installer = env.Create();
        await installer.InstallAsync("pandoc", new Reports(), CancellationToken.None);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void ParseSha256HandlesBothPublishedForms()
    {
        var hex = new string('a', 64);
        Assert.Equal(hex, ToolSources.ParseSha256(hex + "\n", "x"));
        Assert.Equal(hex, ToolSources.ParseSha256(hex.ToUpperInvariant() + "  file.msi", "x"));
        Assert.Throws<ToolInstallException>(() => ToolSources.ParseSha256("<html>nope</html>", "x"));
    }
}
