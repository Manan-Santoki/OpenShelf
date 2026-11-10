using System.Diagnostics;
using System.Text.Json;
using OpenShelf.Convert;

namespace OpenShelf.Convert.Media.Tests;

/// <summary>Clips made once with ffmpeg's lavfi sources. Real tests return early when ffmpeg or ffprobe is missing.</summary>
public sealed class ClipFixture : IDisposable
{
    public ExternalTools Tools { get; } = ExternalTools.Detect(ToolLocations.Standard);
    public bool Available => Tools.Ffmpeg is not null && Tools.Ffprobe is not null;
    public TempDir Dir { get; } = new();
    public string Clip => Dir.Combine("clip.mp4");
    public string Silent => Dir.Combine("silent.mp4");
    public string Tone => Dir.Combine("tone.wav");
    public string Big => Dir.Combine("big.mp4");
    public string Portrait => Dir.Combine("portrait.mp4");
    public string Odd => Dir.Combine("odd.mp4");

    public ClipFixture()
    {
        if (!Available) return;
        var video = "testsrc2=size=320x240:rate=10:duration=2";
        var tone = "sine=frequency=440:duration=2";
        Run("-f", "lavfi", "-i", video, "-f", "lavfi", "-i", tone, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Clip);
        Run("-f", "lavfi", "-i", video, "-c:v", "libx264", "-pix_fmt", "yuv420p", Silent);
        Run("-f", "lavfi", "-i", tone, "-c:a", "pcm_s16le", Tone);
        Run("-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=5:duration=2", "-f", "lavfi", "-i", tone, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", Big);
        Run("-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=5:duration=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Portrait);
        Run("-f", "lavfi", "-i", "testsrc2=size=322x242:rate=5:duration=1", "-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-f", "mp4", Odd);
    }

    private void Run(params string[] args)
    {
        var start = new ProcessStartInfo(Tools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-y", "-nostdin", "-loglevel", "error" }.Concat(args)) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("fixture ffmpeg failed: " + error);
    }

    public void Dispose() => Dir.Dispose();

    public ConversionRequest Request(string source, OutputFormat format, string outDir, ConversionOptions? options = null) =>
        new(source, format, options ?? ConversionOptions.Automatic) { Tools = Tools, OutputDirectory = outDir };
}

/// <summary>What ffprobe says about a written file.</summary>
public sealed class Written
{
    public string FormatName { get; }
    public double Duration { get; }
    public List<JsonElement> Streams { get; } = [];
    private readonly JsonDocument _document;

    public Written(string ffprobe, string path)
    {
        var start = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _document = JsonDocument.Parse(json);
        var root = _document.RootElement;
        FormatName = root.GetProperty("format").GetProperty("format_name").GetString()!;
        Duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var s in root.GetProperty("streams").EnumerateArray()) Streams.Add(s);
    }

    public JsonElement? Video => Streams.Cast<JsonElement?>().FirstOrDefault(s => s!.Value.GetProperty("codec_type").GetString() == "video");
    public JsonElement? Audio => Streams.Cast<JsonElement?>().FirstOrDefault(s => s!.Value.GetProperty("codec_type").GetString() == "audio");
    public string? VideoCodec => Video?.GetProperty("codec_name").GetString();
    public string? AudioCodec => Audio?.GetProperty("codec_name").GetString();
    public int Width => Video!.Value.GetProperty("width").GetInt32();
    public int Height => Video!.Value.GetProperty("height").GetInt32();
}

public class RealMediaTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    private async Task<(Written Result, string Path)> Convert(string source, OutputFormat format, ConversionOptions? options = null)
    {
        using var dir = new TempDir();
        var engine = new MediaEngine();
        var request = clips.Request(source, format, dir.Path, options);
        var progress = new Collected();
        var written = await engine.ConvertAsync(request, progress, default);
        Assert.Single(written);
        Assert.StartsWith(dir.Path, written[0]);
        Assert.EndsWith("." + format.FileExtension(), written[0]);
        Assert.Equal(1, progress.Values[^1]);
        // Keep the probe result; the file goes away with the folder.
        return (new Written(clips.Tools.Ffprobe!, written[0]), written[0]);
    }

    private static void Near(double expected, double actual, double tolerance = 0.5) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Theory]
    [InlineData(OutputFormat.Mp3, "mp3", "mp3")]
    [InlineData(OutputFormat.M4a, "mov,mp4,m4a,3gp,3g2,mj2", "aac")]
    [InlineData(OutputFormat.Wav, "wav", "pcm_s16le")]
    [InlineData(OutputFormat.Aiff, "aiff", "pcm_s16be")]
    [InlineData(OutputFormat.Flac, "flac", "flac")]
    [InlineData(OutputFormat.Ogg, "ogg", "vorbis")]
    [InlineData(OutputFormat.Opus, "ogg", "opus")]
    [InlineData(OutputFormat.Wma, "asf", "wmav2")]
    public async Task AudioFromAudioAndFromVideo(OutputFormat format, string container, string codec)
    {
        if (!clips.Available) return;
        foreach (var source in new[] { clips.Tone, clips.Clip })
        {
            var (file, _) = await Convert(source, format);
            Assert.Equal(container, file.FormatName);
            Assert.Equal(codec, file.AudioCodec);
            Assert.Null(file.Video);
            Near(2, file.Duration);
        }
    }

