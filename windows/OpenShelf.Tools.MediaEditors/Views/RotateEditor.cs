using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Tools.Media;
using OpenShelf.Ui;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Turns a video a quarter or half turn, or mirrors it, with a preview of the result. Given several videos a filmstrip
/// moves between them, and one turn applies to all of them or each can have its own.</summary>
internal sealed class RotateEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static Size ContentSize(int count) => new(470, count > 1 ? 560 + FilmstripView.StripHeight : 560);

    private readonly IReadOnlyList<string> _paths;
    private readonly RotateState _state;
    private readonly BitmapSource?[] _frames;
    private readonly string?[] _errors;
    private readonly HashSet<int> _loading = [];
    private readonly Grid _stage = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly RotateTransform _turn = new(0);
    private readonly ScaleTransform _mirror = new(1, 1);
    private readonly Border _overlay = new();
    private readonly TextBlock _caption = ToolKit.Text("", 11, "Text2");
    private readonly List<(VideoRotation Rotation, Button Button)> _buttons = [];
    private FilmstripView? _strip;
    private int _shown = -1;
    private VideoRotation? _shownRotation;
    private bool _checked;

    public RotateEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths) : base(host, operations, "Rotate", ContentSize(paths.Count))
    {
        _paths = paths;
        _state = new RotateState(paths.Count);
        _frames = new BitmapSource?[paths.Count];
        _errors = new string?[paths.Count];

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _image.RenderTransform = new TransformGroup { Children = [_mirror, _turn] };
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _stage.Children.Add(_image);
        _stage.Children.Add(_overlay);
        _stage.SizeChanged += (_, _) => Layout();
        Canvas = _stage;
        SetCanvasPadding(0);

        var row = new UniformGrid { Rows = 1 };
        string[] glyphs = [Glyphs.RotateLeft, Glyphs.RotateRight, Glyphs.Upside, Glyphs.Flip];
        var index = 0;
        foreach (var rotation in Enum.GetValues<VideoRotation>())
        {
            var value = rotation;
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var glyph = ToolKit.Icon(glyphs[index++], 16, "Text");
            glyph.ClearValue(TextBlock.ForegroundProperty);
            glyph.Margin = new Thickness(0, 0, 0, 4);
            var title = new TextBlock { Text = rotation.Title(), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(glyph);
            content.Children.Add(title);
            var name = rotation == VideoRotation.FlipHorizontal ? "Flip horizontally" : "Rotate " + rotation.Title();
            var button = MediaParts.Choice(content, false, (_, _) => _state.Choose(value), name);
            button.Height = 52;
            button.Margin = new Thickness(index == 1 ? 0 : 4, 0, index == 4 ? 0 : 4, 0);
            _buttons.Add((rotation, button));
            row.Children.Add(button);
        }

        var controls = new StackPanel();
        controls.Children.Add(row);
        _caption.Margin = new Thickness(0, 10, 0, 0);
        controls.Children.Add(_caption);
        controls.Children.Add(Card);
        Controls = controls;

        if (paths.Count > 1)
        {
            _strip = new FilmstripView(paths.Select(_ => (ImageSource?)null).ToList(), paths.Select(Path.GetFileName).ToList()!, withSameForAll: true,
                itemName: "video", placeholderGlyph: Glyphs.Film);
            _strip.Attach(_state.Edits);
            Filmstrip = _strip;
        }

        _state.Edits.Changed += Sync;
        Sync();
        Loaded += (_, _) => Check();
    }

    protected override string PrimaryText => _state.Edits.IsBatch ? _state.Edits.PrimaryTitle("Rotate", "Apply", "Video") : "Apply";
    protected override bool CanExport => _checked && _state.Edits.EditedCount > 0;
    protected override bool HasReset => true;

    protected override void ResetEdits() => _state.Edits.Reset();

    /// <summary>Picks the choice shown, the preview for the shown video, and the caption.</summary>
    private void Sync()
    {
        var i = _state.Edits.Index;
        var rotation = _state.Edits.Current;
        foreach (var (r, button) in _buttons)
        {
            var selected = r == rotation;
            button.Style = ToolKit.Style(selected ? "AccentBtn" : "Btn");
            button.Padding = new Thickness(0);
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "Selected" : "");
        }
        _caption.Text = RotateState.Caption(rotation);
        if (_shown != i)
        {
            _shown = i;
            _shownRotation = null;
            ShowFrame(i);
        }
        Layout();
        Refresh();
    }

    private void ShowFrame(int index)
    {
        if (!_checked) return;
        if (_errors[index] is { } error)
        {
            _image.Source = null;
            _overlay.Child = MediaParts.Notice(error);
            _overlay.Visibility = Visibility.Visible;
            return;
        }
        _overlay.Visibility = Visibility.Collapsed;
        _image.Source = _frames[index];
        if (_frames[index] is null) _ = LoadFrameAsync(index);
    }

    /// <summary>Sizes the picture so the turned frame fits the stage, and turns it (springing between turns).</summary>
    private void Layout()
    {
        if (_image.Source is not BitmapSource frame || _stage.ActualWidth < 40) return;
        var rotation = _state.Edits.Current;
        var quarter = rotation is VideoRotation.Left or VideoRotation.Right;
        double w = frame.PixelWidth, h = frame.PixelHeight;
        var rotated = quarter ? new Size(h, w) : new Size(w, h);
        var scale = Math.Min((_stage.ActualWidth - 24) / rotated.Width, (_stage.ActualHeight - 24) / rotated.Height);
        _image.Width = w * scale;
        _image.Height = h * scale;
        var target = rotation.Degrees();
        var flip = rotation == VideoRotation.FlipHorizontal ? -1 : 1;
        if (_shownRotation == rotation) return;
        if (_shownRotation is null)
        {
            _turn.Angle = target;
            _mirror.ScaleX = flip;
        }
        else
        {
            Motion.Animate(_turn, RotateTransform.AngleProperty, target, 380, Motion.Spring);
            Motion.Animate(_mirror, ScaleTransform.ScaleXProperty, flip, 260, Motion.EaseInOut);
        }
        _shownRotation = rotation;
    }

    private async void Check()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                for (var i = 0; i < _paths.Count; i++) { _errors[i] = Guidance.LoadError(new OpenShelf.Convert.MissingToolException("ffmpeg"), ""); _state.Failed[i] = true; }
            }
            else
            {
                // Every video that can be read gets turned; find out which can't.
                for (var i = 0; i < _paths.Count; i++)
                {
                    try
                    {
                        var info = await Operations.GetInfoAsync(_paths[i], Lifetime);
                        if (!info.HasVideo) throw OpenShelf.Convert.ConversionException.Failed($"“{Path.GetFileName(_paths[i])}” has no video track.");
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { _errors[i] = Guidance.LoadError(e, "This video can’t be read."); _state.Failed[i] = true; }
                }
            }
            _checked = true;
            _shown = -1;
            _state.Edits.ResetAll();   // re-reads which videos are edited (and shows the first)
            if (_strip is not null) _ = LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadFrameAsync(int index)
    {
        if (!_loading.Add(index)) return;
        try
        {
            var png = await Operations.ExtractPosterAsync(_paths[index], 1200, Lifetime);
            _frames[index] = MediaParts.Picture(png);
            if (_shown == index) { _image.Source = _frames[index]; Layout(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _errors[index] = Guidance.LoadError(e, "This video can’t be read.");
            _state.Failed[index] = true;
            if (_shown == index) ShowFrame(index);
            Refresh();
        }
        finally { _loading.Remove(index); }
    }

    private async Task LoadThumbnailsAsync()
    {
        for (var i = 0; i < _paths.Count; i++)
        {
            if (_errors[i] is not null) continue;
            try { _strip?.SetThumbnail(i, MediaParts.Picture(await Operations.ExtractPosterAsync(_paths[i], 160, Lifetime))); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    protected override string? FirstDetail() => BatchDetail(0, _state.Jobs().Count);

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var jobs = _state.Jobs();
        var outcome = new SaveOutcome { SavedLabel = "rotated", UnchangedLabel = "not rotated", Unchanged = _paths.Count - jobs.Count };
        return await ExportRunner.RunAsync(jobs, outcome, i => _paths[i],
            async (i, _, progress, ct) => [await Operations.RotateAsync(_paths[i], _state.Edits[i], progress, ct)],
            (fraction, index) => Report(fraction, BatchDetail(index, jobs.Count)), cancel);
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "left": _state.Choose(VideoRotation.Left); break;
                case "right": _state.Choose(VideoRotation.Right); break;
                case "180": _state.Choose(VideoRotation.UpsideDown); break;
                case "flip": _state.Choose(VideoRotation.FlipHorizontal); break;
                case "second": _state.Edits.Index = Math.Min(1, _paths.Count - 1); break;
                case "own": _state.Edits.SameForAll = false; break;
            }
    }

    internal RotateState State => _state;
}
