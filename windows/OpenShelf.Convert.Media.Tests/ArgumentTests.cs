using OpenShelf.Convert;
using static OpenShelf.Convert.Media.Tests.Media;

namespace OpenShelf.Convert.Media.Tests;

/// <summary>SPEC 2.3.1, the ffmpeg column, row by row: the exact flags each setting gives.</summary>
public class ArgumentTests
{
    private static readonly SourceAudio Stereo = new(2);

    private static IReadOnlyList<string> Args(OutputFormat format, Action<ConversionOptions>? _ = null, ConversionOptions? options = null,
        IReadOnlySet<string>? encoders = null, bool hdr = false, SourceAudio? audio = null, string? encoder = null) =>
        FFmpegArguments.Arguments(format, encoders ?? AllEncoders, audio ?? Stereo, options, hdr, encoder);

    private static ConversionOptions With(VideoOptions video) => new() { Video = video };
    private static ConversionOptions With(AudioOptions audio) => new() { Audio = audio };

    /// <summary>Exactly what each format gets with every setting Automatic.</summary>
    [Fact]
    public void AutomaticIsTheDocumentedDefault()
    {
        string[] h264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"];
        var expected = new Dictionary<OutputFormat, string[]>
        {
            [OutputFormat.Mp4] = [.. h264, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
            [OutputFormat.Mov] = [.. h264, "-c:a", "aac", "-b:a", "192k"],
            [OutputFormat.Mkv] = [.. h264, "-c:a", "aac", "-b:a", "192k"],
            [OutputFormat.M4a] = ["-vn", "-c:a", "aac", "-b:a", "192k"],
            [OutputFormat.Mp3] = ["-vn", "-c:a", "libmp3lame", "-b:a", "192k"],
            [OutputFormat.Opus] = ["-vn", "-c:a", "libopus", "-b:a", "128k"],
            [OutputFormat.Wma] = ["-vn", "-c:a", "wmav2", "-b:a", "160k"],
            [OutputFormat.Ogg] = ["-vn", "-c:a", "libvorbis", "-b:a", "160k"],
            [OutputFormat.Wav] = ["-vn", "-c:a", "pcm_s16le"],
            [OutputFormat.Aiff] = ["-vn", "-c:a", "pcm_s16be"],
            [OutputFormat.Flac] = ["-vn", "-c:a", "flac"],
            [OutputFormat.Webm] = ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"],
            [OutputFormat.Avi] = ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"],
            [OutputFormat.Wmv] = ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "160k"],
            [OutputFormat.Gif] = ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"],
        };
        foreach (var (format, arguments) in expected)
        {
            Assert.Equal(arguments, FFmpegArguments.Arguments(format, AllEncoders, Stereo));
            Assert.Equal(arguments, FFmpegArguments.Arguments(format, AllEncoders, Stereo, ConversionOptions.Automatic));
            // HDR only matters once a video setting is chosen.
            Assert.Equal(arguments, FFmpegArguments.Arguments(format, AllEncoders, Stereo, null, true));
        }
    }

    [Theory]
    [InlineData(VideoQuality.Best, "18")]
    [InlineData(VideoQuality.Automatic, "20")]
    [InlineData(VideoQuality.Balanced, "23")]
    [InlineData(VideoQuality.Smaller, "28")]
    public void H264QualityIsACrf(VideoQuality quality, string crf)
    {
        foreach (var format in new[] { OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Mkv })
            Assert.True(Contains(Args(format, options: With(new VideoOptions { Quality = quality })), "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p"));
    }

