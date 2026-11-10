using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenShelf.Convert;
using SkiaSharp;

namespace OpenShelf.Vision;

/// <summary>An image with everything but its subject made transparent.</summary>
public sealed class Cutout
{
    internal Cutout(int width, int height, byte[] bgra, byte[] mask, double coverage)
    {
        Width = width; Height = height; Bgra = bgra; Mask = mask; Coverage = coverage;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The cut-out picture: Width x Height x 4 bytes, B G R A with straight (not premultiplied) alpha.
    /// Fits WPF's <c>PixelFormats.Bgra32</c>; show it on a checkerboard for the before/after preview.</summary>
    public byte[] Bgra { get; }

    /// <summary>The subject mask, Width x Height bytes: 255 where the subject is, 0 for background, soft at the edges.</summary>
    public byte[] Mask { get; }

    /// <summary>Fraction of the picture the subject covers (0 to 1).</summary>
    public double Coverage { get; }

    /// <summary>The cut-out as PNG bytes.</summary>
    public byte[] ToPng()
    {
        using var image = new MagickImage(Bgra, new PixelReadSettings((uint)Width, (uint)Height, StorageType.Char, PixelMapping.BGRA));
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }
}

/// <summary>What a batch of cutouts did.</summary>
public sealed record BackgroundRemovalOutcome(IReadOnlyList<string> Saved, IReadOnlyList<string> WithoutSubject, IReadOnlyList<(string Path, string Error)> Failed);

/// <summary>Cuts the subject out of a photo, on this PC, with U²-Netp running in ONNX Runtime (CPU).
/// The 4.6 MB model is downloaded once (see <see cref="EnsureModelAsync"/>) and kept in <c>%LOCALAPPDATA%\OpenShelf\models</c>.</summary>
public sealed class BackgroundRemover
{
    /// <summary>"Name No Background.png".</summary>
    public const string Suffix = "No Background";

    private const int InputSize = 320;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private static readonly object SessionLock = new();
    private static string? _sessionPath;
    private static InferenceSession? _session;

    private readonly ModelStore _store;

    public BackgroundRemover(ModelStore? store = null) => _store = store ?? ModelStore.Default;

    /// <summary>Whether the model is on disk, so a cutout needs no download.</summary>
    public bool IsModelAvailable => _store.IsModelAvailable;

    /// <summary>Downloads the model if needed (progress 0 to 1). Call before the first cutout so the UI can show a download step.</summary>
    /// <exception cref="ModelUnavailableException">Offline, or the file did not match its checksum.</exception>
    public Task EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default) => _store.EnsureAsync(progress, ct);

    /// <summary>Finds the subject of one image and returns the cut-out and its mask without saving anything (the editor's preview).
    /// Downloads the model first if it is missing.</summary>
    /// <exception cref="NoSubjectException">No clear subject.</exception>
    /// <exception cref="ConversionException">The image is unreadable.</exception>
    public async Task<Cutout> CutOutAsync(string imagePath, CancellationToken ct = default)
    {
        await EnsureModelAsync(null, ct).ConfigureAwait(false);
        return await Task.Run(() => CutOut(PixelImage.Load(imagePath), ct), ct).ConfigureAwait(false);
    }

    /// <summary>Saves a cut-out next to <paramref name="imagePath"/> as "Name No Background.png". Returns the saved path.</summary>
    public Task<string> SaveAsync(string imagePath, Cutout cutout, CancellationToken ct = default) =>
        FileStaging.SaveAsync(imagePath, Suffix, "png", staged => File.WriteAllBytesAsync(staged, cutout.ToPng(), ct), ct);

