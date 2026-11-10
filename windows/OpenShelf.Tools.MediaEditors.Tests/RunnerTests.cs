using OpenShelf.Convert;
using OpenShelf.Core;
using OpenShelf.Tools.Media;
using OpenShelf.Tools.MediaEditors;

namespace OpenShelf.Tools.MediaEditors.Tests;

public class RunnerTests
{
    [Fact]
    public async Task CountsSavedAndUnchangedAndReportsOverallProgress()
    {
        var outcome = new SaveOutcome { SavedLabel = "saved", UnchangedLabel = "already as small", Unchanged = 1 };
        var reports = new List<(double, int)>();
        var result = await ExportRunner.RunAsync(new[] { "a", "b", "c" }, outcome, p => p,
            (p, i, progress, ct) =>
            {
                progress.Report(0.5);
                return Task.FromResult<IReadOnlyList<string>>(p == "b" ? [] : [p + ".out"]);
            }, (f, i) => reports.Add((f, i)), CancellationToken.None);
        Assert.Equal(["a.out", "c.out"], result.Saved);
        Assert.Equal(2, result.Unchanged);
        Assert.Contains((0.5 / 3, 0), reports);
        Assert.Equal(1.0, reports[^1].Item1);
    }

    [Fact]
    public async Task FailureNamesWhatWasWritten()
    {
        var outcome = new SaveOutcome();
        var e = await Assert.ThrowsAsync<SaveException>(() => ExportRunner.RunAsync(new[] { "a", "b" }, outcome, p => p,
            (p, i, progress, ct) => p == "b" ? throw ConversionException.Failed("boom") : Task.FromResult<IReadOnlyList<string>>(["a.out"]),
            null, CancellationToken.None));
        Assert.Equal("boom", e.Message);
        Assert.Equal(["a.out"], e.SavedBefore);
    }