    [Fact]
    public void CustomIsATargetBitrateWithMaxrateAndBufsize()
    {
        var custom = With(new VideoOptions { Quality = VideoQuality.Custom, CustomMbps = 4 });
        var mp4 = Args(OutputFormat.Mp4, options: custom);
        Assert.True(Contains(mp4, "-b:v", "4000000", "-maxrate", "6000000", "-bufsize", "8000000"));
        Assert.DoesNotContain("-crf", mp4);
        Assert.True(Contains(Args(OutputFormat.Webm, options: With(new VideoOptions { Quality = VideoQuality.Custom, CustomMbps = 1.5 })), "-c:v", "libvpx-vp9", "-b:v", "1500000", "-deadline"));
        Assert.True(Contains(Args(OutputFormat.Avi, options: With(new VideoOptions { Quality = VideoQuality.Custom })), "-vtag", "xvid", "-b:v", "8000000", "-c:a"));
        Assert.True(Contains(Args(OutputFormat.Wmv, options: With(new VideoOptions { Quality = VideoQuality.Custom })), "wmv2", "-b:v", "8000000", "-c:a"));
    }

    [Fact]
    public void Vp9AviAndWmvFollowTheQuality()
    {
        Assert.True(Contains(Args(OutputFormat.Webm, options: With(new VideoOptions { Quality = VideoQuality.Best })), "-crf", "28", "-b:v", "0"));
        Assert.True(Contains(Args(OutputFormat.Webm, options: With(new VideoOptions { Quality = VideoQuality.Balanced })), "-crf", "33", "-b:v", "0"));
        Assert.True(Contains(Args(OutputFormat.Webm, options: With(new VideoOptions { Quality = VideoQuality.Smaller })), "-crf", "38", "-b:v", "0"));
        Assert.True(Contains(Args(OutputFormat.Avi, options: With(new VideoOptions { Quality = VideoQuality.Best })), "-q:v", "2"));
        Assert.True(Contains(Args(OutputFormat.Avi, options: With(new VideoOptions { Quality = VideoQuality.Balanced })), "-q:v", "5"));
        Assert.True(Contains(Args(OutputFormat.Wmv, options: With(new VideoOptions { Quality = VideoQuality.Smaller })), "-q:v", "8"));
        // These containers have one codec each; a codec choice changes nothing.
        Assert.Equal(Args(OutputFormat.Webm), Args(OutputFormat.Webm, options: With(new VideoOptions { Codec = VideoCodec.Hevc })));
    }

