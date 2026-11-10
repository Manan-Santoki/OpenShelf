using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenShelf.Install;

/// <summary>A download resolved to a concrete address with the SHA-256 its publisher lists for it.</summary>
public sealed record ResolvedDownload(Uri Url, string Sha256, string Version, long? Size = null);

/// <summary>Where one copy of a tool can be fetched from. <see cref="Resolve"/> finds the current file and its checksum
/// (network metadata only; the body is not downloaded).</summary>
public sealed record DownloadSource(string Name, Func<SourceHttp, CancellationToken, Task<ResolvedDownload>> Resolve);

public enum PackageKind { Zip, Msi }

/// <summary>Everything the installer knows about one optional tool. All the addresses OpenShelf downloads from are in
/// this file, so a moved or renamed download is a one-line fix here.</summary>
public sealed record ToolDefinition(
    string Id,
    string Name,
    string Description,
    PackageKind Package,
    IReadOnlyList<DownloadSource> Sources,
    string WingetId,
    SignaturePolicy Signature)
{
    /// <summary>The folder under the tools root (the id).</summary>
    public string Folder => Id;
}

public static class ToolSources
{
    // ---- Addresses ---------------------------------------------------------------------------------------------
    // ffmpeg: build providers listed on ffmpeg.org's download page for Windows.
    public const string GyanBase = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string BtbnLatestApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    public const string BtbnAsset = "ffmpeg-master-latest-win64-gpl.zip";
    // Pandoc
    public const string PandocLatestApi = "https://api.github.com/repos/jgm/pandoc/releases/latest";
    // LibreOffice: the stable channel's directory listing, then <version>/win/x86_64/LibreOffice_<version>_Win_x86-64.msi
    public const string LibreOfficeStable = "https://download.documentfoundation.org/libreoffice/stable/";
    // calibre: GitHub release (digest listed by GitHub) and the vendor's own host for the same file.
    public const string CalibreLatestApi = "https://api.github.com/repos/kovidgoyal/calibre/releases/latest";
    public const string CalibreHost = "https://download.calibre-ebook.com/";

    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        new("ffmpeg", "ffmpeg", "MP3, OGG, MKV, WebM, AVI, WMV and every audio and video conversion", PackageKind.Zip,
            [new("gyan.dev", ResolveGyan), new("BtbN on GitHub", ResolveBtbn)], "Gyan.FFmpeg", SignaturePolicy.None),
        new("libreoffice", "LibreOffice", "Office documents, slides and spreadsheets: DOCX, PPTX, XLSX, ODT and more.", PackageKind.Msi,
            [new("download.documentfoundation.org", ResolveLibreOffice)], "TheDocumentFoundation.LibreOffice", SignaturePolicy.IfSigned),
        new("pandoc", "Pandoc", "Markdown, Word, EPUB and OpenDocument text conversions.", PackageKind.Zip,
            [new("github.com/jgm/pandoc", ResolvePandoc)], "JohnMacFarlane.Pandoc", SignaturePolicy.None),
        new("calibre", "calibre", "E-books: EPUB, MOBI, AZW3 and more.", PackageKind.Msi,
            [new("github.com/kovidgoyal/calibre", (h, ct) => ResolveCalibre(h, false, ct)),
             new("download.calibre-ebook.com", (h, ct) => ResolveCalibre(h, true, ct))], "calibre.calibre", SignaturePolicy.IfSigned),
    ];

    // ---- Resolvers ---------------------------------------------------------------------------------------------

    /// <summary>gyan.dev: "essentials" release zip; the .sha256 file beside it holds the checksum.</summary>
    public static async Task<ResolvedDownload> ResolveGyan(SourceHttp http, CancellationToken ct)
    {
        var sha = ParseSha256(await http.GetStringAsync(new Uri(GyanBase + ".sha256"), ct), "ffmpeg");
        string version = "latest";
        try { version = (await http.GetStringAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/release-version"), ct)).Trim(); }
        catch (SourceUnavailableException) { }
        return new ResolvedDownload(new Uri(GyanBase), sha, version);
    }

    /// <summary>BtbN's rolling release: GitHub's API lists each asset's digest.</summary>
    public static async Task<ResolvedDownload> ResolveBtbn(SourceHttp http, CancellationToken ct)
    {
        using var release = await http.GetJsonAsync(new Uri(BtbnLatestApi), ct);
        var asset = FindAsset(release.RootElement, name => name == BtbnAsset)
            ?? throw new SourceUnavailableException("The ffmpeg build wasn’t in the BtbN release.");
        return FromAsset(asset, "ffmpeg", "latest");
    }

    public static async Task<ResolvedDownload> ResolvePandoc(SourceHttp http, CancellationToken ct)
    {
        using var release = await http.GetJsonAsync(new Uri(PandocLatestApi), ct);
        var asset = FindAsset(release.RootElement, name => Regex.IsMatch(name, @"^pandoc-[\d.]+-windows-x86_64\.zip$"))
            ?? throw new SourceUnavailableException("No Pandoc download for Windows was found.");
        return FromAsset(asset, "Pandoc", Tag(release.RootElement));
    }

    public static async Task<ResolvedDownload> ResolveCalibre(SourceHttp http, bool vendorHost, CancellationToken ct)
    {
        using var release = await http.GetJsonAsync(new Uri(CalibreLatestApi), ct);
        var asset = FindAsset(release.RootElement, name => Regex.IsMatch(name, @"^calibre-64bit-[\d.]+\.msi$"))
            ?? throw new SourceUnavailableException("No calibre download for Windows was found.");
        var found = FromAsset(asset, "calibre", Tag(release.RootElement));
        if (!vendorHost) return found;
        var version = Regex.Match(found.Url.AbsolutePath, @"calibre-64bit-([\d.]+)\.msi").Groups[1].Value;
        return found with { Url = new Uri($"{CalibreHost}{version}/calibre-64bit-{version}.msi") };
    }

    /// <summary>The newest stable LibreOffice for Windows x86-64 and the checksum file published next to the MSI.</summary>
    public static async Task<ResolvedDownload> ResolveLibreOffice(SourceHttp http, CancellationToken ct)
    {
        var listing = await http.GetStringAsync(new Uri(LibreOfficeStable), ct);
        var versions = Regex.Matches(listing, "href=\"(\\d+(?:\\.\\d+){1,3})/\"")
            .Select(m => m.Groups[1].Value).Distinct()
            .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0)).ToList();
        if (versions.Count == 0) throw new SourceUnavailableException("No LibreOffice download for Windows was found.");
        SourceUnavailableException? last = null;
        foreach (var version in versions.Take(3))
        {
            var msi = new Uri($"{LibreOfficeStable}{version}/win/x86_64/LibreOffice_{version}_Win_x86-64.msi");
            try
            {
                var sha = ParseSha256(await http.GetStringAsync(new Uri(msi + ".sha256"), ct), "LibreOffice");
                return new ResolvedDownload(msi, sha, version);
            }
            catch (SourceUnavailableException e) { last = e; }
        }
        throw last!;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    /// <summary>The first 64 hex digits in a published checksum file ("hash  name" or just the hash).</summary>
    public static string ParseSha256(string text, string name)
    {
        var match = Regex.Match(text, @"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])");
        if (!match.Success) throw new ToolInstallException($"The checksum for {name} couldn’t be found, so it wasn’t installed.", InstallFailureKind.Verification);
        return match.Value.ToLowerInvariant();
    }

    private static string Tag(JsonElement release) =>
        release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString()!.TrimStart('v') : "latest";

    private static JsonElement? FindAsset(JsonElement release, Func<string, bool> match)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
            if (asset.TryGetProperty("name", out var name) && name.GetString() is { } text && match(text)) return asset;
        return null;
    }

    private static ResolvedDownload FromAsset(JsonElement? asset, string name, string version)
    {
        var element = asset!.Value;
        var url = element.GetProperty("browser_download_url").GetString()!;
        var digest = element.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new ToolInstallException($"The checksum for {name} couldn’t be found, so it wasn’t installed.", InstallFailureKind.Verification);
        long? size = element.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : null;
        return new ResolvedDownload(new Uri(url), ParseSha256(digest, name), version, size);
    }
}

