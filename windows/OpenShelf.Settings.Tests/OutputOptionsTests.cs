using OpenShelf.Settings;

namespace OpenShelf.Settings.Tests;

public sealed class OutputOptionsTests
{
    [Theory]
    [InlineData("mp4", true, true, true, false, false, false)]
    [InlineData("mov", true, true, true, false, false, false)]
    [InlineData("mkv", true, true, true, false, false, false)]
    [InlineData("webm", false, true, true, false, false, false)]
    [InlineData("avi", false, true, true, false, false, false)]
    [InlineData("wmv", false, true, true, false, false, false)]
    [InlineData("m4a", false, false, true, false, false, false)]
    [InlineData("mp3", false, false, true, false, false, false)]
    [InlineData("ogg", false, false, true, false, false, false)]
    [InlineData("opus", false, false, true, false, false, false)]
    [InlineData("wma", false, false, true, false, false, false)]
    [InlineData("jpg", false, false, false, true, true, false)]
    [InlineData("pdf-jpeg", false, false, false, true, false, false)]
    [InlineData("heic", false, false, false, true, true, false)]
    [InlineData("avif", false, false, false, true, true, false)]
    [InlineData("webp", false, false, false, true, false, false)]
    [InlineData("gif", false, false, false, false, false, true)]
    public void RowsPerFormat(string id, bool codec, bool video, bool audio, bool quality, bool metadata, bool gif) =>
        Assert.Equal(new OutputOptions(codec, video, audio, quality, metadata, gif), OutputOptions.For(id));

    [Theory]
    [InlineData("png")]
    [InlineData("pdf")]
    [InlineData("zip")]
    [InlineData("")]
    public void OtherFormatsHaveNothingToChoose(string id) => Assert.True(OutputOptions.For(id).IsEmpty);

    [Fact]
    public void UseAsDefaultKeepsTheRestOfSettingsOutput()
    {
        var stored = new OutputSettings
        {
            Video = new VideoOutput { Codec = VideoCodec.H264, Quality = VideoQuality.Best },
            Audio = new AudioOutput { Kbps = 128 },
            Image = new ImageOutput { Quality = 0.8, KeepsMetadata = false },
            Gif = new GifOutput { FrameRate = 24 },
        };
        // The webm window shows video quality, size and audio, but not the codec.
        var shown = new OutputSettings
        {
            Video = new VideoOutput { Codec = VideoCodec.Hevc, Quality = VideoQuality.Custom, CustomMbps = 3, MaxSize = VideoMaxSize.P720 },
            Audio = new AudioOutput { Kbps = 320 },
            Image = new ImageOutput(),
            Gif = new GifOutput(),
        };
        var merged = stored.Applying(OutputOptions.For("webm"), shown);
        Assert.Equal(VideoCodec.H264, merged.Video.Codec);               // not shown: kept
        Assert.Equal(VideoQuality.Custom, merged.Video.Quality);
        Assert.Equal(3, merged.Video.CustomMbps);
        Assert.Equal(VideoMaxSize.P720, merged.Video.MaxSize);
        Assert.Equal(320, merged.Audio.Kbps);
        Assert.Equal(stored.Image, merged.Image);
        Assert.Equal(stored.Gif, merged.Gif);

        var jpegFromPdf = stored.Applying(OutputOptions.For("pdf-jpeg"), shown with { Image = new ImageOutput { Quality = 0.5, KeepsMetadata = true } });
        Assert.Equal(0.5, jpegFromPdf.Image.Quality);
        Assert.False(jpegFromPdf.Image.KeepsMetadata);                   // metadata row isn't shown for PDF pages
        Assert.Equal(stored.Video, jpegFromPdf.Video);
    }

    [Fact]
    public void WatchedFolderActionsIncludeTools()
    {
        var ids = WatchedFolderList.Actions.Select(a => a.Id).ToList();
        foreach (var id in new[] { "tool.compress", "tool.removebg", "tool.ocr", "tool.readqr", "tool.mute", "convert.jpg" })
            Assert.Contains(id, ids);
    }
}
