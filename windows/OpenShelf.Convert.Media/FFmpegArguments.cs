using System.Globalization;

namespace OpenShelf.Convert;

/// <summary>ffmpeg codec settings for each output format (SPEC 2.3.1, the ffmpeg column). Port of FFmpegArguments in
/// MediaConversion.swift. Everything here is pure: encoders, source facts and settings in, argument lists out.</summary>
public static class FFmpegArguments
{
    /// <summary>HEVC encoders in the order they are tried: software x265 first, then the hardware encoders that take the
    /// place of VideoToolbox (NVIDIA, Intel, AMD).</summary>
    public static readonly IReadOnlyList<string> HevcEncoders = ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"];

    public const string X264 = "libx264";

    /// <summary>The name a scale filter for <see cref="VideoSize"/> fits inside: long and short edges, null to keep the size.</summary>
    public static (int Long, int Short)? Bounds(VideoSize size) => size switch
    {
        VideoSize.UltraHD4K => (3840, 2160),
        VideoSize.FullHD1080 => (1920, 1080),
        VideoSize.HD720 => (1280, 720),
        VideoSize.SD480 => (854, 480),
        _ => null,
    };

    /// <summary>Whether the settings ask for a re-encode (so stream copies are skipped): any video setting, or a fixed audio rate.</summary>
    public static bool Reencodes(ConversionOptions options) => !options.Video.IsAutomatic || options.Audio.BitrateKbps is not null;

    /// <summary>Whether HEVC is wanted: chosen, or kept for an HDR source once any video setting is chosen (H.264 cannot hold HDR's colours).</summary>
    public static bool WantsHevc(VideoOptions video, bool isHdr) => video.Codec == VideoCodec.Hevc || (isHdr && !video.IsAutomatic);

    /// <summary>Whether the encoders can make <paramref name="format"/> with the HEVC chain in front.</summary>
    public static bool TakesHevc(OutputFormat format) => format is OutputFormat.Mp4 or OutputFormat.Mov or OutputFormat.Mkv;

    /// <summary>The HEVC encoders <paramref name="encoders"/> has, in the order to try.</summary>
    public static IReadOnlyList<string> AvailableHevc(IReadOnlySet<string> encoders) => HevcEncoders.Where(encoders.Contains).ToList();