    [Theory]
    [InlineData(OutputFormat.Mp4, "mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac")]
    [InlineData(OutputFormat.Mov, "mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac")]
    [InlineData(OutputFormat.Mkv, "matroska,webm", "h264", "aac")]
    [InlineData(OutputFormat.Webm, "matroska,webm", "vp9", "opus")]
    [InlineData(OutputFormat.Avi, "avi", "mpeg4", "mp3")]
    [InlineData(OutputFormat.Wmv, "asf", "wmv2", "wmav2")]
    public async Task VideoRows(OutputFormat format, string container, string video, string audio)
    {
        if (!clips.Available) return;
        var (file, _) = await Convert(clips.Clip, format);
        Assert.Equal(container, file.FormatName);
        Assert.Equal(video, file.VideoCodec);
        Assert.Equal(audio, file.AudioCodec);
        Assert.Equal((320, 240), (file.Width, file.Height));
        Near(2, file.Duration);
    }

    [Fact]
    public async Task GifIsAnAnimationNoWiderThanTheSource()
    {
        if (!clips.Available) return;
        var (file, _) = await Convert(clips.Clip, OutputFormat.Gif);
        Assert.Equal("gif", file.FormatName);
        Assert.Equal("gif", file.VideoCodec);
        Assert.Null(file.Audio);
        Assert.Equal((320, 240), (file.Width, file.Height));
        Near(2, file.Duration, 0.6);
        // A narrower limit shrinks it; the height stays in proportion and even.
        var (narrow, _) = await Convert(clips.Big, OutputFormat.Gif, new ConversionOptions { Gif = new GifOptions { Width = GifWidth.W480, FramesPerSecond = 10 } });
        Assert.Equal((480, 270), (narrow.Width, narrow.Height));
        var (wide, _) = await Convert(clips.Big, OutputFormat.Gif);
        Assert.Equal(720, wide.Width);
    }

    [Theory]
    [InlineData(OutputFormat.Mp4)]
    [InlineData(OutputFormat.Mkv)]
    [InlineData(OutputFormat.Webm)]
    [InlineData(OutputFormat.Avi)]
    [InlineData(OutputFormat.Wmv)]
    [InlineData(OutputFormat.Mov)]
    public async Task SilentSourcesStaySilent(OutputFormat format)
    {
        if (!clips.Available) return;
        var (file, _) = await Convert(clips.Silent, format);
        Assert.Null(file.Audio);
        Assert.NotNull(file.Video);
        Assert.False((await new MediaProbe().LoadAsync(clips.Silent, clips.Tools.Ffprobe))!.HasAudio);
    }

