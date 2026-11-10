using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Speeds up or slows down videos and audio files, 0.25x to 4x, optionally keeping the pitch of the sound.</summary>
internal sealed class SpeedEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static readonly Size ContentSize = new(430, 410);

    private readonly IReadOnlyList<string> _paths;
    private readonly SpeedState _state = new();
    private readonly TextBlock _summary;
    private readonly Slider _slider;
    private readonly TextBlock _readout = ToolKit.Text("", 12, "Text2");
    private readonly SwitchRow _pitch;
    private readonly List<(double Preset, Button Button)> _presets = [];
    private bool _silent;

    public SpeedEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths) : base(host, operations, "Speed", ContentSize)
    {
        _paths = paths;
        CanvasWell = false;
        SetCanvasPadding(0);
        FitHeightToContent();

        var stack = new StackPanel();
        var title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
        stack.Children.Add(MediaParts.IconHeader(Glyphs.Speed, title, out _summary));

        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var preset in SpeedState.Presets)
        {
            var value = preset;
            var button = MediaParts.Choice(SpeedState.Label(preset), false, (_, _) => Choose(value), MediaFormats.SpeedSuffix(preset) + " speed");
            button.Height = 28;
            button.FontSize = 12;
            button.FontWeight = FontWeights.SemiBold;
            button.Margin = new Thickness(0, 0, 6, 0);
            _presets.Add((preset, button));
            presets.Children.Add(button);
        }
        stack.Children.Add(presets);

        _slider = new Slider
        {
            Minimum = MediaFormats.MinimumSpeed,
            Maximum = MediaFormats.MaximumSpeed,
            SmallChange = SpeedState.Step,
            LargeChange = 0.25,
            TickFrequency = SpeedState.Step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolKit.Name(_slider, "Speed");
        _slider.ValueChanged += (_, e) => { if (!_silent) Choose(e.NewValue); };
        _readout.HorizontalAlignment = HorizontalAlignment.Right;
        _readout.VerticalAlignment = VerticalAlignment.Center;
        _readout.FontWeight = FontWeights.SemiBold;
        var sliderRow = new Grid { MinHeight = 28, Margin = new Thickness(0, 14, 0, 0) };
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var label = ToolKit.Text("Speed", 12, "Text2");
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_slider, 1);
        Grid.SetColumn(_readout, 2);
        sliderRow.Children.Add(label);
        sliderRow.Children.Add(_slider);
        sliderRow.Children.Add(_readout);
        stack.Children.Add(sliderRow);

        _pitch = new SwitchRow("Keep audio pitch", true, "Off makes voices sound higher when sped up and lower when slowed down");
        _pitch.Margin = new Thickness(0, 12, 0, 0);
        _pitch.Toggled += () => _state.KeepsPitch = _pitch.IsChecked;
        stack.Children.Add(_pitch);

        stack.Children.Add(Card);
        Canvas = stack;
        Sync();
        Loaded += (_, _) => LoadLengths();
    }

    protected override string PrimaryText => "Save";
    protected override bool CanExport => _state.CanSave;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _state.Reset();
        _pitch.SetSilently(true);
        Sync();
    }

    private void Choose(double rate)
    {
        _state.SetRate(rate);
        Sync();
    }

    private void Sync()
    {
        _silent = true;
        _slider.Value = _state.Rate;
        _silent = false;
        _readout.Text = SpeedState.Label(_state.Rate);
        _summary.Text = _state.DurationSummary;
        foreach (var (preset, button) in _presets)
        {
            var selected = SpeedState.IsPreset(_state.Rate, preset);
            button.Style = ToolKit.Style(selected ? "AccentBtn" : "Btn");
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "Selected" : "");
        }
        Refresh();
    }

    private async void LoadLengths()
    {
        double total = 0;
        foreach (var path in _paths)
        {
            try { total += (await Operations.GetInfoAsync(path, Lifetime)).Duration; }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* left out of the total */ }
        }
        _state.TotalDuration = total;
        Sync();
    }

    protected override string? FirstDetail() => BatchDetail(0, _paths.Count);

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var rate = _state.Rate;
        var keeps = _state.KeepsPitch;
        var outcome = new SaveOutcome { SavedLabel = "saved" };
        return await ExportRunner.RunAsync(_paths, outcome, p => p,
            async (path, _, progress, ct) => [await Operations.ChangeSpeedAsync(path, rate, keeps, progress, ct)],
            (fraction, index) => Report(fraction, BatchDetail(index, _paths.Count)), cancel);
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
        {
            if (token.StartsWith("rate=", StringComparison.Ordinal)) Choose(double.Parse(token[5..], CultureInfo.InvariantCulture));
            else if (token == "nopitch") { _pitch.IsChecked = false; }
        }
    }

    internal SpeedState State => _state;
}
