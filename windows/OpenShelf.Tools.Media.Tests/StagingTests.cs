using OpenShelf.Convert;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.Media.Tests;

/// <summary>Hidden staging, cancel and failure, with a pretend ffmpeg.</summary>
public class StagingTests
{
    private const string Fake = @"C:\fake\ffmpeg.exe";
    private const string FakeProbe = @"C:\fake\ffprobe.exe";

    private const string VideoJson =
        "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":240,\"pix_fmt\":\"yuv420p\",\"avg_frame_rate\":\"10/1\"}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"sample_rate\":\"48000\",\"bit_rate\":\"128000\"}],\"format\":{\"duration\":\"10\"}}";

    private static MediaEditor Editor(FakeFfmpeg ffmpeg, string keyframes = "0.000000,K__\n")
    {
        var encoders = new FFmpegEncoders((_, _) => Task.FromResult(""));
        encoders.Record(Fake, ["libx264", "libx265", "aac"]);
        var probe = new MediaProbe((_, _) => Task.FromResult(VideoJson));
        return new MediaEditor(() => new ExternalTools { Ffmpeg = Fake, Ffprobe = FakeProbe }, ffmpeg.Runner, probe, encoders, (_, _) => Task.FromResult(keyframes));
    }

    private static string Dummy(TempDir dir, string name = "movie.mp4")
    {
        File.WriteAllText(dir.Combine(name), "source");
        return dir.Combine(name);
    }

    [Fact]
    public async Task WritesToAHiddenFileAndMovesItIntoPlaceWhenComplete()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        string[]? during = null;
        FileAttributes attributes = 0;
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = async (args, onLine, ct) =>
        {
            File.WriteAllText(args[^1], "media");
            await Task.Delay(300, ct); // long enough for the file to be hidden
            during = dir.Names();
            attributes = File.GetAttributes(args[^1]);
            onLine?.Invoke("progress=end");
        };

        var result = await Editor(ffmpeg).MuteAsync(path);

