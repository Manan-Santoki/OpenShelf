using System.Globalization;
using OpenShelf.Convert;

namespace OpenShelf.Tools.Media;

/// <summary>ffmpeg arguments for the media tools, apart from running them so they can be checked without encoding anything.
/// Port of FFmpegEdits in MediaToolsFFmpeg.swift.</summary>
public static class FFmpegEdits
{
    /// <summary>How far before a cut ffmpeg jumps to directly; from there it decodes up to the cut. Jumping straight to the cut
    /// is not safe: in MPEG-TS it can land past the last keyframe and lose the picture until the next.</summary>
    public const double SeekMargin = 30;

    /// <summary>Input arguments for <paramref name="length"/> seconds of the file from <paramref name="start"/> (to the end when null),
    /// exact to the frame (it decodes up to the cut).</summary>
    public static IReadOnlyList<string> Input(string path, double start, double? length)
    {
        var coarse = Math.Max(start - SeekMargin, 0);
        var arguments = new List<string>();
        if (coarse > 0) arguments.AddRange(["-ss", Seconds(coarse)]);
        arguments.AddRange(["-i", path]);
        var fine = start - coarse;
        if (fine > 0) arguments.AddRange(["-ss", Seconds(fine)]);
        if (length is { } l) arguments.AddRange(["-t", Seconds(l)]);
        return arguments;
    }

    /// <summary>Input arguments for a stream copy of start-end: it begins at the keyframe at or before <paramref name="start"/> and
    /// stops at the absolute time <paramref name="end"/> (null: the end of the file).</summary>
    public static IReadOnlyList<string> CopyInput(string path, double start, double? end)
    {
        var arguments = new List<string>();
        if (start > 0) arguments.AddRange(["-ss", Seconds(start)]);
        if (end is { } e) arguments.AddRange(["-to", Seconds(e)]);
        arguments.AddRange(["-i", path]);
        return arguments;
    }

    // Codecs

