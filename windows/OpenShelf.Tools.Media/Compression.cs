using OpenShelf.Convert;

namespace OpenShelf.Tools.Media;

/// <summary>The size Compress fits a video inside. Called VideoResolution on the Mac.</summary>
public enum CompressResolution { Original, P1080, P720, P480 }

/// <summary>Compress's quality. Called VideoQuality on the Mac (renamed here: <see cref="OpenShelf.Convert.VideoQuality"/> is the Convert wheel's).</summary>
public enum CompressQuality { High, Medium, Low }

public static class CompressChoices
{
    public static string Title(this CompressResolution resolution) => resolution switch
    {
        CompressResolution.Original => "Original",
        CompressResolution.P1080 => "1080p",
        CompressResolution.P720 => "720p",
        _ => "480p",
    };

    public static string Title(this CompressQuality quality) => quality switch
    {
        CompressQuality.High => "High",
        CompressQuality.Medium => "Medium",
        _ => "Low",
    };

    /// <summary>The frame a video is fitted inside, by its long and short edges so portrait videos get the same treatment; null keeps the size.</summary>
    public static (double Long, double Short)? Bounds(this CompressResolution resolution) => resolution switch
    {
        CompressResolution.P1080 => (1920, 1080),
        CompressResolution.P720 => (1280, 720),
        CompressResolution.P480 => (854, 480),
        _ => null,
    };

    /// <summary>Share of the bitrate High gets.</summary>
    public static double VideoFactor(this CompressQuality quality) => quality switch
    {
        CompressQuality.High => 1,
        CompressQuality.Medium => 0.6,
        _ => 0.35,
    };

    /// <summary>Stereo AAC ceiling in bits per second; High uses the codec's own (null).</summary>
    public static int? AudioLimit(this CompressQuality quality) => quality switch
    {
        CompressQuality.High => null,
        CompressQuality.Medium => 128_000,
        _ => 96_000,
    };
}

/// <summary>One sound track of a video as Compress sees it.</summary>
public sealed record SourceAudioTrack(SourceAudio Audio, double SampleRate);

/// <summary>What a video's tracks look like, read once so the estimate can follow the settings instantly. Port of VideoSourceInfo.</summary>
/// <param name="Width">Frame size as stored, before the rotation.</param>
/// <param name="Height">Frame height as stored.</param>
/// <param name="Rotation">Degrees clockwise players turn the picture.</param>
public sealed record VideoSourceInfo(int Width, int Height, int Rotation, double Duration, long FileSize)
{
    public double FrameRate { get; init; } = 30;
    /// <summary>Bits per second of the picture; 0 when unknown.</summary>
    public int BitRate { get; init; }
    public bool IsHevc { get; init; }
    public bool IsHdr { get; init; }
    public IReadOnlyList<SourceAudioTrack> AudioTracks { get; init; } = [];

    /// <summary>The size players show, after rotation.</summary>
    public (int Width, int Height) DisplaySize => Rotation % 180 == 0 ? (Width, Height) : (Height, Width);

    /// <summary>Null when the file has no picture.</summary>
    public static VideoSourceInfo? From(ProbedMedia probed, long fileSize)
    {
        if (probed.Video is not { } video) return null;
        var audioTracks = probed.Audio.Select(a => new SourceAudioTrack(a.Source, a.SampleRate ?? 44_100)).ToList();
        var bitRate = video.BitRate ?? 0;
        if (video.BitRate is null && probed.Duration > 0)
        {
            // MPEG-TS does not say what the picture alone takes; the whole file's rate less the sound is close.
            var total = fileSize * 8 / probed.Duration;
            var sound = audioTracks.Sum(t => t.Audio.BitRate ?? 0);
            bitRate = (int)Math.Max(total - sound, 0);
        }
        return new VideoSourceInfo(video.Width, video.Height, video.Rotation, probed.Duration, fileSize)
        {
            FrameRate = video.FramesPerSecond ?? 30,
            BitRate = bitRate,
            IsHevc = video.Codec == "hevc",
            IsHdr = video.IsHdr,
            AudioTracks = audioTracks,
        };
    }
}

public enum PlanCodec { H264, Hevc }

/// <summary>What happens to one sound track.</summary>
/// <param name="Passthrough">The AAC track is copied as it is.</param>
public sealed record PlanAudio(bool Passthrough, int BitRate, int Channels, double SampleRate);

/// <summary>How a video is re-encoded for a resolution and quality. Plain numbers, so the editor's estimate is exactly what the
/// encoder is asked for. Port of VideoCompressionPlan.</summary>
public sealed class VideoCompressionPlan
{
    /// <summary>H.264 at 1080p and 30 fps on High; other sizes scale from this.</summary>
    public const double ReferenceBitRate = 6_000_000;
    public const double ReferencePixels = 1920.0 * 1080;
    /// <summary>A re-encode never gets more than this share of the source's bits for the same picture.</summary>
    public const double SourceShare = 0.8;
    /// <summary>MP4 boxes and indexes on top of the media data.</summary>
    public const double ContainerOverhead = 1.01;

