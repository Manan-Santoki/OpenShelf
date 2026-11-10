using System.Windows;
using System.Windows.Controls;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Re-encodes videos smaller, choosing a maximum resolution and a quality level, with the estimated size shown as the choices change.</summary>
internal sealed class VideoCompressEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static readonly Size ContentSize = new(450, 440);

    private readonly IReadOnlyList<string> _paths;
    private readonly CompressState _state;
    private readonly TextBlock _subtitle;
    private readonly Segmented _resolution;
    private readonly Segmented _quality;
    private readonly TextBlock _estimate = ToolKit.Text("Estimating…", 13, "Text2", FontWeights.SemiBold);
    private readonly TextBlock _saving = ToolKit.Text("", 12, "Text3");
    private readonly TextBlock _output = ToolKit.Text("", 11, "Text2", FontWeights.SemiBold);
    private readonly TextBlock _advice = new() { FontSize = 12, Foreground = ToolKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap, MinHeight = 34 };

    public VideoCompressEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths) : base(host, operations, "Compress Video", ContentSize)
    {
        _paths = paths;
        _state = new CompressState(paths, paths.Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0));
        CanvasWell = false;
        SetCanvasPadding(0);
        FitHeightToContent();

        var stack = new StackPanel();
        stack.Children.Add(MediaParts.IconHeader(Glyphs.Film, _state.Title, out _subtitle));

        _resolution = new Segmented(Enum.GetValues<CompressResolution>().Select(r => r.Title()).ToList(), 1, 12);
        _resolution.SelectionChanged += i => { _state.Resolution = (CompressResolution)i; Changed(); };
        var resolution = ToolKit.Row("Resolution", _resolution);
        resolution.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(resolution);

        _quality = new Segmented(Enum.GetValues<CompressQuality>().Select(q => q.Title()).ToList(), 0, 12);
        _quality.SelectionChanged += i => { _state.Quality = (CompressQuality)i; Changed(); };
        var quality = ToolKit.Row("Quality", _quality);
        quality.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(quality);

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = ToolKit.Icon(Glyphs.Compress, 13, "Accent");
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_estimate);
        text.Children.Add(_saving);
        Grid.SetColumn(text, 1);
        _output.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_output, 2);
        line.Children.Add(icon);
        line.Children.Add(text);
        line.Children.Add(_output);
        var summary = ToolKit.Card(line, 12);
        summary.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(summary);

        _advice.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_advice);
        stack.Children.Add(Card);
        Canvas = stack;

        Sync();
        Loaded += (_, _) => Load();
    }

    protected override string PrimaryText => "Compress";
    protected override bool CanExport => _state.CanCompress;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _state.Reset();
        _resolution.SetSilently((int)_state.Resolution);
        _quality.SetSilently((int)_state.Quality);
        Sync();
    }

    private void Changed()
    {
        _state.Notice = null;
        Sync();
    }

    private void Sync()
    {
        _subtitle.Text = _state.OriginalSummary;
        _estimate.Text = _state.EstimateText;
        _saving.Text = _state.SavingText;
        _saving.Foreground = _state.Sources is null || _state.IsSmaller ? ToolKit.Brush("Text3") : ToolKit.Brush("Danger");
        _estimate.Foreground = _state.LoadError is not null && _state.Sources is null ? ToolKit.Brush("Danger") : _state.Sources is null ? ToolKit.Brush("Text2") : ToolKit.Brush("Text");
        _output.Text = _state.OutputSummary;
        _advice.Text = _state.Notice ?? _state.SizeAdvice ?? "";
        Refresh();
    }

    private async void Load()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                _state.LoadError = Guidance.LoadError(new OpenShelf.Convert.MissingToolException("ffmpeg"), "");
                Sync();
                return;
            }
            var loaded = new List<VideoSourceInfo>();
            foreach (var path in _paths)
            {
                try { loaded.Add(await Operations.LoadCompressSourceAsync(path, Lifetime)); }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    _state.LoadError = Guidance.LoadError(e, _paths.Count == 1 ? "This video can’t be read." : $"“{Path.GetFileName(path)}” can’t be read.");
                    Sync();
                    return;
                }
            }
            _state.HevcAvailable = await Operations.CanEncodeHevcAsync(Lifetime);
            _state.Sources = loaded;
            Sync();
        }
        catch (OperationCanceledException) { }
    }

    protected override string? FirstDetail() => _paths.Count > 1 ? $"Compressing 1 of {_paths.Count}…" : null;

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var resolution = _state.Resolution;
        var quality = _state.Quality;
        var outcome = new SaveOutcome { SavedLabel = "saved", UnchangedLabel = "already as small as it gets" };
        return await ExportRunner.RunAsync(_paths, outcome, p => p,
            async (path, _, progress, ct) => await Operations.CompressAsync(path, resolution, quality, progress, ct) is { } saved ? [saved] : [],
            (fraction, index) => Report(fraction, _paths.Count > 1 ? $"Compressing {index + 1} of {_paths.Count}…" : null), cancel);
    }

    protected override void NothingSaved(SaveOutcome outcome)
    {
        _state.Notice = CompressState.NotSmaller(outcome.Unchanged);
        Sync();
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "720": _resolution.SelectedIndex = (int)CompressResolution.P720; break;
                case "480": _resolution.SelectedIndex = (int)CompressResolution.P480; break;
                case "low": _quality.SelectedIndex = (int)CompressQuality.Low; break;
                case "medium": _quality.SelectedIndex = (int)CompressQuality.Medium; break;
                case "original": _resolution.SelectedIndex = (int)CompressResolution.Original; break;
            }
    }

    internal CompressState State => _state;
}
