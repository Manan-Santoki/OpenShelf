using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>
/// Plays a video or audio file for the Trim preview with WPF's MediaElement. Plays the selection and stops at its end, seeks while
/// paused, and reports a file that will not play (an unsupported codec, a slow or failed open) so the editor can show extracted frames
/// instead. Muted for demos and tests.
/// </summary>
internal sealed class PreviewPlayer : IDisposable
{
    /// <summary>How long a file gets to open before it counts as not playable.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        ScrubbingEnabled = true,
        Stretch = System.Windows.Media.Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
    };

    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _openTimer = new() { Interval = OpenTimeout };
    private double _stopAt = double.PositiveInfinity;
    private bool _isOpen;

    public PreviewPlayer()
    {
        _media.MediaOpened += (_, _) =>
        {
            _openTimer.Stop();
            _isOpen = true;
            IsPlayable = true;
            Opened?.Invoke();
        };
        _media.MediaFailed += (_, e) =>
        {
            e.Handled = true;
            Fail();
        };
        _media.MediaEnded += (_, _) =>
        {
            Pause();
            PositionChanged?.Invoke(Position);
        };
        _tick.Tick += (_, _) => OnTick();
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (!_isOpen) Fail(); };
    }

    /// <summary>The element to place in the preview.</summary>
    public FrameworkElement Visual => _media;

    /// <summary>False once the file failed to open or play.</summary>
    public bool IsPlayable { get; private set; } = true;

    public bool IsOpen => _isOpen;
    public bool IsPlaying { get; private set; }
    public double Position => _media.Position.TotalSeconds;

    public bool IsMuted
    {
        get => _media.IsMuted;
        set => _media.IsMuted = value;
    }

    public event Action? Opened;
    public event Action? Failed;
    public event Action<double>? PositionChanged;
    public event Action? PlayingChanged;

    public void Open(string path)
    {
        Pause();
        _isOpen = false;
        IsPlayable = true;
        _openTimer.Stop();
        _openTimer.Start();
        _media.Source = new Uri(path);
        _media.Pause();   // loads the file and shows its first frame
    }

    /// <summary>Moves the playhead (and pauses).</summary>
    public void Seek(double seconds)
    {
        Pause();
        if (IsPlayable) _media.Position = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        _seekedTo = Math.Max(seconds, 0);
        PositionChanged?.Invoke(_seekedTo);
    }

    private double _seekedTo;

    /// <summary>Plays from <paramref name="start"/> (or from where the playhead is, when inside the selection) and stops at <paramref name="end"/>.</summary>
    public void Toggle(double start, double end)
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (!IsPlayable) return;
        var at = IsPlayable ? Position : _seekedTo;
        if (at < start || at >= end - 0.05) at = start;
        _media.Position = TimeSpan.FromSeconds(at);
        _stopAt = end;
        _media.Play();
        IsPlaying = true;
        _tick.Start();
        PlayingChanged?.Invoke();
    }

    public void Pause()
    {
        _tick.Stop();
        if (IsPlaying)
        {
            IsPlaying = false;
            if (IsPlayable) _media.Pause();
            PlayingChanged?.Invoke();
        }
    }

    private void OnTick()
    {
        var position = Position;
        if (position >= _stopAt)
        {
            Pause();
            _media.Position = TimeSpan.FromSeconds(_stopAt);
            position = _stopAt;
        }
        PositionChanged?.Invoke(position);
    }

    private void Fail()
    {
        _openTimer.Stop();
        if (!IsPlayable && !_isOpen) { Failed?.Invoke(); return; }
        IsPlayable = false;
        _isOpen = false;
        Pause();
        Failed?.Invoke();
    }

    public void Dispose()
    {
        _tick.Stop();
        _openTimer.Stop();
        try
        {
            _media.Stop();
            _media.Close();
            _media.Source = null;
        }
        catch (InvalidOperationException) { }
    }
}
