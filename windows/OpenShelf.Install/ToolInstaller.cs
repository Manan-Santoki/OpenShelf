using System.IO;
using System.Net.Http;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OpenShelf.Convert;
using OpenShelf.Settings;

namespace OpenShelf.Install;

/// <summary>Things a test (or the composition root) may replace. Everything has a sensible default.</summary>
public sealed class ToolInstallerOptions
{
    /// <summary>Where tools are looked for. Defaults to the real machine (default constructor) or to the install root alone (test constructor).</summary>
    public ToolLocations? Locations { get; init; }
    public ISignatureVerifier? Signatures { get; init; }
    /// <summary>Full path of winget.exe, or null when it is not installed.</summary>
    public Func<string?>? FindWinget { get; init; }
    public IReadOnlyList<ToolDefinition>? Definitions { get; init; }
    /// <summary>Called after an install, removal or change in what is installed (the app refreshes its tool detection here).</summary>
    public Action? OnToolsChanged { get; init; }
    /// <summary>For each metadata request and until the first byte of a download.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A download that receives nothing for this long counts as a network failure.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How often to look for tools installed or removed elsewhere; null for never.</summary>
    public TimeSpan? PollInterval { get; init; }
    /// <summary>Full path of msiexec.exe.</summary>
    public string? MsiExecPath { get; init; }
}

/// <summary>Downloads, checks and installs the optional helper tools (ffmpeg, LibreOffice, Pandoc, calibre) into
/// <c>%LOCALAPPDATA%\OpenShelf\tools\&lt;tool&gt;\</c> without administrator rights or a package manager.
/// Port of the Mac's ToolInstaller.</summary>
public sealed class ToolInstaller : IToolInstallerUi, IDisposable
{
    private const string RecordFile = "OpenShelf-install.json";

    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly string _root;
    private readonly ToolRunner _runner;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ToolInstallerOptions _options;
    private readonly ToolLocations _locations;
    private readonly ISignatureVerifier _signatures;
    private readonly HttpClient _client;
    private readonly SourceHttp _http;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<string, bool> _active = new();
    private readonly Timer? _poll;
    private readonly object _lock = new();
    private string _snapshot;