/// <summary>HTTP with timeouts and failures mapped to <see cref="SourceUnavailableException"/> (a down server, an error
/// status, a timeout), so the installer can move to the next source.</summary>
public sealed class SourceHttp(HttpClient client, TimeSpan requestTimeout)
{
    public HttpClient Client { get; } = client;
    public TimeSpan RequestTimeout { get; } = requestTimeout;

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception e) when (IsNetwork(e, ct)) { throw Unavailable(url, e); }
    }

    public async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct);
        try { return JsonDocument.Parse(text); }
        catch (JsonException e) { throw new SourceUnavailableException($"{url.Host} answered with something unexpected.", e); }
    }

    /// <summary>Sends a request and returns once the headers are in; the status is already checked.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        HttpResponseMessage? response = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new SourceUnavailableException($"{url.Host} answered with an error ({(int)response.StatusCode}).");
            var final = response.RequestMessage?.RequestUri;
            if (final is not null && final.Scheme != Uri.UriSchemeHttps)
                throw new SourceUnavailableException($"{url.Host} sent the download somewhere that isn’t secure ({final.Host}).");
            return response;
        }
        catch (Exception e) when (IsNetwork(e, ct))
        {
            response?.Dispose();
            throw Unavailable(url, e);
        }
        catch (SourceUnavailableException)
        {
            response?.Dispose();
            throw;
        }
    }

    internal static bool IsNetwork(Exception e, CancellationToken ct) =>
        (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        && !ct.IsCancellationRequested;

    internal static SourceUnavailableException Unavailable(Uri url, Exception e) =>
        new(e is TaskCanceledException or OperationCanceledException
            ? $"{url.Host} took too long to respond."
            : $"{url.Host} couldn’t be reached ({e.Message}).", e);
}