    [Fact]
    public async Task CancellationPassesThroughUnwrapped()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(new[] { "a", "b" }, new SaveOutcome(), p => p,
            (p, i, progress, ct) => { cts.Cancel(); return Task.FromResult<IReadOnlyList<string>>(["x"]); }, null, cts.Token));
    }

    [Fact]
    public void MissingFfmpegIsWordedLikeTheConvertWheel()
    {
        Assert.Equal("Trim needs ffmpeg · " + MissingToolAdvice.InstallHint, Guidance.NeedsFfmpeg("Trim"));
        Assert.Contains("Settings → Extra Formats", Guidance.LoadError(new MissingToolException("ffmpeg"), "x"));
        Assert.Equal("fallback", Guidance.LoadError(new InvalidOperationException(), "fallback"));
        var save = Assert.IsType<SaveException>(Guidance.ForSave(new MissingToolException("ffmpeg"), "a.mp4"));
        Assert.Contains("Extra Formats", save.Message);
    }

    [Fact]
    public void OutcomeMessagesMatchTheMac()
    {
        var o = new SaveOutcome(["a"]) { SavedLabel = "trimmed", UnchangedLabel = "not trimmed", Unchanged = 2 };
        Assert.Equal("1 trimmed · 2 not trimmed · a", o.Message);
    }

    // ---- registration and Mute

    private sealed class Toasts : IToastService
    {
        public List<string> Shown { get; } = [];
        public void Show(string message, ScreenPoint near) => Shown.Add(message);
    }

    private sealed class Shelves : IShelfService
    {
        public List<string> Added { get; } = [];
        public void NewShelf(IReadOnlyList<string> paths, ScreenPoint near) { }
        public bool AddToRecentShelf(IReadOnlyList<string> paths) => false;
        public bool HasOpenShelf => false;
        public void ShowAll() { }
        public void CloseAll() { }
        public IReadOnlyList<string> SelectionForWheel() => [];
        public object? DraggingShelf => null;
        public void AddResults(IReadOnlyList<string> paths, object? sourceShelf) => Added.AddRange(paths);
        public void Flush() { }
    }

    private sealed class Cards : IProgressService
    {
        public List<string> Finished { get; } = [];
        public string? Title;
        public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel) { Title = title; return new Card(this); }
        private sealed class Card(Cards owner) : IProgressCard
        {
            public void Report(int completed, double? fraction = null, string? detail = null) { }
            public void Finish(string message) => owner.Finished.Add(message);
            public void Dispose() { }
        }
    }

    private sealed class FakeOps(bool available = true) : IMediaOperations
    {
        public bool IsAvailable { get; } = available;
        public List<string> Muted { get; } = [];
        public Task<string> MuteAsync(string path, IProgress<double>? progress, CancellationToken ct)
        {
            if (path.Contains("bad")) throw ConversionException.Failed("no video track");
            progress?.Report(1);
            Muted.Add(path);
            return Task.FromResult(path.Replace(".mp4", " Muted.mp4"));
        }
        public Task<MediaInfo> GetInfoAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> TrimAsync(string path, double start, double end, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ChangeSpeedAsync(string path, double rate, bool keepsPitch, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CropAsync(string path, CropFraction crop, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> RotateAsync(string path, VideoRotation rotation, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SplitAsync(string path, IReadOnlyList<SplitSegment> segments, IProgress<double>? progress, IProgress<int>? partStarted, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> MergeAsync(IReadOnlyList<string> paths, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> CompressAsync(string path, CompressResolution resolution, CompressQuality quality, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ExtractFrameAsync(string path, double seconds, int maxSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ExtractPosterAsync(string path, int maxSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]>> ExtractThumbnailStripAsync(string path, int count, int maxSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<float[]> ExtractWaveformAsync(string path, int bars, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoSourceInfo> LoadCompressSourceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CanEncodeHevcAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static WheelRun Run(params string[] paths) => new(paths, new ScreenPoint(10, 10), false, null);

    [Fact]
    public async Task MuteRunsEachFileWithTheProgressCard()
    {
        var toasts = new Toasts(); var shelves = new Shelves(); var cards = new Cards();
        var services = new ToolServices(toasts, cards, shelves);
        var ops = new FakeOps();
        await MediaTools.MuteAsync(Run(@"C:\a.mp4", @"C:\b.mp4"), services, ops);
        Assert.Equal("Removing audio", cards.Title);
        Assert.Equal(2, ops.Muted.Count);
        Assert.Equal(["Saved 2 files"], cards.Finished);
        Assert.Equal(2, shelves.Added.Count);
    }

    [Fact]
    public async Task MuteFailureKeepsWhatWasSaved()
    {
        var shelves = new Shelves(); var cards = new Cards();
        await MediaTools.MuteAsync(Run(@"C:\a.mp4", @"C:\bad.mp4"), new ToolServices(new Toasts(), cards, shelves), new FakeOps());
        Assert.Equal(["Couldn’t mute · no video track"], cards.Finished);
        Assert.Equal([@"C:\a Muted.mp4"], shelves.Added);
    }

    [Fact]
    public async Task WithoutFfmpegToolsSayWhatToDo()
    {
        var toasts = new Toasts(); var cards = new Cards();
        await MediaTools.MuteAsync(Run(@"C:\a.mp4"), new ToolServices(toasts, cards, new Shelves()), new FakeOps(available: false));
        Assert.Equal(["Mute needs ffmpeg · " + MissingToolAdvice.InstallHint], toasts.Shown);
        Assert.Null(cards.Title);
    }

    [Fact]
    public async Task RegistersTheWheelEntriesForVideoAndAudio()
    {
        var toasts = new Toasts();
        var catalog = new ToolsCatalog(toasts, new Cards(), new Shelves(), registerBuiltIns: false);
        MediaTools.Register(catalog, new FakeOps(available: false));
        string[] Titles(params string[] paths) => catalog.For(paths).Entries.Select(e => e.Title).ToArray();
        Assert.Equal(["Trim", "Compress", "Crop", "Rotate", "Split", "Speed", "Mute"], Titles(@"C:\a.mp4"));
        Assert.Equal(["Merge", "Trim", "Compress", "Crop", "Rotate", "Split", "Speed", "Mute"], Titles(@"C:\a.mp4", @"C:\b.mov"));
        Assert.Equal(["Trim", "Split", "Speed"], Titles(@"C:\a.m4a"));
        Assert.Equal(["Merge", "Trim", "Split", "Speed"], Titles(@"C:\a.m4a", @"C:\b.mp3"));
        Assert.Empty(Titles(@"C:\a.mp4", @"C:\b.m4a"));
        Assert.Empty(Titles(@"C:\a.mkv"));
        // the editors do not open without ffmpeg: a toast says why
        var trim = catalog.For([@"C:\a.mp4"]).Entries.First(e => e.Id == MediaToolIds.Trim);
        await trim.Run(Run(@"C:\a.mp4"));
        Assert.Equal(["Trim needs ffmpeg · " + MissingToolAdvice.InstallHint], toasts.Shown);
        Assert.Equal("tool.mute", MediaToolIds.Mute);
    }
}
