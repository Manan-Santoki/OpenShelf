using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageMagick;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>Hides parts of images: drag regions, then hide each with a black box, pixelation or blur. Redacted copies lose their metadata.
/// Given several images a filmstrip moves between them; each keeps its own areas, undo history and selection, and only images with areas are saved.</summary>
public sealed class RedactEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(580, count > 1 ? 680 + FilmstripView.StripHeight : 680);

    private const int PreviewLongSide = 1600;

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly FileEdits<IReadOnlyList<RedactRegion>> _regions;
    private readonly EditHistory<IReadOnlyList<RedactRegion>>[] _histories;
    private readonly Guid?[] _selected;
    private readonly MagickImage?[] _working;
    private readonly object _lock = new();
    private readonly RedactSurface _surface = new();
    private readonly TextBlock _hint = ToolKit.Text("", 11, "Text3");
    private readonly Segmented _stylePicker;
    private readonly LabeledSlider _strength;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly TextBlock _styleLabel = ToolKit.Text("Style", 12, "Text2");
    private CancellationTokenSource? _rendering;
    private RedactionLook _look = new(RedactionStyle.Box, 50);
    private int _shown = -1;

    private enum Drag { None, Move, Create }
    private Drag _drag;
    private RedactRegion? _moveOriginal;
    private Point _start;

    public RedactEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Redact", ContentSize(images.Count))
    {
        _images = images;
        _regions = new FileEdits<IReadOnlyList<RedactRegion>>(images.Count, _ => Array.Empty<RedactRegion>(), (_, r) => r.Count > 0);
        _histories = images.Select(_ => new EditHistory<IReadOnlyList<RedactRegion>>()).ToArray();
        _selected = new Guid?[images.Count];
        _working = new MagickImage?[images.Count];

        Canvas = _surface;
        SetCanvasPadding(0);
        _surface.MouseLeftButtonDown += OnDown;
        _surface.MouseMove += OnMove;
        _surface.MouseLeftButtonUp += OnUp;
        _surface.MouseLeave += (_, _) => _surface.Hovered = null;

        _stylePicker = new Segmented(Enum.GetValues<RedactionStyle>().Select(RedactionLook.Title).ToList(), 0, 14);
        _stylePicker.SelectionChanged += _ => LookChanged("style");
        _strength = new LabeledSlider("Strength", 1, 100, 50, "%");
        _strength.ValueChanged += _ => LookChanged("strength");
        _strength.DragCompleted += () => History.EndCoalescing();
        var (undoRedo, undo, redo) = EditorParts.UndoRedo(Undo, Redo);
        _undo = undo;
        _redo = redo;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(undoRedo, 1);
        top.Children.Add(_hint);
        top.Children.Add(undoRedo);

        var styleRow = new Grid { MinHeight = 28, Margin = new Thickness(0, 10, 0, 0) };
        styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _styleLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_stylePicker, 1);
        styleRow.Children.Add(_styleLabel);
        styleRow.Children.Add(_stylePicker);
        _strength.Margin = new Thickness(0, 8, 0, 0);

        var controls = new StackPanel();
        controls.Children.Add(top);
        controls.Children.Add(styleRow);
        controls.Children.Add(_strength);
        Controls = controls;

        if (images.Count > 1)
        {
            var strip = EditorParts.Filmstrip(images, sameForAll: false);
            strip.Attach(_regions);
            Filmstrip = strip;
        }
        _regions.Changed += OnChanged;
        PreviewKeyDown += OnKeys;
        OnChanged();
        Loaded += (_, _) => RenderPreview();
    }

    private int Index => _regions.Index;
    private IReadOnlyList<RedactRegion> Regions => _regions.Current;
    private EditHistory<IReadOnlyList<RedactRegion>> History => _histories[Index];

    private Guid? SelectedId
    {
        get => _selected[Index];
        set
        {
            if (_selected[Index] == value) return;
            _selected[Index] = value;
            _surface.SelectedId = value;
            History.EndCoalescing();
            // The controls show the selected area's look, ready to change it.
            if (Regions.FirstOrDefault(r => r.Id == value) is { } region)
            {
                _look = region.Look;
                SyncLookControls();
            }
            SyncLabels();
        }
    }

    private void OnChanged()
    {
        if (_shown != Index)
        {
            _shown = Index;
            _drag = Drag.None;
            _surface.Draft = null;
            History.EndCoalescing();
            var image = _images[Index];
            _surface.SetImage(image.Preview, new Size(image.Width, image.Height));
            _surface.SelectedId = SelectedId;
        }
        _surface.Regions = Regions;
        _hint.Text = Regions.Count switch
        {
            0 => "Drag over anything you want to hide.",
            1 => "1 area · drag to move, Delete to remove",
            var n => $"{n} areas · drag to move, Delete to remove",
        };
        _undo.IsEnabled = History.CanUndo;
        _redo.IsEnabled = History.CanRedo;
        SyncLabels();
        RenderPreview();
        Refresh();
    }

    private void SyncLabels() => _styleLabel.Text = SelectedId is null ? "Style" : "This area";

    private void SyncLookControls()
    {
        _stylePicker.SetSilently((int)_look.Style);
        _strength.SetSilently(_look.Strength);
        _strength.IsEnabled = _look.Style != RedactionStyle.Box;
        _strength.Opacity = _look.Style == RedactionStyle.Box ? 0.45 : 1;
    }

    /// <summary>The controls changed: applies to the selected area, or to the look new areas get.</summary>
    private void LookChanged(string property)
    {
        _look = new RedactionLook((RedactionStyle)_stylePicker.SelectedIndex, Math.Round(_strength.Value));
        _strength.IsEnabled = _look.Style != RedactionStyle.Box;
        _strength.Opacity = _look.Style == RedactionStyle.Box ? 0.45 : 1;
        if (SelectedId is not { } id) return;
        var list = Regions.ToList();
        var index = list.FindIndex(r => r.Id == id);
        if (index < 0 || list[index].Look == _look) return;
        History.Record(Regions, property);
        list[index] = list[index] with { Look = _look };
        _regions.Current = list;
    }

    // ---------------------------------------------------------------- mouse

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var view = e.GetPosition(_surface);
        // The remove badge on the hovered or selected area.
        if (_surface.BadgeAt(view) is { } removeId)
        {
            Remove(removeId);
            e.Handled = true;
            return;
        }
        var start = _surface.ToImage(view);
        _start = start;
        var hit = Regions.LastOrDefault(r => r.Rect.Contains(start));
        if (hit is not null)
        {
            SelectedId = hit.Id;
            _moveOriginal = hit;
            _drag = Drag.Move;
        }
        else
        {
            SelectedId = null;
            _drag = Drag.Create;
        }
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var view = e.GetPosition(_surface);
        if (_drag == Drag.None)
        {
            var point = _surface.ToImage(view);
            var margin = _surface.ImageLength(10);
            _surface.Hovered = Regions.LastOrDefault(r => Inflate(r.Rect, margin).Contains(point))?.Id;
            return;
        }
        var p = _surface.ToImage(view);
        if (_drag == Drag.Move && _moveOriginal is { } original)
        {
            var rect = Rect.Offset(original.Rect, p - _start);
            var image = _images[Index];
            rect.X = Math.Min(Math.Max(rect.X, 0), image.Width - rect.Width);
            rect.Y = Math.Min(Math.Max(rect.Y, 0), image.Height - rect.Height);
            var list = Regions.ToList();
            var index = list.FindIndex(r => r.Id == original.Id);
            if (index < 0) return;
            list[index] = original with { Rect = rect };
            _regions.Current = list;
        }
        else if (_drag == Drag.Create)
        {
            _surface.Draft = new Rect(_start, p);
            _surface.DraftLook = _look;
            RenderPreview();
        }
    }

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        _surface.ReleaseMouseCapture();
        if (drag == Drag.Create)
        {
            if (_surface.Draft is { } draft && draft.Width >= _surface.ImageLength(4) && draft.Height >= _surface.ImageLength(4))
            {
                var region = RedactRegion.Create(draft, _look);
                History.Record(Regions);
                _regions.Current = Regions.Append(region).ToList();
                SelectedId = region.Id;
            }
            _surface.Draft = null;
            OnChanged();
        }
        else if (_moveOriginal is { } original)
        {
            // Record the move as one step, from where it started.
            var index = Regions.ToList().FindIndex(r => r.Id == original.Id);
            if (index >= 0 && Regions[index] != original)
            {
                var before = Regions.ToList();
                before[index] = original;
                History.Record(before);
                OnChanged();
            }
        }
        _moveOriginal = null;
    }

    private void Remove(Guid id)
    {
        if (Regions.All(r => r.Id != id)) return;
        History.Record(Regions);
        _regions.Current = Regions.Where(r => r.Id != id).ToList();
        if (SelectedId == id) SelectedId = null;
        _surface.Hovered = null;
    }

    private void Undo()
    {
        if (!History.CanUndo) return;
        Restore(History.Undo(Regions)!);
    }

    private void Redo()
    {
        if (!History.CanRedo) return;
        Restore(History.Redo(Regions)!);
    }

    private void Restore(IReadOnlyList<RedactRegion> snapshot)
    {
        _surface.Draft = null;
        if (SelectedId is { } id && snapshot.All(r => r.Id != id)) SelectedId = null;
        _regions.Current = snapshot;
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key is Key.Delete or Key.Back && SelectedId is { } id) { Remove(id); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
    }

    // ---------------------------------------------------------------- preview

    private void RenderPreview()
    {
        _rendering?.Cancel();
        var index = Index;
        var image = _images[index];
        var areas = Regions.Select(r => (r.Rect, r.Look)).ToList();
        if (_surface.Draft is { } draft && _drag == Drag.Create) areas.Add((draft, _look));
        if (areas.Count == 0)
        {
            _surface.Source = image.Preview;
            return;
        }
        var cancel = _rendering = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(15, cancel.Token);
                MagickImage working;
                lock (_lock)
                {
                    _working[index] ??= EditableImage.Scaled(ImageFiles.ReadUpright(image.Path), PreviewLongSide);
                    working = (MagickImage)_working[index]!.Clone();
                }
                using (working)
                {
                    var factor = (double)working.Width / Math.Max(image.Width, 1);
                    RedactOperation.Apply(working, areas.Select(a => (new Rect(a.Rect.X * factor, a.Rect.Y * factor, a.Rect.Width * factor, a.Rect.Height * factor), a.Look)));
                    cancel.Token.ThrowIfCancellationRequested();
                    var bitmap = MagickWpf.ToBitmap(working);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!cancel.IsCancellationRequested) _surface.Source = bitmap;
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }

    protected override string PrimaryTitle => _regions.IsBatch ? _regions.PrimaryTitle("Save") : "Save";
    protected override bool CanApply => _regions.EditedCount > 0;
    protected override bool HasReset => true;

    /// <summary>Clears the shown image's areas.</summary>
    protected override void ResetEdits()
    {
        _surface.Draft = null;
        SelectedId = null;
        if (Regions.Count == 0) return;
        History.Record(Regions);
        _regions.Current = Array.Empty<RedactRegion>();
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        RedactOperation.Run(_images.Select((image, i) => (image.Path, _regions[i])).ToList(), progress, cancel);

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "areas":
                    var image = _images[Index];
                    double w = image.Width, h = image.Height;
                    var list = new List<RedactRegion>
                    {
                        RedactRegion.Create(new Rect(w * 0.60, h * 0.30, w * 0.25, h * 0.30), new RedactionLook(RedactionStyle.Pixelate, 60)),
                        RedactRegion.Create(new Rect(w * 0.08, h * 0.60, w * 0.30, h * 0.12), new RedactionLook(RedactionStyle.Box, 0)),
                        RedactRegion.Create(new Rect(w * 0.12, h * 0.12, w * 0.28, h * 0.22), new RedactionLook(RedactionStyle.Blur, 70)),
                    };
                    _regions.Current = list;
                    SelectedId = list[0].Id;
                    break;
                case "second": _regions.Index = Math.Min(1, _images.Count - 1); break;
            }
    }
}

