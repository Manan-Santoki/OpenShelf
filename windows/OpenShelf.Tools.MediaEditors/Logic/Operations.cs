using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>What the editors need from the media engine, so the logic around it can be tested with a fake.
/// <see cref="MediaOperations"/> forwards to <see cref="MediaEditor"/>.</summary>
internal interface IMediaOperations
{
    /// <summary>Whether ffmpeg and ffprobe are installed.</summary>
    bool IsAvailable { get; }

    Task<MediaInfo> GetInfoAsync(string path, CancellationToken ct = default);
    Task<string> TrimAsync(string path, double start, double end, IProgress<double>? progress, CancellationToken ct);
    Task<string> ChangeSpeedAsync(string path, double rate, bool keepsPitch, IProgress<double>? progress, CancellationToken ct);
    Task<string> CropAsync(string path, CropFraction crop, IProgress<double>? progress, CancellationToken ct);
    Task<string> RotateAsync(string path, VideoRotation rotation, IProgress<double>? progress, CancellationToken ct);
    Task<IReadOnlyList<string>> SplitAsync(string path, IReadOnlyList<SplitSegment> segments, IProgress<double>? progress, IProgress<int>? partStarted, CancellationToken ct);
    Task<string> MergeAsync(IReadOnlyList<string> paths, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Null when the result would not be smaller (nothing is kept).</summary>
    Task<string?> CompressAsync(string path, CompressResolution resolution, CompressQuality quality, IProgress<double>? progress, CancellationToken ct);
    Task<string> MuteAsync(string path, IProgress<double>? progress, CancellationToken ct);

    Task<byte[]> ExtractFrameAsync(string path, double seconds, int maxSize, CancellationToken ct);
    Task<byte[]> ExtractPosterAsync(string path, int maxSize, CancellationToken ct);
    Task<IReadOnlyList<byte[]>> ExtractThumbnailStripAsync(string path, int count, int maxSize, CancellationToken ct);
    Task<float[]> ExtractWaveformAsync(string path, int bars, CancellationToken ct);

    Task<VideoSourceInfo> LoadCompressSourceAsync(string path, CancellationToken ct);
    Task<bool> CanEncodeHevcAsync(CancellationToken ct);
}

internal sealed class MediaOperations(MediaEditor editor) : IMediaOperations
{
    public bool IsAvailable => editor.IsAvailable;

    public Task<MediaInfo> GetInfoAsync(string path, CancellationToken ct = default) => editor.GetInfoAsync(path, ct);
    public Task<string> TrimAsync(string path, double start, double end, IProgress<double>? progress, CancellationToken ct) => editor.TrimAsync(path, start, end, progress, ct);
    public Task<string> ChangeSpeedAsync(string path, double rate, bool keepsPitch, IProgress<double>? progress, CancellationToken ct) => editor.ChangeSpeedAsync(path, rate, keepsPitch, progress, ct);
    public Task<string> CropAsync(string path, CropFraction crop, IProgress<double>? progress, CancellationToken ct) => editor.CropAsync(path, crop, progress, ct);
    public Task<string> RotateAsync(string path, VideoRotation rotation, IProgress<double>? progress, CancellationToken ct) => editor.RotateAsync(path, rotation, progress, ct);
    public Task<IReadOnlyList<string>> SplitAsync(string path, IReadOnlyList<SplitSegment> segments, IProgress<double>? progress, IProgress<int>? partStarted, CancellationToken ct) =>
        editor.SplitAsync(path, segments, progress, partStarted, ct);
    public Task<string> MergeAsync(IReadOnlyList<string> paths, IProgress<double>? progress, CancellationToken ct) => editor.MergeAsync(paths, progress, ct);
    public Task<string?> CompressAsync(string path, CompressResolution resolution, CompressQuality quality, IProgress<double>? progress, CancellationToken ct) =>
        editor.CompressAsync(path, resolution, quality, progress, ct);
    public Task<string> MuteAsync(string path, IProgress<double>? progress, CancellationToken ct) => editor.MuteAsync(path, progress, ct);
    public Task<byte[]> ExtractFrameAsync(string path, double seconds, int maxSize, CancellationToken ct) => editor.ExtractFrameAsync(path, seconds, maxSize, ct);
    public Task<byte[]> ExtractPosterAsync(string path, int maxSize, CancellationToken ct) => editor.ExtractPosterAsync(path, maxSize, ct);
    public Task<IReadOnlyList<byte[]>> ExtractThumbnailStripAsync(string path, int count, int maxSize, CancellationToken ct) => editor.ExtractThumbnailStripAsync(path, count, maxSize, ct);
    public Task<float[]> ExtractWaveformAsync(string path, int bars, CancellationToken ct) => editor.ExtractWaveformAsync(path, bars, ct);
    public Task<VideoSourceInfo> LoadCompressSourceAsync(string path, CancellationToken ct) => editor.LoadCompressSourceAsync(path, ct);
    public Task<bool> CanEncodeHevcAsync(CancellationToken ct) => editor.CanEncodeHevcAsync(ct);
}
