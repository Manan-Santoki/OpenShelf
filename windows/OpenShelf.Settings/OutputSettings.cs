using System.Text;
using System.Text.Json;

namespace OpenShelf.Settings;

// Port of Sources/OpenShelf/Convert/OutputSettings.swift. No WPF and no project
// references, so this file can be shared with the Convert engines as it is.

public enum VideoCodec { Automatic, H264, Hevc }

/// <summary>In menu order, best first.</summary>
public enum VideoQuality { Automatic, Best, Balanced, Smaller, Custom }

public enum VideoMaxSize { Original, Uhd4K, P1080, P720, P480 }

public enum GifWidth { W480, W720, W1080, Original }

/// <summary>How the Convert wheel encodes what it writes. <see cref="Automatic"/> is exactly what
/// conversions do without settings. Immutable; use <c>with</c> to change a part.</summary>
public sealed record OutputSettings
{
    public VideoOutput Video { get; init; } = new();
    public AudioOutput Audio { get; init; } = new();
    public ImageOutput Image { get; init; } = new();
    public GifOutput Gif { get; init; } = new();

    public static OutputSettings Automatic { get; } = new();

    /// <summary>Whether an MP4 or MOV is re-encoded to these settings rather than exported or copied the usual way.</summary>
    public bool ReencodesVideo => !Video.IsAutomatic || Audio.Kbps is not null;

    // ------------------------------------------------------------ storing

    /// <summary>JSON, as kept under the preferences key "output".</summary>
    public string Stored
    {
        get
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteStartObject("video");
                w.WriteString("codec", Names.Of(Video.Codec));
                w.WriteString("quality", Names.Of(Video.Quality));
                w.WriteNumber("customMbps", Video.CustomMbps);
                w.WriteString("maxSize", Names.Of(Video.MaxSize));
                w.WriteEndObject();
                w.WriteStartObject("audio");
                if (Audio.Kbps is { } kbps) w.WriteNumber("kbps", kbps);
                w.WriteEndObject();
                w.WriteStartObject("image");
                if (Image.Quality is { } q) w.WriteNumber("quality", q);
                w.WriteBoolean("keepsMetadata", Image.KeepsMetadata);
                w.WriteEndObject();
                w.WriteStartObject("gif");
                w.WriteString("width", Names.Of(Gif.Width));
                w.WriteNumber("frameRate", Gif.FrameRate);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>Nothing stored, or something unreadable, means <see cref="Automatic"/>. Settings missing from or
    /// out of range in what's stored keep their defaults, so one odd value doesn't reset the rest.</summary>
    public static OutputSettings Decode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Automatic;
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Automatic;
            return new OutputSettings
            {
                Video = DecodeVideo(Section(root, "video")),
                Audio = DecodeAudio(Section(root, "audio")),
                Image = DecodeImage(Section(root, "image")),
                Gif = DecodeGif(Section(root, "gif")),
            };
        }
        catch (JsonException) { return Automatic; }
    }

    private static JsonElement? Section(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    private static VideoOutput DecodeVideo(JsonElement? e)
    {
        var d = new VideoOutput();
        if (e is not { } o) return d;
        return d with
        {
            Codec = Names.Read(o, "codec", d.Codec, Names.Of),
            Quality = Names.Read(o, "quality", d.Quality, Names.Of),
            CustomMbps = VideoOutput.ClampedMbps(Number(o, "customMbps") ?? d.CustomMbps),
            MaxSize = Names.Read(o, "maxSize", d.MaxSize, Names.Of),
        };
    }

    private static AudioOutput DecodeAudio(JsonElement? e)
    {
        if (e is not { } o || Number(o, "kbps") is not { } n) return new AudioOutput();
        return n == Math.Floor(n) && AudioOutput.Choices.Contains((int)n) ? new AudioOutput { Kbps = (int)n } : new AudioOutput();
    }

    private static ImageOutput DecodeImage(JsonElement? e)
    {
        var d = new ImageOutput();
        if (e is not { } o) return d;
        double? quality = Number(o, "quality") is { } q
            ? Math.Round(Math.Clamp(q, ImageOutput.QualityMin, ImageOutput.QualityMax) * 10, MidpointRounding.AwayFromZero) / 10
            : null;
        var keeps = o.TryGetProperty("keepsMetadata", out var k) && k.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? k.GetBoolean() : d.KeepsMetadata;
        return new ImageOutput { Quality = quality, KeepsMetadata = keeps };
    }

    private static GifOutput DecodeGif(JsonElement? e)
    {
        var d = new GifOutput();
        if (e is not { } o) return d;
        var rate = Number(o, "frameRate") is { } n && n == Math.Floor(n) && GifOutput.FrameRates.Contains((int)n) ? (int)n : d.FrameRate;
        return d with { Width = Names.Read(o, "width", d.Width, Names.Of), FrameRate = rate };
    }

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v) ? v : null;

    // -------------------------------------------------- which settings apply

    /// <summary>These settings with the parts <paramref name="options"/> cover taken from <paramref name="other"/>:
    /// to save what the Alt window shows without touching the rest, or to reset it.</summary>
    public OutputSettings Applying(OutputOptions options, OutputSettings other)
    {
        var video = Video;
        if (options.VideoCodec) video = video with { Codec = other.Video.Codec };
        if (options.Video)
            video = video with { Quality = other.Video.Quality, CustomMbps = other.Video.CustomMbps, MaxSize = other.Video.MaxSize };
        return this with
        {
            Video = video,
            Audio = options.AudioBitRate ? other.Audio : Audio,
            Image = Image with
            {
                Quality = options.ImageQuality ? other.Image.Quality : Image.Quality,
                KeepsMetadata = options.ImageMetadata ? other.Image.KeepsMetadata : Image.KeepsMetadata,
            },
            Gif = options.Gif ? other.Gif : Gif,
        };
    }
}

