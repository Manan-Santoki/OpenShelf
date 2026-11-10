using OpenShelf.Convert;
using OpenShelf.Core;

namespace OpenShelf.Tools.Media;

/// <summary>What an editor shows before an edit: a file's length, kind and picture size.</summary>
/// <param name="Duration">Seconds; 0 when unknown.</param>
/// <param name="DisplaySize">The picture size players show, after rotation; null without video.</param>
/// <param name="Rotation">Degrees clockwise the picture is turned by metadata (0 without video).</param>
public sealed record MediaInfo(double Duration, bool HasVideo, bool HasAudio, (int Width, int Height)? DisplaySize, int Rotation, long FileSize);

/// <summary>The video and audio tools (SPEC 2.4): Trim, Speed, Crop, Rotate, Split, Merge, Compress, Mute, and the previews the editors
/// show. Headless, async and cancellable, each with an <see cref="IProgress{T}"/> that gets 0 to 1. Every edit writes a new file beside
/// the source and never overwrites: it is written to a hidden file first and moved into place only when complete, so a cancelled or
/// failed export leaves nothing behind. Everything goes through ffmpeg (found by <see cref="ExternalToolsProvider"/>).
/// Port of MediaTools, MediaEdits and MediaLoading.</summary>
public sealed partial class MediaEditor
{
    private readonly Func<ExternalTools> _tools;
    private readonly ToolRunner _runner;
    private readonly MediaProbe _probe;
    private readonly FFmpegEncoders _encoders;
    private readonly OutputRunner _output;

    /// <param name="tools">Where ffmpeg and ffprobe are; default <see cref="ExternalToolsProvider.Default"/>.</param>
    /// <param name="runner">Starts ffmpeg; tests swap in a fake.</param>
    /// <param name="probe">Reads files with ffprobe.</param>
    /// <param name="encoders">Asks ffmpeg which encoders it has.</param>
    /// <param name="output">Runs short ffprobe queries (keyframe positions).</param>
    public MediaEditor(Func<ExternalTools>? tools = null, ToolRunner? runner = null, MediaProbe? probe = null, FFmpegEncoders? encoders = null, OutputRunner? output = null)
    {
        _tools = tools ?? (() => ExternalToolsProvider.Default.Current);
        _runner = runner ?? ToolRunners.Default;
        _probe = probe ?? MediaProbe.Default;
        _encoders = encoders ?? FFmpegEncoders.Default;
        _output = output ?? OutputRunners.Default;
    }

    /// <summary>Whether ffmpeg and ffprobe are both installed, so the tools can be offered.</summary>
    public bool IsAvailable => _tools() is { Ffmpeg: not null, Ffprobe: not null };

    // Opening

    private sealed record Source(string Path, string Ffmpeg, string Ffprobe, ProbedMedia Probed, IReadOnlySet<string> Encoders)
    {
        public bool Has(string encoder) => Encoders.Count == 0 || Encoders.Contains(encoder);
        public bool HasHevc => Has("libx265");
        public bool CanEncodeH264 => Has("libx264");
    }

    private async Task<Source> OpenAsync(string path, CancellationToken ct)
    {
        var tools = _tools();
        var ffmpeg = tools.Ffmpeg ?? throw new MissingToolException("ffmpeg");
        var ffprobe = tools.Ffprobe ?? throw new MissingToolException("ffprobe");
        var full = System.IO.Path.GetFullPath(FileKinds.ResolveLink(path));
        if (!File.Exists(full)) throw ConversionException.Unreadable(full);
        var probed = await _probe.LoadAsync(full, ffprobe, ct).ConfigureAwait(false) ?? throw ConversionException.Unreadable(full);
        var encoders = await _encoders.LoadAsync(ffmpeg, ct).ConfigureAwait(false);
        return new Source(full, ffmpeg, ffprobe, probed, encoders);
    }

    private static string Name(Source s) => System.IO.Path.GetFileName(s.Path);

    private static ConversionException NoVideo(Source s) => ConversionException.Failed($"“{Name(s)}” has no video track.");

