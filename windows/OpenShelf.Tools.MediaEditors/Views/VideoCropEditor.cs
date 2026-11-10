using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Tools.Imaging;
using OpenShelf.Tools.Media;
using ImageCropMath = OpenShelf.Tools.Imaging.CropMath;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Crops a video's frame with the same handles and presets as the image Crop editor. Given several videos a filmstrip moves
/// between them and one crop can apply to all of them (as fractions of each frame) or each can have its own.</summary>
internal sealed class VideoCropEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static Size ContentSize(int count) => new(520, count > 1 ? 640 + FilmstripView.StripHeight : 640);

    private readonly IReadOnlyList<string> _paths;
    private readonly CropState _state;
    private readonly VideoCropSurface _surface = new();
    private readonly Segmented _ratioPicker;
    private readonly NumberBox _width = new(72);
    private readonly NumberBox _height = new(72);
    private readonly BitmapSource?[] _frames;
    private readonly string?[] _errors;
    private readonly HashSet<int> _loading = [];
    private readonly Border _overlay = new();
    private FilmstripView? _strip;
    private int _shown = -1;
    private bool _sized;

    public VideoCropEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths)
        : base(host, operations, paths.Count > 1 ? "Crop Videos" : "Crop Video", ContentSize(paths.Count))
    {
        _paths = paths;
        _state = new CropState(paths.Count);
        _frames = new BitmapSource?[paths.Count];
        _errors = new string?[paths.Count];

        var stage = new Grid();
        stage.Children.Add(_surface);
        stage.Children.Add(_overlay);
        Canvas = stage;
        SetCanvasPadding(0);
        _surface.CropChanged += crop => _state.Edits.Current = crop;

        _ratioPicker = new Segmented(CropRatio.All.Select(r => r.Title).ToList(), 0, 8);
        _ratioPicker.SelectionChanged += index => _state.Snap(CropRatio.All[index]);
        _width.ValueCommitted += w => _state.SetWidth(w);
        _height.ValueCommitted += h => _state.SetHeight(h);

        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(Label("W"));
        size.Children.Add(_width);
        size.Children.Add(Label("H", 10));
        size.Children.Add(_height);
        size.Children.Add(Label("px", 8));
        var controls = new StackPanel();
        controls.Children.Add(ToolKit.Row("Aspect ratio", _ratioPicker));
        var sizeRow = ToolKit.Row("Size", size);
        sizeRow.Margin = new Thickness(0, 10, 0, 0);
        controls.Children.Add(sizeRow);
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
        Loaded += (_, _) => Load();
    }

    private static TextBlock Label(string text, double left = 0)
    {
        var label = ToolKit.Text(text, 12, "Text2");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(left, 0, 6, 0);
        return label;
    }

    protected override string PrimaryText => _state.Edits.IsBatch ? _state.Edits.PrimaryTitle("Crop", "Apply", "Video") : "Apply";
    // Cropping to the whole frame would only re-encode the video.
    protected override bool CanExport => _state.Edits.EditedCount > 0;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _ratioPicker.SetSilently(0);
        _surface.Ratio = null;
        _state.Reset();
    }

    private void Sync()
    {
        var i = _state.Edits.Index;
        if (_shown != i)
        {
            _shown = i;
            ShowFrame(i);
        }
        _surface.Ratio = _state.Ratio.Value;
        _surface.Crop = _state.Edits.Current;
        _width.Value = (int)Math.Round(_state.Edits.Current.Width);
        _height.Value = (int)Math.Round(_state.Edits.Current.Height);
        Refresh();
    }

    private void ShowFrame(int index)
    {
        if (!_sized) return;
        if (_errors[index] is { } error)
        {
            _overlay.Child = MediaParts.Notice(error);
            _overlay.Visibility = Visibility.Visible;
            return;
        }
        _overlay.Visibility = Visibility.Collapsed;
        var bounds = _state.Bounds[index];
        _surface.SetImage(_frames[index], new Size(bounds.Width, bounds.Height));
        if (_frames[index] is null) _ = LoadFrameAsync(index);
    }

    private async void Load()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                for (var i = 0; i < _paths.Count; i++) _errors[i] = Guidance.LoadError(new OpenShelf.Convert.MissingToolException("ffmpeg"), "");
            }
            else
            {
                for (var i = 0; i < _paths.Count; i++)
                {
                    try
                    {
                        var info = await Operations.GetInfoAsync(_paths[i], Lifetime);
                        if (info.DisplaySize is not { } size) throw OpenShelf.Convert.ConversionException.Failed($"“{Path.GetFileName(_paths[i])}” has no video track.");
                        _state.SetSize(i, size.Width, size.Height);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { _errors[i] = Guidance.LoadError(e, "This video can’t be read."); }
                }
            }
            _sized = true;
            _shown = -1;
            Sync();
            if (_strip is not null) _ = LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadFrameAsync(int index)
    {
        if (!_loading.Add(index)) return;
        try
        {
            var png = await Operations.ExtractPosterAsync(_paths[index], 1600, Lifetime);
            _frames[index] = MediaParts.Picture(png);
            if (_shown == index) _surface.SetImage(_frames[index], new Size(_state.Bounds[index].Width, _state.Bounds[index].Height));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _errors[index] = Guidance.LoadError(e, "This video can’t be read.");
            if (_shown == index) ShowFrame(index);
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
        var outcome = new SaveOutcome { SavedLabel = "cropped", UnchangedLabel = "not cropped", Unchanged = _paths.Count - jobs.Count };
        return await ExportRunner.RunAsync(jobs, outcome, i => _paths[i],
            async (i, _, progress, ct) => [await Operations.CropAsync(_paths[i], _state.Fraction(i), progress, ct)],
            (fraction, index) => Report(fraction, BatchDetail(index, jobs.Count)), cancel);
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "half":
                    var b = _state.CurrentBounds;
                    _state.Edits.Current = new Rect(0, 0, b.Width, Math.Round(b.Height / 2));
                    break;
                case "ratio": _ratioPicker.SelectedIndex = 2; break;
                case "second": _state.Edits.Index = Math.Min(1, _paths.Count - 1); break;
                case "own": _state.Edits.SameForAll = false; break;
            }
    }

    internal CropState State => _state;
}