    /// <summary>Arguments between the input and the output path. <paramref name="encoders"/> is what the installed ffmpeg offers,
    /// so OGG can fall back when libvorbis is missing. <paramref name="videoEncoder"/> forces the picture encoder
    /// (an entry of <see cref="HevcEncoders"/> or <see cref="X264"/>); null takes the best one. <paramref name="display"/> is the
    /// size players show, used only to even out odd sides, which x264 and x265 refuse.</summary>
    public static IReadOnlyList<string> Arguments(
        OutputFormat format,
        IReadOnlySet<string> encoders,
        SourceAudio? audio = null,
        ConversionOptions? options = null,
        bool isHdr = false,
        string? videoEncoder = null,
        (int Width, int Height)? display = null)
    {
        options ??= ConversionOptions.Automatic;
        var video = options.Video;
        List<string> BitRate(AudioCodec codec) => ["-b:a", AudioBitRate.Argument(options.Audio, codec, audio)];
        List<string> Aac() => ["-c:a", "aac", .. BitRate(AudioCodec.Aac)];
        var scale = new List<string>();
        if (Bounds(video.LargestSize) is { } bounds) scale.AddRange(["-vf", ScaleFilter(bounds)]);

        switch (format)
        {
            case OutputFormat.Mp4:
                return [.. EvenOrScale(scale, display, video), .. VideoEncoding(format, video, encoders, isHdr, videoEncoder), .. Aac(), "-movflags", "+faststart"];
            case OutputFormat.Mov:
            case OutputFormat.Mkv:
                return [.. EvenOrScale(scale, display, video), .. VideoEncoding(format, video, encoders, isHdr, videoEncoder), .. Aac()];
            case OutputFormat.M4a: return ["-vn", .. Aac()];
            case OutputFormat.Mp3: return ["-vn", "-c:a", "libmp3lame", .. BitRate(AudioCodec.Mp3)];
            case OutputFormat.Wav: return ["-vn", "-c:a", "pcm_s16le"];
            case OutputFormat.Aiff: return ["-vn", "-c:a", "pcm_s16be"];
            case OutputFormat.Flac: return ["-vn", "-c:a", "flac"];
            case OutputFormat.Opus: return ["-vn", "-c:a", "libopus", .. BitRate(AudioCodec.Opus)];
            case OutputFormat.Wma: return ["-vn", "-c:a", "wmav2", .. BitRate(AudioCodec.Wma)];
            case OutputFormat.Ogg:
                if (encoders.Contains("libvorbis")) return ["-vn", "-c:a", "libvorbis", .. BitRate(AudioCodec.Vorbis)];
                // Opus in an Ogg container is a valid .ogg file.
                if (encoders.Contains("libopus")) return ["-vn", "-c:a", "libopus", .. BitRate(AudioCodec.Opus)];
                // ffmpeg's own Vorbis encoder is experimental, stereo-only and ignores bitrates.
                return ["-vn", "-c:a", "vorbis", "-strict", "experimental", "-ac", "2"];
            case OutputFormat.Webm:
                return [.. scale, "-c:v", "libvpx-vp9", .. Vp9Rate(video), "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", .. BitRate(AudioCodec.Opus)];
            case OutputFormat.Avi:
                return [.. scale, "-c:v", "mpeg4", "-vtag", "xvid", .. Quantizer(video), "-c:a", "libmp3lame", .. BitRate(AudioCodec.Mp3)];
            case OutputFormat.Wmv:
                return [.. scale, "-c:v", "wmv2", .. Quantizer(video), "-c:a", "wmav2", .. BitRate(AudioCodec.Wma)];
            case OutputFormat.Gif:
                // Two-pass palette for clean colours; 720 px wide at 12 fps unless the settings say otherwise.
                return ["-vf", GifFilter(options.Gif), "-loop", "0"];
            default:
                return [];
        }
    }

    /// <summary>The size limit if there is one, else (for H.264/HEVC outputs) a filter that evens out odd sides when the source has them.</summary>
    private static List<string> EvenOrScale(List<string> scale, (int Width, int Height)? display, VideoOptions video)
    {
        if (scale.Count > 0 || display is not { } size || (size.Width % 2 == 0 && size.Height % 2 == 0)) return scale;
        return ["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"];
    }

    /// <summary>The GIF filter chain: <c>fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse</c>.</summary>
    public static string GifFilter(GifOptions gif) =>
        string.Create(CultureInfo.InvariantCulture,
            $"fps={gif.FramesPerSecond},scale='min({GifMaxWidth(gif.Width)},iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse");

    /// <summary>The widest a GIF gets; narrower videos keep their width. "Original" is the video's own width up to 1920 px.</summary>
    public static int GifMaxWidth(GifWidth width) => width switch
    {
        GifWidth.W480 => 480,
        GifWidth.W720 => 720,
        GifWidth.W1080 => 1080,
        _ => 1920,
    };

    /// <summary>The picture in MP4, MOV and MKV: H.264 with x264 unless HEVC is chosen (or the source is HDR and any video
    /// setting is) and an HEVC encoder exists. Automatic settings give x264 at CRF 20.</summary>
    public static IReadOnlyList<string> VideoEncoding(OutputFormat format, VideoOptions video, IReadOnlySet<string> encoders, bool isHdr, string? forced = null)
    {
        var wantsHevc = forced is null ? WantsHevc(video, isHdr) : forced != X264;
        if (wantsHevc)
        {
            var encoder = forced ?? AvailableHevc(encoders).FirstOrDefault();
            if (encoder is not null) return Hevc(encoder, format, video, isHdr);
        }
        return ["-c:v", X264, "-preset", "medium", .. CustomRate(video) ?? ["-crf", X264Crf(video.Quality).ToString(CultureInfo.InvariantCulture)], "-pix_fmt", "yuv420p"];
    }

