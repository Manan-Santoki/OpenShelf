using System.IO;
using System.Windows.Threading;

namespace OpenShelf.Tour;

/// <summary>The practice folder as the tour shows it: whether its files are ready, and what the person has made from them.</summary>
internal sealed class TourSession
{
    private readonly DispatcherTimer _timer;
    private bool _started;

    public TourSession(string? folder = null)
    {
        Folder = folder ?? PracticeFiles.DefaultFolder;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public string Folder { get; }
    public bool IsReady { get; private set; }
    public string? Failure { get; private set; }

    /// <summary>Files made from the practice files, newest first.</summary>
    public IReadOnlyList<string> Results { get; private set; } = [];

    public event Action? Changed;

    public string PathOf(PracticeFile file) => PracticeFiles.PathOf(file, Folder);

    /// <summary>Makes any missing practice files (after the window has had a chance to draw), then follows the folder while the tour is open.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Start();
        _timer.Dispatcher.BeginInvoke(DispatcherPriority.Background, Prepare);
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    private void Prepare()
    {
        try
        {
            PracticeFiles.Prepare(Folder);
            IsReady = true;
            Failure = null;
        }
        catch (Exception ex)
        {
            IsReady = false;
            Failure = "The practice files couldn’t be made: " + ex.Message;
        }
        Results = PracticeFiles.Results(Folder);
        Changed?.Invoke();
    }

    /// <summary>Makes the practice files again after they were removed.</summary>
    public void Restore()
    {
        Stop();
        IsReady = false;
        Changed?.Invoke();
        Start();
    }

    public void RemovePracticeFiles()
    {
        try { PracticeFiles.Remove(Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failure = ex.Message; }
        IsReady = false;
        Results = [];
        Changed?.Invoke();
    }

    private void Refresh()
    {
        if (!IsReady) return;
        var found = PracticeFiles.Results(Folder);
        // Practice files a person deleted by hand are made again, so the step they are on keeps working.
        var missing = Enum.GetValues<PracticeFile>().Any(f => !File.Exists(PathOf(f)));
        if (missing) { Prepare(); return; }
        if (found.SequenceEqual(Results)) return;
        Results = found;
        Changed?.Invoke();
    }
}