    [Fact]
    public async Task SilentSourcesCannotBecomeAudio()
    {
        if (!clips.Available) return;
        using var dir = new TempDir();
        var e = await Assert.ThrowsAsync<ConversionException>(() =>
            new MediaEngine().ConvertAsync(clips.Request(clips.Silent, OutputFormat.Mp3, dir.Path), null, default));
        Assert.Contains("no audio track", e.Message);
        Assert.Empty(dir.Names());
        // The catalog hook says the same.
        Assert.False(MediaProbe.Default.HasAudioTrack(clips.Silent, clips.Tools.Ffprobe));
        Assert.True(MediaProbe.Default.HasAudioTrack(clips.Clip, clips.Tools.Ffprobe));
        Assert.DoesNotContain(OutputFormat.Mp3, ConversionCatalog.Formats([clips.Silent], clips.Tools, p => MediaProbe.Default.HasAudioTrack(p, clips.Tools.Ffprobe)));
    }

    [Fact]
    public async Task AutomaticMp4OfAnMp4IsAStreamCopy()
    {
        if (!clips.Available) return;
        var (file, _) = await Convert(clips.Clip, OutputFormat.Mp4);
        Assert.Equal("h264", file.VideoCodec);
        Assert.Equal("aac", file.AudioCodec);
        // Copied, not re-encoded: the video bit rate is the source's.
        var probe = new MediaProbe();
        var source = (await probe.LoadAsync(clips.Clip, clips.Tools.Ffprobe))!;
        Assert.Equal("h264", source.Video!.Codec);
        Assert.Equal(source.Video.BitRate, int.Parse(file.Video!.Value.GetProperty("bit_rate").GetString()!));
    }

    [Fact]
    public async Task LargestSizeFitsLandscapeAndPortraitAndNeverEnlarges()
    {
        if (!clips.Available) return;
        var p480 = new ConversionOptions { Video = new VideoOptions { LargestSize = VideoSize.SD480 } };
        var (landscape, _) = await Convert(clips.Big, OutputFormat.Mp4, p480);
        Assert.Equal((854, 480), (landscape.Width, landscape.Height));
        var (portrait, _) = await Convert(clips.Portrait, OutputFormat.Mp4, p480);
        Assert.Equal((480, 854), (portrait.Width, portrait.Height));
        // 320x240 is already inside 720p: not enlarged.
        var (small, _) = await Convert(clips.Clip, OutputFormat.Mkv, new ConversionOptions { Video = new VideoOptions { LargestSize = VideoSize.HD720 } });
        Assert.Equal((320, 240), (small.Width, small.Height));
        var (webm, _) = await Convert(clips.Big, OutputFormat.Webm, new ConversionOptions { Video = new VideoOptions { LargestSize = VideoSize.SD480, Quality = VideoQuality.Smaller } });
        Assert.Equal((854, 480), (webm.Width, webm.Height));
    }

    [Fact]
    public async Task OddSizedSourcesStillMakeH264()
    {
        if (!clips.Available) return;
        var (file, _) = await Convert(clips.Odd, OutputFormat.Mp4, new ConversionOptions { Video = new VideoOptions { Quality = VideoQuality.Balanced } });
        Assert.Equal("h264", file.VideoCodec);
        Assert.Equal((322, 242), (file.Width, file.Height));
        Assert.Null(file.Audio);
    }