    /// <summary>HEVC with the given encoder. HDR is kept 10-bit. QuickTime only plays HEVC tagged "hvc1".</summary>
    public static IReadOnlyList<string> Hevc(string encoder, OutputFormat format, VideoOptions video, bool isHdr)
    {
        List<string> tag = format is OutputFormat.Mp4 or OutputFormat.Mov ? ["-tag:v", "hvc1"] : [];
        var crf = X264Crf(video.Quality);
        string I(int value) => value.ToString(CultureInfo.InvariantCulture);
        string[] depth(string sdr) => isHdr ? ["-pix_fmt", "p010le", "-profile:v", "main10"] : ["-pix_fmt", sdr];
        switch (encoder)
        {
            case "libx265":
                // x265 needs about five more CRF for the same size as x264.
                return ["-c:v", "libx265", "-preset", "medium", .. CustomRate(video) ?? ["-crf", I(crf + 5)],
                    "-pix_fmt", isHdr ? "yuv420p10le" : "yuv420p", .. tag, "-x265-params", "log-level=error"];
            case "hevc_nvenc":
            {
                string[] rate = CustomRate(video) is { } nv ? ["-rc", "vbr", .. nv] : ["-rc", "vbr", "-cq", I(crf + 4), "-b:v", "0"];
                return ["-c:v", "hevc_nvenc", "-preset", "p5", .. rate, .. depth("yuv420p"), .. tag];
            }
            case "hevc_qsv":
            {
                string[] rate = CustomRate(video) ?? ["-global_quality", I(crf + 4)];
                return ["-c:v", "hevc_qsv", "-preset", "medium", .. rate, .. depth("nv12"), .. tag];
            }
            case "hevc_amf":
            {
                string[] rate = CustomRate(video) is { } amf ? ["-rc", "vbr_peak", .. amf] : ["-rc", "cqp", "-qp_i", I(crf + 4), "-qp_p", I(crf + 4)];
                return ["-c:v", "hevc_amf", "-quality", "quality", .. rate, .. depth("nv12"), .. tag];
            }
            default:
                throw new ArgumentException($"Unknown HEVC encoder {encoder}", nameof(encoder));
        }
    }

    /// <summary>x264's constant rate factor; lower is better and larger.</summary>
    public static int X264Crf(VideoQuality quality) => quality switch
    {
        VideoQuality.Best => 18,
        VideoQuality.Balanced => 23,
        VideoQuality.Smaller => 28,
        _ => 20,
    };

