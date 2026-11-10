using OpenShelf.Convert;

namespace OpenShelf.Tools.Media;

/// <summary>Previews for the editors: one frame, a thumbnail strip, and waveform peaks. Frames come back as PNG bytes.</summary>
public sealed partial class MediaEditor
{
    /// <summary>The time Swift shows a video by when nothing else is chosen: a second in, or halfway through shorter clips.</summary>
    public static double PosterTime(double duration) => duration > 2 ? 1 : duration / 2;

    /// <summary>One frame at <paramref name="seconds"/>, upright, as PNG bytes. <paramref name="maxSize"/> (pixels, 0 for none) limits the longer
    /// side; the frame is never enlarged.</summary>
    public async Task<byte[]> ExtractFrameAsync(string path, double seconds, int maxSize = 0, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (s.Probed.Video is null) throw NoVideo(s);
        return await FrameAsync(s, seconds, maxSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The frame to show a video by (<see cref="PosterTime"/>), upright, as PNG bytes.</summary>
    public async Task<byte[]> ExtractPosterAsync(string path, int maxSize = 0, CancellationToken cancellationToken = default)
    {
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (s.Probed.Video is null) throw NoVideo(s);
        return await FrameAsync(s, PosterTime(s.Probed.Duration), maxSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="count"/> frames spread evenly over the video (each from the middle of its slice), for a trimming filmstrip, as
    /// PNG bytes at most <paramref name="maxSize"/> on the longer side. Frames that will not decode are left out.</summary>
    public async Task<IReadOnlyList<byte[]>> ExtractThumbnailStripAsync(string path, int count, int maxSize = 200, CancellationToken cancellationToken = default)
    {
        if (count <= 0) return [];
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (s.Probed.Video is null) throw NoVideo(s);
        var duration = s.Probed.Duration;
        var frames = new byte[]?[count];
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(Enumerable.Range(0, count).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                frames[index] = await FrameAsync(s, duration * (index + 0.5) / count, maxSize, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ProcessFailure or ConversionException or IOException) { /* left out */ }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return frames.OfType<byte[]>().ToList();
    }

    private async Task<byte[]> FrameAsync(Source s, double seconds, int maxSize, CancellationToken ct)
    {
        var duration = s.Probed.Duration;
        var fps = s.Probed.Video?.FramesPerSecond is > 0 ? s.Probed.Video!.FramesPerSecond!.Value : 30;
        seconds = Math.Max(seconds, 0);
        // A frame lasts 1/fps, and asking past the last frame's start finds none: stay a frame and a half inside the end.
        if (duration > 0) seconds = Math.Min(seconds, Math.Max(duration - 1.5 / fps, 0));
        // If the container's length was a little long, try a bit earlier and then the first frame.
        foreach (var at in new[] { seconds, Math.Max(seconds - 0.5, 0), 0 }.Distinct())
        {
            if (await TryFrameAsync(s, at, maxSize, ct).ConfigureAwait(false) is { } frame) return frame;
        }
        throw ConversionException.Unreadable(s.Path);
    }

    private async Task<byte[]?> TryFrameAsync(Source s, double seconds, int maxSize, CancellationToken ct)
    {
        var temporary = TempPath("png");
        try
        {
            var arguments = new List<string> { "-y", "-nostdin", "-loglevel", "error" };
            arguments.AddRange(FFmpegEdits.Input(s.Path, seconds, null));
            arguments.AddRange(["-map", "0:V:0", "-frames:v", "1"]);
            if (maxSize > 0)
                arguments.AddRange(["-vf", $"scale=w='min(iw,{maxSize})':h='min(ih,{maxSize})':force_original_aspect_ratio=decrease"]);
            arguments.AddRange(["-f", "image2", "-c:v", "png", temporary]);
            await _runner(new ToolInvocation(s.Ffmpeg, arguments) { Timeout = TimeSpan.FromMinutes(2) }, null, ct).ConfigureAwait(false);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) return null;
            return await File.ReadAllBytesAsync(temporary, ct).ConfigureAwait(false);
        }
        finally
        {
            FFmpegJob.Discard(temporary);
        }
    }

    /// <summary>Peak levels (0 to 1) for <paramref name="bars"/> equal slices of the first sound track, from ffmpeg decoding it as mono at a
    /// rate that gives each bar plenty of samples without decoding long files at full rate. Empty when there is no sound.</summary>
    public async Task<float[]> ExtractWaveformAsync(string path, int bars, CancellationToken cancellationToken = default)
    {
        if (bars <= 0) return [];
        var s = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (!s.Probed.HasAudio) return [];
        var wanted = bars * 64.0 / Math.Max(s.Probed.Duration, 0.1);
        var sampleRate = (int)Math.Min(Math.Max(wanted, 100), 8000);
        var temporary = TempPath("raw");
        try
        {
            IReadOnlyList<string> arguments =
            [
                "-y", "-nostdin", "-loglevel", "error", "-i", s.Path,
                "-map", "0:a:0", "-ac", "1", "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "-c:a", "pcm_s16le", temporary,
            ];
            try
            {
                await _runner(new ToolInvocation(s.Ffmpeg, arguments) { Timeout = FFmpegJob.Timeout(s.Probed.Duration) }, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ProcessFailure or IOException) { return []; }
            if (!File.Exists(temporary)) return [];
            var data = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
            var samples = new short[data.Length / 2];
            Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 2);
            return Peaks(samples, bars);
        }
        finally
        {
            FFmpegJob.Discard(temporary);
        }
    }

    /// <summary>The loudest sample in each of <paramref name="bars"/> equal slices, 0 to 1.</summary>
    public static float[] Peaks(IReadOnlyList<short> samples, int bars)
    {
        if (samples.Count == 0 || bars <= 0) return [];
        var perBar = (double)samples.Count / bars;
        var count = perBar >= 1 ? bars : samples.Count;
        var size = Math.Max(perBar, 1);
        var peaks = new float[count];
        for (var bar = 0; bar < count; bar++)
        {
            var start = (int)(bar * size);
            var end = Math.Min(Math.Max((int)((bar + 1) * size), start + 1), samples.Count);
            var peak = 0;
            for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs((int)samples[i]));
            peaks[bar] = peak / (float)short.MaxValue;
        }
        return peaks;
    }

    private static string TempPath(string extension) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openshelf-media-{Guid.NewGuid():N}.{extension}");
}
