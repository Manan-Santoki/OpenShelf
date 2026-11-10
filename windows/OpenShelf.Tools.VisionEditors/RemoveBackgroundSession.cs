using OpenShelf.Convert;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>What the Remove BG editor needs from the cutout engine; <see cref="BackgroundEngine"/> is the real one.</summary>
public interface IBackgroundEngine
{
    bool IsModelAvailable { get; }
    Task EnsureModelAsync(IProgress<double>? progress, CancellationToken cancel);
    Task<Cutout> CutOutAsync(string path, CancellationToken cancel);
    Task<string> SaveAsync(string path, Cutout cutout, CancellationToken cancel);
}

/// <summary>The segmentation engine from OpenShelf.Vision (U²-Netp on this PC).</summary>
public sealed class BackgroundEngine(BackgroundRemover? remover = null) : IBackgroundEngine
{
    private readonly BackgroundRemover _remover = remover ?? new BackgroundRemover();
    public bool IsModelAvailable => _remover.IsModelAvailable;
    public Task EnsureModelAsync(IProgress<double>? progress, CancellationToken cancel) => _remover.EnsureModelAsync(progress, cancel);
    public Task<Cutout> CutOutAsync(string path, CancellationToken cancel) => _remover.CutOutAsync(path, cancel);
    public Task<string> SaveAsync(string path, Cutout cutout, CancellationToken cancel) => _remover.SaveAsync(path, cutout, cancel);
}

/// <summary>Whether a subject has been found in an image yet.</summary>
public enum SubjectState { Unknown, Found, None }

/// <summary>Where the shown image is in getting its cutout.</summary>
public enum CutoutPhase
{
    /// <summary>Fetching the segmentation model (only when it is not bundled).</summary>
    Downloading,
    /// <summary>"Finding the subject…"</summary>
    Finding,
    /// <summary>The cutout is ready to preview and save.</summary>
    Ready,
    /// <summary>No clear subject: explained in the window; the image is left out of the save.</summary>
    NoSubject,
    /// <summary>The model or the image could not be used; the message says why.</summary>
    Failed,
}

/// <summary>
/// The Remove BG editor's logic (SPEC §2.4), free of any window. Each image is looked at when it is shown, and only the shown image's
/// cutout is kept, so a long selection never piles up large pictures. Images found to have no subject are left out of the save and
/// counted. There is no "Same for all": a cutout belongs to one picture.
/// </summary>
public sealed class RemoveBackgroundSession
{
    public const string SavedNote = "Saved as a PNG with a transparent background.";
    public const string FindingText = "Finding the subject…";
    public const string FailedText = "The background couldn’t be removed from this image.";

    private readonly IBackgroundEngine _engine;
    private CancellationTokenSource? _looking;
    private int _generation;