    /// <summary>The picture re-encoded about as well as the Mac's highest-quality preset: H.264, or 10-bit HEVC for HDR (when
    /// <paramref name="hevc"/>) so it keeps its colours.</summary>
    public static IReadOnlyList<string> VideoEncoding(ProbedVideo? video, bool hevc = true) =>
        video?.IsHdr == true && hevc
            ? ["-c:v", "libx265", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1", "-x265-params", "log-level=error"]
            : ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"];

    /// <summary>The picture copied as it is.</summary>
    public static IReadOnlyList<string> VideoCopy(ProbedVideo video) =>
        // QuickTime only plays HEVC tagged "hvc1"; ffmpeg's default is "hev1".
        video.Codec == "hevc" ? ["-c:v", "copy", "-tag:v", "hvc1"] : ["-c:v", "copy"];

    /// <summary>For sound tracks mapped in order: AAC is copied, anything else becomes AAC at the rate <see cref="AudioBitRate"/> picks.</summary>
    public static IReadOnlyList<string> AudioCopyOrAac(IReadOnlyList<ProbedAudio> tracks)
    {
        var arguments = new List<string>();
        for (var index = 0; index < tracks.Count; index++)
        {
            if (tracks[index].Codec == "aac") arguments.AddRange([$"-c:a:{index}", "copy"]);
            else arguments.AddRange(Aac(tracks[index], index));
        }
        return arguments;
    }

    /// <summary>The <paramref name="index"/>th output sound track as AAC at <see cref="AudioBitRate"/>'s rate.</summary>
    public static IReadOnlyList<string> Aac(ProbedAudio track, int index) =>
        [$"-c:a:{index}", "aac", $"-b:a:{index}", AudioBitRate.Argument(AudioCodec.Aac, track.Source)];

    // Speed

    /// <summary>Plays the media <paramref name="rate"/> times as fast, whether it has a picture, sound or both. Every frame is kept,
    /// shown for less (or more) time; every sound track keeps its pitch or follows the speed, like a record.</summary>
    public static IReadOnlyList<string> Speed(ProbedMedia probed, double rate, bool keepsPitch, bool hevc = true)
    {
        var graph = new List<string>();
        var arguments = new List<string>();
        if (probed.Video is { } video)
        {
            graph.Add($"[0:V:0]setpts=PTS/{Factor(rate)}[v]");
            arguments.AddRange(["-map", "[v]"]);
            arguments.AddRange(VideoEncoding(video, hevc));
            if (video.FramesPerSecond is { } fps) arguments.AddRange(["-r", Factor(fps * rate)]);
        }
        for (var index = 0; index < probed.Audio.Count; index++)
        {
            var track = probed.Audio[index];
            var filter = keepsPitch ? AtempoChain(rate) : Varispeed(rate, track.SampleRate ?? 48_000);
            graph.Add($"[0:a:{index}]{filter}[a{index}]");
            arguments.AddRange(["-map", $"[a{index}]"]);
            arguments.AddRange(Aac(track, index));
        }
        return ["-filter_complex", string.Join(';', graph), .. arguments];
    }

    /// <summary><c>atempo</c> changes tempo and keeps pitch. One filter takes 0.5-2 only in older ffmpeg, so larger changes are a chain.</summary>
    public static string AtempoChain(double rate)
    {
        if (!(rate > 0)) return "anull";
        var remaining = rate;
        var factors = new List<double>();
        while (remaining > 2) { factors.Add(2); remaining /= 2; }
        while (remaining < 0.5) { factors.Add(0.5); remaining /= 0.5; }
        factors.Add(remaining);
        return string.Join(',', factors.Select(f => $"atempo={Factor(f)}"));
    }

    /// <summary>Pitch follows speed: the same samples played at a scaled rate, then resampled back to <paramref name="sampleRate"/>.</summary>
    public static string Varispeed(double rate, double sampleRate)
    {
        var scaled = (long)Math.Round(sampleRate * rate, MidpointRounding.AwayFromZero);
        return $"asetrate={scaled},aresample={(long)Math.Round(sampleRate, MidpointRounding.AwayFromZero)}";
    }

    // Rotate and crop

    /// <summary>Re-encoding filters for each change, applied to the upright picture.</summary>
    public static string RotationFilter(VideoRotation rotation) => rotation switch
    {
        VideoRotation.Right => "transpose=clock",
        VideoRotation.Left => "transpose=cclock",
        VideoRotation.UpsideDown => "hflip,vflip",
        _ => "hflip",
    };

    /// <summary>The <c>-display_rotation</c> for a video turned by <paramref name="rotation"/> on top of its own rotation: degrees
    /// anticlockwise, 0 to 270.</summary>
    public static int DisplayRotation(ProbedVideo video, VideoRotation rotation)
    {
        var clockwise = video.Rotation + rotation.Degrees();
        var normalized = (clockwise % 360 + 360) % 360;
        return (360 - normalized) % 360;
    }

    public static string CropFilter(PixelRect rect) => $"crop={rect.Width}:{rect.Height}:{rect.X}:{rect.Y}";

    // Compress

    /// <summary>Encodes as <paramref name="plan"/> lays out: the same size, bitrates and sound, a keyframe every two seconds, and an MP4
    /// ready for streaming. ffmpeg turns frames upright first, so they are scaled straight to the size players show.</summary>
    public static IReadOnlyList<string> Compress(string path, VideoCompressionPlan plan, VideoSourceInfo source)
    {
        var fps = source.FrameRate > 0 ? source.FrameRate : 30;
        var keyframeInterval = Math.Max((int)Math.Round(fps * 2, MidpointRounding.AwayFromZero), 1);
        var arguments = new List<string>
        {
            "-i", path, "-map", "0:V:0", "-map", "0:a?",
            "-vf", $"scale={plan.DisplaySize.Width}:{plan.DisplaySize.Height},setsar=1",
        };
        if (plan.Codec == PlanCodec.H264)
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-pix_fmt", "yuv420p"]);
        else
            arguments.AddRange(["-c:v", "libx265", "-preset", "medium", "-pix_fmt", "yuv420p10le", "-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        arguments.AddRange(["-b:v", plan.VideoBitRate.ToString(CultureInfo.InvariantCulture), "-g", keyframeInterval.ToString(CultureInfo.InvariantCulture)]);
        for (var index = 0; index < plan.Audio.Count; index++)
        {
            var audio = plan.Audio[index];
            if (audio.Passthrough) arguments.AddRange([$"-c:a:{index}", "copy"]);
            else
                arguments.AddRange([
                    $"-c:a:{index}", "aac", $"-b:a:{index}", audio.BitRate.ToString(CultureInfo.InvariantCulture),
                    $"-ac:a:{index}", audio.Channels.ToString(CultureInfo.InvariantCulture), $"-ar:a:{index}", ((int)audio.SampleRate).ToString(CultureInfo.InvariantCulture),
                ]);
        }
        arguments.AddRange(["-movflags", "+faststart"]);
        return arguments;
    }

    // Merge

    /// <summary>Joins clips (each given with <c>-i</c>, in order) into one H.264 video. The first clip sets the size and frame rate and the
    /// others are fitted inside it, with black bars where the shapes differ (ffmpeg turns each upright first). Clips without sound get silence.</summary>
    public static IReadOnlyList<string> MergeVideos(IReadOnlyList<ProbedMedia> clips)
    {
        if (clips.Count == 0 || clips[0].Video is not { } first) return [];
        var (width, height) = CropMath.EvenSize(first.DisplaySize.Width, first.DisplaySize.Height);
        var fps = first.FrameRate ?? "30";
        var hasSound = clips.Any(c => c.HasAudio);
        var layout = ChannelLayout(clips);

        var graph = new List<string>();
        var segments = new System.Text.StringBuilder();
        for (var index = 0; index < clips.Count; index++)
        {
            var fit = $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
            var pad = $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";
            graph.Add($"[{index}:V:0]{fit},{pad},setsar=1,fps={fps},format=yuv420p[v{index}]");
            segments.Append($"[v{index}]");
            if (hasSound)
            {
                graph.Add(clips[index].HasAudio
                    ? $"[{index}:a:0]{SoundFormat(layout)}[a{index}]"
                    : $"anullsrc=r=48000:cl={layout},atrim=duration={Seconds(clips[index].Duration)}[a{index}]");
                segments.Append($"[a{index}]");
            }
        }
        graph.Add($"{segments}concat=n={clips.Count}:v=1:a={(hasSound ? 1 : 0)}{(hasSound ? "[v][a]" : "[v]")}");

        var arguments = new List<string> { "-filter_complex", string.Join(';', graph), "-map", "[v]" };
        arguments.AddRange(VideoEncoding(null));
        if (hasSound) arguments.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", MergedBitRate(clips)]);
        arguments.AddRange(["-movflags", "+faststart"]);
        return arguments;
    }

    /// <summary>Joins the first sound track of each clip into one AAC track.</summary>
    public static IReadOnlyList<string> MergeAudio(IReadOnlyList<ProbedMedia> clips)
    {
        var layout = ChannelLayout(clips);
        var graph = new List<string>();
        var segments = new System.Text.StringBuilder();
        for (var index = 0; index < clips.Count; index++)
        {
            graph.Add($"[{index}:a:0]{SoundFormat(layout)}[a{index}]");
            segments.Append($"[a{index}]");
        }
        graph.Add($"{segments}concat=n={clips.Count}:v=0:a=1[a]");
        return ["-filter_complex", string.Join(';', graph), "-map", "[a]", "-c:a", "aac", "-b:a", MergedBitRate(clips)];
    }

    /// <summary>Joined sound needs one format: 48 kHz, in stereo unless every clip is mono.</summary>
    private static string SoundFormat(string layout) => $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts={layout}";

    private static string ChannelLayout(IReadOnlyList<ProbedMedia> clips) =>
        clips.Any(c => (c.Audio.FirstOrDefault()?.Channels ?? 1) > 1) ? "stereo" : "mono";

    /// <summary>The richest clip's rate, so none sounds worse than it did.</summary>
    private static string MergedBitRate(IReadOnlyList<ProbedMedia> clips)
    {
        var rates = clips.Where(c => c.HasAudio).Select(c => AudioBitRate.Target(AudioCodec.Aac, c.Audio[0].Source)).ToList();
        var best = rates.Count > 0 ? rates.Max() : AudioBitRate.Target(AudioCodec.Aac, null);
        return $"{best / 1000}k";
    }

    // Numbers

    /// <summary>Seconds to the millisecond, as ffmpeg reads them.</summary>
    public static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>A factor or rate without needless digits: "2", "0.5", "1.25".</summary>
    public static string Factor(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
