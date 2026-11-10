using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>Crops images to a rectangle chosen with handles, an aspect preset or exact pixel dimensions. Given several images a filmstrip
/// moves between them, and one crop can apply to all of them ("the top half") or each can have its own.</summary>
public sealed class CropEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(470, count > 1 ? 640 + FilmstripView.StripHeight : 640);

    private const double MinimumSize = 16;

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly FileEdits<Rect> _crops;
    private readonly CropSurface _surface = new();
    private readonly Segmented _ratioPicker;
    private readonly NumberBox _width = new(72);
    private readonly NumberBox _height = new(72);
    private CropRatio _ratio = CropRatio.Free;
    private int _shown = -1;

    public CropEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, images.Count > 1 ? "Crop Images" : "Crop Image", ContentSize(images.Count))
    {
        _images = images;
        Rect Bounds(int i) => new(0, 0, images[i].Width, images[i].Height);
        _crops = new FileEdits<Rect>(images.Count, Bounds, (i, crop) => crop != Bounds(i),
            carry: (crop, from, to) => CropMath.Mapped(CropMath.Unit(crop, Bounds(from)), Bounds(to), _ratio.Value),
            sameForAll: true);

        Canvas = _surface;
        SetCanvasPadding(0);
        _surface.CropChanged += crop => _crops.Current = crop;

        _ratioPicker = new Segmented(CropRatio.All.Select(r => r.Title).ToList(), 0, 8);
        _ratioPicker.SelectionChanged += index =>
        {
            _ratio = CropRatio.All[index];
            _surface.Ratio = _ratio.Value;
            if (_ratio.Value is { } value)
            {
                var c = _crops.Current;
                _crops.Current = CropMath.Largest(value, new Point(c.Left + c.Width / 2, c.Top + c.Height / 2), Bounds(_crops.Index));
            }
        };
        _width.ValueCommitted += w => _crops.Current = CropMath.Resized(_crops.Current, w, null, _ratio.Value, Bounds(_crops.Index), MinimumSize);
        _height.ValueCommitted += h => _crops.Current = CropMath.Resized(_crops.Current, null, h, _ratio.Value, Bounds(_crops.Index), MinimumSize);

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
        Controls = controls;

        if (images.Count > 1)
        {
            var strip = new FilmstripView(images.Select(i => (System.Windows.Media.ImageSource?)i.Thumbnail).ToList(), images.Select(i => i.Name).ToList(), withSameForAll: true);
            strip.Attach(_crops);
            Filmstrip = strip;
        }

        _crops.Changed += OnCropsChanged;
        OnCropsChanged();
    }

    private static TextBlock Label(string text, double left = 0)
    {
        var label = ToolKit.Text(text, 12, "Text2");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(left, 0, 6, 0);
        return label;
    }

    private void OnCropsChanged()
    {
        var image = _images[_crops.Index];
        if (_shown != _crops.Index)
        {
            _shown = _crops.Index;
            _surface.SetImage(image.Preview, new Size(image.Width, image.Height));
        }
        _surface.Crop = _crops.Current;
        _width.Value = (int)Math.Round(_crops.Current.Width);
        _height.Value = (int)Math.Round(_crops.Current.Height);
        Refresh();
    }

    protected override string PrimaryTitle => _crops.IsBatch ? _crops.PrimaryTitle("Crop") : "Apply";

    // Cropping to the whole image would only save a copy of it.
    protected override bool CanApply => _crops.EditedCount > 0;

    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _ratio = CropRatio.Free;
        _surface.Ratio = null;
        _ratioPicker.SetSilently(0);
        _crops.Reset();
    }

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "half": _crops.Current = new Rect(0, 0, _images[_crops.Index].Width, Math.Round(_images[_crops.Index].Height / 2.0)); break;
                case "ratio": _ratioPicker.SelectedIndex = 2; break;
                case "second": _crops.Index = Math.Min(1, _images.Count - 1); break;
                case "own": _crops.SameForAll = false; break;
            }
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        var jobs = _images.Select((image, i) => (image.Path, _crops[i], new Size(image.Width, image.Height))).ToList();
        return CropOperation.Run(jobs, progress, cancel);
    }
}

/// <summary>The picture with the crop on it: the outside dimmed, an outline, eight handles. Dragging a handle resizes, dragging inside moves.</summary>
internal sealed class CropSurface : ImageSurface
{
    private Rect _crop;
    private CropHandle? _dragHandle;
    private bool _dragging;
    private Rect _dragStart;
    private Point _mouseStart;

    public CropSurface() => Inset = 18;

    public Rect Crop
    {
        get => _crop;
        set { _crop = value; InvalidateVisual(); }
    }

    public double? Ratio { get; set; }

    public event Action<Rect>? CropChanged;

    public void SetImage(System.Windows.Media.Imaging.BitmapSource source, Size size)
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
        var bestDistance = 14.0;   // a generous target
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
        var point = ToImage(view, clamp: false);
        var delta = point - _mouseStart;
        var bounds = new Rect(0, 0, ImageSize.Width, ImageSize.Height);
        _crop = CropMath.Adjusted(_dragStart, _dragHandle, delta, Ratio, bounds, 16);
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
