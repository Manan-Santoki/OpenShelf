using OpenShelf.Settings;

namespace OpenShelf.Settings.Tests;

public sealed class OutputSettingsTests
{
    private static OutputSettings Everything => new()
    {
        Video = new VideoOutput { Codec = VideoCodec.Hevc, Quality = VideoQuality.Custom, CustomMbps = 2.5, MaxSize = VideoMaxSize.Uhd4K },
        Audio = new AudioOutput { Kbps = 256 },
        Image = new ImageOutput { Quality = 0.7, KeepsMetadata = false },
        Gif = new GifOutput { Width = GifWidth.Original, FrameRate = 24 },
    };

    [Fact]
    public void RoundTripsThroughJson()
    {
        Assert.Equal(Everything, OutputSettings.Decode(Everything.Stored));
        Assert.Equal(OutputSettings.Automatic, OutputSettings.Decode(OutputSettings.Automatic.Stored));
    }

    [Fact]
    public void DefaultsAreTodaysChoices()
    {
        var s = OutputSettings.Automatic;
        Assert.True(s.Video.IsAutomatic);
        Assert.Equal(8, s.Video.CustomMbps);
        Assert.Null(s.Audio.Kbps);
        Assert.Null(s.Image.Quality);
        Assert.True(s.Image.KeepsMetadata);
        Assert.Equal(720, GifOutput.MaxWidth(s.Gif.Width));
        Assert.Equal(12, s.Gif.FrameRate);
        Assert.False(s.ReencodesVideo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void NothingOrSomethingUnreadableMeansAutomatic(string? stored) =>
        Assert.Equal(OutputSettings.Automatic, OutputSettings.Decode(stored));

    [Fact]
    public void MissingOrOddValuesKeepTheirDefaults()
    {
        var partial = OutputSettings.Decode("""{"video": {"maxSize": "p720"}, "gif": {"frameRate": 15}}""");
        Assert.Equal(VideoMaxSize.P720, partial.Video.MaxSize);
        Assert.Equal(VideoCodec.Automatic, partial.Video.Codec);
        Assert.Equal(VideoQuality.Automatic, partial.Video.Quality);
        Assert.Equal(15, partial.Gif.FrameRate);
        Assert.Equal(GifWidth.W720, partial.Gif.Width);
        Assert.Null(partial.Audio.Kbps);
        Assert.True(partial.Image.KeepsMetadata);

        var odd = OutputSettings.Decode("""{"video": {"codec": "av1", "quality": "best", "customMbps": 9000}, "audio": {"kbps": 200}, "image": {"quality": 0.13}, "gif": {"width": "480", "frameRate": 60}}""");
        Assert.Equal(VideoCodec.Automatic, odd.Video.Codec);
        Assert.Equal(VideoQuality.Best, odd.Video.Quality);
        Assert.Equal(200, odd.Video.CustomMbps);
        Assert.Null(odd.Audio.Kbps);
        Assert.Equal(0.4, odd.Image.Quality);
        Assert.Equal(GifWidth.W480, odd.Gif.Width);
        Assert.Equal(12, odd.Gif.FrameRate);
        Assert.Equal(0.9, OutputSettings.Decode("""{"image": {"quality": 0.86}}""").Image.Quality);
    }

    [Fact]
    public void WrongTypesFallBack()
    {
        var s = OutputSettings.Decode("""{"video": "x", "audio": {"kbps": "128"}, "image": {"keepsMetadata": 3}, "gif": []}""");
        Assert.Equal(OutputSettings.Automatic, s);
    }

    [Fact]
    public void EachSettingSaysWhetherMp4sAreReencoded()
    {
        var s = OutputSettings.Automatic;
        Assert.False((s with { Image = new ImageOutput { Quality = 0.5 } }).ReencodesVideo);
        Assert.False((s with { Gif = new GifOutput { FrameRate = 24 } }).ReencodesVideo);
        Assert.True((s with { Audio = new AudioOutput { Kbps = 128 } }).ReencodesVideo);
        Assert.True((s with { Video = new VideoOutput { MaxSize = VideoMaxSize.P480 } }).ReencodesVideo);
        Assert.True((s with { Video = new VideoOutput { Codec = VideoCodec.H264 } }).ReencodesVideo);
    }

    [Fact]
    public void Helpers()
    {
        Assert.Equal(8_000_000, OutputSettings.Automatic.Video.CustomBitRate);
        Assert.Equal(1.5, VideoOutput.VideoFactor(VideoQuality.Automatic));
        Assert.Null(VideoOutput.VideoFactor(VideoQuality.Custom));
        Assert.Equal(0.8, VideoOutput.SourceShare(VideoQuality.Smaller));
        Assert.Equal((1280, 720), VideoOutput.Bounds(VideoMaxSize.P720));
        Assert.Equal("88", new ImageOutput().WebPQuality);
        Assert.Equal("70", new ImageOutput { Quality = 0.7 }.WebPQuality);
        Assert.Equal(0.9, new ImageOutput().QualityOr(0.9));
    }

    [Fact]
    public void ApplyingTakesOnlyTheCoveredParts()
    {
        var applied = OutputSettings.Automatic.Applying(new OutputOptions(Gif: true, ImageMetadata: true), Everything);
        Assert.Equal(Everything.Gif, applied.Gif);
        Assert.False(applied.Image.KeepsMetadata);
        Assert.Null(applied.Image.Quality);
        Assert.True(applied.Video.IsAutomatic);
        Assert.True(new OutputOptions().IsEmpty);
    }

    [Fact]
    public void SliderMapsLogarithmically()
    {
        Assert.Equal(0, MbpsSlider.ToPosition(0.1), 6);
        Assert.Equal(1, MbpsSlider.ToPosition(200), 6);
        Assert.Equal(8, MbpsSlider.Round(MbpsSlider.FromPosition(MbpsSlider.ToPosition(8))));
    }
}