    public RemoveBackgroundSession(IReadOnlyList<string> paths, IBackgroundEngine? engine = null)
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to work on.", nameof(paths));
        Paths = paths;
        _engine = engine ?? new BackgroundEngine();
        States = new FileEdits<SubjectState>(paths.Count, _ => SubjectState.Unknown, (_, state) => state != SubjectState.None);
    }

    public IReadOnlyList<string> Paths { get; }
    public bool IsBatch => Paths.Count > 1;

    /// <summary>Which image is shown, and whether each has a subject (the filmstrip dots are the images that will be saved).</summary>
    public FileEdits<SubjectState> States { get; }

    public int Index
    {
        get => States.Index;
    }

    public CutoutPhase Phase { get; private set; } = CutoutPhase.Finding;

    /// <summary>Model download progress, 0 to 1, while <see cref="Phase"/> is Downloading.</summary>
    public double DownloadProgress { get; private set; }

    /// <summary>The shown image without its background, once <see cref="Phase"/> is Ready.</summary>
    public Cutout? Cutout { get; private set; }

    /// <summary>The explanation shown over the picture when <see cref="Phase"/> is NoSubject or Failed.</summary>
    public string? Message { get; private set; }

    /// <summary>Raised on the context that called <see cref="ShowAsync"/> whenever the phase, progress or result changed.</summary>
    public event Action? Changed;

    /// <summary>Images that saving would cut out: all but those already found to have no subject.</summary>
    public int EditedCount => States.EditedCount;

    public bool IsSaveEnabled => IsBatch ? EditedCount > 0 : Phase == CutoutPhase.Ready;

    /// <summary>"Save" for one image; "Save Images" / "Save 1 Image" / "Save 3 Images" for several.</summary>
    public string PrimaryTitle => States.PrimaryTitle("Save", "Save");

    /// <summary>Shows image <paramref name="index"/> and finds its subject, dropping the last image's result. Restarts if called again
    /// before it finishes; the earlier call then ends quietly.</summary>
    public async Task ShowAsync(int index)
    {
        _looking?.Cancel();
        var cancel = _looking = new CancellationTokenSource();
        var generation = ++_generation;
        States.Index = index;
        index = States.Index;
        Cutout = null;
        Message = null;
        DownloadProgress = 0;
        Phase = _engine.IsModelAvailable ? CutoutPhase.Finding : CutoutPhase.Downloading;
        Changed?.Invoke();
        try
        {
            if (!_engine.IsModelAvailable)
            {
                await _engine.EnsureModelAsync(new Progress<double>(p =>
                {
                    if (generation != _generation || Phase != CutoutPhase.Downloading) return;
                    DownloadProgress = p;
                    Changed?.Invoke();
                }), cancel.Token);
                if (generation != _generation) return;
                Phase = CutoutPhase.Finding;
                Changed?.Invoke();
            }
            var cutout = await _engine.CutOutAsync(Paths[index], cancel.Token);
            if (generation != _generation) return;
            States.SetOnly(index, SubjectState.Found);
            Cutout = cutout;
            Phase = CutoutPhase.Ready;
        }
        catch (OperationCanceledException)
        {
            return;   // another image is showing now
        }
        catch (NoSubjectException e)
        {
            if (generation != _generation) return;
            States.SetOnly(index, SubjectState.None);
            Message = e.Message;
            Phase = CutoutPhase.NoSubject;
        }
        catch (Exception e) when (e is VisionException or ConversionException or IOException)
        {
            if (generation != _generation) return;
            Message = e.Message;
            Phase = CutoutPhase.Failed;
        }
        catch (Exception)
        {
            if (generation != _generation) return;
            Message = FailedText;
            Phase = CutoutPhase.Failed;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops looking (the window closed).</summary>
    public void Cancel()
    {
        _generation++;
        _looking?.Cancel();
    }

    /// <summary>Saves "Name No Background.png" for the shown image, or for every image that has a subject. Runs on a worker thread.
    /// Images with no clear subject are left alone and counted. A failure stops the rest and reports what was already written.</summary>
    public SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        var outcome = new SaveOutcome { SavedLabel = "saved", UnchangedLabel = "without a subject" };
        var shown = Index;
        var cutoutOfShown = Cutout;
        var states = States.All.ToArray();
        var targets = Enumerable.Range(0, Paths.Count).Where(i => !IsBatch || states[i] != SubjectState.None).ToList();
        if (!IsBatch && cutoutOfShown is null) return outcome;
        return Batch.Run(targets, outcome, i =>
        {
            try
            {
                var cutout = i == shown && cutoutOfShown is not null ? cutoutOfShown : _engine.CutOutAsync(Paths[i], cancel).GetAwaiter().GetResult();
                return _engine.SaveAsync(Paths[i], cutout, cancel).GetAwaiter().GetResult();
            }
            catch (NoSubjectException)
            {
                return null;   // counted as without a subject
            }
            catch (Exception e) when (e is VisionException or ConversionException)
            {
                throw new SaveException(SaveException.Reason.Failed, e.Message, e);
            }
        }, progress, cancel);
    }
}