/// <summary>Which settings apply to one conversion (the Alt options window): only lossy outputs have anything to choose.</summary>
public sealed record OutputOptions(bool VideoCodec = false, bool Video = false, bool AudioBitRate = false,
    bool ImageQuality = false, bool ImageMetadata = false, bool Gif = false)
{
    public bool IsEmpty => this == new OutputOptions();

    /// <summary>The rows that apply when files are converted to <paramref name="formatId"/> (lower-case id such as
    /// "mp4"; "pdf-jpeg" is JPEG from PDF pages). Only lossy outputs have anything to choose (SPEC 2.3.1).</summary>
    public static OutputOptions For(string formatId) => formatId.ToLowerInvariant() switch
    {
        "mp4" or "mov" or "mkv" => new OutputOptions(VideoCodec: true, Video: true, AudioBitRate: true),
        "webm" or "avi" or "wmv" => new OutputOptions(Video: true, AudioBitRate: true),
        "m4a" or "mp3" or "ogg" or "opus" or "wma" => new OutputOptions(AudioBitRate: true),
        "jpg" or "jpeg" => new OutputOptions(ImageQuality: true, ImageMetadata: true),
        "pdf-jpeg" => new OutputOptions(ImageQuality: true),
        "heic" or "avif" => new OutputOptions(ImageQuality: true, ImageMetadata: true),
        "webp" => new OutputOptions(ImageQuality: true),
        "gif" => new OutputOptions(Gif: true),
        _ => new OutputOptions(),
    };
}

/// <summary>MP4, MOV, MKV, WebM, AVI and WMV.</summary>
public sealed record VideoOutput
{
    public const double CustomMbpsMin = 0.1, CustomMbpsMax = 200;
    /// <summary>Compress's share of the source's own bits for Balanced and Smaller (80%).</summary>
    public const double CompressionSourceShare = 0.8;

    public VideoCodec Codec { get; init; } = VideoCodec.Automatic;
    public VideoQuality Quality { get; init; } = VideoQuality.Automatic;
    /// <summary>Megabits per second for <see cref="VideoQuality.Custom"/>.</summary>
    public double CustomMbps { get; init; } = 8;
    public VideoMaxSize MaxSize { get; init; } = VideoMaxSize.Original;

    public bool IsAutomatic => Codec == VideoCodec.Automatic && Quality == VideoQuality.Automatic && MaxSize == VideoMaxSize.Original;

    /// <summary><see cref="CustomMbps"/> in bits per second.</summary>
    public int CustomBitRate => (int)Math.Round(CustomMbps * 1_000_000, MidpointRounding.AwayFromZero);

    /// <summary>Within the allowed range; the default for something that isn't a number.</summary>
    public static double ClampedMbps(double mbps) =>
        double.IsFinite(mbps) ? Math.Min(Math.Max(mbps, CustomMbpsMin), CustomMbpsMax) : new VideoOutput().CustomMbps;

    public static string Title(VideoCodec c) => c switch { VideoCodec.H264 => "H.264", VideoCodec.Hevc => "HEVC (H.265)", _ => "Automatic" };
    public static string Title(VideoQuality q) => q switch
    {
        VideoQuality.Best => "Best", VideoQuality.Balanced => "Balanced", VideoQuality.Smaller => "Smaller",
        VideoQuality.Custom => "Custom", _ => "Automatic",
    };
    public static string Title(VideoMaxSize s) => s switch
    {
        VideoMaxSize.Uhd4K => "4K", VideoMaxSize.P1080 => "1080p", VideoMaxSize.P720 => "720p", VideoMaxSize.P480 => "480p", _ => "Original",
    };