/// <summary>The picture with the hidden areas outlined.</summary>
internal sealed class RedactSurface : ImageSurface
{
    private IReadOnlyList<RedactRegion> _regions = [];
    private Rect? _draft;

    public RedactSurface() => Inset = 8;

    public IReadOnlyList<RedactRegion> Regions
    {
        get => _regions;
        set { _regions = value; InvalidateVisual(); }
    }

    public Rect? Draft
    {
        get => _draft;
        set { _draft = value; InvalidateVisual(); }
    }

    public RedactionLook? DraftLook { get; set; }
    public Guid? SelectedId { get; set; }
    private Guid? _hovered;

    public Guid? Hovered
    {
        get => _hovered;
        set { if (_hovered == value) return; _hovered = value; InvalidateVisual(); }
    }

    public void SetImage(System.Windows.Media.Imaging.BitmapSource source, Size size)
    {
        ImageSize = size;
        Source = source;
    }

    private RedactRegion? BadgeRegion => _regions.FirstOrDefault(r => r.Id == (Hovered ?? SelectedId));

    /// <summary>The id of the area whose remove badge is at <paramref name="view"/>.</summary>
    public Guid? BadgeAt(Point view)
    {
        if (BadgeRegion is not { } region) return null;
        var rect = ToView(region.Rect);
        return (new Point(rect.Right, rect.Top) - view).Length <= 10 ? region.Id : null;
    }

    protected override void DrawOverlay(DrawingContext dc, Rect imageRect)
    {
        var accent = ToolKit.Brush("Accent");
        foreach (var region in _regions)
        {
            var rect = ToView(region.Rect);
            var selected = region.Id == SelectedId;
            var pen = new Pen(selected ? accent : OutlineWhite, selected ? 2 : 1);
            if (!selected) pen.DashStyle = new DashStyle([4, 3], 0);
            dc.DrawRectangle(null, pen, rect);
        }
        if (_draft is { } draft)
            dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, ToView(draft));
        if (BadgeRegion is { } badge && _draft is null)
        {
            var rect = ToView(badge.Rect);
            var center = new Point(rect.Right, rect.Top);
            dc.DrawEllipse(Brushes.White, new Pen(accent, 1.5), center, 8, 8);
            var cross = new Pen(accent, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(cross, new Point(center.X - 3, center.Y - 3), new Point(center.X + 3, center.Y + 3));
            dc.DrawLine(cross, new Point(center.X - 3, center.Y + 3), new Point(center.X + 3, center.Y - 3));
        }
    }

    private static readonly Brush OutlineWhite = Freeze(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)));
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
}