    /// <summary>The app's installer: the real network, the real tools folder, real processes.</summary>
    public ToolInstaller() : this(
        new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = true, MaxAutomaticRedirections = 8 },
        ToolLocations.Standard.OwnToolsRoot, ToolRunners.Default, () => DateTimeOffset.UtcNow,
        new ToolInstallerOptions
        {
            Locations = ToolLocations.Standard,
            OnToolsChanged = () => ExternalToolsProvider.Default.Refresh(),
            PollInterval = TimeSpan.FromSeconds(20),
        }, disposeHandler: true)
    {
    }

    /// <summary>For tests: an HTTP handler, the folder tools go into, a process runner (msiexec and winget run through it)
    /// and a clock. Detection looks only in <paramref name="installRoot"/> unless <paramref name="options"/> says otherwise.</summary>
    public ToolInstaller(HttpMessageHandler handler, string installRoot, ToolRunner runner, Func<DateTimeOffset> clock, ToolInstallerOptions? options = null)
        : this(handler, installRoot, runner, clock, options, disposeHandler: false)
    {
    }

    private ToolInstaller(HttpMessageHandler handler, string installRoot, ToolRunner runner, Func<DateTimeOffset> clock, ToolInstallerOptions? options, bool disposeHandler)
    {
        _options = options ?? new ToolInstallerOptions();
        _root = installRoot;
        _runner = runner;
        _clock = clock;
        _definitions = _options.Definitions ?? ToolSources.All;
        _locations = _options.Locations ?? new ToolLocations { OwnToolsRoot = installRoot };
        _signatures = _options.Signatures ?? new AuthenticodeVerifier();
        _client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenShelf", "1.0"));
        _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http = new SourceHttp(_client, _options.RequestTimeout);
        _ownsClient = true;
        _snapshot = Snapshot();
        if (_options.PollInterval is { } interval)
            _poll = new Timer(_ => Rescan(), null, interval, interval);
    }

    public event Action? Changed;

    public IReadOnlyList<ToolRow> Tools => _definitions.Select(RowFor).ToList();

    /// <summary>What was checked when the tool was installed by OpenShelf; null for a copy found elsewhere.</summary>
    public InstallRecord? RecordFor(string id)
    {
        var file = Path.Combine(_root, id, RecordFile);
        try { return File.Exists(file) ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(file)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Looks again for tools; raises <see cref="Changed"/> when the answer differs from the last one.</summary>
    public void Rescan()
    {
        bool changed;
        lock (_lock)
        {
            var now = Snapshot();
            changed = now != _snapshot;
            _snapshot = now;
        }
        if (changed)
        {
            _options.OnToolsChanged?.Invoke();
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        if (_ownsClient) _client.Dispose();
    }

    // ---- Detection -------------------------------------------------------------------------------------------

    private string Snapshot() => string.Join("|", Tools.Select(t => $"{t.Id}={t.Installed}:{t.Location}:{t.CanRemove}"));

    private string? Find(ToolDefinition tool)
    {
        var ignored = _locations.IgnoredTools.Contains(tool.Id);
        return tool.Id switch
        {
            "ffmpeg" => _locations.FindCommand("ffmpeg", ignored),
            "pandoc" => _locations.FindCommand("pandoc", ignored),
            "libreoffice" => _locations.FindLibreOffice(ignored),
            "calibre" => _locations.FindCalibre(ignored),
            _ => null,
        };
    }

    private bool IsOwn(string path)
    {
        if (_root.Length == 0) return false;
        var prefix = Path.GetFullPath(_root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private ToolRow RowFor(ToolDefinition tool)
    {
        var location = Find(tool);
        return new ToolRow(tool.Id, tool.Name, tool.Description, location is not null, location, location is not null && IsOwn(location));
    }

    private ToolDefinition Definition(string id) =>
        _definitions.FirstOrDefault(t => t.Id == id) ?? throw new ToolInstallException($"“{id}” isn’t a tool OpenShelf can install.", InstallFailureKind.Other);

    private void NotifyChanged()
    {
        lock (_lock) _snapshot = Snapshot();
        _options.OnToolsChanged?.Invoke();
        Changed?.Invoke();
    }

    // ---- Install ---------------------------------------------------------------------------------------------

    public async Task InstallAsync(string id, IProgress<double> progress, CancellationToken ct)
    {
        var tool = Definition(id);
        if (_root.Length == 0)
            throw new ToolInstallException("OpenShelf couldn’t find a place to keep tools on this PC.", InstallFailureKind.Other);
        if (!_active.TryAdd(id, true))
            throw new ToolInstallException($"{tool.Name} is already being installed.", InstallFailureKind.Other);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_root);
            ClearLeftovers();
            var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                progress.Report(0);
                try
                {
                    await InstallFromSourcesAsync(tool, staging, progress, ct);
                }
                catch (ToolInstallException e) when (e.Kind == InstallFailureKind.SourcesUnavailable && _options.FindWinget?.Invoke() is { Length: > 0 } winget)
                {
                    await InstallWithWingetAsync(tool, winget, e, ct);
                }
                progress.Report(1);
            }
            finally
            {
                TryDelete(staging);
            }
        }
        finally
        {
            _active.TryRemove(id, out _);
            NotifyChanged();
        }
    }

    /// <summary>Installs from the sources in order; only an unreachable or erroring source moves on to the next.</summary>
    private async Task InstallFromSourcesAsync(ToolDefinition tool, string staging, IProgress<double> progress, CancellationToken ct)
    {
        SourceUnavailableException? last = null;
        foreach (var source in tool.Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resolved = await source.Resolve(_http, ct);
                await InstallFromAsync(tool, source, resolved, staging, progress, ct);
                return;
            }
            catch (SourceUnavailableException e)
            {
                last = e;
                ResetStaging(staging);
            }
        }
        throw new ToolInstallException($"The {tool.Name} download server isn’t responding right now.", InstallFailureKind.SourcesUnavailable, last?.Message, last);
    }

    private static void ResetStaging(string staging)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging)) TryDelete(entry);
        }
        catch (IOException) { }
    }

    private async Task InstallFromAsync(ToolDefinition tool, DownloadSource source, ResolvedDownload resolved, string staging, IProgress<double> progress, CancellationToken ct)
    {
        var package = Path.Combine(staging, tool.Package == PackageKind.Zip ? "download.zip" : "download.msi");
        await DownloadAsync(resolved, package, value => progress.Report(value * 0.8), ct);

        progress.Report(0.82);
        var actual = await HashAsync(package, ct);
        if (!string.Equals(actual, resolved.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ToolInstallException($"The download of {tool.Name} was damaged or altered, so it was deleted.", InstallFailureKind.Verification,
                $"Expected {resolved.Sha256}, got {actual}.");

        progress.Report(0.85);
        var unpacked = Path.Combine(staging, "unpacked");
        Directory.CreateDirectory(unpacked);
        await UnpackAsync(tool, package, unpacked, ct);
        File.Delete(package);

        progress.Report(0.93);
        var (folder, mainExe) = Prepare(tool, unpacked, staging);

        var signature = "Not applicable: the publisher doesn’t sign this program; the SHA-256 was checked.";
        if (tool.Signature != SignaturePolicy.None && mainExe is not null)
        {
            var result = _signatures.Verify(mainExe);
            switch (result.State)
            {
                case SignatureState.Valid:
                    signature = "Valid Authenticode signature" + (result.Signer is null ? "" : $" by {result.Signer}");
                    break;
                case SignatureState.Unsigned when tool.Signature == SignaturePolicy.IfSigned:
                    signature = "The program carries no signature; the SHA-256 was checked.";
                    break;
                case SignatureState.Unsigned:
                    throw new ToolInstallException($"{tool.Name} isn’t signed by its publisher, so it was deleted.", InstallFailureKind.Verification);
                default:
                    throw new ToolInstallException($"{tool.Name} isn’t signed by its publisher, so it was deleted.", InstallFailureKind.Verification, result.Detail);
            }
        }

        var record = new InstallRecord(tool.Id, resolved.Version, source.Name, resolved.Url.ToString(), resolved.Sha256, true, signature, _clock());
        try { File.WriteAllText(Path.Combine(folder, RecordFile), JsonSerializer.Serialize(record)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the record is informational */ }

        progress.Report(0.97);
        ct.ThrowIfCancellationRequested();
        Swap(tool, folder);
    }

    private async Task DownloadAsync(ResolvedDownload resolved, string file, Action<double> report, CancellationToken ct)
    {
        using var response = await _http.SendAsync(HttpMethod.Get, resolved.Url, ct);
        var total = response.Content.Headers.ContentLength ?? resolved.Size;
        long done = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                try
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(_options.StallTimeout);
                    read = await source.ReadAsync(buffer, stall.Token);
                }
                catch (Exception e) when (SourceHttp.IsNetwork(e, ct)) { throw SourceHttp.Unavailable(resolved.Url, e); }
                if (read == 0) break;
                try { await destination.WriteAsync(buffer.AsMemory(0, read), ct); }
                catch (IOException e) { throw new ToolInstallException("The download couldn’t be saved. Is the disk full?", InstallFailureKind.Other, e.Message, e); }
                done += read;
                if (total is > 0) report(Math.Min(1, (double)done / total.Value));
            }
        }
        catch (IOException e) when (!ct.IsCancellationRequested)
        {
            throw SourceHttp.Unavailable(resolved.Url, e);
        }
        if (total is > 0 && done != total) throw new SourceUnavailableException($"{resolved.Url.Host} ended the download early.");
        report(1);
    }

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return System.Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    // ---- Unpacking -------------------------------------------------------------------------------------------

    private async Task UnpackAsync(ToolDefinition tool, string package, string destination, CancellationToken ct)
    {
        if (tool.Package == PackageKind.Zip)
        {
            try { await Task.Run(() => ExtractZip(package, destination, ct), ct); }
            catch (InvalidDataException e) { throw new ToolInstallException($"The download of {tool.Name} couldn’t be unpacked.", InstallFailureKind.Unpack, e.Message, e); }
            return;
        }

        // An administrative unpack extracts the files without installing, registering or elevating.
        var msiexec = _options.MsiExecPath ?? Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        string last = "";
        try
        {
            await _runner(new ToolInvocation(msiexec, ["/a", package, "/qn", "TARGETDIR=" + destination]) { Timeout = TimeSpan.FromMinutes(20) },
                line => { if (!string.IsNullOrWhiteSpace(line)) last = line; }, ct);
        }
        catch (ProcessFailure e)
        {
            throw new ToolInstallException(
                e.TimedOut ? $"Unpacking {tool.Name} took too long and was stopped." : $"{tool.Name} couldn’t be unpacked.",
                InstallFailureKind.Unpack, e.Message, e);
        }
        catch (MissingToolException e)
        {
            throw new ToolInstallException($"{tool.Name} couldn’t be unpacked because Windows Installer wasn’t found.", InstallFailureKind.Unpack, last, e);
        }
    }

    private static void ExtractZip(string package, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(package);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An entry pointed outside the folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string? FindFile(string folder, string name) =>
        Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();

    /// <summary>Arranges the unpacked files the way detection looks for them. Returns the folder to swap into place
    /// and the program whose signature is checked.</summary>
    private static (string Folder, string? MainExe) Prepare(ToolDefinition tool, string unpacked, string staging)
    {
        string Missing(string exe) => throw new ToolInstallException($"The download didn’t contain {exe}.", InstallFailureKind.Unpack);
        switch (tool.Id)
        {
            case "ffmpeg":
            {
                var ffmpeg = FindFile(unpacked, "ffmpeg.exe") ?? Missing("ffmpeg.exe");
                var ffprobe = FindFile(unpacked, "ffprobe.exe") ?? Missing("ffprobe.exe");
                var folder = Path.Combine(staging, "ffmpeg-new");
                var bin = Path.Combine(folder, "bin");
                Directory.CreateDirectory(bin);
                File.Move(ffmpeg, Path.Combine(bin, "ffmpeg.exe"));
                File.Move(ffprobe, Path.Combine(bin, "ffprobe.exe"));
                return (folder, null);
            }
            case "pandoc":
            {
                var exe = FindFile(unpacked, "pandoc.exe") ?? Missing("pandoc.exe");
                return (Path.GetDirectoryName(exe)!, null);
            }
            case "libreoffice":
            {
                var exe = FindFile(unpacked, "soffice.exe") ?? Missing("soffice.exe");
                var program = Path.GetDirectoryName(exe)!;
                if (!Path.GetFileName(program).Equals("program", StringComparison.OrdinalIgnoreCase)) Missing("program\\soffice.exe");
                return (Path.GetDirectoryName(program)!, exe);
            }
            case "calibre":
            {
                var exe = FindFile(unpacked, "ebook-convert.exe") ?? Missing("ebook-convert.exe");
                return (Path.GetDirectoryName(exe)!, exe);
            }
            default:
                throw new ToolInstallException($"“{tool.Id}” isn’t a tool OpenShelf can install.", InstallFailureKind.Other);
        }
    }

    /// <summary>Puts a complete folder in place in one rename, keeping the old copy until the new one is there.</summary>
    private void Swap(ToolDefinition tool, string folder)
    {
        var final = Path.Combine(_root, tool.Folder);
        string? old = null;
        try
        {
            if (Directory.Exists(final))
            {
                old = Path.Combine(_root, ".old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(final, old);
            }
            try
            {
                Directory.Move(folder, final);
            }
            catch
            {
                if (old is not null && !Directory.Exists(final)) Directory.Move(old, final);
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolInstallException($"OpenShelf couldn’t put {tool.Name} in place. Close anything that is using it and try again.", InstallFailureKind.Other, e.Message, e);
        }
        if (old is not null) TryDelete(old);
    }

    // ---- winget ----------------------------------------------------------------------------------------------

    /// <summary>Runs winget for a person whose download sources were all down. Progress stays where it was (winget reports none).</summary>
    private async Task InstallWithWingetAsync(ToolDefinition tool, string winget, ToolInstallException sourcesDown, CancellationToken ct)
    {
        string last = "";
        try
        {
            await _runner(new ToolInvocation(winget,
                    ["install", "--id", tool.WingetId, "-e", "--accept-source-agreements", "--accept-package-agreements", "--scope", "user"])
                { Timeout = TimeSpan.FromMinutes(30) },
                line => { if (!string.IsNullOrWhiteSpace(line) && line.Any(char.IsLetter)) last = line.Trim(); }, ct);
        }
        catch (ProcessFailure e)
        {
            var why = e.TimedOut ? "it took too long" : last.Length > 0 ? last : e.Message;
            throw new ToolInstallException($"The {tool.Name} download server isn’t responding, and winget couldn’t install it either: {why}", InstallFailureKind.Winget, e.Message, e);
        }
        catch (MissingToolException e)
        {
            throw new ToolInstallException(sourcesDown.Message + " Try again later.", InstallFailureKind.SourcesUnavailable, sourcesDown.Detail, e);
        }
        NotifyChanged();
        if (Find(tool) is null)
            throw new ToolInstallException($"winget installed {tool.Name}, but OpenShelf can’t find it yet. Restart OpenShelf, or sign out and back in.", InstallFailureKind.Winget);
    }

    // ---- Remove ----------------------------------------------------------------------------------------------

    public Task RemoveAsync(string id)
    {
        var tool = Definition(id);
        if (_root.Length == 0) return Task.CompletedTask;
        if (_active.ContainsKey(id))
            throw new ToolInstallException($"{tool.Name} is being installed right now.", InstallFailureKind.Other);
        var final = Path.Combine(_root, tool.Folder);
        if (Directory.Exists(final))
        {
            var old = Path.Combine(_root, ".old-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(final, old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ToolInstallException($"{tool.Name} couldn’t be removed. Close anything that is using it and try again.", InstallFailureKind.Other, e.Message, e);
            }
            TryDelete(old);
        }
        NotifyChanged();
        return Task.CompletedTask;
    }

    // ---- Housekeeping ----------------------------------------------------------------------------------------

    /// <summary>Removes staging and replaced folders an earlier run left behind (a crash, a killed app). Recent ones may belong to another running OpenShelf.</summary>
    private void ClearLeftovers()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_root, ".*"))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith(".staging-") && !name.StartsWith(".old-")) continue;
                if (_clock() - new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero) > TimeSpan.FromHours(6) || name.StartsWith(".old-"))
                    TryDelete(directory);
            }
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
