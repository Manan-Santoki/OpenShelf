using OpenShelf.Convert;
using static OpenShelf.Convert.Media.Tests.Media;

namespace OpenShelf.Convert.Media.Tests;

public class ProbeTests
{
    [Fact]
    public void ReadsStreamsRotationAndHdr()
    {
        var probed = ProbedMedia.Parse("""
            {"streams":[
              {"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"pix_fmt":"yuv420p10le","avg_frame_rate":"0/0","r_frame_rate":"30000/1001",
               "bit_rate":"20000000","color_transfer":"arib-std-b67","color_primaries":"bt2020","side_data_list":[{"rotation":-90}]},
              {"codec_type":"video","codec_name":"mjpeg","width":600,"height":600,"disposition":{"attached_pic":1}},
              {"codec_type":"audio","codec_name":"aac","channels":6,"sample_rate":"48000","bit_rate":"384000"},
              {"codec_type":"audio","codec_name":"opus","channels":1}],
             "format":{"duration":"12.5","bit_rate":"21000000"}}
            """)!;
        Assert.Equal(12.5, probed.Duration);
        var video = probed.Video!;
        Assert.Equal((3840, 2160), (video.Width, video.Height));
        Assert.Equal(90, video.Rotation);
        Assert.Equal((2160, 3840), video.DisplaySize);
        Assert.Equal("30000/1001", video.FrameRate);
        Assert.Equal(29.97, video.FramesPerSecond!.Value, 2);
        Assert.Equal(20_000_000, video.BitRate);
        Assert.True(video.IsHdr);
        Assert.Equal("bt2020", video.ColorPrimaries);
        Assert.Equal(2, probed.Audio.Count);
        Assert.Equal(new ProbedAudio("aac", 6, 48000, 384000), probed.Audio[0]);
        Assert.Equal(new SourceAudio(1, null, AudioCodec.Opus), probed.Audio[1].Source);
    }

    [Fact]
    public void OlderRotateTagAndNoDurationFallBack()
    {
        var probed = ProbedMedia.Parse("""{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"tags":{"rotate":"270"},"duration":"3.5"}]}""")!;
        Assert.Equal(270, probed.Video!.Rotation);
        Assert.Equal(3.5, probed.Duration);
        Assert.Null(probed.Video.FrameRate);
    }

    [Fact]
    public void AudioOnlyFilesBorrowTheContainersRate()
    {
        var ogg = ProbedMedia.Parse("""{"streams":[{"codec_type":"audio","codec_name":"vorbis","channels":1}],"format":{"bit_rate":"48000"}}""")!;
        Assert.Equal(new SourceAudio(1, 48000, AudioCodec.Vorbis), ogg.Audio[0].Source);
        // With video in the file, the overall rate says nothing about the sound.
        var mkv = ProbedMedia.Parse("""{"streams":[{"codec_type":"video","width":2,"height":2},{"codec_type":"audio","channels":2}],"format":{"bit_rate":"5000000"}}""")!;
        Assert.Equal(new SourceAudio(2, null, null), mkv.Audio[0].Source);
        Assert.Null(ProbedMedia.Parse("""{"streams":[{"codec_type":"subtitle"}]}"""));
        Assert.Null(ProbedMedia.Parse("not json"));
    }

