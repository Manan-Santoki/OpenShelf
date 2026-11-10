using OpenShelf.Convert;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.Media.Tests;

/// <summary>The arithmetic and argument building: no ffmpeg needed.</summary>
public class PlanTests
{
    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(62.5, "1:02.5")]
    [InlineData(59.97, "1:00.0")]
    [InlineData(3600, "60:00.0")]
    [InlineData(-4, "0:00.0")]
    public void FormatsTimes(double seconds, string expected) => Assert.Equal(expected, MediaTime.Format(seconds));

    [Theory]
    [InlineData("1:02.5", 62.5)]
    [InlineData("62.5", 62.5)]
    [InlineData("1:02", 62)]
    [InlineData(" 0:30 ", 30)]
    [InlineData("1:00:05", 3605)]
    public void ParsesTimes(string text, double expected) => Assert.Equal(expected, MediaTime.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1::2")]
    [InlineData("NaN")]
    [InlineData("1e3")]
    public void RejectsBadTimes(string text) => Assert.Null(MediaTime.Parse(text));

    [Fact]
    public void LengthFollowsWhatTheFieldsShow() => Assert.Equal(3.0, MediaTime.Length(1.04, 4.04), 6);

    [Fact]
    public void TrimRangeCutsTheSameSecondsOffEachEnd()
    {
        // 2 s off the start and 3 s off the end of a 20 s file leaves 2.0 - 17.0.
        var range = TrimRange.CuttingStartAndEnd(2, 3, 20);
        Assert.Equal((2.0, 17.0), range!.Value);
        // The same seconds off a 4 s file leave nothing: it is "cut away", not an empty export.
        Assert.Null(TrimRange.CuttingStartAndEnd(2, 3, 4));
        Assert.Equal((2.0, 3.0), TrimRange.Cut(2, 17, 20));
    }

    [Fact]
    public void TrimRangeKeepsTheVeryEnd()
    {
        // 9.97 s shows as the last tenth of a 9.98 s file, so the file's real end is used.
        Assert.Equal(9.98, TrimRange.ClampedEnd(9.97, 0, 9.98));
        Assert.Equal(0.1, TrimRange.ClampedEnd(0, 0, 10), 6);
        Assert.False(TrimRange.Changes(0, 10, 10));
        Assert.True(TrimRange.Changes(0.1, 10, 10));
        Assert.True(TrimRange.Changes(0, 9, 10));
        Assert.False(TrimRange.Changes(5, 5.02, 10));
    }

    [Fact]
    public void EqualPartsCoverTheFileAndAreClamped()
    {
        var parts = SplitPlan.EqualParts(10, 4);
        Assert.Equal(4, parts.Count);
        Assert.Equal(0, parts[0].Start);
        Assert.Equal(2.5, parts[0].End);
        Assert.Equal(10, parts[^1].End);
        Assert.Equal(2, SplitPlan.EqualParts(10, 1).Count);
        Assert.Equal(20, SplitPlan.EqualParts(100, 99).Count);
        Assert.Empty(SplitPlan.EqualParts(0, 3));
    }

    [Fact]
    public void EveryIntervalJoinsAShortTail()
    {
        var parts = SplitPlan.Every(30, 100);
        Assert.Equal([(0.0, 30.0), (30.0, 60.0), (60.0, 90.0), (90.0, 100.0)], parts.Select(p => (p.Start, p.End)));
        // A 0.1 s tail would be a useless part: it joins the one before.
        var tail = SplitPlan.Every(30, 90.1);
        Assert.Equal(3, tail.Count);
        Assert.Equal(90.1, tail[^1].End);
    }

    [Fact]
    public void EveryIntervalChecksItsLimits()
    {
        Assert.Equal(SplitPlanProblem.IntervalTooShort, Assert.Throws<SplitPlanException>(() => SplitPlan.Every(0.5, 10)).Problem);
        var many = Assert.Throws<SplitPlanException>(() => SplitPlan.Every(1, 500));
        Assert.Equal(SplitPlanProblem.TooMany, many.Problem);
        Assert.Contains("500", many.Message);
        Assert.Equal(100, SplitPlan.Every(1, 100).Count);
    }

    [Fact]
    public void ParsesTimeLists()
    {
        var parts = SplitPlan.At("0:30, 1:15", 120);
        Assert.Equal([(0.0, 30.0), (30.0, 75.0), (75.0, 120.0)], parts.Select(p => (p.Start, p.End)));
        // Order, duplicates, other separators and a cut at zero do not matter.
        var messy = SplitPlan.At("1:15;0:30\n0:30, 0", 120);
        Assert.Equal(3, messy.Count);
    }

    [Fact]
    public void TimeListErrorsSayWhatIsWrong()
    {
        var unreadable = Assert.Throws<SplitPlanException>(() => SplitPlan.At("0:30, soon", 120));
        Assert.Equal(SplitPlanProblem.Unreadable, unreadable.Problem);
        Assert.Contains("soon", unreadable.Message);
        var past = Assert.Throws<SplitPlanException>(() => SplitPlan.At("0:30, 5:00", 120));
        Assert.Equal(SplitPlanProblem.OutOfRange, past.Problem);
        Assert.Equal(SplitPlanProblem.NoCuts, Assert.Throws<SplitPlanException>(() => SplitPlan.At("", 120)).Problem);
        Assert.Equal(SplitPlanProblem.NoCuts, Assert.Throws<SplitPlanException>(() => SplitPlan.At("0", 120)).Problem);
        var tooMany = string.Join(",", Enumerable.Range(1, 100));
        Assert.Equal(SplitPlanProblem.TooMany, Assert.Throws<SplitPlanException>(() => SplitPlan.At(tooMany, 1000)).Problem);
    }

    [Fact]
    public void SameForAllKeepsOnlyTheTimesInsideEachFile()
    {
        // One list, files of different lengths: later times are left out; none inside leaves the file whole (no parts).
        Assert.Equal(3, SplitPlan.AtInside("0:30, 1:15", 120).Count);
        var short60 = SplitPlan.AtInside("0:30, 1:15", 60);
        Assert.Equal([(0.0, 30.0), (30.0, 60.0)], short60.Select(p => (p.Start, p.End)));
        Assert.Empty(SplitPlan.AtInside("0:30, 1:15", 20));
        Assert.Throws<SplitPlanException>(() => SplitPlan.AtInside("0:30, nope", 20));
    }

    [Fact]
    public void PreviewNamesTheParts()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Holiday.mp4");
        File.WriteAllText(dir.Combine("Holiday Part 2.mp4"), "taken");
        var parts = SplitPlan.Parts(path, SplitPlan.EqualParts(9, 3), hasVideo: true);
        Assert.Equal(["Holiday Part 1.mp4", "Holiday Part 2 2.mp4", "Holiday Part 3.mp4"], parts.Select(p => p.FileName));
        Assert.Equal(3.0, parts[0].Duration);
        // Sound keeps its own kind when it can be copied, and becomes M4A otherwise.
        Assert.Equal("Song Part 1.mp3", SplitPlan.Parts(dir.Combine("Song.mp3"), [new(0, 1)], hasVideo: false)[0].FileName);
        Assert.Equal("Song Part 1.m4a", SplitPlan.Parts(dir.Combine("Song.wma"), [new(0, 1)], hasVideo: false)[0].FileName);
        // Video containers other than MP4 and M4V become MOV, as on the Mac.
        Assert.Equal("Clip Part 1.mov", SplitPlan.Parts(dir.Combine("Clip.mkv"), [new(0, 1)], hasVideo: true)[0].FileName);
    }

    [Fact]
    public void CropFractionsKeepTheirShapeOnAnySize()
    {
        var top = new CropFraction(0, 0, 1, 0.5);
        Assert.Equal(new PixelRect(0, 0, 320, 120), top.ToPixels(320, 240));
        Assert.Equal(new PixelRect(0, 0, 1920, 540), top.ToPixels(1920, 1080));
        // Odd results are made even; the crop stays inside the frame.
        var odd = new CropFraction(0.33, 0.33, 0.5, 0.5).ToPixels(321, 241);
        Assert.Equal(0, odd.Width % 2);
        Assert.Equal(0, odd.Height % 2);
        Assert.True(odd.X + odd.Width <= 321 && odd.Y + odd.Height <= 241);
        Assert.True(CropFraction.Full.IsFullFrame);
        Assert.False(top.IsFullFrame);
        Assert.Equal(new CropFraction(0.25, 0.5, 0.5, 0.25), CropFraction.FromPixels(new PixelRect(80, 120, 160, 60), 320, 240));
        Assert.Equal(new PixelRect(318, 238, 2, 2), CropMath.Even(new PixelRect(400, 400, 50, 50), 320, 240));
    }

    [Fact]
    public void SpeedSuffixIsShort()
    {
        Assert.Equal("2x", MediaFormats.SpeedSuffix(2));
        Assert.Equal("0.5x", MediaFormats.SpeedSuffix(0.5));
        Assert.Equal("1.25x", MediaFormats.SpeedSuffix(1.25));
        Assert.Equal("0.33x", MediaFormats.SpeedSuffix(1.0 / 3));
    }

    [Theory]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(0.5, "atempo=0.5")]
    [InlineData(1.5, "atempo=1.5")]
    [InlineData(4, "atempo=2,atempo=2")]
    [InlineData(3, "atempo=2,atempo=1.5")]
    public void PitchKeepingSpeedChainsAtempo(double rate, string expected) => Assert.Equal(expected, FFmpegEdits.AtempoChain(rate));

    [Fact]
    public void WithoutPitchKeepingSoundFollowsSpeed() =>
        Assert.Equal("asetrate=96000,aresample=48000", FFmpegEdits.Varispeed(2, 48000));

    [Theory]
    [InlineData(0, VideoRotation.Right, 270)]
    [InlineData(0, VideoRotation.Left, 90)]
    [InlineData(0, VideoRotation.UpsideDown, 180)]
    [InlineData(90, VideoRotation.Right, 180)]
    [InlineData(90, VideoRotation.Left, 0)]
    [InlineData(270, VideoRotation.Right, 0)]
    public void TurnsAddToTheVideosOwnRotation(int existing, VideoRotation turn, int displayRotation)
    {
        var video = new ProbedVideo(320, 240, existing, "10/1", null, "h264", "yuv420p", null);
        Assert.Equal(displayRotation, FFmpegEdits.DisplayRotation(video, turn));
    }

    [Fact]
    public void OnlyAMirrorRewritesFrames()
    {
        Assert.True(VideoRotation.Left.IsLossless());
        Assert.True(VideoRotation.Right.IsLossless());
        Assert.True(VideoRotation.UpsideDown.IsLossless());
        Assert.False(VideoRotation.FlipHorizontal.IsLossless());
        Assert.Equal("Flip", VideoRotation.FlipHorizontal.Title());
    }

    [Fact]
    public void CompressSizesFollowTheMacRules()
    {
        // Fits inside 1280x720 by long and short edge, never enlarging, with even sides.
        Assert.Equal((1280, 720), VideoCompressionPlan.FitSize(1920, 1080, CompressResolution.P720.Bounds()));
        Assert.Equal((720, 1280), VideoCompressionPlan.FitSize(1080, 1920, CompressResolution.P720.Bounds()));
        Assert.Equal((640, 480), VideoCompressionPlan.FitSize(640, 480, CompressResolution.P1080.Bounds()));
        Assert.Equal((322, 242), VideoCompressionPlan.FitSize(322, 242, null));
        Assert.Equal((322, 242), VideoCompressionPlan.FitSize(323, 243, null));
    }

    [Fact]
    public void CompressEstimateShrinksWithResolutionAndQuality()
    {
        var source = new VideoSourceInfo(1920, 1080, 0, 60, 200_000_000) { FrameRate = 30, BitRate = 25_000_000, AudioTracks = [new(new SourceAudio(2, 256_000, AudioCodec.Aac), 48000)] };
        var high = VideoCompressionPlan.Estimate(source, CompressResolution.Original, CompressQuality.High);
        var medium = VideoCompressionPlan.Estimate(source, CompressResolution.Original, CompressQuality.Medium);
        var low = VideoCompressionPlan.Estimate(source, CompressResolution.Original, CompressQuality.Low);
        var small = VideoCompressionPlan.Estimate(source, CompressResolution.P480, CompressQuality.Low);
        Assert.True(high > medium && medium > low && low > small, $"{high} {medium} {low} {small}");
        Assert.True(high < source.FileSize);
        // 6 Mb/s for High at 1080p30, plus the AAC track, over 60 s, plus container overhead.
        var plan = new VideoCompressionPlan(source, CompressResolution.Original, CompressQuality.High);
        Assert.Equal(6_000_000, plan.VideoBitRate);
        Assert.Equal((1920, 1080), plan.DisplaySize);
    }

    [Fact]
    public void CompressNeverSpendsMoreThanTheSourceDid()
    {
        var source = new VideoSourceInfo(1920, 1080, 0, 10, 1_000_000) { BitRate = 2_000_000 };
        Assert.Equal(1_600_000, new VideoCompressionPlan(source, CompressResolution.Original, CompressQuality.High).VideoBitRate);
    }

    [Fact]
    public void CompressKeepsRotationInTheDisplaySizeAndCopiesSmallAac()
    {
        var source = new VideoSourceInfo(1920, 1080, 90, 10, 5_000_000)
        {
            AudioTracks = [new(new SourceAudio(2, 96_000, AudioCodec.Aac), 44100), new(new SourceAudio(2, 320_000, AudioCodec.Mp3), 44100)],
        };
        var plan = new VideoCompressionPlan(source, CompressResolution.P720, CompressQuality.Medium);
        Assert.Equal((1280, 720), plan.EncodedSize);
        Assert.Equal((720, 1280), plan.DisplaySize);
        Assert.True(plan.Audio[0].Passthrough);
        Assert.False(plan.Audio[1].Passthrough);
        Assert.True(plan.Audio[1].BitRate <= 128_000);
        var arguments = FFmpegEdits.Compress("in.mp4", plan, source);
        Assert.Equal("scale=720:1280,setsar=1", Arg(arguments, "-vf"));
        Assert.Equal("copy", Arg(arguments, "-c:a:0"));
        Assert.Equal("aac", Arg(arguments, "-c:a:1"));
        Assert.Equal("+faststart", Arg(arguments, "-movflags"));
    }

    [Fact]
    public void HdrKeepsTenBitHevcWhenItCanAndFallsBackWhenItCannot()
    {
        var source = new VideoSourceInfo(3840, 2160, 0, 10, 50_000_000) { IsHdr = true };
        Assert.Equal(PlanCodec.Hevc, new VideoCompressionPlan(source, CompressResolution.P1080, CompressQuality.High).Codec);
        Assert.Equal(PlanCodec.H264, new VideoCompressionPlan(source, CompressResolution.P1080, CompressQuality.High, hevcAvailable: false).Codec);
        var hdr = new ProbedVideo(3840, 2160, 0, "30/1", null, "hevc", "yuv420p10le", "smpte2084");
        Assert.Contains("libx265", FFmpegEdits.VideoEncoding(hdr));
        Assert.Contains("libx264", FFmpegEdits.VideoEncoding(hdr, hevc: false));
        Assert.Contains("hvc1", FFmpegEdits.VideoCopy(hdr));
    }

    [Fact]
    public void KeyframeListsAreRead()
    {
        const string packets = "0.000000,K__\n0.100000,___\n1.000000,K__\n1.100000,___\n";
        Assert.True(MediaEditor.KeyframeAt(packets, 1.0, 0.05));
        Assert.True(MediaEditor.KeyframeAt(packets, 1.03, 0.05));
        Assert.False(MediaEditor.KeyframeAt(packets, 1.1, 0.05));
        Assert.False(MediaEditor.KeyframeAt(packets, 0.5, 0.05));
        Assert.False(MediaEditor.KeyframeAt("", 1, 0.05));
    }

    [Fact]
    public void PeaksTakeTheLoudestSampleOfEachSlice()
    {
        short[] samples = [0, 100, -200, 50, 0, 0, 32767, 1];
        var peaks = MediaEditor.Peaks(samples, 4);
        Assert.Equal(4, peaks.Length);
        Assert.Equal(100f / short.MaxValue, peaks[0]);
        Assert.Equal(200f / short.MaxValue, peaks[1]);
        Assert.Equal(0f, peaks[2]);
        Assert.Equal(1f, peaks[3]);
        // Fewer samples than bars gives one bar per sample.
        Assert.Equal(2, MediaEditor.Peaks([5, 6], 10).Length);
        Assert.Empty(MediaEditor.Peaks([], 10));
    }

    [Fact]
    public void BatchProgressSpansAllFiles()
    {
        Assert.Equal(0.375, MediaBatch.Overall(0.5, 1, 4));
        Assert.Equal(1, MediaBatch.Overall(0.5, 0, 0));
        Assert.Equal(0.25, MediaBatch.Overall(-3, 1, 4));
    }

    [Fact]
    public void EditedVideosKeepMp4M4vAndMovOnly()
    {
        Assert.Equal("mp4", MediaFormats.VideoContainerExtension("a.MP4"));
        Assert.Equal("m4v", MediaFormats.VideoContainerExtension("a.m4v"));
        Assert.Equal("mov", MediaFormats.VideoContainerExtension("a.mkv"));
        Assert.Equal("mov", MediaFormats.VideoContainerExtension("a.mov"));
    }

    private static string Arg(IEnumerable<string> arguments, string flag)
    {
        var list = arguments.ToList();
        return list[list.IndexOf(flag) + 1];
    }
}