    /// <summary>Cuts out one image and saves "Name No Background.png". Returns the saved path.</summary>
    /// <exception cref="NoSubjectException">No clear subject; nothing was saved.</exception>
    public async Task<string> RemoveAsync(string imagePath, CancellationToken ct = default) =>
        await SaveAsync(imagePath, await CutOutAsync(imagePath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Cuts out and saves each image; images with no clear subject are left alone and listed in the result.
    /// Downloads the model first when needed (its progress is the first part of <paramref name="progress"/>).</summary>
    public async Task<BackgroundRemovalOutcome> RemoveAsync(IReadOnlyList<string> imagePaths, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var download = _store.IsModelAvailable ? 0.0 : 0.3;
        if (download > 0) await EnsureModelAsync(new Progress<double>(p => progress?.Report(p * download)), ct).ConfigureAwait(false);
        var saved = new List<string>();
        var without = new List<string>();
        var failed = new List<(string, string)>();
        for (var i = 0; i < imagePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = imagePaths[i];
            try { saved.Add(await RemoveAsync(path, ct).ConfigureAwait(false)); }
            catch (NoSubjectException) { without.Add(path); }
            catch (Exception e) when (e is ConversionException or ModelUnavailableException) { failed.Add((path, e.Message)); }
            progress?.Report(download + (1 - download) * (i + 1) / imagePaths.Count);
        }
        return new BackgroundRemovalOutcome(saved, without, failed);
    }

    // ---- Segmentation ----------------------------------------------------------------------------------------

    private Cutout CutOut(PixelImage image, CancellationToken ct)
    {
        var session = Session();
        var small = image.OnWhite().Resize(InputSize, InputSize);
        var input = Preprocess(small);
        ct.ThrowIfCancellationRequested();
        var name = session.InputMetadata.Keys.First();
        float[] probabilities;
        using (var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]))
            probabilities = results.First().AsTensor<float>().ToArray();
        ct.ThrowIfCancellationRequested();

        // The network already ends in a sigmoid. Judge how clear the subject is before touching the picture.
        var max = probabilities.Max();
        var foreground = probabilities.Count(p => p > 0.5f);
        var coverage = (double)foreground / probabilities.Length;
        if (max < 0.5f || coverage < 0.005) throw new NoSubjectException();

        var mask = Upscale(probabilities, image.Width, image.Height);
        var bgra = new byte[image.Bgra.Length];
        Parallel.For(0, image.Height, y =>
        {
            for (var x = 0; x < image.Width; x++)
            {
                var p = y * image.Width + x;
                var o = p * 4;
                bgra[o] = image.Bgra[o]; bgra[o + 1] = image.Bgra[o + 1]; bgra[o + 2] = image.Bgra[o + 2];
                bgra[o + 3] = (byte)(image.Bgra[o + 3] * mask[p] / 255);
            }
        });
        return new Cutout(image.Width, image.Height, bgra, mask, coverage);
    }

    private static DenseTensor<float> Preprocess(PixelImage small)
    {
        var max = 1;
        for (var i = 0; i < small.Bgra.Length; i += 4)
            max = Math.Max(max, Math.Max(small.Bgra[i], Math.Max(small.Bgra[i + 1], small.Bgra[i + 2])));
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (var y = 0; y < InputSize; y++)
            for (var x = 0; x < InputSize; x++)
            {
                var o = (y * InputSize + x) * 4;
                tensor[0, 0, y, x] = (small.Bgra[o + 2] / (float)max - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (small.Bgra[o + 1] / (float)max - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (small.Bgra[o] / (float)max - Mean[2]) / Std[2];
            }
        return tensor;
    }

    /// <summary>Bilinear upscale of the 320 x 320 probabilities to the picture size, with soft edges (smoothstep from 0.1 to 0.9).</summary>
    private static byte[] Upscale(float[] probabilities, int width, int height)
    {
        var mask = new byte[width * height];
        var scaleX = (InputSize - 1.0) / Math.Max(width - 1, 1);
        var scaleY = (InputSize - 1.0) / Math.Max(height - 1, 1);
        Parallel.For(0, height, y =>
        {
            var fy = y * scaleY;
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, InputSize - 1);
            var ty = (float)(fy - y0);
            for (var x = 0; x < width; x++)
            {
                var fx = x * scaleX;
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, InputSize - 1);
                var tx = (float)(fx - x0);
                var top = probabilities[y0 * InputSize + x0] * (1 - tx) + probabilities[y0 * InputSize + x1] * tx;
                var bottom = probabilities[y1 * InputSize + x0] * (1 - tx) + probabilities[y1 * InputSize + x1] * tx;
                var value = top * (1 - ty) + bottom * ty;
                var t = Math.Clamp((value - 0.1f) / 0.8f, 0f, 1f);
                mask[y * width + x] = (byte)Math.Round(t * t * (3 - 2 * t) * 255);
            }
        });
        return mask;
    }

    private InferenceSession Session()
    {
        var path = _store.FindModel() ?? throw new ModelUnavailableException("The Remove Background model hasn’t been downloaded yet.");
        lock (SessionLock)
        {
            if (_session is not null && _sessionPath == path) return _session;
            _session?.Dispose();
            try
            {
                using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _session = new InferenceSession(path, options);
            }
            catch (OnnxRuntimeException e)
            {
                throw new ModelUnavailableException("The Remove Background model couldn’t be loaded. Remove it from the models folder to download it again.", e);
            }
            _sessionPath = path;
            return _session;
        }
    }
}