    /// <summary>Share of Compress's High bitrate; null for a custom bitrate.</summary>
    public static double? VideoFactor(VideoQuality q) => q switch
    {
        VideoQuality.Automatic => 1.5, VideoQuality.Best => 2, VideoQuality.Balanced => 1, VideoQuality.Smaller => 0.5, _ => null,
    };

    /// <summary>The most of the source's own bits a re-encode may spend for the same picture.</summary>
    public static double SourceShare(VideoQuality q) => q is VideoQuality.Balanced or VideoQuality.Smaller ? CompressionSourceShare : 1;

    /// <summary>The frame a video is fitted inside by its long and short edges; null keeps the size.</summary>
    public static (double Long, double Short)? Bounds(VideoMaxSize s) => s switch
    {
        VideoMaxSize.Uhd4K => (3840, 2160), VideoMaxSize.P1080 => (1920, 1080), VideoMaxSize.P720 => (1280, 720),
        VideoMaxSize.P480 => (854, 480), _ => null,
    };
}

/// <summary>The sound in lossy outputs: MP3, M4A, OGG, Opus, WMA and video.</summary>
public sealed record AudioOutput
{
    /// <summary>The fixed rates offered, in kb/s.</summary>
    public static readonly IReadOnlyList<int> Choices = [96, 128, 160, 192, 256, 320];

    /// <summary>kb/s, or null to follow the source.</summary>
    public int? Kbps { get; init; }
}

/// <summary>JPEG, HEIC, AVIF and WebP quality, and metadata in every image format.</summary>
public sealed record ImageOutput
{
    public const double QualityMin = 0.4, QualityMax = 1;
    /// <summary>The qualities offered, highest first.</summary>
    public static readonly IReadOnlyList<double> QualityChoices = [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4];

    /// <summary>0.4 to 1, or null for each format's usual (JPEG, HEIC and AVIF 0.9, WebP 88, PDF pages 0.92).</summary>
    public double? Quality { get; init; }
    public bool KeepsMetadata { get; init; } = true;

    /// <summary>The quality to write with, given the format's usual one.</summary>
    public double QualityOr(double usual) => Quality ?? usual;

    /// <summary>The 0 to 100 value cwebp and ffmpeg take for WebP.</summary>
    public string WebPQuality => Quality is { } q ? ((int)Math.Round(q * 100, MidpointRounding.AwayFromZero)).ToString() : "88";
}

/// <summary>Animated GIFs made from video.</summary>
public sealed record GifOutput
{
    public static readonly IReadOnlyList<int> FrameRates = [10, 12, 15, 24];

    public GifWidth Width { get; init; } = GifWidth.W720;
    public int FrameRate { get; init; } = 12;

    public static string Title(GifWidth w) => w switch
    {
        GifWidth.W480 => "480 px", GifWidth.W720 => "720 px", GifWidth.W1080 => "1080 px", _ => "Original (up to 1920 px)",
    };

    /// <summary>The widest a GIF gets; narrower videos keep their width.</summary>
    public static int MaxWidth(GifWidth w) => w switch { GifWidth.W480 => 480, GifWidth.W1080 => 1080, GifWidth.Original => 1920, _ => 720 };
}

/// <summary>Stored names, matching the Mac app's raw values.</summary>
internal static class Names
{
    public static string Of(VideoCodec v) => v switch { VideoCodec.H264 => "h264", VideoCodec.Hevc => "hevc", _ => "automatic" };
    public static string Of(VideoQuality v) => v.ToString().ToLowerInvariant();
    public static string Of(VideoMaxSize v) => v switch
    {
        VideoMaxSize.Uhd4K => "4k", VideoMaxSize.P1080 => "p1080", VideoMaxSize.P720 => "p720", VideoMaxSize.P480 => "p480", _ => "original",
    };
    public static string Of(GifWidth v) => v switch { GifWidth.W480 => "480", GifWidth.W720 => "720", GifWidth.W1080 => "1080", _ => "original" };

    public static T Read<T>(JsonElement o, string name, T fallback, Func<T, string> nameOf) where T : struct, Enum
    {
        if (!o.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return fallback;
        var text = e.GetString();
        foreach (var value in Enum.GetValues<T>())
            if (nameOf(value) == text) return value;
        return fallback;
    }
}
