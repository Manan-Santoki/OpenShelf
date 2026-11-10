using OpenShelf.Convert;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors.Tests;

/// <summary>An engine the tests can steer; cutouts come from the real model, so the model must be bundled beside the tests.</summary>
internal sealed class SteerableEngine : IBackgroundEngine
{
    private readonly BackgroundEngine _real = new();
    public bool ModelPresent { get; set; } = true;
    public Func<string, CancellationToken, Task>? BeforeCutOut { get; set; }
    public Func<IProgress<double>?, CancellationToken, Task>? Download { get; set; }
    public Func<string, string>? SaveOverride { get; set; }
    public List<string> CutOuts { get; } = [];
    public List<string> Saves { get; } = [];

    public bool IsModelAvailable => ModelPresent;

    public async Task EnsureModelAsync(IProgress<double>? progress, CancellationToken cancel)
    {
        if (Download is not null) await Download(progress, cancel);
        ModelPresent = true;
    }

    public async Task<Cutout> CutOutAsync(string path, CancellationToken cancel)
    {
        CutOuts.Add(Path.GetFileName(path));
        if (BeforeCutOut is not null) await BeforeCutOut(path, cancel);
        return await _real.CutOutAsync(path, cancel);
    }

    public Task<string> SaveAsync(string path, Cutout cutout, CancellationToken cancel)
    {
        Saves.Add(Path.GetFileName(path));
        if (SaveOverride is { } hook) return Task.FromResult(hook(path));
        return _real.SaveAsync(path, cutout, cancel);
    }
}

