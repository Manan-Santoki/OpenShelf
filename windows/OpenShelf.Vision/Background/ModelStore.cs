using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenShelf.Vision;

/// <summary>A model file OpenShelf may download: a pinned address and the SHA-256 of the exact bytes expected.</summary>
/// <param name="InputSize">The square size the network reads.</param>
/// <param name="Mean">Per-channel (R, G, B) mean subtracted from 0..1 pixel values.</param>
/// <param name="Std">Per-channel divisor applied after the mean.</param>
public sealed record ModelDefinition(string FileName, Uri Url, string Sha256, long SizeBytes, string Licence,
    int InputSize = 320, float[]? Mean = null, float[]? Std = null)
{
    public float[] ChannelMean => Mean ?? [0.485f, 0.456f, 0.406f];
    public float[] ChannelStd => Std ?? [0.229f, 0.224f, 0.225f];

    /// <summary>ISNet, general use (Qin et al., "Highly Accurate Dichotomous Image Segmentation", ECCV 2022), 1024 x 1024, ONNX
    /// export published with the rembg project's release v0.0.0. Much finer edges (hair, fur, thin objects) than U²-Netp, at
    /// 178 MB and a few seconds per picture on a CPU. Licences: the model is Apache-2.0 (github.com/xuebinqin/DIS); rembg is MIT.
    /// GitHub lists no digest for this old release; the SHA-256 is the file's as downloaded on 2026-11-18.</summary>
    public static ModelDefinition IsNet { get; } = new(
        "isnet-general-use.onnx",
        new Uri("https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx"),
        "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a",
        178_648_008,
        "Apache-2.0",
        InputSize: 1024, Mean: [0.5f, 0.5f, 0.5f], Std: [1f, 1f, 1f]);

    /// <summary>U²-Netp (Qin et al., "U²-Net: Going Deeper with Nested U-Structure for Salient Object Detection", Pattern Recognition 2020),
    /// the 4.6 MB variant, ONNX export published with the rembg project's release v0.0.0.
    /// Licences: the model is Apache-2.0 (github.com/xuebinqin/U-2-Net); rembg, which hosts the file, is MIT.</summary>
    public static ModelDefinition U2Netp { get; } = new(
        "u2netp.onnx",
        new Uri("https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx"),
        "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
        4_574_861,
        "Apache-2.0");
}

/// <summary>Where the segmentation model lives and how it is fetched the first time: <c>%LOCALAPPDATA%\OpenShelf\models</c>.
/// A copy shipped beside the app in a <c>models</c> folder is used first, so a bundled build never downloads.</summary>
public sealed class ModelStore
{
    private readonly string _directory;
    private readonly HttpMessageHandler? _handler;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="directory">Where downloads go; null for the user's <c>%LOCALAPPDATA%\OpenShelf\models</c>.</param>
    /// <param name="definition">The model; null for U²-Netp.</param>
    /// <param name="handler">For tests: replaces the network.</param>
    /// <param name="bundledDirectory">Folder checked before downloading; null for <c>models</c> beside the app. Pass an empty string for none.</param>
    public ModelStore(string? directory = null, ModelDefinition? definition = null, HttpMessageHandler? handler = null, string? bundledDirectory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "models");
        Definition = definition ?? ModelDefinition.U2Netp;
        _handler = handler;
        BundledDirectory = bundledDirectory ?? Path.Combine(AppContext.BaseDirectory, "models");
    }

    /// <summary>The store the app uses: U²-Netp, or ISNet when Settings asks for high-quality cutouts.</summary>
    public static ModelStore Default { get; set; } = new();

    /// <summary>Points <see cref="Default"/> at the fast or the high-quality model.</summary>
    public static void UseHighQuality(bool highQuality)
    {
        var wanted = highQuality ? ModelDefinition.IsNet : ModelDefinition.U2Netp;
        if (Default.Definition != wanted) Default = new ModelStore(definition: wanted);
    }

    public ModelDefinition Definition { get; }

    public string BundledDirectory { get; }

    /// <summary>Where a downloaded model is kept.</summary>
    public string DownloadedPath => Path.Combine(_directory, Definition.FileName);

    /// <summary>The model file to load, or null when it has not been downloaded yet.</summary>
    public string? FindModel()
    {
        if (BundledDirectory.Length > 0 && Path.Combine(BundledDirectory, Definition.FileName) is var bundled && File.Exists(bundled)) return bundled;
        return File.Exists(DownloadedPath) ? DownloadedPath : null;
    }

    /// <summary>Whether the model is on disk (bundled or downloaded) and so removing a background needs no download.</summary>
    public bool IsModelAvailable => FindModel() is not null;

    /// <summary>Downloads the model when it is missing, checking its SHA-256 before it is put in place. Does nothing if present.</summary>
    /// <param name="progress">0 to 1 while downloading.</param>
    /// <exception cref="ModelUnavailableException">The download failed or the file did not match its checksum; nothing is kept.</exception>
    public async Task EnsureAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsModelAvailable) { progress?.Report(1); return; }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsModelAvailable) { progress?.Report(1); return; }
            await DownloadAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var part = Path.Combine(_directory, $".{Definition.FileName}.{Guid.NewGuid():N}.part");
        using var client = _handler is null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = true, MaxAutomaticRedirections = 8 })
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenShelf", "1.0"));
        try
        {
            progress?.Report(0);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync(Definition.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? Definition.SizeBytes;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(60)); // no bytes for a minute counts as a network failure
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(Math.Min((double)received / Math.Max(total, 1), 0.99));
                }
            }
            var actual = System.Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(Definition.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ModelUnavailableException("The downloaded model didn’t match its checksum, so it was discarded. Try again later.");
            try { File.Move(part, DownloadedPath); }
            catch (IOException) when (File.Exists(DownloadedPath)) { /* another process finished first */ }
            progress?.Report(1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException("The download stalled. Check your internet connection and try again.");
        }
        catch (HttpRequestException e)
        {
            throw new ModelUnavailableException("The Remove Background model couldn’t be downloaded. Check your internet connection and try again.", e);
        }
        catch (IOException e)
        {
            throw new ModelUnavailableException("The Remove Background model couldn’t be saved. Check the free space on your disk.", e);
        }
        finally
        {
            try { File.Delete(part); } catch (Exception) { /* best effort */ }
        }
    }
}