    [Fact]
    public void ProbesAreCachedPerFileAndModificationTime()
    {
        using var dir = new TempDir();
        var path = dir.Combine("clip.mp4");
        File.WriteAllText(path, "a");
        var asked = new List<string>();
        var probe = FakeProbe(ProbeJson(), asked);
        Assert.NotNull(probe.Load(path, FakeFfprobePath));
        Assert.NotNull(probe.Load(path, FakeFfprobePath));
        Assert.Single(asked);
        File.WriteAllText(path, "longer content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        probe.Load(path, FakeFfprobePath);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public void HasAudioTrackAnswersTrueFalseOrUnknown()
    {
        using var dir = new TempDir();
        var path = dir.Combine("clip.mkv");
        File.WriteAllText(path, "a");
        Assert.True(FakeProbe(ProbeJson()).HasAudioTrack(path, FakeFfprobePath));
        Assert.False(FakeProbe(ProbeJson(audio: false)).HasAudioTrack(path, FakeFfprobePath));
        // Unknown without ffprobe, or when ffprobe cannot read the file.
        Assert.Null(FakeProbe(ProbeJson()).HasAudioTrack(path, null));
        var failing = new MediaProbe((_, _) => throw new ProcessFailure("ffprobe", "Invalid data found when processing input"));
        Assert.Null(failing.HasAudioTrack(path, FakeFfprobePath));
    }

    [Fact]
    public void TheCatalogDropsAudioOutputsOfSilentVideos()
    {
        using var dir = new TempDir();
        var path = dir.Combine("clip.mkv");
        File.WriteAllText(path, "a");
        var tools = FakeTools;
        var silent = FakeProbe(ProbeJson(audio: false));
        var formats = ConversionCatalog.Formats([path], tools, p => silent.HasAudioTrack(p, tools.Ffprobe));
        Assert.DoesNotContain(OutputFormat.Mp3, formats);
        Assert.DoesNotContain(OutputFormat.M4a, formats);
        Assert.Contains(OutputFormat.Mp4, formats);
        var loud = FakeProbe(ProbeJson());
        Assert.Contains(OutputFormat.Mp3, ConversionCatalog.Formats([path], tools, p => loud.HasAudioTrack(p, tools.Ffprobe)));
    }

    [Fact]
    public void EncoderListIsParsedAndAskedOnce()
    {
        const string output = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D libx264              libx264 H.264 / AVC (codec h264)
             V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
             A....D libopus              libopus Opus (codec opus)
            """;
        var parsed = FFmpegEncoders.Parse(output);
        Assert.Equal(["libx264", "hevc_nvenc", "libopus"], parsed.OrderBy(x => x == "libx264" ? 0 : x == "hevc_nvenc" ? 1 : 2));
        var calls = 0;
        var encoders = new FFmpegEncoders((inv, _) =>
        {
            calls++;
            Assert.Equal(new[] { "-hide_banner", "-encoders" }, inv.Arguments);
            return Task.FromResult(output);
        });
        Assert.Contains("libx264", encoders.Available(FakeFfmpegPath));
        Assert.Contains("libopus", encoders.Available(FakeFfmpegPath));
        Assert.Equal(1, calls);
    }
}

public class ProgressTests
{
    [Fact]
    public void ParsesOutTimeAgainstTheDuration()
    {
        Assert.Equal(0.25, FFmpegJob.ParseProgress("out_time_us=2500000", 10));
        Assert.Equal(1, FFmpegJob.ParseProgress("out_time_us=99000000", 10));
        Assert.Equal(0, FFmpegJob.ParseProgress("out_time_us=-5", 10));
        Assert.Equal(1, FFmpegJob.ParseProgress("progress=end", 10));
        Assert.Null(FFmpegJob.ParseProgress("progress=continue", 10));
        Assert.Null(FFmpegJob.ParseProgress("out_time_us=N/A", 10));
        Assert.Null(FFmpegJob.ParseProgress("out_time_us=2500000", 0));
        Assert.Null(FFmpegJob.ParseProgress("frame=12", 10));
    }

    [Fact]
    public void TheGateStopsWhenClosedAndNeverRunsBackwards()
    {
        var log = new Collected();
        var gate = new ProgressGate(log);
        gate.Report(0.5);
        gate.Report(0.3);
        gate.Report(0.6);
        gate.Close();
        gate.Report(0.9);
        Assert.Equal([0.5, 0.6], log.Values);
    }

    [Fact]
    public void TimeoutGrowsWithTheDuration()
    {
        Assert.True(FFmpegJob.Timeout(60) < FFmpegJob.Timeout(3600));
        Assert.True(FFmpegJob.Timeout(1) >= TimeSpan.FromMinutes(10));
        Assert.True(FFmpegJob.Timeout(0) >= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task EngineReportsProgressAndEndsAtOne()
    {
        using var dir = new TempDir();
        var source = dir.Combine("clip.mp4");
        File.WriteAllText(source, "x");
        var ffmpeg = new FakeFfmpeg();
        var log = new Collected();
        await Engine(ffmpeg, ProbeJson(duration: 4)).ConvertAsync(Request(source, OutputFormat.Mkv), log, default);
        Assert.Equal([0.25, 0.5, 1], log.Values);
    }
}

public class FakeRunnerTests
{
    private static string Source(TempDir dir, string name = "clip.mp4")
    {
        var path = dir.Combine(name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task TheCommandLineIsQuietReadsTheSourceAndWritesBesideIt()
    {
        using var dir = new TempDir();
        var source = Source(dir);
        var ffmpeg = new FakeFfmpeg();
        var result = await Engine(ffmpeg, ProbeJson("vp9")).ConvertAsync(Request(source, OutputFormat.Mp4), null, default);

        Assert.Equal([dir.Combine("clip 2.mp4")], result);
        Assert.True(File.Exists(result[0]));
        var call = ffmpeg.Last;
        Assert.Equal(new[] { "-y", "-nostdin", "-loglevel", "error", "-i", source }, call.Take(6));
        Assert.Equal(new[] { "-progress", "pipe:1", "-nostats", result[0] }, call.TakeLast(4));
    }

    [Fact]
    public async Task TheRunnersStagingFolderIsWhatIsWrittenTo()
    {
        using var dir = new TempDir();
        var source = Source(dir);
        var staging = Directory.CreateDirectory(dir.Combine(".os-1234")).FullName;
        var ffmpeg = new FakeFfmpeg();
        var result = await Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(source, OutputFormat.Mov, outputDirectory: staging), null, default);
        Assert.Equal([Path.Combine(staging, "clip.mov")], result);
    }

    [Fact]
    public async Task AutomaticMp4TriesAStreamCopyFirstAndKeepsItWhenItWorks()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        await Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(Source(dir, "a.mov"), OutputFormat.Mp4), null, default);
        Assert.Single(ffmpeg.Calls);
        Assert.True(Contains(ffmpeg.Last, "-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart"));
    }

    [Fact]
    public async Task AFailedCopyFallsBackToH264AndAac()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            if (args.Contains("copy")) throw new ProcessFailure("ffmpeg", "Could not write header");
            File.WriteAllText(args[^1], "media");
            return Task.CompletedTask;
        };
        var result = await Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(Source(dir, "a.mov"), OutputFormat.Mp4), null, default);
        Assert.Equal(["copy", "libx264"], ffmpeg.VideoCodecs());
        Assert.True(Contains(ffmpeg.Last, "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"));
        Assert.True(File.Exists(result[0]));
    }

    [Fact]
    public async Task MkvCopiesEveryStreamFirstAndWebmNeverCopies()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        var engine = Engine(ffmpeg, ProbeJson("vp9", audioCodec: "opus"));
        await engine.ConvertAsync(Request(Source(dir, "a.webm"), OutputFormat.Mkv), null, default);
        Assert.True(Contains(ffmpeg.Last, "-map", "0", "-c", "copy"));
        ffmpeg.Calls.Clear();
        await engine.ConvertAsync(Request(Source(dir, "b.mp4"), OutputFormat.Webm), null, default);
        Assert.Single(ffmpeg.Calls);
        Assert.Equal("libvpx-vp9", After(ffmpeg.Last, "-c:v"));
    }

    [Fact]
    public async Task EveryFormatHasItsEncoderFlagsInTheCommandLine()
    {
        using var dir = new TempDir();
        var source = Source(dir, "a.mkv");
        var expectations = new (OutputFormat Format, string[] Flags)[]
        {
            (OutputFormat.Mp3, ["-vn", "-c:a", "libmp3lame", "-b:a", "192k"]),
            (OutputFormat.M4a, ["-vn", "-c:a", "aac", "-b:a", "192k"]),
            (OutputFormat.Wav, ["-vn", "-c:a", "pcm_s16le"]),
            (OutputFormat.Aiff, ["-vn", "-c:a", "pcm_s16be"]),
            (OutputFormat.Flac, ["-vn", "-c:a", "flac"]),
            (OutputFormat.Ogg, ["-vn", "-c:a", "libvorbis", "-b:a", "160k"]),
            (OutputFormat.Opus, ["-vn", "-c:a", "libopus", "-b:a", "128k"]),
            (OutputFormat.Wma, ["-vn", "-c:a", "wmav2", "-b:a", "160k"]),
            (OutputFormat.Mov, ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"]),
            (OutputFormat.Webm, ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"]),
            (OutputFormat.Avi, ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"]),
            (OutputFormat.Wmv, ["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "160k"]),
            (OutputFormat.Gif, ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"]),
        };
        foreach (var (format, flags) in expectations)
        {
            var ffmpeg = new FakeFfmpeg();
            await Engine(ffmpeg, ProbeJson("vp9", audioCodec: "opus", audioBitRate: 1_411_200)).ConvertAsync(Request(source, format), null, default);
            Assert.True(Contains(ffmpeg.Last, flags), $"{format}: {string.Join(' ', ffmpeg.Last)}");
            Assert.EndsWith("." + format.FileExtension(), ffmpeg.Last[^1]);
        }
    }

    [Fact]
    public async Task HevcFallsBackToTheNextEncoderOnFailure()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            var encoder = After(args, "-c:v");
            if (encoder is "libx265" or "hevc_nvenc") throw new ProcessFailure("ffmpeg", $"{encoder} failed");
            File.WriteAllText(args[^1], "media");
            return Task.CompletedTask;
        };
        var options = new ConversionOptions { Video = new VideoOptions { Codec = VideoCodec.Hevc } };
        var result = await Engine(ffmpeg, ProbeJson("vp9")).ConvertAsync(Request(Source(dir), OutputFormat.Mp4, options), null, default);
        Assert.Equal(["libx265", "hevc_nvenc", "hevc_qsv"], ffmpeg.VideoCodecs());
        Assert.True(File.Exists(result[0]));
        Assert.True(Contains(ffmpeg.Last, "-tag:v", "hvc1"));
    }

    [Fact]
    public async Task HevcEndsInX264WhenEveryHevcEncoderFails()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            if (After(args, "-c:v") != "libx264") throw new ProcessFailure("ffmpeg", "no device");
            File.WriteAllText(args[^1], "media");
            return Task.CompletedTask;
        };
        var options = new ConversionOptions { Video = new VideoOptions { Codec = VideoCodec.Hevc } };
        await Engine(ffmpeg, ProbeJson("vp9")).ConvertAsync(Request(Source(dir), OutputFormat.Mkv, options), null, default);
        Assert.Equal(["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx264"], ffmpeg.VideoCodecs());
    }

    [Fact]
    public async Task WhenEveryAttemptFailsTheLastErrorIsThrownAndNothingIsLeft()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            File.WriteAllText(args[^1], "partial");
            throw new ProcessFailure("ffmpeg", "Invalid data found when processing input");
        };
        var e = await Assert.ThrowsAsync<ProcessFailure>(() => Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(Source(dir, "a.mov"), OutputFormat.Mp4), null, default));
        Assert.Contains("Invalid data", e.Message);
        Assert.Equal(["a.mov"], dir.Names());
    }

    [Fact]
    public async Task AMissingFfmpegOrATimeoutStopsAtOnce()
    {
        using var dir = new TempDir();
        var source = Source(dir, "a.mov");
        var ffmpeg = new FakeFfmpeg { Behavior = (_, _, _) => throw new ProcessFailure("ffmpeg", "", timedOut: true) };
        await Assert.ThrowsAsync<ProcessFailure>(() => Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(source, OutputFormat.Mp4), null, default));
        Assert.Single(ffmpeg.Calls);

        await Assert.ThrowsAsync<MissingToolException>(() =>
            Engine(new FakeFfmpeg(), ProbeJson()).ConvertAsync(Request(source, OutputFormat.Mp4, tools: ExternalTools.None), null, default));
    }

    [Fact]
    public async Task CancellingStopsWithoutFallingBackAndLeavesNoFile()
    {
        using var dir = new TempDir();
        var source = Source(dir, "a.mov");
        var ffmpeg = new FakeFfmpeg();
        using var cancel = new CancellationTokenSource();
        ffmpeg.Behavior = async (args, onLine, ct) =>
        {
            File.WriteAllText(args[^1], "partial");
            onLine?.Invoke("out_time_us=500000");
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var options = new ConversionOptions { Video = new VideoOptions { Codec = VideoCodec.Hevc } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(source, OutputFormat.Mp4, options), null, cancel.Token));
        Assert.Single(ffmpeg.Calls);
        Assert.Equal(["a.mov"], dir.Names());
    }

    [Fact]
    public async Task AlreadyCancelledNeverStartsFfmpeg()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(Source(dir), OutputFormat.Mkv), null, new CancellationToken(true)));
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task SilentVideoCannotBecomeAudioAndAudioCannotBecomeVideo()
    {
        using var dir = new TempDir();
        var source = Source(dir, "a.mkv");
        var ffmpeg = new FakeFfmpeg();
        var silent = await Assert.ThrowsAsync<ConversionException>(() => Engine(ffmpeg, ProbeJson(audio: false)).ConvertAsync(Request(source, OutputFormat.Mp3), null, default));
        Assert.Contains("no audio track", silent.Message);
        var noVideo = await Assert.ThrowsAsync<ConversionException>(() => Engine(ffmpeg, ProbeJson(video: false)).ConvertAsync(Request(source, OutputFormat.Gif), null, default));
        Assert.Contains("no video", noVideo.Message);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedWithoutRunningFfmpeg()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        var probe = new MediaProbe((_, _) => throw new ProcessFailure("ffprobe", "Invalid data found"));
        var engine = new MediaEngine(ffmpeg.Runner, probe, FakeEncoders());
        var e = await Assert.ThrowsAsync<ConversionException>(() => engine.ConvertAsync(Request(Source(dir), OutputFormat.Mp4), null, default));
        Assert.Equal(ConversionErrorKind.Unreadable, e.Kind);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task ASourceWithAnAudioRateGetsTheMatchingBitrate()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        // A 33 kb/s mono AAC voice memo to Opus: 32 kb/s, not the 128 ceiling.
        var json = ProbeJson(video: false, audioChannels: 1, audioBitRate: 33333);
        await Engine(ffmpeg, json).ConvertAsync(Request(Source(dir, "memo.m4a"), OutputFormat.Opus), null, default);
        Assert.True(Contains(ffmpeg.Last, "-c:a", "libopus", "-b:a", "32k"));
    }

    [Fact]
    public async Task SettingsReachTheCommandLine()
    {
        using var dir = new TempDir();
        var ffmpeg = new FakeFfmpeg();
        var options = new ConversionOptions
        {
            Video = new VideoOptions { Quality = VideoQuality.Custom, CustomMbps = 2, LargestSize = VideoSize.HD720 },
            Audio = new AudioOptions { BitrateKbps = 96 },
        };
        await Engine(ffmpeg, ProbeJson()).ConvertAsync(Request(Source(dir), OutputFormat.Mp4, options), null, default);
        Assert.Single(ffmpeg.Calls); // no stream copy when settings ask for a re-encode
        Assert.Contains("force_divisible_by=2", After(ffmpeg.Last, "-vf"));
        Assert.True(Contains(ffmpeg.Last, "-b:v", "2000000", "-maxrate", "3000000", "-bufsize", "4000000"));
        Assert.True(Contains(ffmpeg.Last, "-c:a", "aac", "-b:a", "96k"));
    }

    [Fact]
    public void CanConvertFollowsKindsAndTheInstalledTools()
    {
        var engine = new MediaEngine(new FakeFfmpeg().Runner, FakeProbe(ProbeJson()), FakeEncoders());
        Assert.True(engine.CanConvert(FileKind.Audio, OutputFormat.Ogg));
        Assert.False(engine.CanConvert(FileKind.Audio, OutputFormat.Mp4));
        Assert.True(engine.CanConvert(FileKind.Video, OutputFormat.Gif));
        Assert.True(engine.CanConvert(FileKind.Video, OutputFormat.Mp3));
        Assert.False(engine.CanConvert(FileKind.Image, OutputFormat.Mp4));
        IConversionEngine asked = engine;
        Assert.True(asked.CanConvert(Request("a.mp4", OutputFormat.Mkv)));
        Assert.False(asked.CanConvert(Request("a.mp4", OutputFormat.Mkv, tools: ExternalTools.None)));
    }

    [Fact]
    public void RegisteringMakesTheMediaFormatsConvertible()
    {
        var registry = new EngineRegistry();
        MediaEngines.Register(registry);
        Assert.NotNull(registry.Find(Request("a.mp4", OutputFormat.Webm)));
        Assert.NotNull(registry.Find(Request("a.flac", OutputFormat.Opus)));
        Assert.Null(registry.Find(Request("a.png", OutputFormat.Webm)));
        // Transcripts need whisper and a model.
        Assert.Null(registry.Find(Request("a.mp3", OutputFormat.Srt)));
    }
}