    /// <summary>The file's length, kind and picture size, for the editor to set itself up.</summary>
    public async Task<MediaInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var source = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return new MediaInfo(source.Probed.Duration, source.Probed.Video is not null, source.Probed.HasAudio,
            source.Probed.Video?.DisplaySize, source.Probed.Video?.Rotation ?? 0, new FileInfo(source.Path).Length);
    }

    private async Task<string> ExportAsync(Source s, IReadOnlyList<IReadOnlyList<string>> attempts, string destination, double duration, IProgress<double>? progress, CancellationToken ct) =>
        await StagedExport.RunAsync(_runner, s.Ffmpeg, attempts, destination, duration, progress, ct).ConfigureAwait(false) ?? destination;

    // Mute

    /// <summary>A copy of a video with its audio tracks removed ("Name Muted.mp4"). The picture is copied when the container allows.</summary>
    public async Task<string> MuteAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var video = s.Probed.Video ?? throw NoVideo(s);
        var destination = OutputNaming.Destination(s.Path, "Muted", MediaFormats.VideoContainerExtension(s.Path));
        // Every picture stream, copied, unless the container can't hold its codec.
        string[] input = ["-i", s.Path, "-map", "0:V"];
        var attempts = new List<IReadOnlyList<string>>();
        attempts.Add([.. input, .. FFmpegEdits.VideoCopy(video)]);
        if (s.CanEncodeH264) attempts.Add([.. input, .. FFmpegEdits.VideoEncoding(video, s.HasHevc)]);
        return await ExportAsync(s, attempts, destination, s.Probed.Duration, progress, cancellationToken).ConfigureAwait(false);
    }

    // Trim and Split

    private enum CopyPolicy
    {
        /// <summary>Copy only when the cut is exact: from the start, or at a keyframe. Otherwise re-encode the picture.</summary>
        WhenExact,
        /// <summary>Copy always (a part may start at the keyframe before its time), re-encoding only if the copy fails.</summary>
        Always,
    }

    /// <summary>The part of a video or audio file between <paramref name="start"/> and <paramref name="end"/> seconds ("Name Trimmed.mp4").
    /// Sound is copied. A video is copied as well when the cut is exact (it starts at 0 or on a keyframe), otherwise its picture is
    /// re-encoded so it starts exactly there.</summary>
    public Task<string> TrimAsync(string path, double start, double end, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ExportRangeAsync(path, start, end, "Trimmed", CopyPolicy.WhenExact, progress, cancellationToken);

    /// <summary>Exports each segment as "Name Part N" without re-encoding where the container allows. Reports overall progress, and
    /// <paramref name="partStarted"/> gets each part's number (1-based) as it begins. Cancelling or failing deletes the parts already
    /// written, so no partial set is left behind.</summary>
    public async Task<IReadOnlyList<string>> SplitAsync(string path, IReadOnlyList<SplitSegment> segments, IProgress<double>? progress = null,
        IProgress<int>? partStarted = null, CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0) throw new ArgumentException("Nothing to split into.", nameof(segments));
        var outputs = new List<string>();
        try
        {
            for (var index = 0; index < segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                partStarted?.Report(index + 1);
                var i = index;
                var scaled = new DirectProgress(fraction => progress?.Report(MediaBatch.Overall(fraction, i, segments.Count)));
                outputs.Add(await ExportRangeAsync(path, segments[index].Start, segments[index].End, $"Part {index + 1}", CopyPolicy.Always, scaled, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            foreach (var output in outputs) FFmpegJob.Discard(output);
            throw;
        }
        return outputs;
    }

    private async Task<string> ExportRangeAsync(string path, double start, double end, string suffix, CopyPolicy policy, IProgress<double>? progress, CancellationToken ct)
    {
        var s = await OpenAsync(path, ct).ConfigureAwait(false);
        var duration = s.Probed.Duration;
        start = Math.Max(start, 0);
        if (duration > 0) end = Math.Min(end, duration);
        if (!(end - start > 0)) throw ConversionException.Failed($"Nothing would be left of “{Name(s)}” after that cut.");
        // A cut that reaches the file's last frames goes to its very end.
        var toEnd = duration > 0 && end >= duration - 0.05;
        double? length = toEnd ? duration - start : end - start;
        var outputDuration = length ?? 0;
        var attempts = new List<IReadOnlyList<string>>();
        string destination;

        if (s.Probed.Video is { } video)
        {
            destination = OutputNaming.Destination(s.Path, suffix, MediaFormats.VideoContainerExtension(s.Path));
            string[] maps = ["-map", "0:V", "-map", "0:a?"];
            var exact = policy == CopyPolicy.Always || start <= 0.001 || await IsKeyframeNearAsync(s, video, start, ct).ConfigureAwait(false);
            if (exact)
                attempts.Add([.. FFmpegEdits.CopyInput(s.Path, start, toEnd ? null : end), .. maps, .. FFmpegEdits.VideoCopy(video),
                    .. FFmpegEdits.AudioCopyOrAac(s.Probed.Audio)]);
            // A cut rarely falls on a keyframe, so the picture is re-encoded to start exactly there.
            if (s.CanEncodeH264)
                attempts.Add([.. FFmpegEdits.Input(s.Path, start, length), .. maps, .. FFmpegEdits.VideoEncoding(video, s.HasHevc), .. FFmpegEdits.AudioCopyOrAac(s.Probed.Audio)]);
        }
        else
        {
            if (!s.Probed.HasAudio) throw ConversionException.Failed($"“{Name(s)}” has no audio or video.");
            var extension = MediaFormats.OutputExtension(s.Path, hasVideo: false);
            destination = OutputNaming.Destination(s.Path, suffix, extension);
            string[] maps = ["-map", "0:a", "-vn"];
            if (extension.Equals(System.IO.Path.GetExtension(s.Path).TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                attempts.Add([.. FFmpegEdits.CopyInput(s.Path, start, toEnd ? null : end), .. maps, "-c:a", "copy"]);
            else
                // Formats that cannot be copied into place become AAC in an M4A.
                attempts.Add([.. FFmpegEdits.Input(s.Path, start, length), .. maps, .. FFmpegEdits.AudioCopyOrAac(s.Probed.Audio)]);
        }
        return await ExportAsync(s, attempts, destination, outputDuration, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Whether a keyframe sits at <paramref name="time"/> (to within half a frame), so a stream copy starting there is exact.</summary>
    private async Task<bool> IsKeyframeNearAsync(Source s, ProbedVideo video, double time, CancellationToken ct)
    {
        var fps = video.FramesPerSecond is > 0 ? video.FramesPerSecond.Value : 30;
        var tolerance = 0.5 / fps + 0.002;
        try
        {
            var text = await _output(new ToolInvocation(s.Ffprobe, KeyframeQuery(s.Path, time)) { Timeout = TimeSpan.FromSeconds(60) }, ct).ConfigureAwait(false);
            return KeyframeAt(text, time, tolerance);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ProcessFailure or IOException) { return false; }
    }

    /// <summary>The ffprobe arguments listing the packets (time and flags) around <paramref name="time"/>.</summary>
    public static IReadOnlyList<string> KeyframeQuery(string path, double time) =>
    [
        "-v", "error", "-select_streams", "v:0",
        "-read_intervals", $"{FFmpegEdits.Seconds(Math.Max(time - 1, 0))}%{FFmpegEdits.Seconds(time + 1)}",
        "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path,
    ];

    /// <summary>Whether ffprobe's packet list (<c>12.5,K__</c> per line) has a keyframe within <paramref name="tolerance"/> of <paramref name="time"/>.</summary>
    public static bool KeyframeAt(string packets, double time, double tolerance)
    {
        foreach (var line in packets.Split('\n'))
        {
            var fields = line.Trim().Split(',');
            if (fields.Length < 2 || !fields[1].Contains('K')) continue;
            if (double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pts) && Math.Abs(pts - time) <= tolerance)
                return true;
        }
        return false;
    }

    // Speed

    /// <summary>Plays the whole file <paramref name="rate"/> times faster (or slower, below 1), 0.25x to 4x, as "Name 2x.mp4" (audio: "Name 2x.m4a").
    /// Always re-encodes. With <paramref name="keepsPitch"/> the sound keeps its pitch; without, it follows the speed like a record.</summary>
    public async Task<string> ChangeSpeedAsync(string path, double rate, bool keepsPitch, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!(rate >= MediaFormats.MinimumSpeed && rate <= MediaFormats.MaximumSpeed))
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "Speed is 0.25x to 4x.");
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (s.Probed.Video is null && !s.Probed.HasAudio) throw ConversionException.Failed($"“{Name(s)}” has no audio or video.");
        var extension = s.Probed.Video is not null ? MediaFormats.VideoContainerExtension(s.Path) : "m4a";
        var destination = OutputNaming.Destination(s.Path, MediaFormats.SpeedSuffix(rate), extension);
        if (s.Probed.Video is not null && !s.CanEncodeH264) throw ConversionException.Failed("This copy of ffmpeg can’t encode video (it has no libx264 encoder). A full ffmpeg build can.");
        IReadOnlyList<string> attempt = ["-i", s.Path, .. FFmpegEdits.Speed(s.Probed, rate, keepsPitch, s.HasHevc)];
        return await ExportAsync(s, [attempt], destination, s.Probed.Duration / rate, progress, cancellationToken).ConfigureAwait(false);
    }

    // Crop

    /// <summary>Crops a video to a rectangle given as fractions of the upright frame ("Name Cropped.mp4"); the pixels are worked out
    /// here, rounded to even sides. Audio is kept.</summary>
    public async Task<string> CropAsync(string path, CropFraction crop, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var video = s.Probed.Video ?? throw NoVideo(s);
        var size = video.DisplaySize;
        return await CropCoreAsync(s, video, crop.ToPixels(size.Width, size.Height), progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Crops a video to <paramref name="rect"/>, in pixels of the upright frame with the origin at the top left.</summary>
    public async Task<string> CropAsync(string path, PixelRect rect, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var video = s.Probed.Video ?? throw NoVideo(s);
        var size = video.DisplaySize;
        return await CropCoreAsync(s, video, CropMath.Even(rect, size.Width, size.Height), progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CropCoreAsync(Source s, ProbedVideo video, PixelRect rect, IProgress<double>? progress, CancellationToken ct)
    {
        if (!s.CanEncodeH264) throw ConversionException.Failed("This copy of ffmpeg can’t encode video (it has no libx264 encoder). A full ffmpeg build can.");
        var destination = OutputNaming.Destination(s.Path, "Cropped", MediaFormats.VideoContainerExtension(s.Path));
        // ffmpeg turns frames upright before filtering them, so the crop applies to the picture as the editor showed it.
        IReadOnlyList<string> attempt =
        [
            "-i", s.Path, "-map", "0:V:0", "-map", "0:a?", "-vf", FFmpegEdits.CropFilter(rect),
            .. FFmpegEdits.VideoEncoding(video, s.HasHevc), .. FFmpegEdits.AudioCopyOrAac(s.Probed.Audio),
        ];
        return await ExportAsync(s, [attempt], destination, s.Probed.Duration, progress, ct).ConfigureAwait(false);
    }

    // Rotate

    /// <summary>Turns or mirrors a video ("Name Rotated.mp4"). Turns keep the original frames and only change the angle players show
    /// them at; a mirror re-encodes. Audio is kept.</summary>
    public async Task<string> RotateAsync(string path, VideoRotation rotation, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var video = s.Probed.Video ?? throw NoVideo(s);
        var destination = OutputNaming.Destination(s.Path, "Rotated", MediaFormats.VideoContainerExtension(s.Path));
        string[] streams = ["-map", "0:V:0", "-map", "0:a?"];
        var audio = FFmpegEdits.AudioCopyOrAac(s.Probed.Audio);
        var attempts = new List<IReadOnlyList<string>>();
        if (rotation.IsLossless())
        {
            // A turn only changes the angle players show the frames at. ffmpeg before 6.0 cannot set it, and re-encodes.
            var angle = FFmpegEdits.DisplayRotation(video, rotation);
            attempts.Add(["-display_rotation:v:0", angle.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", s.Path, .. streams, .. FFmpegEdits.VideoCopy(video), .. audio]);
        }
        if (s.CanEncodeH264 || attempts.Count == 0)
            attempts.Add(["-i", s.Path, .. streams, "-vf", FFmpegEdits.RotationFilter(rotation), .. FFmpegEdits.VideoEncoding(video, s.HasHevc), .. audio]);
        return await ExportAsync(s, attempts, destination, s.Probed.Duration, progress, cancellationToken).ConfigureAwait(false);
    }

    // Merge

    /// <summary>Joins videos into "Merged.mp4" or audio files into "Merged.m4a" in the given order, saved next to the first one. The first
    /// video sets the size and frame rate; the others are fitted inside it (letterboxed, and turned upright). Videos without sound get silence.</summary>
    public async Task<string> MergeAsync(IReadOnlyList<string> paths, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count < 2) throw ConversionException.Failed("Choose at least two files to merge.");
        var sources = new List<Source>();
        foreach (var path in paths) sources.Add(await OpenAsync(path, cancellationToken).ConfigureAwait(false));
        var clips = sources.Select(x => x.Probed).ToList();
        var folder = System.IO.Path.GetDirectoryName(sources[0].Path) ?? ".";
        var isVideo = clips.All(c => c.Video is not null);
        var inputs = sources.SelectMany(x => new[] { "-i", x.Path }).ToList();
        string destination;
        IReadOnlyList<string> filters;
        if (isVideo)
        {
            if (!sources[0].CanEncodeH264) throw ConversionException.Failed("This copy of ffmpeg can’t encode video (it has no libx264 encoder). A full ffmpeg build can.");
            destination = OutputNaming.UniquePath(folder, "Merged", "mp4");
            filters = FFmpegEdits.MergeVideos(clips);
        }
        else
        {
            foreach (var source in sources)
                if (!source.Probed.HasAudio) throw ConversionException.Failed($"“{Name(source)}” has no audio or video.");
            destination = OutputNaming.UniquePath(folder, "Merged", "m4a");
            filters = FFmpegEdits.MergeAudio(clips);
        }
        IReadOnlyList<string> attempt = [.. inputs, .. filters];
        return await StagedExport.RunAsync(_runner, sources[0].Ffmpeg, [attempt], destination, clips.Sum(c => c.Duration), progress, cancellationToken).ConfigureAwait(false) ?? destination;
    }

    // Compress

    /// <summary>Reads what Compress needs to estimate sizes instantly as the choices change: load once, then call
    /// <see cref="VideoCompressionPlan.Estimate"/> for each resolution and quality.</summary>
    public async Task<VideoSourceInfo> LoadCompressSourceAsync(string path, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return VideoSourceInfo.From(s.Probed, new FileInfo(s.Path).Length) ?? throw NoVideo(s);
    }

    /// <summary>Whether HDR videos can keep their colours (HEVC), for <see cref="VideoCompressionPlan.Estimate"/>'s last argument.</summary>
    public async Task<bool> CanEncodeHevcAsync(CancellationToken cancellationToken = default)
    {
        var ffmpeg = _tools().Ffmpeg ?? throw new MissingToolException("ffmpeg");
        var encoders = await _encoders.LoadAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        return encoders.Count == 0 || encoders.Contains("libx265");
    }

    /// <summary>Re-encodes a video into "Name Compressed.mp4" as <see cref="VideoCompressionPlan"/> lays out, keeping its rotation and
    /// sound. Returns null, keeping nothing, when the result is not smaller than the original.</summary>
    public async Task<string?> CompressAsync(string path, CompressResolution resolution, CompressQuality quality, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var info = VideoSourceInfo.From(s.Probed, new FileInfo(s.Path).Length) ?? throw NoVideo(s);
        if (!s.CanEncodeH264) throw ConversionException.Failed("This copy of ffmpeg can’t encode video (it has no libx264 encoder). A full ffmpeg build can.");
        var plan = new VideoCompressionPlan(info, resolution, quality, s.HasHevc);
        var destination = OutputNaming.Destination(s.Path, "Compressed", "mp4");
        IReadOnlyList<string> attempt = FFmpegEdits.Compress(s.Path, plan, info);
        return await StagedExport.RunAsync(_runner, s.Ffmpeg, [attempt], destination, info.Duration, progress, cancellationToken, keep: size => size < info.FileSize).ConfigureAwait(false);
    }
}
