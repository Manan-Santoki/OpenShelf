using System.Windows;
using System.Windows.Controls;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>The "Compress" tool: smaller JPEG/PNG/WebP copies of one or more images, by quality or by a target size.
/// The estimate is a real encode of the first image, scaled to the batch.</summary>
public sealed class CompressEditor : EditorWindow, IEditorDemo
{
    public static readonly Size ContentSize = new(450, 440);

    private static readonly int?[] MaxSizes = [null, 4096, 2048, 1600, 1080];

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly long _totalBytes;
    private CompressOptions _options = new();
    private long? _estimate;
    private CancellationTokenSource? _estimating;
    private MagickImage? _firstUpright;

    private readonly Segmented _modePicker;
    private readonly LabeledSlider _slider;
    private readonly Segmented _formatPicker;
    private readonly ComboBox _maxSize = new() { Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _estimateText = ToolKit.Text("Estimating…", 12, "Text2");
    private readonly TextBlock _savingText = ToolKit.Text("", 12, "Text2");
    private readonly TextBlock _note = ToolKit.Status(2);
    private string? _notice;

    public CompressEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Compress", ContentSize)
    {
        _images = images;
        _totalBytes = images.Sum(i => i.FileBytes);
        var first = images[0];

        CanvasWell = false;
        SetCanvasPadding(0);
        var stack = new StackPanel();
        stack.Children.Add(EditorParts.Header(first.Preview, images.Count == 1 ? first.Name : $"{images.Count} images",
            images.Count == 1 ? $"{first.Width} × {first.Height} · {EditorParts.Bytes(_totalBytes)}" : $"{EditorParts.Bytes(_totalBytes)} in total",
            images.Count > 1 ? images.Count : null));

        _modePicker = new Segmented(["Quality", "Target size"], 0, 22);
        _modePicker.SelectionChanged += index =>
        {
            _options = _options with { Mode = (CompressMode)index };
            SyncSlider();
            Changed();
        };
        _modePicker.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_modePicker);

        _slider = new LabeledSlider("Quality", 10, 100, 75, "%");
        _slider.Margin = new Thickness(0, 12, 0, 0);
        _slider.ValueChanged += v =>
        {
            _options = _options.Mode == CompressMode.Quality ? _options with { Quality = Math.Round(v) } : _options with { TargetPercent = Math.Round(v) };
            Changed();
        };
        stack.Children.Add(_slider);

        _formatPicker = new Segmented(["Keep", "JPEG"], 0, 20);
        _formatPicker.SelectionChanged += index => { _options = _options with { Format = (CompressFormat)index }; Changed(); };
        var format = ToolKit.Row("Format", _formatPicker);
        format.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(format);

        foreach (var size in MaxSizes) _maxSize.Items.Add(size is null ? "Original" : $"{size} px");
        _maxSize.SelectedIndex = 0;
        _maxSize.SelectionChanged += (_, _) =>
        {
            _options = _options with { MaxDimension = MaxSizes[Math.Max(0, _maxSize.SelectedIndex)] };
            Changed();
        };
        var max = ToolKit.Row("Max size", _maxSize);
        max.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(max);

        var summary = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(ToolKit.Glyph("", 13, "Accent"));
        _estimateText.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(_estimateText);
        _savingText.Margin = new Thickness(0);
        line.Children.Add(_savingText);
        summary.Children.Add(line);
        var card = ToolKit.Card(summary, 10);
        card.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(card);

        _note.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_note);
        Canvas = stack;
        Changed();
        Loaded += (_, _) => UpdateEstimate();
    }

    private void SyncSlider()
    {
        if (_options.Mode == CompressMode.Quality) _slider.Configure("Quality", 10, 100, _options.Quality);
        else _slider.Configure("Target size", 10, 90, _options.TargetPercent);
    }

    private void Changed()
    {
        _notice = null;
        UpdateNote();
        UpdateEstimate();
        Refresh();
    }

    private void UpdateNote()
    {
        if (_notice is not null)
        {
            _note.Text = _notice;
            _note.Foreground = ToolKit.Brush("Danger");
        }
        else
        {
            _note.Text = CompressOperation.FormatNote(_images, _options.Format) ?? "";
            _note.Foreground = ToolKit.Brush("Text3");
        }
    }

    /// <summary>Encodes the first image in the background and scales its size to the whole batch.</summary>
    private void UpdateEstimate()
    {
        _estimating?.Cancel();
        _estimate = null;
        _estimateText.Text = "Estimating…";
        _savingText.Text = "";
        var cancel = _estimating = new CancellationTokenSource();
        var options = _options;
        var first = _images[0];
        var total = _totalBytes;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cancel.Token);
                _firstUpright ??= ImageFiles.ReadUpright(first.Path);
                MagickImage copy;
                lock (_firstUpright) copy = (MagickImage)_firstUpright.Clone();
                using (copy)
                {
                    var (data, _) = CompressOperation.Compress(copy, first.Path, options, first.IsTransparent, first.Format);
                    cancel.Token.ThrowIfCancellationRequested();
                    var estimate = first.FileBytes > 0 ? (long)(data.Length * (double)total / first.FileBytes) : data.Length;
                    await Dispatcher.InvokeAsync(() => { if (!cancel.IsCancellationRequested) ShowEstimate(estimate); });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { await Dispatcher.InvokeAsync(() => _estimateText.Text = "Couldn’t estimate"); }
        });
    }

    private void ShowEstimate(long estimate)
    {
        _estimate = estimate;
        var saving = _totalBytes > 0 ? 100 - (int)(estimate * 100.0 / _totalBytes) : 0;
        _estimateText.Text = _images.Count == 1 ? $"≈ {EditorParts.Bytes(estimate)}" : $"≈ {EditorParts.Bytes(estimate)} for {_images.Count} images";
        _estimateText.FontWeight = FontWeights.SemiBold;
        _savingText.Text = saving > 0 ? $"  ({saving}% smaller)" : "  (no smaller)";
        _savingText.Foreground = saving > 0 ? ToolKit.Brush("Text3") : ToolKit.Brush("Danger");
        Refresh();
    }

    /// <summary>For a single image the estimate is the real result, so a copy that would not be smaller is known before anything is saved.</summary>
    private bool IsKnownNotSmaller => _images.Count == 1 && _estimate is { } e && !CompressOperation.IsWorthSaving(e, _images[0].FileBytes);

    protected override string PrimaryTitle => "Compress";
    protected override bool CanApply => !IsKnownNotSmaller;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _options = new CompressOptions();
        _modePicker.SetSilently(0);
        _formatPicker.SetSilently(0);
        _maxSize.SelectedIndex = 0;
        SyncSlider();
        Changed();
    }

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "target":
                    _options = _options with { Mode = CompressMode.TargetSize, TargetPercent = 30, MaxDimension = 2048 };
                    _modePicker.SetSilently(1);
                    _maxSize.SelectedIndex = 2;
                    SyncSlider();
                    Changed();
                    break;
            }
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        CompressOperation.Run(_images.Select(i => i.Path).ToList(), _options, progress, cancel);

    protected override void NothingSaved(SaveOutcome outcome)
    {
        _notice = CompressOperation.NotSmallerMessage(outcome.Unchanged);
        UpdateNote();
    }
}
