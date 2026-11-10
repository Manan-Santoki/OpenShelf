using OpenShelf.Convert;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.Media.Tests;

/// <summary>The tools on real clips made with ffmpeg's testsrc and sine sources, checked with ffprobe. Skipped (and shown as skipped)
/// when ffmpeg or ffprobe is missing.</summary>
public class RealMediaTests
{
    private static MediaEditor Editor => Clips.Editor();

    private static void AssertDuration(string path, double expected, double tolerance = 0.2) =>
        Assert.InRange(Clips.Probe(path).Duration, expected - tolerance, expected + tolerance);

    [RequiresFfmpegFact]
    public async Task GetInfoDescribesTheFile()
    {
        using var dir = Clips.Copy(Clips.Video, Clips.Portrait, Clips.Wav);
        var video = await Editor.GetInfoAsync(dir.Combine(Clips.Video));
        Assert.True(video.HasVideo && video.HasAudio);
        Assert.Equal((320, 240), video.DisplaySize);
        Assert.InRange(video.Duration, 3.8, 4.2);
        var portrait = await Editor.GetInfoAsync(dir.Combine(Clips.Portrait));
        Assert.Equal((160, 320), portrait.DisplaySize);
        Assert.Equal(90, portrait.Rotation);
        var wav = await Editor.GetInfoAsync(dir.Combine(Clips.Wav));
        Assert.False(wav.HasVideo);
        Assert.Null(wav.DisplaySize);
    }