    /// <summary>VP9 in constant quality mode (CRF with no bitrate), or a target bitrate.</summary>
    public static IReadOnlyList<string> Vp9Rate(VideoOptions video)
    {
        if (video.Quality == VideoQuality.Custom) return ["-b:v", Bps(video).ToString(CultureInfo.InvariantCulture)];
        var crf = video.Quality switch { VideoQuality.Best => 28, VideoQuality.Balanced => 33, VideoQuality.Smaller => 38, _ => 32 };
        return ["-crf", crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0"];
    }

    /// <summary>MPEG-4 (Xvid) and WMV's fixed quantizer, 2-31; lower is better and larger. Or a target bitrate.</summary>
    public static IReadOnlyList<string> Quantizer(VideoOptions video)
    {
        if (video.Quality == VideoQuality.Custom) return ["-b:v", Bps(video).ToString(CultureInfo.InvariantCulture)];
        var q = video.Quality switch { VideoQuality.Best => 2, VideoQuality.Balanced => 5, VideoQuality.Smaller => 8, _ => 4 };
        return ["-q:v", q.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>A custom bitrate as x264 and x265 take it, with room for busy scenes (1.5x max, 2x buffer); null for the other qualities.</summary>
    public static string[]? CustomRate(VideoOptions video)
    {
        if (video.Quality != VideoQuality.Custom) return null;
        var rate = Bps(video);
        string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        return ["-b:v", I(rate), "-maxrate", I(rate * 3 / 2), "-bufsize", I(rate * 2)];
    }

    /// <summary>The custom Mb/s in bits per second (8 Mb/s when unset).</summary>
    public static long Bps(VideoOptions video) => (long)Math.Round(Math.Clamp(video.CustomMbps ?? 8, 0.1, 200) * 1_000_000);

    /// <summary>Scales a video to fit inside <paramref name="bounds"/> in either orientation, keeping its shape, never enlarging,
    /// with even sides. Frames are already upright here (ffmpeg autorotates), so portrait fits turned.</summary>
    public static string ScaleFilter((int Long, int Short) bounds) =>
        $"scale=w='if(gte(iw,ih),min({bounds.Long},iw),min({bounds.Short},iw))':h='if(gte(iw,ih),min({bounds.Short},ih),min({bounds.Long},ih))'"
        + ":force_original_aspect_ratio=decrease:force_divisible_by=2";

    /// <summary>Arguments that copy <paramref name="probed"/>'s picture and sound into an MP4 or MOV untouched, when they are in
    /// codecs QuickTime-style players handle: 8-bit H.264, HEVC and AAC. Null when they need re-encoding.</summary>
    public static IReadOnlyList<string>? StreamCopy(OutputFormat format, ProbedMedia probed)
    {
        if (format is not (OutputFormat.Mp4 or OutputFormat.Mov) || probed.Video is not { } video) return null;
        var pixelFormat = video.PixelFormat ?? "";
        var playable = video.Codec switch
        {
            "h264" => pixelFormat is "yuv420p" or "yuvj420p",
            "hevc" => pixelFormat is "yuv420p" or "yuvj420p" or "yuv420p10le",
            _ => false,
        };
        if (!playable || !probed.Audio.All(a => a.Codec == "aac")) return null;

        // Every sound track comes along, not just the first.
        List<string> arguments = ["-map", "0:V:0", "-map", "0:a?", "-c", "copy"];
        // QuickTime only plays HEVC tagged "hvc1"; ffmpeg's default is "hev1".
        if (video.Codec == "hevc") arguments.AddRange(["-tag:v", "hvc1"]);
        if (format == OutputFormat.Mp4) arguments.AddRange(["-movflags", "+faststart"]);
        return arguments;
    }

    /// <summary>The argument lists to try in turn, each complete (without the input and output): a lossless copy first when the
    /// container allows and no setting asks for a re-encode; then the encode, and for HEVC each encoder after the first as a
    /// fallback, ending in x264.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Attempts(
        OutputFormat format, IReadOnlySet<string> encoders, ProbedMedia? probed, ConversionOptions options)
    {
        var attempts = new List<IReadOnlyList<string>>();
        var reencode = Reencodes(options);
        if (!reencode)
        {
            if (format == OutputFormat.Mkv)
                // Matroska holds almost any codec, so try copying every stream first.
                attempts.Add(["-map", "0", "-c", "copy"]);
            else if (probed is not null && StreamCopy(format, probed) is { } copy)
                attempts.Add(copy);
        }
        var isHdr = probed?.Video?.IsHdr ?? false;
        var audio = probed?.Audio.FirstOrDefault()?.Source;
        var display = probed?.Video?.DisplaySize;
        if (TakesHevc(format) && WantsHevc(options.Video, isHdr) && AvailableHevc(encoders).Count > 0)
        {
            foreach (var encoder in AvailableHevc(encoders))
                attempts.Add(Arguments(format, encoders, audio, options, isHdr, encoder, display));
            attempts.Add(Arguments(format, encoders, audio, options, isHdr, X264, display));
        }
        else
        {
            attempts.Add(Arguments(format, encoders, audio, options, isHdr, TakesHevc(format) ? X264 : null, display));
        }
        return attempts;
    }
}