/// <summary>The frame with the crop on it: the outside dimmed, an outline, eight handles. Dragging a handle resizes, dragging inside moves.</summary>
internal sealed class VideoCropSurface : ImageSurface
{
    private Rect _crop;
    private CropHandle? _dragHandle;
    private bool _dragging;
    private Rect _dragStart;
    private Point _mouseStart;

    public VideoCropSurface() => Inset = 18;

    public Rect Crop
    {
        get => _crop;
        set { _crop = value; InvalidateVisual(); }
    }

    public double? Ratio { get; set; }

    public event Action<Rect>? CropChanged;

    public void SetImage(BitmapSource? source, Size size)
    {
        _dragging = false;
        ImageSize = size;
        Source = source;
    }

    protected override void DrawOverlay(DrawingContext dc, Rect imageRect)
    {
        var crop = ToView(_crop);
        var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(imageRect), new RectangleGeometry(crop));
        dc.DrawGeometry(DimBrush, null, dim);
        var accent = ToolKit.Brush("Accent");
        dc.DrawRectangle(null, new Pen(accent, 2), crop);
        foreach (var handle in CropHandles.All)
        {
            var center = handle.PositionIn(crop);
            var size = handle.IsCorner() ? new Size(12, 12) : handle.MovesTop() || handle.MovesBottom() ? new Size(24, 6) : new Size(6, 24);
            dc.DrawRoundedRectangle(accent, new Pen(HandleEdge, 1), new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height), 3, 3);
        }
    }

    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));
    private static readonly Brush HandleEdge = Frozen(new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)));
    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    private CropHandle? HandleAt(Point view)
    {
        var crop = ToView(_crop);
        CropHandle? best = null;
        var bestDistance = 14.0;
        foreach (var handle in CropHandles.All)
        {
            var distance = (handle.PositionIn(crop) - view).Length;
            if (distance < bestDistance) { best = handle; bestDistance = distance; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var view = e.GetPosition(this);
        var handle = HandleAt(view);
        if (handle is null && !ToView(_crop).Contains(view)) return;
        _dragHandle = handle;
        _dragging = true;
        _dragStart = _crop;
        _mouseStart = ToImage(view, clamp: false);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var view = e.GetPosition(this);
        if (!_dragging)
        {
            Cursor = CursorFor(HandleAt(view), ToView(_crop).Contains(view));
            return;
        }
        var delta = ToImage(view, clamp: false) - _mouseStart;
        var bounds = new Rect(0, 0, ImageSize.Width, ImageSize.Height);
        _crop = ImageCropMath.Adjusted(_dragStart, _dragHandle, delta, Ratio, bounds, CropState.MinimumSize);
        InvalidateVisual();
        CropChanged?.Invoke(_crop);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    private static Cursor CursorFor(CropHandle? handle, bool inside) => handle switch
    {
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
        _ => inside ? Cursors.SizeAll : Cursors.Arrow,
    };
}