        Assert.Equal(dir.Combine("movie Muted.mp4"), result);
        Assert.Equal("media", File.ReadAllText(result));
        Assert.Equal(FileAttributes.Hidden, attributes & FileAttributes.Hidden);
        Assert.Equal(2, during!.Length);
        Assert.Contains(during, n => n.StartsWith(".openshelf-") && n.EndsWith("-movie Muted.mp4"));
        Assert.Equal(["movie Muted.mp4", "movie.mp4"], dir.Names());
        Assert.Equal((FileAttributes)0, File.GetAttributes(result) & FileAttributes.Hidden);
    }

    [Fact]
    public async Task FailureLeavesNothingBehindAndTriesTheNextAttemptFirst()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            File.WriteAllText(args[^1], "half");
            throw new ProcessFailure("ffmpeg", "Invalid data found");
        };

        var failure = await Assert.ThrowsAsync<ProcessFailure>(() => Editor(ffmpeg).MuteAsync(path));

        Assert.Contains("Invalid data", failure.Message);
        Assert.Equal(2, ffmpeg.Calls.Count); // the copy, then the re-encode
        Assert.Equal(["movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task ACopyThatFailsFallsBackToReencoding()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, onLine, _) =>
        {
            if (args.Contains("copy")) throw new ProcessFailure("ffmpeg", "codec not supported in mp4");
            File.WriteAllText(args[^1], "encoded");
            return Task.CompletedTask;
        };

        var result = await Editor(ffmpeg).MuteAsync(path);

        Assert.Equal("encoded", File.ReadAllText(result));
        Assert.Contains("libx264", ffmpeg.Calls[1]);
        Assert.Equal(["movie Muted.mp4", "movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task CancellingMidwayLeavesNothingBehind()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        using var cts = new CancellationTokenSource();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = async (args, onLine, ct) =>
        {
            File.WriteAllText(args[^1], "half");
            onLine?.Invoke("out_time_us=1000000");
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor(ffmpeg).TrimAsync(path, 0, 5, null, cts.Token));

        Assert.Equal(["movie.mp4"], dir.Names());
        Assert.Single(ffmpeg.Calls); // a cancel does not try the next attempt
    }

    [Fact]
    public async Task CancellingBeforeStartingDoesNothing()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor(ffmpeg).MuteAsync(path, null, new CancellationToken(true)));
        Assert.Empty(ffmpeg.Calls);
        Assert.Equal(["movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task ASplitThatIsCancelledDeletesTheFinishedParts()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        using var cts = new CancellationTokenSource();
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = async (args, onLine, ct) =>
        {
            if (ffmpeg.Calls.Count == 3) { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            File.WriteAllText(args[^1], "part");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Editor(ffmpeg).SplitAsync(path, SplitPlan.EqualParts(10, 4), null, null, cts.Token));

        Assert.Equal(3, ffmpeg.Calls.Count);
        Assert.Equal(["movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task ASplitThatFailsLeavesNoPartialSetEither()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            if (ffmpeg.Calls.Count >= 3) throw new ProcessFailure("ffmpeg", "disk full");
            File.WriteAllText(args[^1], "part");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<ProcessFailure>(() => Editor(ffmpeg).SplitAsync(path, SplitPlan.EqualParts(10, 4)));

        Assert.Equal(["movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task SplitNamesProgressAndCopiesWithoutReencoding()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        var progress = new Collected();
        var started = new List<int>();

        var outputs = await Editor(ffmpeg).SplitAsync(path, SplitPlan.EqualParts(10, 3), progress, new DirectIntProgress(started.Add));

        Assert.Equal(["movie Part 1.mp4", "movie Part 2.mp4", "movie Part 3.mp4"], outputs.Select(Path.GetFileName));
        Assert.Equal([1, 2, 3], started);
        // Every part is a stream copy ("-c:v copy") of its own seconds.
        Assert.All(ffmpeg.Calls, call => Assert.Contains("copy", call));
        Assert.DoesNotContain(ffmpeg.Calls, call => call.Contains("libx264"));
        Assert.Equal("3.333", ffmpeg.Calls[0][Array.IndexOf(ffmpeg.Calls[0], "-to") + 1]);
        Assert.Equal("3.333", ffmpeg.Calls[1][Array.IndexOf(ffmpeg.Calls[1], "-ss") + 1]);
        Assert.DoesNotContain("-to", ffmpeg.Calls[2]); // the last part runs to the end
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
        Assert.Equal(["movie Part 1.mp4", "movie Part 2.mp4", "movie Part 3.mp4", "movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task TrimCopiesOnlyWhenTheCutIsExact()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);

        var fromStart = new FakeFfmpeg();
        await Editor(fromStart).TrimAsync(path, 0, 4);
        Assert.Contains("copy", fromStart.Calls[0]);

        // 2.5 s is not a keyframe (the only one is at 0): the picture is re-encoded so it starts exactly there.
        var offKey = new FakeFfmpeg();
        await Editor(offKey, "0.000000,K__\n1.000000,___\n").TrimAsync(path, 2.5, 6);
        var encode = Assert.Single(offKey.Calls);
        Assert.Contains("libx264", encode);
        Assert.Equal("2.500", encode[Array.IndexOf(encode, "-ss") + 1]);
        Assert.Equal("3.500", encode[Array.IndexOf(encode, "-t") + 1]);

        // At a keyframe the copy is exact, with the re-encode kept as the fallback.
        var onKey = new FakeFfmpeg();
        await Editor(onKey, "2.500000,K__\n").TrimAsync(path, 2.5, 6);
        Assert.Contains("copy", onKey.Calls[0]);
    }

    [Fact]
    public async Task ATrimToTheEndOmitsTheEndTime()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        await Editor(ffmpeg).TrimAsync(path, 0, 9.99);
        Assert.DoesNotContain("-to", ffmpeg.Calls[0]);
        await Assert.ThrowsAsync<ConversionException>(() => Editor(ffmpeg).TrimAsync(path, 6, 6));
    }

    [Fact]
    public async Task NeverOverwritesAFileThatAppearedMeanwhile()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.Behavior = (args, _, _) =>
        {
            File.WriteAllText(args[^1], "mine");
            File.WriteAllText(dir.Combine("movie Muted.mp4"), "theirs"); // someone saved the same name while we worked
            return Task.CompletedTask;
        };

        var result = await Editor(ffmpeg).MuteAsync(path);

        Assert.Equal(dir.Combine("movie Muted 2.mp4"), result);
        Assert.Equal("theirs", File.ReadAllText(dir.Combine("movie Muted.mp4")));
        Assert.Equal("mine", File.ReadAllText(result));
    }

    [Fact]
    public async Task SpeedOutsideTheRangeIsRefused()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Editor(ffmpeg).ChangeSpeedAsync(path, 0.2, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Editor(ffmpeg).ChangeSpeedAsync(path, 4.5, true));
        Assert.Empty(ffmpeg.Calls);
        await Editor(ffmpeg).ChangeSpeedAsync(path, 4, true);
        await Editor(ffmpeg).ChangeSpeedAsync(path, 0.25, false);
        Assert.Equal(2, ffmpeg.Calls.Count);
    }

    [Fact]
    public async Task MissingToolsAreReportedNotCrashed()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var editor = new MediaEditor(() => ExternalTools.None);
        Assert.False(editor.IsAvailable);
        var missing = await Assert.ThrowsAsync<MissingToolException>(() => editor.MuteAsync(path));
        Assert.Equal("ffmpeg", missing.Name);
        Assert.Equal(["movie.mp4"], dir.Names());
    }

    [Fact]
    public async Task MergeNeedsTwoFiles()
    {
        using var dir = new TempDir();
        var path = Dummy(dir);
        var ffmpeg = new FakeFfmpeg();
        await Assert.ThrowsAsync<ConversionException>(() => Editor(ffmpeg).MergeAsync([path]));
        Assert.Empty(ffmpeg.Calls);
    }

    private sealed class DirectIntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
