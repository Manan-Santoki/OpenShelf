using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Cuts a video or audio file into parts: equal parts, every so often, or at chosen times, and shows the parts before
/// exporting. Given several files a filmstrip moves between them; each keeps its own split, or one split applies to all of them
/// where it fits.</summary>
internal sealed class SplitEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static Size ContentSize(int count) => new(470, count > 1 ? 560 + FilmstripView.StripHeight : 560);

    private readonly IReadOnlyList<string> _paths;
    private readonly SplitState _state;
    private readonly TextBlock _title;
    private readonly TextBlock _duration;
    private readonly Segmented _modePicker;
    private readonly TextBlock _partsValue = ToolKit.Text("2", 13, "Text", FontWeights.SemiBold);
    private readonly Button _fewer;
    private readonly Button _more;
    private readonly TextBox _interval = new() { Style = ToolKit.Style("Field"), Width = 64, TextAlignment = TextAlignment.Right };
    private readonly ComboBox _unit = new() { Width = 110 };
    private readonly TextBox _times = new() { Style = ToolKit.Style("Field") };
    private readonly TextBlock _timesHint = ToolKit.Text("0:30, 1:15, 2:00", 12, "Text3");
    private readonly Grid _partsRow;
    private readonly Grid _everyRow;
    private readonly Grid _timesRow;
    private readonly TextBlock _problem = new() { FontSize = 11, Foreground = ToolKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, MinHeight = 15.5 };
    private readonly Grid _bar = new() { Height = 10, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _note = ToolKit.Text(" ", 11, "Text2");
    private FilmstripView? _strip;
    private bool _loaded;
    private bool _silent;

    public SplitEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths) : base(host, operations, "Split", ContentSize(paths.Count))
    {
        _paths = paths;
        _state = new SplitState(paths);
        CanvasWell = false;
        SetCanvasPadding(0);

        var stack = new Grid();
        for (var i = 0; i < 6; i++) stack.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = MediaParts.IconHeader(Glyphs.Split, Path.GetFileName(paths[0]), out _duration);
        _title = (TextBlock)((StackPanel)header.Children[1]).Children[0];
        stack.Children.Add(header);

        _modePicker = new Segmented(Enum.GetValues<SplitMode>().Select(SplitSettings.Title).ToList(), 0, 8);
        _modePicker.Margin = new Thickness(0, 14, 0, 0);
        _modePicker.SelectionChanged += index => Edit(s => s with { Mode = (SplitMode)index });
        Grid.SetRow(_modePicker, 1);
        stack.Children.Add(_modePicker);

        _fewer = ToolKit.IconButton("minus", "Fewer parts", (_, _) => Edit(s => s with { Parts = Math.Max(s.Parts - 1, SplitPlan.MinimumEqualParts) }), 28);
        _more = ToolKit.IconButton("plus", "More parts", (_, _) => Edit(s => s with { Parts = Math.Min(s.Parts + 1, SplitPlan.MaximumEqualParts) }), 28);
        var stepper = new StackPanel { Orientation = Orientation.Horizontal };
        _partsValue.Width = 36;
        _partsValue.TextAlignment = TextAlignment.Center;
        _partsValue.VerticalAlignment = VerticalAlignment.Center;
        stepper.Children.Add(_fewer);
        stepper.Children.Add(_partsValue);
        stepper.Children.Add(_more);
        _partsRow = ToolKit.Row("Parts", stepper);

        _unit.Items.Add("seconds");
        _unit.Items.Add("minutes");
        _unit.SelectedIndex = 0;
        _unit.SelectionChanged += (_, _) => { if (!_silent) Edit(s => s with { IntervalInMinutes = _unit.SelectedIndex == 1 }); };
        ToolKit.Name(_interval, "Length");
        ToolKit.Name(_unit, "Unit");
        _interval.LostKeyboardFocus += (_, _) => CommitInterval();
        _interval.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitInterval(); };
        var every = new StackPanel { Orientation = Orientation.Horizontal };
        _unit.Margin = new Thickness(8, 0, 0, 0);
        every.Children.Add(_interval);
        every.Children.Add(_unit);
        _everyRow = ToolKit.Row("Every", every);

        ToolKit.Name(_times, "Split at");
        _times.TextChanged += (_, _) =>
        {
            _timesHint.Visibility = _times.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_silent) Edit(s => s with { TimesText = _times.Text });
        };
        _timesHint.IsHitTestVisible = false;
        _timesHint.Margin = new Thickness(9, 0, 0, 0);
        _timesHint.VerticalAlignment = VerticalAlignment.Center;
        var timesHost = new Grid();
        timesHost.Children.Add(_times);
        timesHost.Children.Add(_timesHint);
        _timesRow = ToolKit.Row("Split at", timesHost);

        var modeHost = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        modeHost.Children.Add(_partsRow);
        modeHost.Children.Add(_everyRow);
        modeHost.Children.Add(_timesRow);
        Grid.SetRow(modeHost, 2);
        stack.Children.Add(modeHost);

        _problem.Margin = new Thickness(0, 6, 0, 6);
        Grid.SetRow(_problem, 3);
        stack.Children.Add(_problem);

        var listPanel = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Top);
        listPanel.Children.Add(_bar);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        listPanel.Children.Add(scroll);
        var listCard = new Border
        {
            Child = listPanel,
            BorderBrush = ToolKit.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0, 10, 0, 0),
            ClipToBounds = true,
        };
        Grid.SetRow(listCard, 4);
        stack.Children.Add(listCard);
        _note.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_note, 5);
        stack.Children.Add(_note);
        Canvas = stack;

        Controls = Card;

        if (paths.Count > 1)
        {
            _strip = new FilmstripView(paths.Select(_ => (ImageSource?)null).ToList(), paths.Select(Path.GetFileName).ToList()!, withSameForAll: true,
                itemName: "file", placeholderGlyph: Glyphs.Film);
            _strip.Attach(_state.Edits);
            Filmstrip = _strip;
        }

        _state.Edits.Changed += Sync;
        Sync();
        Loaded += (_, _) => Load();
    }

    private void Edit(Func<SplitSettings, SplitSettings> change)
    {
        if (!_loaded) return;
        _state.Settings = change(_state.Settings);
    }

    private void CommitInterval()
    {
        if (_silent) return;
        if (double.TryParse(_interval.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value > 0) Edit(s => s with { Interval = value });
        else Sync();
    }

    protected override string PrimaryText => _state.Edits.IsBatch ? _state.Edits.PrimaryTitle("Split", "Split", "File") : "Split";
    protected override bool CanExport => _loaded && _state.Edits.EditedCount > 0;
    protected override bool HasReset => true;
    protected override void ResetEdits() => _state.Edits.Reset();

    private void Sync()
    {
        var i = _state.Index;
        var s = _state.Settings;
        _silent = true;
        _title.Text = Path.GetFileName(_paths[i]);
        _duration.Text = _state.Durations[i] > 0 ? MediaTime.Format(_state.Durations[i]) : " ";
        _modePicker.SetSilently((int)s.Mode);
        _partsRow.Visibility = s.Mode == SplitMode.EqualParts ? Visibility.Visible : Visibility.Collapsed;
        _everyRow.Visibility = s.Mode == SplitMode.EveryInterval ? Visibility.Visible : Visibility.Collapsed;
        _timesRow.Visibility = s.Mode == SplitMode.AtTimes ? Visibility.Visible : Visibility.Collapsed;
        _partsValue.Text = s.Parts.ToString(CultureInfo.CurrentCulture);
        _fewer.IsEnabled = s.Parts > SplitPlan.MinimumEqualParts;
        _more.IsEnabled = s.Parts < SplitPlan.MaximumEqualParts;
        if (!_interval.IsKeyboardFocused) _interval.Text = s.Interval.ToString("0.##", CultureInfo.CurrentCulture);
        _unit.SelectedIndex = s.IntervalInMinutes ? 1 : 0;
        if (_times.Text != s.TimesText) _times.Text = s.TimesText;
        _timesHint.Visibility = _times.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _silent = false;

        _problem.Text = _state.Problem() ?? "";
        ShowParts(_state.CurrentSegments, _state.Durations[i]);
        _note.Text = _state.Footer();
        Refresh();
    }

    private void ShowParts(IReadOnlyList<SplitSegment> segments, double duration)
    {
        _list.Children.Clear();
        _bar.Children.Clear();
        _bar.ColumnDefinitions.Clear();
        for (var n = 0; n < segments.Count; n++)
        {
            var segment = segments[n];
            var row = new Grid { Margin = new Thickness(0), Background = n % 2 == 0 ? ToolKit.Brush("Fill") : Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var name = ToolKit.Text($"Part {n + 1}", 12, "Text", FontWeights.Medium);
            var range = ToolKit.Text($"{MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}", 11, "Text3");
            var length = ToolKit.Text(MediaTime.Format(MediaTime.Length(segment.Start, segment.End)), 11, "Text");
            length.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(range, 1);
            Grid.SetColumn(length, 2);
            row.Children.Add(name);
            row.Children.Add(range);
            row.Children.Add(length);
            row.Margin = new Thickness(0);
            row.Children.OfType<TextBlock>().ToList().ForEach(t => t.Margin = new Thickness(10, 6, t == length ? 10 : 8, 6));
            ToolKit.Name(row, $"Part {n + 1}, {MediaTime.Format(segment.Start)} to {MediaTime.Format(segment.End)}");
            _list.Children.Add(row);

            // The bar shows where the cuts fall.
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(segment.Duration, 0.001), GridUnitType.Star) });
            var piece = new Border
            {
                Background = ToolKit.Brush(n % 2 == 0 ? "Accent" : "AccentHover"),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(n == 0 ? 10 : 1.5, 0, n == segments.Count - 1 ? 10 : 1.5, 0),
            };
            Grid.SetColumn(piece, n);
            _bar.Children.Add(piece);
        }
        _bar.Visibility = segments.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Load()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                _problem.Text = Guidance.LoadError(new OpenShelf.Convert.MissingToolException("ffmpeg"), "");
                _loaded = false;
                return;
            }
            for (var i = 0; i < _paths.Count; i++)
            {
                try
                {
                    var info = await Operations.GetInfoAsync(_paths[i], Lifetime);
                    _state.Durations[i] = info.Duration;
                    _state.HasVideo[i] = info.HasVideo;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { if (i == _state.Index) _problem.Text = Guidance.LoadError(e, "This file can’t be read."); }
            }
            _loaded = true;
            _state.Edits.ResetAll();   // refreshes the parts now the lengths are known
            Sync();
            if (_strip is not null) _ = LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadThumbnailsAsync()
    {
        for (var i = 0; i < _paths.Count; i++)
        {
            if (!_state.HasVideo[i]) continue;
            try { _strip?.SetThumbnail(i, MediaParts.Picture(await Operations.ExtractPosterAsync(_paths[i], 160, Lifetime))); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    protected override string? FirstDetail() => _state.Jobs() is { Count: 1 } one ? $"Exporting part 1 of {one[0].Segments.Count}…" : BatchDetail(0, _state.Jobs().Count);

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var jobs = _state.Jobs();
        var outcome = new SaveOutcome { SavedLabel = "saved", UnchangedLabel = "not split", Unchanged = _paths.Count - jobs.Count };
        var part = 0;
        return await ExportRunner.RunAsync(jobs, outcome, job => _paths[job.Index],
            async (job, _, progress, ct) =>
            {
                part = 0;
                var started = new Direct<int>(n => part = n);
                return await Operations.SplitAsync(_paths[job.Index], job.Segments, progress, started, ct);
            },
            (fraction, index) => Report(fraction, jobs.Count == 1 ? $"Exporting part {Math.Max(part, 1)} of {jobs[0].Segments.Count}…" : BatchDetail(index, jobs.Count)), cancel);
    }

    private sealed class Direct<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
        {
            if (token.StartsWith("parts=", StringComparison.Ordinal)) _state.Settings = _state.Settings with { Mode = SplitMode.EqualParts, Parts = int.Parse(token[6..], CultureInfo.InvariantCulture) };
            else if (token.StartsWith("every=", StringComparison.Ordinal)) _state.Settings = _state.Settings with { Mode = SplitMode.EveryInterval, Interval = double.Parse(token[6..], CultureInfo.InvariantCulture) };
            else if (token.StartsWith("at=", StringComparison.Ordinal)) _state.Settings = _state.Settings with { Mode = SplitMode.AtTimes, TimesText = token[3..].Replace(';', ',') };
            else if (token == "second") _state.Edits.Index = Math.Min(1, _paths.Count - 1);
            else if (token == "same") _state.Edits.SameForAll = true;
        }
    }

    internal SplitState State => _state;
}