    public PlanCodec Codec { get; }
    /// <summary>Encoded frame size, in the stored (unrotated) orientation.</summary>
    public (int Width, int Height) EncodedSize { get; }
    /// <summary>The size players show, after rotation.</summary>
    public (int Width, int Height) DisplaySize { get; }
    public int VideoBitRate { get; }
    public IReadOnlyList<PlanAudio> Audio { get; }
    public long EstimatedBytes { get; }

    /// <summary>Compress's plan: HEVC for HDR (when <paramref name="hevcAvailable"/>), else H.264, at a resolution and quality.</summary>
    public VideoCompressionPlan(VideoSourceInfo source, CompressResolution resolution, CompressQuality quality, bool hevcAvailable = true)
    {
        Codec = source.IsHdr && hevcAvailable ? PlanCodec.Hevc : PlanCodec.H264;
        EncodedSize = FitSize(source.Width, source.Height, resolution.Bounds());
        DisplaySize = source.Rotation % 180 == 0 ? EncodedSize : (EncodedSize.Height, EncodedSize.Width);
        VideoBitRate = ComputeVideoBitRate(EncodedSize, quality.VideoFactor(), SourceShare, Codec, source);
        Audio = source.AudioTracks.Select(t => ForAudio(t, quality.AudioLimit())).ToList();
        var bitsPerSecond = VideoBitRate + Audio.Sum(a => a.BitRate);
        EstimatedBytes = (long)(bitsPerSecond / 8.0 * Math.Max(source.Duration, 0) * ContainerOverhead);
    }

    /// <summary>The estimated size of the result for <paramref name="source"/>, in bytes.</summary>
    public static long Estimate(VideoSourceInfo source, CompressResolution resolution, CompressQuality quality, bool hevcAvailable = true) =>
        new VideoCompressionPlan(source, resolution, quality, hevcAvailable).EstimatedBytes;

    /// <summary>Fits the stored size inside <paramref name="bounds"/> (long and short edges, so either orientation fits), keeping its shape,
    /// never enlarging, with even sides; null bounds only even out the sides.</summary>
    public static (int Width, int Height) FitSize(int width, int height, (double Long, double Short)? bounds)
    {
        double w = Math.Abs(width), h = Math.Abs(height), scale = 1;
        if (bounds is { } b && w > 0 && h > 0)
            scale = Math.Min(1, Math.Min(b.Long / Math.Max(w, h), b.Short / Math.Min(w, h)));
        return (EvenSide(w * scale, w), EvenSide(h * scale, h));
    }

    private static int EvenSide(double value, double limit)
    {
        var nearest = Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
        var largest = Math.Floor(limit / 2) * 2;
        return (int)Math.Max(2, Math.Min(nearest, largest));
    }

    /// <summary>Bits per second for the picture. Larger frames need fewer bits per pixel, so the rate grows with pixels^0.75; frame
    /// rates above 30 cost half as much again per frame. Capped by what the source itself spent.</summary>
    public static int ComputeVideoBitRate((int Width, int Height) size, double factor, double share, PlanCodec codec, VideoSourceInfo source)
    {
        double pixels = (double)size.Width * size.Height;
        var sizeScale = Math.Pow(pixels / ReferencePixels, 0.75);
        var fps = source.FrameRate > 0 ? source.FrameRate : 30;
        var motion = (fps <= 30 ? fps : 30 + (fps - 30) / 2) / 30;
        var rate = ReferenceBitRate * sizeScale * motion * factor * Efficiency(codec);
        if (source.BitRate > 0)
        {
            var sourcePixels = Math.Max((double)source.Width * source.Height, 1);
            var asH264 = source.BitRate / Efficiency(source.IsHevc ? PlanCodec.Hevc : PlanCodec.H264);
            var cap = asH264 * Efficiency(codec) * Math.Pow(pixels / sourcePixels, 0.75) * share;
            rate = Math.Min(rate, cap);
        }
        return Math.Max((int)rate, 100_000);
    }

    /// <summary>Bits needed relative to H.264 at the same quality.</summary>
    public static double Efficiency(PlanCodec codec) => codec == PlanCodec.Hevc ? 0.65 : 1;

    /// <summary>AAC tracks already at or below the target are copied untouched; anything else becomes AAC at the shared audio policy's rate.</summary>
    public static PlanAudio ForAudio(SourceAudioTrack track, int? limit)
    {
        var target = AudioBitRate.Target(AudioCodec.Aac, track.Audio, limit);
        if (track.Audio.Codec == AudioCodec.Aac)
        {
            var rate = track.Audio.BitRate ?? target;
            if (rate <= target * 1.1) return new PlanAudio(true, rate, track.Audio.Channels, track.SampleRate);
        }
        // AAC tops out at 48 kHz; lower rates are lifted to 44.1 kHz.
        var sampleRate = track.SampleRate >= 44_100 ? Math.Min(track.SampleRate, 48_000) : 44_100;
        return new PlanAudio(false, target, Math.Min(track.Audio.Channels, 2), sampleRate);
    }
}