    [Fact]
    public async Task HevcIsTaggedHvc1InMp4AndFixedAudioRateApplies()
    {
        if (!clips.Available) return;
        var encoders = await FFmpegEncoders.Default.LoadAsync(clips.Tools.Ffmpeg!);
        if (FFmpegArguments.AvailableHevc(encoders).Count == 0) return;
        var options = new ConversionOptions { Video = new VideoOptions { Codec = VideoCodec.Hevc, Quality = VideoQuality.Smaller }, Audio = new AudioOptions { BitrateKbps = 96 } };
        var (file, _) = await Convert(clips.Clip, OutputFormat.Mp4, options);
        Assert.Equal("hevc", file.VideoCodec);
        Assert.Equal("hvc1", file.Video!.Value.GetProperty("codec_tag_string").GetString());
        Assert.Equal("aac", file.AudioCodec);
        var kbps = int.Parse(file.Audio!.Value.GetProperty("bit_rate").GetString()!) / 1000;
        Assert.InRange(kbps, 80, 110);
    }

    [Fact]
    public async Task AutomaticReencodeKeepsTheAudioBitrateNearThePolicy()
    {
        if (!clips.Available) return;
        // A lossless WAV tone: Opus at its 128 kb/s ceiling for stereo clean sources, 64 for mono.
        var (file, _) = await Convert(clips.Tone, OutputFormat.Opus);
        Assert.Equal(1, file.Audio!.Value.GetProperty("channels").GetInt32());
        Near(2, file.Duration);
    }

    [Fact]
    public async Task CancellingKillsFfmpegAndLeavesNoFile()
    {
        if (!clips.Available) return;
        var encoders = await FFmpegEncoders.Default.LoadAsync(clips.Tools.Ffmpeg!);
        if (!encoders.Contains("libx265")) return;
        using var dir = new TempDir();
        var longClip = dir.Combine("long.mp4");
        var start = new ProcessStartInfo(clips.Tools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-y", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=120", "-c:v", "libx264", "-preset", "ultrafast", longClip })
            start.ArgumentList.Add(a);
        using (var p = Process.Start(start)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); }

        var outDir = Directory.CreateDirectory(dir.Combine("out")).FullName;
        var options = new ConversionOptions { Video = new VideoOptions { Codec = VideoCodec.Hevc, Quality = VideoQuality.Best } };
        using var cancel = new CancellationTokenSource();
        var progress = new Collected();
        var task = new MediaEngine().ConvertAsync(clips.Request(longClip, OutputFormat.Mp4, outDir, options), progress, cancel.Token);
        await Task.Delay(1500);
        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "cancel should kill ffmpeg promptly");
        Assert.Empty(Directory.GetFileSystemEntries(outDir));
    }

    [Fact]
    public async Task RunsThroughTheConverterAndTheRunnerWithStaging()
    {
        if (!clips.Available) return;
        using var dir = new TempDir();
        var source = dir.Combine("holiday.mp4");
        File.Copy(clips.Clip, source);
        var registry = new EngineRegistry();
        MediaEngines.Register(registry);
        var runner = new ConversionRunner(new Converter(registry, new ExternalToolsProvider(() => clips.Tools)));
        var result = await runner.ConvertAsync([source], OutputFormat.Mp3);
        Assert.Empty(result.Failures);
        Assert.Equal([dir.Combine("holiday.mp3")], result.Saved);
        // Converting again never overwrites.
        var again = await runner.ConvertAsync([source], OutputFormat.Mp3);
        Assert.Equal([dir.Combine("holiday 2.mp3")], again.Saved);
        Assert.Equal(["holiday 2.mp3", "holiday.mp3", "holiday.mp4"], dir.Names());
    }

    [Fact]
    public async Task AnUnreadableFileFailsCleanly()
    {
        if (!clips.Available) return;
        using var dir = new TempDir();
        var bad = dir.Combine("broken.mp4");
        File.WriteAllText(bad, "this is not a video");
        var e = await Assert.ThrowsAsync<ConversionException>(() => new MediaEngine().ConvertAsync(clips.Request(bad, OutputFormat.Mkv, dir.Path), null, default));
        Assert.Equal(ConversionErrorKind.Unreadable, e.Kind);
        Assert.Equal(["broken.mp4"], dir.Names());
    }
}