    [RequiresFfmpegFact]
    public async Task TrimFromTheStartCopiesTheStreams()
    {
        using var dir = Clips.Copy(Clips.Video);
        var progress = new Collected();
        var result = await Editor.TrimAsync(dir.Combine(Clips.Video), 0, 2.5, progress);

        Assert.Equal(dir.Combine("video Trimmed.mp4"), result);
        AssertDuration(result, 2.5, 0.3);
        var probed = Clips.Probe(result);
        Assert.Equal("h264", probed.Video!.Codec);
        Assert.Equal("aac", probed.Audio[0].Codec);
        Assert.Equal((320, 240), probed.Video.DisplaySize);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(["video Trimmed.mp4", "video.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task TrimAtAKeyframeKeepsTheSourcePicture()
    {
        using var dir = Clips.Copy(Clips.Video);
        // The fixture has a keyframe every second, so 1.0 is exact without re-encoding.
        var result = await Editor.TrimAsync(dir.Combine(Clips.Video), 1.0, 3.0);
        AssertDuration(result, 2.0, 0.3);
        Assert.Equal("h264", Clips.VideoCodec(result));
        Assert.Equal(["video Trimmed.mp4", "video.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task TrimOffAKeyframeReencodesSoItStartsExactly()
    {
        using var dir = Clips.Copy(Clips.Video);
        var result = await Editor.TrimAsync(dir.Combine(Clips.Video), 1.5, 3.5);
        // A copy would have started at 1.0 and run 2.5 s; the re-encode is exactly 2.0.
        AssertDuration(result, 2.0, 0.12);
        var probed = Clips.Probe(result);
        Assert.Equal("h264", probed.Video!.Codec);
        Assert.True(probed.HasAudio);
    }

    [RequiresFfmpegFact]
    public async Task TrimAudioKeepsItsFormat()
    {
        using var dir = Clips.Copy(Clips.Wav, Clips.Mp3, Clips.M4a);
        var wav = await Editor.TrimAsync(dir.Combine(Clips.Wav), 0.5, 2.0);
        Assert.Equal(dir.Combine("tone Trimmed.wav"), wav);
        AssertDuration(wav, 1.5, 0.1);
        Assert.Equal("pcm_s16le", Clips.Probe(wav).Audio[0].Codec);

        var mp3 = await Editor.TrimAsync(dir.Combine(Clips.Mp3), 1.0, 2.0);
        Assert.EndsWith(".mp3", mp3);
        AssertDuration(mp3, 1.0, 0.15);
        Assert.Equal("mp3", Clips.Probe(mp3).Audio[0].Codec);

        var m4a = await Editor.TrimAsync(dir.Combine(Clips.M4a), 0, 1.0);
        Assert.EndsWith(".m4a", m4a);
        AssertDuration(m4a, 1.0, 0.15);
        Assert.False(Clips.Probe(m4a).Video is not null);
    }

    [RequiresFfmpegFact]
    public async Task ChangeSpeedOfVideoScalesTheLengthAndKeepsSound()
    {
        using var dir = Clips.Copy(Clips.Video);
        var fast = await Editor.ChangeSpeedAsync(dir.Combine(Clips.Video), 2, keepsPitch: true);
        Assert.Equal(dir.Combine("video 2x.mp4"), fast);
        AssertDuration(fast, 2.0, 0.3);
        Assert.True(Clips.Probe(fast).HasAudio);
        Assert.Equal((320, 240), Clips.Probe(fast).Video!.DisplaySize);

        var slow = await Editor.ChangeSpeedAsync(dir.Combine(Clips.Video), 0.5, keepsPitch: false);
        Assert.Equal(dir.Combine("video 0.5x.mp4"), slow);
        AssertDuration(slow, 8.0, 0.4);
        Assert.True(Clips.Probe(slow).HasAudio);
    }

    [RequiresFfmpegFact]
    public async Task ChangeSpeedOfAudioMakesAnM4aAtTheLimits()
    {
        using var dir = Clips.Copy(Clips.Wav);
        var quad = await Editor.ChangeSpeedAsync(dir.Combine(Clips.Wav), 4, keepsPitch: true);
        Assert.Equal(dir.Combine("tone 4x.m4a"), quad);
        AssertDuration(quad, 0.75, 0.15);
        Assert.Null(Clips.Probe(quad).Video);

        var quarter = await Editor.ChangeSpeedAsync(dir.Combine(Clips.Wav), 0.25, keepsPitch: false);
        Assert.Equal(dir.Combine("tone 0.25x.m4a"), quarter);
        AssertDuration(quarter, 12.0, 0.5);
    }

    [RequiresFfmpegFact]
    public async Task CropByFractionsGivesEvenDimensionsAndKeepsSound()
    {
        using var dir = Clips.Copy(Clips.Video);
        var result = await Editor.CropAsync(dir.Combine(Clips.Video), new CropFraction(0.25, 0.25, 0.5, 0.5));
        Assert.Equal(dir.Combine("video Cropped.mp4"), result);
        var probed = Clips.Probe(result);
        Assert.Equal((160, 120), probed.Video!.DisplaySize);
        Assert.True(probed.HasAudio);
        AssertDuration(result, 4.0, 0.3);

        // A crop that would come out odd is made even.
        var odd = await Editor.CropAsync(dir.Combine(Clips.Video), new PixelRect(3, 5, 101, 77));
        var size = Clips.Probe(odd).Video!.DisplaySize;
        Assert.Equal((100, 76), size);
    }

    [RequiresFfmpegFact]
    public async Task CropAppliesToThePictureAsPlayersShowIt()
    {
        using var dir = Clips.Copy(Clips.Portrait);
        // The portrait clip is stored 320x160 but shown 160x320: "the top half" is 160x160.
        var result = await Editor.CropAsync(dir.Combine(Clips.Portrait), new CropFraction(0, 0, 1, 0.5));
        var probed = Clips.Probe(result);
        Assert.Equal((160, 160), probed.Video!.DisplaySize);
        Assert.Equal(0, probed.Video.Rotation);
    }

    [RequiresFfmpegFact]
    public async Task TurningKeepsTheFramesAndOnlyChangesTheAngle()
    {
        using var dir = Clips.Copy(Clips.Video);
        var source = dir.Combine(Clips.Video);

        var right = await Editor.RotateAsync(source, VideoRotation.Right);
        Assert.Equal(dir.Combine("video Rotated.mp4"), right);
        var probed = Clips.Probe(right);
        Assert.Equal(90, probed.Video!.Rotation);
        Assert.Equal((320, 240), (probed.Video.Width, probed.Video.Height)); // frames untouched
        Assert.Equal((240, 320), probed.Video.DisplaySize);
        Assert.True(probed.HasAudio);

        var left = await Editor.RotateAsync(source, VideoRotation.Left);
        Assert.Equal(270, Clips.Probe(left).Video!.Rotation);

        var half = await Editor.RotateAsync(source, VideoRotation.UpsideDown);
        Assert.Equal(180, Clips.Probe(half).Video!.Rotation);
        AssertDuration(half, 4.0, 0.3);
        Assert.Equal(["video Rotated 2.mp4", "video Rotated 3.mp4", "video Rotated.mp4", "video.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task TurnsAddToTheRotationAVideoAlreadyHas()
    {
        using var dir = Clips.Copy(Clips.Portrait);
        var result = await Editor.RotateAsync(dir.Combine(Clips.Portrait), VideoRotation.Left);
        // Was turned 90 clockwise; turned back 90 anticlockwise it is upright.
        var video = Clips.Probe(result).Video!;
        Assert.Equal(0, video.Rotation);
        Assert.Equal((320, 160), video.DisplaySize);
    }

    [RequiresFfmpegFact]
    public async Task FlippingReencodesTheFrames()
    {
        using var dir = Clips.Copy(Clips.Video);
        var original = await File.ReadAllBytesAsync(dir.Combine(Clips.Video));
        var result = await Editor.RotateAsync(dir.Combine(Clips.Video), VideoRotation.FlipHorizontal);
        var video = Clips.Probe(result).Video!;
        Assert.Equal(0, video.Rotation);
        Assert.Equal((320, 240), video.DisplaySize);
        Assert.Equal("h264", video.Codec);
        Assert.NotEqual(original.Length, new FileInfo(result).Length);
        AssertDuration(result, 4.0, 0.3);
    }

    [RequiresFfmpegFact]
    public async Task MuteRemovesTheSound()
    {
        using var dir = Clips.Copy(Clips.Video);
        var result = await Editor.MuteAsync(dir.Combine(Clips.Video));
        Assert.Equal(dir.Combine("video Muted.mp4"), result);
        var probed = Clips.Probe(result);
        Assert.False(probed.HasAudio);
        Assert.Equal("h264", probed.Video!.Codec);
        AssertDuration(result, 4.0, 0.3);
    }

    [RequiresFfmpegFact]
    public async Task SplitIntoEqualPartsWithoutReencoding()
    {
        using var dir = Clips.Copy(Clips.Video);
        var path = dir.Combine(Clips.Video);
        var info = await Editor.GetInfoAsync(path);
        var segments = SplitPlan.EqualParts(info.Duration, 2);
        var preview = SplitPlan.Parts(path, segments, info.HasVideo);
        var progress = new Collected();

        var outputs = await Editor.SplitAsync(path, segments, progress);

        Assert.Equal(preview.Select(p => p.FileName), outputs.Select(Path.GetFileName));
        Assert.Equal(["video Part 1.mp4", "video Part 2.mp4"], outputs.Select(Path.GetFileName));
        var total = 0.0;
        foreach (var output in outputs)
        {
            var probed = Clips.Probe(output);
            Assert.Equal("h264", probed.Video!.Codec);
            Assert.True(probed.HasAudio);
            total += probed.Duration;
        }
        // Parts begin at the keyframe at or before their time, so they can be a little longer than asked, never shorter overall.
        Assert.InRange(total, info.Duration - 0.3, info.Duration + 1.2);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(["video Part 1.mp4", "video Part 2.mp4", "video.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task SplitAtChosenTimes()
    {
        using var dir = Clips.Copy(Clips.Wav);
        var path = dir.Combine(Clips.Wav);
        var segments = SplitPlan.At("0:01, 0:02", 3);
        var outputs = await Editor.SplitAsync(path, segments);
        Assert.Equal(["tone Part 1.wav", "tone Part 2.wav", "tone Part 3.wav"], outputs.Select(Path.GetFileName));
        foreach (var output in outputs) AssertDuration(output, 1.0, 0.1);
    }

    [RequiresFfmpegFact]
    public async Task MergeVideosAtTheFirstClipsSizeWithLetterboxingAndUprightTurning()
    {
        using var dir = Clips.Copy(Clips.Video, Clips.Silent, Clips.Portrait);
        var paths = new[] { Clips.Video, Clips.Portrait, Clips.Silent }.Select(dir.Combine).ToList();
        var progress = new Collected();

        var result = await Editor.MergeAsync(paths, progress);

        Assert.Equal(dir.Combine("Merged.mp4"), result);
        var probed = Clips.Probe(result);
        Assert.Equal((320, 240), probed.Video!.DisplaySize);
        Assert.Equal(0, probed.Video.Rotation);
        Assert.True(probed.HasAudio); // the silent clip got silence, so the sound runs through
        Assert.InRange(probed.Duration, 4 + 2 + 4 - 0.5, 4 + 2 + 4 + 0.5);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(new[] { "Merged.mp4", "portrait.mp4", "silent.mp4", "video.mp4" }, dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task MergedVideosFollowTheOrderGiven()
    {
        using var dir = Clips.Copy(Clips.Video, Clips.Portrait);
        // The portrait clip first: the result is portrait, and a second merge does not overwrite the first.
        var result = await Editor.MergeAsync([dir.Combine(Clips.Portrait), dir.Combine(Clips.Video)]);
        Assert.Equal((160, 320), Clips.Probe(result).Video!.DisplaySize);
        var again = await Editor.MergeAsync([dir.Combine(Clips.Video), dir.Combine(Clips.Video)]);
        Assert.Equal(dir.Combine("Merged 2.mp4"), again);
        Assert.Equal((320, 240), Clips.Probe(again).Video!.DisplaySize);
        AssertDuration(again, 8.0, 0.5);
    }

    [RequiresFfmpegFact]
    public async Task MergeAudioIntoOneM4a()
    {
        using var dir = Clips.Copy(Clips.Wav, Clips.Mp3, Clips.M4a);
        var result = await Editor.MergeAsync([dir.Combine(Clips.Mp3), dir.Combine(Clips.Wav), dir.Combine(Clips.M4a)]);
        Assert.Equal(dir.Combine("Merged.m4a"), result);
        var probed = Clips.Probe(result);
        Assert.Null(probed.Video);
        Assert.Equal("aac", probed.Audio[0].Codec);
        AssertDuration(result, 9.0, 0.4);
    }

    [RequiresFfmpegFact]
    public async Task CompressMakesASmallerMp4AndEstimatesItsSize()
    {
        using var dir = Clips.Copy(Clips.Heavy);
        var path = dir.Combine(Clips.Heavy);
        var editor = Editor;
        var source = await editor.LoadCompressSourceAsync(path);
        var estimate = VideoCompressionPlan.Estimate(source, CompressResolution.P480, CompressQuality.Low);
        var progress = new Collected();

        var result = await editor.CompressAsync(path, CompressResolution.P480, CompressQuality.Low, progress);

        Assert.Equal(dir.Combine("heavy Compressed.mp4"), result);
        var written = new FileInfo(result!).Length;
        Assert.True(written < new FileInfo(path).Length / 2, $"{written} vs {new FileInfo(path).Length}");
        Assert.InRange(written, estimate * 0.4, estimate * 2.5);
        var probed = Clips.Probe(result!);
        Assert.Equal("h264", probed.Video!.Codec);
        Assert.Equal((640, 480), probed.Video.DisplaySize); // 480p never enlarges and fits inside 854x480
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(["heavy Compressed.mp4", "heavy.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task CompressScalesDownToTheChosenResolution()
    {
        using var dir = Clips.Copy(Clips.Heavy);
        // 640x480 into a 1280x720 box is left alone; make a taller target by choosing 480p on the short edge.
        var result = await Editor.CompressAsync(dir.Combine(Clips.Heavy), CompressResolution.P480, CompressQuality.Medium);
        Assert.NotNull(result);
        var size = Clips.Probe(result!).Video!.DisplaySize;
        Assert.True(size.Width <= 854 && size.Height <= 480);
        Assert.Equal(0, size.Width % 2);
    }

    [RequiresFfmpegFact]
    public async Task CompressKeepsNothingWhenTheResultIsNotSmaller()
    {
        using var dir = Clips.Copy(Clips.Tiny);
        var result = await Editor.CompressAsync(dir.Combine(Clips.Tiny), CompressResolution.Original, CompressQuality.High);
        Assert.Null(result);
        Assert.Equal(["tiny.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task ExtractsAnUprightFrameAsPng()
    {
        using var dir = Clips.Copy(Clips.Video, Clips.Portrait);
        var frame = await Editor.ExtractFrameAsync(dir.Combine(Clips.Video), 1.5);
        Assert.Equal((320, 240), PngSize(frame));

        var small = await Editor.ExtractFrameAsync(dir.Combine(Clips.Video), 1.5, maxSize: 100);
        Assert.Equal((100, 75), PngSize(small));

        // Never enlarged.
        Assert.Equal((320, 240), PngSize(await Editor.ExtractFrameAsync(dir.Combine(Clips.Video), 0, maxSize: 1000)));

        // Turned upright, like players show it, and clamped when asked past the end.
        var portrait = await Editor.ExtractFrameAsync(dir.Combine(Clips.Portrait), 99);
        Assert.Equal((160, 320), PngSize(portrait));

        var poster = await Editor.ExtractPosterAsync(dir.Combine(Clips.Video), 160);
        Assert.Equal((160, 120), PngSize(poster));
        Assert.Equal(1.0, MediaEditor.PosterTime(10));
        Assert.Equal(0.75, MediaEditor.PosterTime(1.5));
        Assert.Equal(["portrait.mp4", "video.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task ExtractsAThumbnailStrip()
    {
        using var dir = Clips.Copy(Clips.Video);
        var strip = await Editor.ExtractThumbnailStripAsync(dir.Combine(Clips.Video), 6, 80);
        Assert.Equal(6, strip.Count);
        Assert.All(strip, png => Assert.Equal((80, 60), PngSize(png)));
        Assert.Empty(await Editor.ExtractThumbnailStripAsync(dir.Combine(Clips.Video), 0));
    }

    [RequiresFfmpegFact]
    public async Task ExtractsWaveformPeaks()
    {
        using var dir = Clips.Copy(Clips.Wav, Clips.Silent);
        var peaks = await Editor.ExtractWaveformAsync(dir.Combine(Clips.Wav), 50);
        Assert.Equal(50, peaks.Length);
        Assert.All(peaks, p => Assert.InRange(p, 0f, 1f));
        Assert.True(peaks.Max() > 0.1f);
        Assert.Empty(await Editor.ExtractWaveformAsync(dir.Combine(Clips.Silent), 50));
    }

    [RequiresFfmpegFact]
    public async Task AnUnreadableFileFailsCleanlyAndLeavesNothing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("broken.mp4"), "this is not a video");
        await Assert.ThrowsAsync<ConversionException>(() => Editor.TrimAsync(dir.Combine("broken.mp4"), 0, 1));
        await Assert.ThrowsAsync<ConversionException>(() => Editor.MuteAsync(dir.Combine("missing.mp4")));
        Assert.Equal(["broken.mp4"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task VideoToolsRefuseAudioFilesAndAudioToolsAcceptThem()
    {
        using var dir = Clips.Copy(Clips.Wav);
        var wav = dir.Combine(Clips.Wav);
        var failure = await Assert.ThrowsAsync<ConversionException>(() => Editor.CropAsync(wav, CropFraction.Full));
        Assert.Contains("no video", failure.Message);
        await Assert.ThrowsAsync<ConversionException>(() => Editor.MuteAsync(wav));
        await Assert.ThrowsAsync<ConversionException>(() => Editor.RotateAsync(wav, VideoRotation.Left));
        await Assert.ThrowsAsync<ConversionException>(() => Editor.CompressAsync(wav, CompressResolution.P720, CompressQuality.High));
        Assert.Equal(["tone.wav"], dir.Names());
    }

    [RequiresFfmpegFact]
    public async Task ARealCancelLeavesNothingBehind()
    {
        using var dir = Clips.Copy(Clips.Heavy);
        using var cts = new CancellationTokenSource();
        // A quarter speed makes 300 frames to encode: long enough to be cancelled while ffmpeg is running.
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor.ChangeSpeedAsync(dir.Combine(Clips.Heavy), 0.25, true, null, cts.Token));
        Assert.Equal(["heavy.mp4"], dir.Names());
    }

    /// <summary>Width and height from a PNG's IHDR.</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        int Read(int at) => png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
        return (Read(16), Read(20));
    }
}
