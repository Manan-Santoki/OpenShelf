using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Convert;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Demo hooks: the screenshots never play sound.</summary>
internal static class DemoSettings
{
    public static bool Muted { get; set; }
}

/// <summary>
/// Cuts a video or audio file down to a range chosen on a thumbnail strip or waveform, with playback. Given several files a filmstrip
/// moves between them; each keeps its own range, or "Same for all" cuts the same seconds from the start and end of every file.
/// A file that will not play (a codec Windows lacks) shows frames extracted with ffmpeg instead.
/// </summary>
internal sealed class TrimEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static Size ContentSize(int count, bool audio) =>
        new(600, (audio ? 440 : 570) + (count > 1 ? FilmstripView.StripHeight + 10 : 0));

    private readonly IReadOnlyList<string> _paths;
    private readonly TrimState _state;
    private readonly bool _audio;
    private readonly PreviewPlayer _player = new() { IsMuted = DemoSettings.Muted };
    private readonly TrimTimeline _timeline = new();
    private readonly TimeField _startField = new("Start time");
    private readonly TimeField _endField = new("End time");
    private readonly TextBlock _length = ToolKit.Text("", 11, "Text2");
    private readonly TextBlock _cutAway = ToolKit.Text("Shorter than these cuts", 11, "Danger");
    private readonly Button _play;
    private readonly Image _still = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(6) };
    private readonly Border _overlay = new();
    private readonly string?[] _errors;
    private readonly bool[] _isVideo;
    private readonly IReadOnlyList<ImageSource>?[] _frames;
    private readonly float[]?[] _waves;
    private FilmstripView? _strip;
    private CancellationTokenSource? _stillRequest;
    private bool _loaded;
    private int _shown = -1;

    public TrimEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths)
        : base(host, operations, "Trim", ContentSize(paths.Count, IsAudio(paths)))
    {
        _paths = paths;
        _audio = IsAudio(paths);
        _state = new TrimState(paths.Count);
        _errors = new string?[paths.Count];
        _isVideo = Enumerable.Repeat(!_audio, paths.Count).ToArray();
        _frames = new IReadOnlyList<ImageSource>?[paths.Count];
        _waves = new float[]?[paths.Count];

        // ---- preview
        var stage = new Grid { ClipToBounds = true };
        _player.Visual.HorizontalAlignment = HorizontalAlignment.Center;
        _player.Visual.VerticalAlignment = VerticalAlignment.Center;
        _player.Visual.Margin = new Thickness(6);
        if (_audio)
        {
            // Sound has nothing to show; the element still has to be in the tree to play.
            _player.Visual.Width = 1;
            _player.Visual.Height = 1;
            _player.Visual.Opacity = 0;
            var icon = ToolKit.Glyph(Glyphs.Waveform, 34, "Text3");
            var name = ToolKit.Text(paths[0] is { } p ? Path.GetFileName(p) : "", 12, "Text2");
            name.Name = "FileName";
            name.MaxWidth = 420;
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(icon);
            panel.Children.Add(name);
            _audioName = name;
            stage.Children.Add(panel);
        }
        else
        {
            stage.Children.Add(_still);
        }
        stage.Children.Add(_player.Visual);
        stage.Children.Add(_overlay);
        Canvas = stage;
        SetCanvasPadding(0);

        // ---- timeline and fields
        _timeline.Height = _audio ? 110 : 58;
        _timeline.IsVideo = !_audio;
        _timeline.StartDragged += v => { _state.SetStart(v); _player.Seek(_state.Start); };
        _timeline.EndDragged += v => { _state.SetEnd(v); _player.Seek(_state.End); };
        _timeline.Seeked += v => _player.Seek(v);
        _startField.ValueCommitted += v => { _state.SetStart(v); _player.Seek(_state.Start); };
        _endField.ValueCommitted += v => { _state.SetEnd(v); _player.Seek(_state.End); };

        _play = ToolKit.IconButton(Glyphs.Play, "Play the selection (Space)", (_, _) => TogglePlayback(), 30);
        _play.IsEnabled = false;
        _cutAway.ToolTip = "Nothing would be left of this file, so it isn’t trimmed.";
        _cutAway.Visibility = Visibility.Collapsed;

        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (var i = 0; i < 6; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Put(UIElement element, int column, Thickness? margin = null)
        {
            Grid.SetColumn(element, column);
            if (element is FrameworkElement f) { f.VerticalAlignment = VerticalAlignment.Center; if (margin is { } m) f.Margin = m; }
            row.Children.Add(element);
        }
        Put(_play, 0, new Thickness(0, 0, 10, 0));
        Put(ToolKit.Text("Start", 12, "Text2"), 1, new Thickness(0, 0, 6, 0));
        Put(_startField, 2);
        Put(ToolKit.Text("End", 12, "Text2"), 3, new Thickness(12, 0, 6, 0));
        Put(_endField, 4);
        _endField.HorizontalAlignment = HorizontalAlignment.Left;
        Put(_length, 6);
        Put(_cutAway, 6);

        var controls = new StackPanel();
        controls.Children.Add(_timeline);
        controls.Children.Add(row);
        controls.Children.Add(Card);
        Controls = controls;

        if (paths.Count > 1)
        {
            _strip = new FilmstripView(paths.Select(_ => (ImageSource?)null).ToList(), paths.Select(Path.GetFileName).ToList()!, withSameForAll: true,
                itemName: "file", placeholderGlyph: _audio ? Glyphs.Waveform : Glyphs.Film);
            _strip.Attach(_state.Edits);
            Filmstrip = _strip;
        }

        _player.PositionChanged += OnPosition;
        _player.PlayingChanged += () => _play.Content = _player.IsPlaying ? Glyphs.Pause : Glyphs.Play;
        _player.Opened += () =>
        {
            _still.Visibility = Visibility.Collapsed;
            _play.IsEnabled = true;
        };
        _player.Failed += OnPlayerFailed;
        _state.Edits.Changed += Sync;

        Sync();
        Loaded += (_, _) => Load();
    }

    private TextBlock? _audioName;

    private static bool IsAudio(IReadOnlyList<string> paths) => paths.Count > 0 && FileKinds.Of(paths[0]) == FileKind.Audio;

    // ---------------------------------------------------------------- the editor's button

    protected override string PrimaryText => _state.Edits.IsBatch ? _state.Edits.PrimaryTitle("Trim", "Save", "File") : "Save";
    protected override bool CanExport => _loaded && _state.Edits.EditedCount > 0;
    protected override bool HasReset => true;
    protected override bool HandlesSpace => true;

    protected override void ResetEdits()
    {
        _state.Edits.Reset();
        _player.Seek(_state.Start);
    }

    protected override void SpacePressed() => TogglePlayback();

    private void TogglePlayback()
    {
        if (_errors[_state.Index] is null) _player.Toggle(_state.Start, _state.End);
    }

    protected override bool PrepareToSave()
    {
        _player.Pause();
        return base.PrepareToSave();
    }

    protected override string? FirstDetail() => BatchDetail(0, _state.Jobs().Count);

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var jobs = _state.Jobs();
        var outcome = new SaveOutcome { SavedLabel = "trimmed", UnchangedLabel = "not trimmed", Unchanged = _paths.Count - jobs.Count };
        return await ExportRunner.RunAsync(jobs, outcome, job => _paths[job.Index],
            async (job, _, progress, ct) => [await Operations.TrimAsync(_paths[job.Index], job.Start, job.End, progress, ct)],
            (fraction, index) => Report(fraction, BatchDetail(index, jobs.Count)), cancel);
    }

    protected override void Disposed()
    {
        _stillRequest?.Cancel();
        _player.Dispose();
    }

    // ---------------------------------------------------------------- showing the selection

    private void Sync()
    {
        var i = _state.Index;
        if (_loaded && _shown != i) ShowFile(i);
        _timeline.Duration = _state.Duration;
        _timeline.Start = _state.Start;
        _timeline.End = _state.End;
        _startField.Seconds = _state.Start;
        _endField.Seconds = _state.End;
        var cutAway = _state.IsCutAway(i);
        _cutAway.Visibility = cutAway ? Visibility.Visible : Visibility.Collapsed;
        _length.Visibility = cutAway ? Visibility.Collapsed : Visibility.Visible;
        _length.Text = _state.Duration > 0 ? "Length " + _state.LengthText(i) : "";
        Refresh();
    }

    private void OnPosition(double seconds)
    {
        _timeline.Playhead = seconds;
        if (!_player.IsPlayable && !_audio) RequestStill(seconds);
    }

    private void ShowFile(int index)
    {
        _shown = index;
        _player.Pause();
        _stillRequest?.Cancel();
        _timeline.Playhead = 0;
        _timeline.IsVideo = _isVideo[index];
        _timeline.Frames = _frames[index] ?? [];
        _timeline.Waveform = _waves[index] ?? [];
        if (_audioName is not null) _audioName.Text = Path.GetFileName(_paths[index]);
        if (_errors[index] is { } error)
        {
            _overlay.Child = MediaParts.Notice(error);
            _overlay.Background = ToolKit.Brush("Window");
            _overlay.Visibility = Visibility.Visible;
            _play.IsEnabled = false;
            return;
        }
        _overlay.Visibility = Visibility.Collapsed;
        _play.IsEnabled = false;
        _still.Source = null;
        _still.Visibility = Visibility.Visible;
        _player.Open(_paths[index]);
        if (_isVideo[index]) RequestStill(Math.Min(MediaEditor.PosterTime(_state.Durations[index]), _state.Durations[index]), immediately: true);
        if (_frames[index] is null && _waves[index] is null) _ = LoadPreviewsAsync(index);
    }

    private void OnPlayerFailed()
    {
        // The file will not play here: extracted frames stand in for the picture.
        _play.IsEnabled = false;
        _player.Visual.Visibility = Visibility.Collapsed;
        _still.Visibility = Visibility.Visible;
        if (!_audio) RequestStill(_state.Start, immediately: true);
    }

    private async void RequestStill(double seconds, bool immediately = false)
    {
        _stillRequest?.Cancel();
        var source = _stillRequest = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        var index = _shown;
        if (index < 0 || !_isVideo[index]) return;
        try
        {
            if (!immediately) await Task.Delay(120, source.Token);
            var png = await Task.Run(() => Operations.ExtractFrameAsync(_paths[index], seconds, 960, source.Token), source.Token);
            if (source.IsCancellationRequested || _shown != index) return;
            // Once the element has a picture of its own, the still is not needed.
            if (_player.IsPlayable && _player.IsOpen) return;
            _still.Source = MediaParts.Picture(png);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception) { /* no picture; the timeline is still usable */ }
    }

    // ---------------------------------------------------------------- loading

    private async void Load()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                for (var i = 0; i < _paths.Count; i++) _errors[i] = Guidance.LoadError(new MissingToolException("ffmpeg"), "");
                _loaded = true;
                Sync();
                return;
            }
            for (var i = 0; i < _paths.Count; i++)
            {
                try
                {
                    var info = await Operations.GetInfoAsync(_paths[i], Lifetime);
                    _isVideo[i] = info.HasVideo;
                    if (!double.IsFinite(info.Duration) || info.Duration <= 0) _errors[i] = "This file has no playable length.";
                    else _state.SetDuration(i, info.Duration);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { _errors[i] = Guidance.LoadError(e, "This file can’t be read."); }
            }
            _loaded = true;
            Sync();
            if (_strip is not null) _ = LoadFilmstripAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadFilmstripAsync()
    {
        for (var i = 0; i < _paths.Count; i++)
        {
            if (!_isVideo[i] || _errors[i] is not null) continue;
            try
            {
                var png = await Operations.ExtractPosterAsync(_paths[i], 160, Lifetime);
                _strip?.SetThumbnail(i, MediaParts.Picture(png));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* keeps the placeholder */ }
        }
    }

    private async Task LoadPreviewsAsync(int index)
    {
        try
        {
            if (_isVideo[index])
            {
                var pngs = await Operations.ExtractThumbnailStripAsync(_paths[index], 10, 200, Lifetime);
                _frames[index] = pngs.Select(p => (ImageSource)MediaParts.Picture(p)).ToList();
            }
            else
            {
                _waves[index] = await Operations.ExtractWaveformAsync(_paths[index], 300, Lifetime);
            }
            if (_shown == index)
            {
                _timeline.Frames = _frames[index] ?? [];
                _timeline.Waveform = _waves[index] ?? [];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _frames[index] ??= []; _waves[index] ??= []; }
    }

    // ---------------------------------------------------------------- demos

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "range":
                    _state.SetStart(_state.Duration * 0.25);
                    _state.SetEnd(_state.Duration * 0.75);
                    _player.Seek(_state.Start);
                    break;
                case "second": _state.Edits.Index = Math.Min(1, _paths.Count - 1); break;
                case "same": _state.Edits.SameForAll = true; break;
                case "play": TogglePlayback(); break;
                case "unplayable": _player.Dispose(); OnPlayerFailed(); break;
            }
    }

    internal TrimState State => _state;
    internal bool PlayerOpened => _player.IsOpen;
    internal bool PlayerPlayable => _player.IsPlayable;
    internal ExportCard ProgressCard => Card;
    internal Task ExportForTest(CancellationToken ct) => ExportAsync(ct);
}