public class RemoveBackgroundSessionTests
{
    private sealed class Sync(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public void The_model_is_bundled_beside_the_program_and_matches_its_pinned_checksum()
    {
        var store = ModelStore.Default;
        var found = store.FindModel();
        Assert.NotNull(found);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "models", "u2netp.onnx"), found);
        Assert.Equal(ModelDefinition.U2Netp.SizeBytes, new FileInfo(found!).Length);
        using var stream = File.OpenRead(found!);
        Assert.Equal(ModelDefinition.U2Netp.Sha256, System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), ignoreCase: true);
    }

    [Fact]
    public async Task A_photo_with_a_subject_ends_ready_with_a_cutout()
    {
        using var dir = new TempFolder();
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("Photo.png"))], new SteerableEngine());
        Assert.Equal(CutoutPhase.Finding, session.Phase);
        Assert.False(session.IsSaveEnabled);

        await session.ShowAsync(0);

        Assert.Equal(CutoutPhase.Ready, session.Phase);
        Assert.NotNull(session.Cutout);
        Assert.Equal(SubjectState.Found, session.States[0]);
        Assert.True(session.IsSaveEnabled);
        Assert.Equal("Save", session.PrimaryTitle);
        Assert.Null(session.Message);
    }

    [Fact]
    public async Task A_picture_with_no_subject_explains_why_and_cannot_be_saved()
    {
        using var dir = new TempFolder();
        var session = new RemoveBackgroundSession([Pictures.Plain(dir.File("Wall.png"))], new SteerableEngine());

        await session.ShowAsync(0);

        Assert.Equal(CutoutPhase.NoSubject, session.Phase);
        Assert.Equal(new NoSubjectException().Message, session.Message);
        Assert.StartsWith("No clear subject was found.", session.Message);
        Assert.Null(session.Cutout);
        Assert.Equal(SubjectState.None, session.States[0]);
        Assert.False(session.IsSaveEnabled);
    }

    [Fact]
    public async Task An_unreadable_image_is_reported_as_a_failure()
    {
        using var dir = new TempFolder();
        File.WriteAllText(dir.File("Broken.png"), "not a picture");
        var session = new RemoveBackgroundSession([dir.File("Broken.png")], new SteerableEngine());

        await session.ShowAsync(0);

        Assert.Equal(CutoutPhase.Failed, session.Phase);
        Assert.False(string.IsNullOrEmpty(session.Message));
        Assert.False(session.IsSaveEnabled);
    }

    [Fact]
    public async Task Several_images_count_those_that_will_be_saved()
    {
        using var dir = new TempFolder();
        var paths = new[]
        {
            Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.Plain(dir.File("B.png")), Pictures.SubjectOnPlain(dir.File("C.png")),
        };
        var session = new RemoveBackgroundSession(paths, new SteerableEngine());

        Assert.Equal("Save 3 Images", session.PrimaryTitle);   // nothing is known to lack a subject yet
        Assert.Equal(3, session.EditedCount);
        await session.ShowAsync(1);
        Assert.Equal("Save 2 Images", session.PrimaryTitle);
        Assert.Equal([true, false, true], session.States.Edited);
        Assert.True(session.IsSaveEnabled);
    }

    [Fact]
    public async Task When_every_image_lacks_a_subject_nothing_can_be_saved()
    {
        using var dir = new TempFolder();
        var session = new RemoveBackgroundSession([Pictures.Plain(dir.File("A.png")), Pictures.Plain(dir.File("B.png"))], new SteerableEngine());
        await session.ShowAsync(0);
        await session.ShowAsync(1);
        Assert.Equal("Save Images", session.PrimaryTitle);
        Assert.False(session.IsSaveEnabled);
    }

    [Fact]
    public async Task One_image_saves_name_no_background_png_with_a_transparent_backdrop()
    {
        using var dir = new TempFolder();
        var path = Pictures.SubjectOnPlain(dir.File("Photo.png"));
        var engine = new SteerableEngine();
        var session = new RemoveBackgroundSession([path], engine);
        await session.ShowAsync(0);

        var outcome = session.Save(new Sync(_ => { }), CancellationToken.None);

        Assert.Equal([dir.File("Photo No Background.png")], outcome.Saved);
        Assert.Equal("Saved · Photo No Background.png", outcome.Message);
        Assert.Equal(["Photo.png"], engine.CutOuts);   // the preview was reused, not recomputed
        Assert.Equal((640u, 480u), Pictures.SizeOf(outcome.Saved[0]));
        Assert.Equal(0, Pictures.At(outcome.Saved[0], 4, 4).A);                       // the corner backdrop is gone
        Assert.True(Pictures.At(outcome.Saved[0], 320, 240).A > 250);                 // the subject stays
        Assert.True(File.Exists(path));                                              // the original is untouched
        Assert.Equal(["Photo No Background.png", "Photo.png"], dir.Names());
    }

    [Fact]
    public async Task A_second_save_never_overwrites_the_first()
    {
        using var dir = new TempFolder();
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("Photo.png"))], new SteerableEngine());
        await session.ShowAsync(0);
        session.Save(new Sync(_ => { }), CancellationToken.None);
        var second = session.Save(new Sync(_ => { }), CancellationToken.None);
        Assert.Equal(dir.File("Photo No Background 2.png"), second.Saved[0]);
    }

    [Fact]
    public async Task Several_images_save_the_ones_with_a_subject_and_count_the_rest()
    {
        using var dir = new TempFolder();
        var paths = new[]
        {
            Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.Plain(dir.File("B.png")), Pictures.SubjectOnPlain(dir.File("C.png")),
        };
        var session = new RemoveBackgroundSession(paths, new SteerableEngine());
        await session.ShowAsync(0);                // A: found. B and C are looked at while saving.
        var reports = new List<double>();

        var outcome = session.Save(new Sync(reports.Add), CancellationToken.None);

        Assert.Equal([dir.File("A No Background.png"), dir.File("C No Background.png")], outcome.Saved);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("2 saved · 1 without a subject", outcome.Message);
        Assert.Equal(1.0, reports[^1]);
        Assert.False(File.Exists(dir.File("B No Background.png")));
    }

    [Fact]
    public async Task Images_already_found_to_have_no_subject_are_not_looked_at_again_when_saving()
    {
        using var dir = new TempFolder();
        var engine = new SteerableEngine();
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.Plain(dir.File("B.png"))], engine);
        await session.ShowAsync(1);
        await session.ShowAsync(0);
        engine.CutOuts.Clear();

        var outcome = session.Save(new Sync(_ => { }), CancellationToken.None);

        Assert.Empty(engine.CutOuts);
        Assert.Equal([dir.File("A No Background.png")], outcome.Saved);
        Assert.Equal(0, outcome.Unchanged);
    }

    [Fact]
    public async Task A_failure_while_saving_stops_the_batch_and_hands_back_what_was_written()
    {
        using var dir = new TempFolder();
        var paths = new[] { Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.SubjectOnPlain(dir.File("B.png")), Pictures.SubjectOnPlain(dir.File("C.png")) };
        var engine = new SteerableEngine
        {
            SaveOverride = path => Path.GetFileName(path) == "B.png" ? throw ConversionException.Failed("There isn’t enough free space on the disk.") : Path.ChangeExtension(path, ".out"),
        };
        var session = new RemoveBackgroundSession(paths, engine);
        await session.ShowAsync(0);

        var error = Assert.Throws<SaveException>(() => session.Save(new Sync(_ => { }), CancellationToken.None));

        Assert.Equal("There isn’t enough free space on the disk.", error.Message);
        Assert.Equal([Path.ChangeExtension(paths[0], ".out")], error.SavedBefore);
        Assert.DoesNotContain("C.png", engine.Saves);
    }

    [Fact]
    public async Task Cancelling_a_batch_save_stops_it()
    {
        using var dir = new TempFolder();
        var paths = new[] { Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.SubjectOnPlain(dir.File("B.png")) };
        var session = new RemoveBackgroundSession(paths, new SteerableEngine());
        await session.ShowAsync(0);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.Save(new Sync(_ => { }), cancel.Token));
        Assert.DoesNotContain("A No Background.png", dir.Names());
    }

    [Fact]
    public async Task A_missing_model_is_downloaded_first_with_visible_progress()
    {
        using var dir = new TempFolder();
        var phases = new List<(CutoutPhase Phase, double Progress)>();
        var engine = new SteerableEngine
        {
            ModelPresent = false,
            Download = async (progress, _) => { progress?.Report(0.25); progress?.Report(0.8); await Task.Delay(150); },
        };
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("Photo.png"))], engine);
        session.Changed += () => phases.Add((session.Phase, session.DownloadProgress));

        await session.ShowAsync(0);

        Assert.Equal(CutoutPhase.Downloading, phases[0].Phase);
        Assert.Equal(CutoutPhase.Ready, session.Phase);
        Assert.Contains(phases, p => p.Phase == CutoutPhase.Finding);
        Assert.Contains(phases, p => p.Phase == CutoutPhase.Downloading && p.Progress >= 0.8);
        Assert.True(engine.IsModelAvailable);
    }

    [Fact]
    public async Task A_failed_download_says_so_and_nothing_can_be_saved()
    {
        using var dir = new TempFolder();
        var engine = new SteerableEngine
        {
            ModelPresent = false,
            Download = (_, _) => throw new ModelUnavailableException("The Remove Background model couldn’t be downloaded. Check your internet connection and try again."),
        };
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("Photo.png"))], engine);

        await session.ShowAsync(0);

        Assert.Equal(CutoutPhase.Failed, session.Phase);
        Assert.Contains("Check your internet connection", session.Message);
        Assert.False(session.IsSaveEnabled);
    }

    [Fact]
    public async Task Showing_another_image_drops_the_first_ones_late_result()
    {
        using var dir = new TempFolder();
        var gate = new TaskCompletionSource();
        var engine = new SteerableEngine
        {
            BeforeCutOut = async (path, cancel) => { if (Path.GetFileName(path) == "A.png") await gate.Task; },
        };
        var paths = new[] { Pictures.SubjectOnPlain(dir.File("A.png")), Pictures.Plain(dir.File("B.png")) };
        var session = new RemoveBackgroundSession(paths, engine);

        var first = session.ShowAsync(0);           // stuck on A
        await session.ShowAsync(1);                 // B is shown and has no subject
        gate.SetResult();
        await first;                                // A finishes late and must not overwrite B's state

        Assert.Equal(1, session.Index);
        Assert.Equal(CutoutPhase.NoSubject, session.Phase);
        Assert.Null(session.Cutout);
        Assert.Equal(SubjectState.Unknown, session.States[0]);
    }

    [Fact]
    public async Task Cancelling_silences_the_session()
    {
        using var dir = new TempFolder();
        var gate = new TaskCompletionSource();
        var engine = new SteerableEngine { BeforeCutOut = (_, _) => gate.Task };
        var session = new RemoveBackgroundSession([Pictures.SubjectOnPlain(dir.File("A.png"))], engine);
        var running = session.ShowAsync(0);
        var changes = 0;
        session.Changed += () => changes++;

        session.Cancel();
        gate.SetResult();
        await running;

        Assert.Equal(0, changes);
        Assert.Null(session.Cutout);
    }

    [Fact]
    public void A_session_needs_at_least_one_image()
    {
        Assert.Throws<ArgumentException>(() => new RemoveBackgroundSession([], new SteerableEngine()));
    }
}