    [Fact]
    public void HevcPrefersX265ThenHardware()
    {
        var hevc = With(new VideoOptions { Codec = VideoCodec.Hevc, Quality = VideoQuality.Balanced });
        // x265 needs five more CRF; the hvc1 tag only matters in QuickTime's containers.
        Assert.True(Contains(Args(OutputFormat.Mp4, options: hevc), "-c:v", "libx265", "-preset", "medium", "-crf", "28", "-pix_fmt", "yuv420p", "-tag:v", "hvc1", "-x265-params", "log-level=error"));
        Assert.True(Contains(Args(OutputFormat.Mov, options: hevc), "-c:v", "libx265") && Contains(Args(OutputFormat.Mov, options: hevc), "-tag:v", "hvc1"));
        Assert.DoesNotContain("-tag:v", Args(OutputFormat.Mkv, options: With(new VideoOptions { Codec = VideoCodec.Hevc })));
        Assert.True(Contains(Args(OutputFormat.Mkv, options: With(new VideoOptions { Codec = VideoCodec.Hevc })), "-crf", "25"));

        var autoHevc = With(new VideoOptions { Codec = VideoCodec.Hevc });
        var noX265 = AllEncoders.Where(e => e != "libx265").ToHashSet();
        Assert.True(Contains(Args(OutputFormat.Mp4, options: autoHevc, encoders: noX265), "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0", "-pix_fmt", "yuv420p", "-tag:v", "hvc1"));
        var noNv = noX265.Where(e => e != "hevc_nvenc").ToHashSet();
        Assert.True(Contains(Args(OutputFormat.Mp4, options: autoHevc, encoders: noNv), "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", "24", "-pix_fmt", "nv12", "-tag:v", "hvc1"));
        var onlyAmf = new HashSet<string> { "libx264", "hevc_amf" };
        Assert.True(Contains(Args(OutputFormat.Mov, options: autoHevc, encoders: onlyAmf), "-c:v", "hevc_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "24", "-qp_p", "24", "-pix_fmt", "nv12", "-tag:v", "hvc1"));
        // A custom bitrate goes to every encoder.
        var custom = With(new VideoOptions { Codec = VideoCodec.Hevc, Quality = VideoQuality.Custom });
        Assert.True(Contains(Args(OutputFormat.Mp4, options: custom, encoders: noX265), "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-b:v", "8000000", "-maxrate", "12000000", "-bufsize", "16000000"));
        // Without any HEVC encoder, H.264 it is.
        Assert.True(Contains(Args(OutputFormat.Mp4, options: autoHevc, encoders: new HashSet<string> { "libx264" }), "-c:v", "libx264"));
    }

    [Fact]
    public void HdrKeepsTenBitHevcOnceVideoSettingsAreChosen()
    {
        var hdr = Args(OutputFormat.Mp4, options: With(new VideoOptions { Codec = VideoCodec.H264, LargestSize = VideoSize.FullHD1080 }), hdr: true);
        Assert.True(Contains(hdr, "-c:v", "libx265") && hdr.Contains("yuv420p10le"));
        var nvenc = Args(OutputFormat.Mov, options: With(new VideoOptions { Quality = VideoQuality.Best }), hdr: true, encoders: new HashSet<string> { "hevc_nvenc", "libx264" });
        Assert.True(Contains(nvenc, "-pix_fmt", "p010le", "-profile:v", "main10"));
        // A fixed audio rate alone leaves the picture as it always was.
        Assert.True(Contains(Args(OutputFormat.Mp4, options: With(new AudioOptions { BitrateKbps = 128 }), hdr: true), "-c:v", "libx264", "-preset", "medium", "-crf", "20"));
    }

    [Fact]
    public void LargestSizeFitsEitherOrientationWithoutEnlarging()
    {
        var filter = FFmpegArguments.ScaleFilter((1280, 720));
        Assert.Equal("scale=w='if(gte(iw,ih),min(1280,iw),min(720,iw))':h='if(gte(iw,ih),min(720,ih),min(1280,ih))':force_original_aspect_ratio=decrease:force_divisible_by=2", filter);
        foreach (var format in new[] { OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Mkv, OutputFormat.Webm, OutputFormat.Avi, OutputFormat.Wmv })
        {
            var scaled = Args(format, options: With(new VideoOptions { LargestSize = VideoSize.HD720 }));
            Assert.Equal(new[] { "-vf", filter }, scaled.Take(2));
        }
        Assert.Contains("min(3840,iw)", Args(OutputFormat.Mp4, options: With(new VideoOptions { LargestSize = VideoSize.UltraHD4K }))[1]);
        Assert.Contains("min(1920,iw)", Args(OutputFormat.Mp4, options: With(new VideoOptions { LargestSize = VideoSize.FullHD1080 }))[1]);
        Assert.Contains("min(854,iw)", Args(OutputFormat.Mp4, options: With(new VideoOptions { LargestSize = VideoSize.SD480 }))[1]);
        Assert.DoesNotContain("-vf", Args(OutputFormat.Mp4));
    }

    [Fact]
    public void OddSourcesAreEvenedOutForTheH264Family()
    {
        var odd = FFmpegArguments.Arguments(OutputFormat.Mp4, AllEncoders, Stereo, null, false, null, (321, 241));
        Assert.Equal(new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2" }, odd.Take(2));
        var even = FFmpegArguments.Arguments(OutputFormat.Mp4, AllEncoders, Stereo, null, false, null, (320, 240));
        Assert.DoesNotContain("-vf", even);
        // A size limit already makes the sides even.
        var limited = FFmpegArguments.Arguments(OutputFormat.Mp4, AllEncoders, Stereo, With(new VideoOptions { LargestSize = VideoSize.HD720 }), false, null, (321, 241));
        Assert.Single(limited, a => a == "-vf");
        Assert.Contains("force_divisible_by=2", limited[1]);
    }

    [Fact]
    public void AFixedAudioRateReplacesThePolicy()
    {
        // A lean source, which the automatic policy would give far less.
        var voice = new SourceAudio(2, 64000, AudioCodec.Aac);
        var fixed256 = With(new AudioOptions { BitrateKbps = 256 });
        foreach (var format in new[] { OutputFormat.Mp3, OutputFormat.M4a, OutputFormat.Opus, OutputFormat.Wma, OutputFormat.Ogg, OutputFormat.Mp4, OutputFormat.Webm, OutputFormat.Avi, OutputFormat.Wmv })
            Assert.True(Contains(FFmpegArguments.Arguments(format, AllEncoders, voice, fixed256), "-b:a", "256k"), format.ToString());
        // Lossless formats have no rate to set.
        Assert.Equal(new[] { "-vn", "-c:a", "flac" }, FFmpegArguments.Arguments(OutputFormat.Flac, AllEncoders, voice, fixed256));
        Assert.Equal(new[] { "-vn", "-c:a", "pcm_s16le" }, FFmpegArguments.Arguments(OutputFormat.Wav, AllEncoders, voice, fixed256));
        Assert.Equal("256k", AudioBitRate.Argument(new AudioOptions { BitrateKbps = 256 }, AudioCodec.Opus, voice));
        Assert.Equal(AudioBitRate.Argument(AudioCodec.Opus, voice), AudioBitRate.Argument(new AudioOptions(), AudioCodec.Opus, voice));
    }

    [Fact]
    public void MonoCapsApplyToAFixedRate()
    {
        var mono = new SourceAudio(1, 64000, AudioCodec.Aac);
        var fixed320 = new AudioOptions { BitrateKbps = 320 };
        Assert.Equal("256k", AudioBitRate.Argument(fixed320, AudioCodec.Aac, mono));
        Assert.Equal("256k", AudioBitRate.Argument(fixed320, AudioCodec.Opus, mono));
        Assert.Equal("192k", AudioBitRate.Argument(fixed320, AudioCodec.Vorbis, mono));
        Assert.Equal("320k", AudioBitRate.Argument(fixed320, AudioCodec.Mp3, mono));
        Assert.Equal("320k", AudioBitRate.Argument(fixed320, AudioCodec.Wma, mono));
        Assert.Equal("320k", AudioBitRate.Argument(fixed320, AudioCodec.Aac, new SourceAudio(2)));
    }

    [Fact]
    public void OggFallsBackFromVorbisToOpusToTheBuiltInEncoder()
    {
        Assert.True(Contains(Args(OutputFormat.Ogg, encoders: new HashSet<string> { "libvorbis", "libopus" }), "-c:a", "libvorbis", "-b:a", "160k"));
        Assert.True(Contains(Args(OutputFormat.Ogg, encoders: new HashSet<string> { "libopus" }), "-c:a", "libopus", "-b:a", "128k"));
        Assert.True(Contains(Args(OutputFormat.Ogg, encoders: new HashSet<string>()), "-c:a", "vorbis", "-strict", "experimental", "-ac", "2"));
    }

    [Fact]
    public void GifWidthAndFrameRate()
    {
        var gif = FFmpegArguments.Arguments(OutputFormat.Gif, AllEncoders, Stereo, new ConversionOptions { Gif = new GifOptions { Width = GifWidth.W1080, FramesPerSecond = 24 } });
        Assert.StartsWith("fps=24,scale='min(1080,iw)':-2:flags=lanczos,", gif[1]);
        Assert.EndsWith("split[a][b];[a]palettegen[p];[b][p]paletteuse", gif[1]);
        Assert.Equal(new[] { "-loop", "0" }, gif.Skip(2));
        Assert.Contains("min(1920,iw)", FFmpegArguments.GifFilter(new GifOptions { Width = GifWidth.Original }));
        Assert.StartsWith("fps=10,scale='min(480,iw)'", FFmpegArguments.GifFilter(new GifOptions { Width = GifWidth.W480, FramesPerSecond = 10 }));
    }

    [Fact]
    public void AudioBitRatePolicyMatchesTheMac()
    {
        // The 55 KB voice memo QA converted: mono AAC at 33 kb/s.
        var voice = new SourceAudio(1, 33333, AudioCodec.Aac);
        Assert.Equal(32000, AudioBitRate.Target(AudioCodec.Opus, voice));
        Assert.Equal(new[] { "-b:a", "32k" }, FFmpegArguments.Arguments(OutputFormat.Ogg, new HashSet<string> { "libopus" }, voice).TakeLast(2));

        var cd = new SourceAudio(2, 1_411_200);
        Assert.Equal(128000, AudioBitRate.Target(AudioCodec.Opus, cd));
        Assert.Equal(160000, AudioBitRate.Target(AudioCodec.Vorbis, cd));
        Assert.Equal(192000, AudioBitRate.Target(AudioCodec.Aac, cd));
        Assert.Equal(192000, AudioBitRate.Target(AudioCodec.Mp3, cd));
        Assert.Equal(64000, AudioBitRate.Target(AudioCodec.Opus, new SourceAudio(1, 705_600)));
        Assert.Equal(128000, AudioBitRate.Target(AudioCodec.Opus, null));

        var podcast = new SourceAudio(2, 96000, AudioCodec.Aac);
        Assert.Equal(96000, AudioBitRate.Target(AudioCodec.Opus, podcast));
        Assert.Equal(96000, AudioBitRate.Target(AudioCodec.Aac, podcast));
        Assert.Equal(112000, AudioBitRate.Target(AudioCodec.Mp3, podcast));
        var mp3 = new SourceAudio(1, 64000, AudioCodec.Mp3);
        Assert.Equal(48000, AudioBitRate.Target(AudioCodec.Opus, mp3));
        Assert.Equal(64000, AudioBitRate.Target(AudioCodec.Mp3, mp3));
        Assert.Equal(64000, AudioBitRate.Target(AudioCodec.Wma, mp3));
        Assert.Equal(64000, AudioBitRate.Target(AudioCodec.Aac, new SourceAudio(2, 24000)));
    }

    [Fact]
    public void EveryAutomaticRateIsACommonOne()
    {
        foreach (var codec in Enum.GetValues<AudioCodec>())
            foreach (var channels in new[] { 1, 2, 6 })
                foreach (var kbps in new[] { 8, 33, 70, 129, 250, 1411 })
                    Assert.Contains(AudioBitRate.Target(codec, new SourceAudio(channels, kbps * 1000)) / 1000, AudioBitRate.Steps);
    }

    private static ProbedMedia Probed(string codec = "h264", string pix = "yuv420p", string audio = "aac") =>
        ProbedMedia.Parse(ProbeJson(codec, pix, audio))!;

    [Fact]
    public void StreamCopyOnlyForPlayableCodecs()
    {
        Assert.Equal(new[] { "-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart" }, FFmpegArguments.StreamCopy(OutputFormat.Mp4, Probed()));
        Assert.Equal(new[] { "-map", "0:V:0", "-map", "0:a?", "-c", "copy" }, FFmpegArguments.StreamCopy(OutputFormat.Mov, Probed()));
        Assert.Equal(new[] { "-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-tag:v", "hvc1", "-movflags", "+faststart" }, FFmpegArguments.StreamCopy(OutputFormat.Mp4, Probed("hevc")));
        Assert.Null(FFmpegArguments.StreamCopy(OutputFormat.Mp4, Probed("vp9")));
        Assert.Null(FFmpegArguments.StreamCopy(OutputFormat.Mp4, Probed("h264", "yuv444p")));
        Assert.Null(FFmpegArguments.StreamCopy(OutputFormat.Mp4, Probed(audio: "opus")));
        Assert.Null(FFmpegArguments.StreamCopy(OutputFormat.Mkv, Probed()));
    }

    [Fact]
    public void StreamCopiesAreSkippedWhenSettingsAskForAReencode()
    {
        var probed = Probed();
        Assert.Equal("copy", Copy(OutputFormat.Mp4, ConversionOptions.Automatic, probed));
        Assert.Equal("copy", Copy(OutputFormat.Mkv, ConversionOptions.Automatic, probed));
        foreach (var options in new[]
        {
            With(new VideoOptions { Codec = VideoCodec.H264 }), With(new VideoOptions { Quality = VideoQuality.Smaller }),
            With(new VideoOptions { LargestSize = VideoSize.HD720 }), With(new AudioOptions { BitrateKbps = 128 }),
        })
        {
            Assert.Equal("libx264", Copy(OutputFormat.Mp4, options, probed));
            Assert.Equal("libx264", Copy(OutputFormat.Mkv, options, probed));
        }
    }

    private static string? Copy(OutputFormat format, ConversionOptions options, ProbedMedia probed)
    {
        var first = FFmpegArguments.Attempts(format, AllEncoders, probed, options)[0].ToList();
        var i = first.IndexOf("-c:v");
        return i >= 0 ? first[i + 1] : first.Contains("copy") ? "copy" : null;
    }

    [Fact]
    public void AttemptsListEveryHevcEncoderThenX264()
    {
        var hevc = With(new VideoOptions { Codec = VideoCodec.Hevc });
        var attempts = FFmpegArguments.Attempts(OutputFormat.Mp4, AllEncoders, Probed("vp9"), hevc);
        Assert.Equal(["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264"], attempts.Select(a => Media.After(a, "-c:v")));

        var noX265 = AllEncoders.Where(e => e != "libx265").ToHashSet();
        Assert.Equal(["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264"], FFmpegArguments.Attempts(OutputFormat.Mkv, noX265, null, hevc).Select(a => Media.After(a, "-c:v")));

        // No HEVC encoder at all: one H.264 attempt.
        var x264Only = new HashSet<string> { "libx264" };
        Assert.Equal(["libx264"], FFmpegArguments.Attempts(OutputFormat.Mp4, x264Only, null, hevc).Select(a => Media.After(a, "-c:v")));
        // Automatic needs no HEVC, and a source copy comes first.
        var auto = FFmpegArguments.Attempts(OutputFormat.Mp4, AllEncoders, Probed(), ConversionOptions.Automatic);
        Assert.Equal(2, auto.Count);
        Assert.Contains("copy", auto[0]);
        Assert.Equal("libx264", Media.After(auto[1], "-c:v"));
    }

    [Fact]
    public void HdrSourcesGoThroughHevcWhenAnySettingIsChosen()
    {
        var hdr = ProbedMedia.Parse(ProbeJson("hevc", "yuv420p10le", extraVideo: ",\"color_transfer\":\"smpte2084\",\"color_primaries\":\"bt2020\""))!;
        Assert.True(hdr.Video!.IsHdr);
        var attempts = FFmpegArguments.Attempts(OutputFormat.Mp4, AllEncoders, hdr, With(new VideoOptions { LargestSize = VideoSize.HD720 }));
        Assert.Equal("libx265", Media.After(attempts[0], "-c:v"));
        Assert.Contains("yuv420p10le", attempts[0]);
    }

    [Fact]
    public void RequireEncodersExplainsAMissingOne()
    {
        var e = Assert.Throws<ConversionException>(() => MediaEngine.RequireEncoders(OutputFormat.Mp3, new HashSet<string> { "libx264" }));
        Assert.Contains("libmp3lame", e.Message);
        MediaEngine.RequireEncoders(OutputFormat.Mp3, AllEncoders);
        MediaEngine.RequireEncoders(OutputFormat.Mp3, new HashSet<string>());
    }
}
