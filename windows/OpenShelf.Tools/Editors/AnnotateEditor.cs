using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenShelf.Tools.Imaging;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.Editors;

/// <summary>Draws on images: freehand, arrows, boxes, highlights, text and numbered callouts, with colours and a stroke width, select/move/delete,
/// and full undo/redo. Given several images a filmstrip moves between them; each keeps its own marks, undo history and selection, and only
/// images with marks are saved.</summary>
public sealed class AnnotateEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(580, count > 1 ? 700 + FilmstripView.StripHeight : 700);

    private static readonly Color[] PresetColors =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x00, 0x7A, 0xFF), Colors.Black, Colors.White,
    ];

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly FileEdits<IReadOnlyList<Annotation>> _marks;
    private readonly EditHistory<IReadOnlyList<Annotation>>[] _histories;
    private readonly Guid?[] _selected;
    private readonly AnnotateSurface _surface = new();
    private readonly System.Windows.Controls.Canvas _overlay = new() { IsHitTestVisible = true, Background = null };
    private readonly Segmented _toolPicker;
    private readonly TextBlock _hint = ToolKit.Text("", 11, "Text3");
    private readonly LabeledSlider _width;
    private readonly List<SwatchButton> _swatches = [];
    private readonly ColorWell _custom;
    private readonly Button _undo;
    private readonly Button _redo;

    private AnnotationTool _tool = AnnotationTool.Draw;
    private Color _color = PresetColors[0];
    private double _lineWidth;
    private TextBox? _textBox;
    private Point _textOrigin;

    private enum Drag { None, Move, Create, Tap, Ignore }
    private Drag _drag;
    private Annotation? _moveOriginal;
    private Point _dragStart;

    public AnnotateEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Annotate", ContentSize(images.Count))
    {
        _images = images;
        _marks = new FileEdits<IReadOnlyList<Annotation>>(images.Count, _ => Array.Empty<Annotation>(), (_, marks) => marks.Count > 0);
        _histories = images.Select(_ => new EditHistory<IReadOnlyList<Annotation>>()).ToArray();
        _selected = new Guid?[images.Count];
        var longSide = Math.Max(images[0].Width, images[0].Height);
        _lineWidth = Math.Min(Math.Max(longSide / 200.0, 2), 24);

        var host2 = new Grid();
        host2.Children.Add(_surface);
        host2.Children.Add(_overlay);
        Canvas = host2;
        SetCanvasPadding(0);
        _surface.MouseLeftButtonDown += OnDown;
        _surface.MouseMove += OnMove;
        _surface.MouseLeftButtonUp += OnUp;
        _surface.LostMouseCapture += (_, _) => { if (_drag is Drag.Create or Drag.Move) CancelDrag(); };

        // ---- controls
        _toolPicker = new Segmented(AnnotationTools.All.Select(t => t.Title()).ToList(), 0, 11, AnnotationTools.All.Select(t => t.Glyph()).ToList());
        _toolPicker.SelectionChanged += index =>
        {
            CommitText();
            _tool = AnnotationTools.All[index];
            UpdateTool();
        };
        var (undoRedo, undo, redo) = EditorParts.UndoRedo(Undo, Redo);
        _undo = undo;
        _redo = redo;
        var toolRow = new Grid();
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(_hint, 1);
        Grid.SetColumn(undoRedo, 2);
        toolRow.Children.Add(_toolPicker);
        toolRow.Children.Add(_hint);
        toolRow.Children.Add(undoRedo);

        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in PresetColors)
        {
            var swatch = new SwatchButton(new SolidColorBrush(preset), ColorName(preset), 18, 9);
            swatch.Click += (_, _) => ChooseColor(preset);
            swatch.Margin = new Thickness(0, 0, 2, 0);
            _swatches.Add(swatch);
            colors.Children.Add(swatch);
        }
        _custom = new ColorWell(Color.FromRgb(0xB0, 0x5C, 0xF0));
        _custom.ColorChosen += ChooseColor;
        colors.Children.Add(_custom);

        _width = new LabeledSlider("Width", 2, 24, _lineWidth, "px");
        _width.ValueChanged += v =>
        {
            _lineWidth = v;
            UpdateSelection("width", a => a with { LineWidth = v });
        };
        _width.DragCompleted += () => _histories[_marks.Index].EndCoalescing();

        var controls = new StackPanel();
        controls.Children.Add(toolRow);
        var colorRow = ToolKit.Row("Color", colors);
        colorRow.Margin = new Thickness(0, 10, 0, 0);
        controls.Children.Add(colorRow);
        _width.Margin = new Thickness(0, 6, 0, 0);
        controls.Children.Add(_width);
        Controls = controls;

        if (images.Count > 1)
        {
            var strip = EditorParts.Filmstrip(images, sameForAll: false);
            strip.Attach(_marks);
            Filmstrip = strip;
        }
        _marks.Changed += OnMarksChanged;
        PreviewKeyDown += OnKeys;
        UpdateTool();
        SelectSwatch();
        OnMarksChanged();
    }

    private static string ColorName(Color c) =>
        c == Colors.Black ? "Black" : c == Colors.White ? "White" : c.R > 200 && c.G < 90 ? "Red" : c.R > 200 && c.G > 140 && c.G < 170 ? "Orange" : c.R > 200 && c.G > 190 ? "Yellow" : c.G > 180 ? "Green" : "Blue";

    private int Index => _marks.Index;
    private IReadOnlyList<Annotation> Marks => _marks.Current;
    private EditHistory<IReadOnlyList<Annotation>> History => _histories[Index];

    private Guid? SelectedId
    {
        get => _selected[Index];
        set { _selected[Index] = value; _surface.SelectedId = value; }
    }

    // ---------------------------------------------------------------- state

    private int _shown = -1;

    private void OnMarksChanged()
    {
        if (_shown != Index)
        {
            CommitText();
            _drag = Drag.None;
            _surface.Draft = null;
            History.EndCoalescing();
            _shown = Index;
            var image = _images[Index];
            _surface.SetImage(image.Preview, new Size(image.Width, image.Height));
        }
        _surface.Annotations = Marks;
        _surface.SelectedId = SelectedId;
        _undo.IsEnabled = History.CanUndo && _textBox is null;
        _redo.IsEnabled = History.CanRedo && _textBox is null;
        Refresh();
    }

    private void SetMarks(IReadOnlyList<Annotation> marks) => _marks.Current = marks;

    private void UpdateTool()
    {
        _hint.Text = _tool.Hint();
        if (_tool == AnnotationTool.Text) _width.Configure("Size", 2, 24, _lineWidth);
        else _width.Configure("Width", 2, 24, _lineWidth);
        _widthMapsToFont = _tool == AnnotationTool.Text;
        _width.SetDisplay(_widthMapsToFont ? v => AnnotationRenderer.FontSize(v) : v => v);
    }

    private bool _widthMapsToFont;

    private void ChooseColor(Color color)
    {
        _color = color;
        SelectSwatch();
        UpdateSelection("color", a => a with { Color = color });
        if (_textBox is not null) _textBox.Foreground = new SolidColorBrush(color);
    }

    private void SelectSwatch()
    {
        for (var i = 0; i < PresetColors.Length; i++) _swatches[i].IsSelected = PresetColors[i] == _color;
        _custom.IsSelected = !PresetColors.Contains(_color);
    }

    /// <summary>Applies a colour or width change to the selected mark. Continuous changes to one property undo as one step.</summary>
    private void UpdateSelection(string property, Func<Annotation, Annotation> change)
    {
        if (SelectedId is not { } id) return;
        var index = Marks.ToList().FindIndex(a => a.Id == id);
        if (index < 0) return;
        var updated = change(Marks[index]);
        if (updated == Marks[index]) return;
        History.Record(Marks, $"{property}-{id}");
        var list = Marks.ToList();
        list[index] = updated;
        SetMarks(list);
    }

    // ---------------------------------------------------------------- mouse

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var view = e.GetPosition(_surface);
        if (_textBox is not null)
        {
            // A click outside the text field finishes the text rather than starting something new.
            CommitText();
            _drag = Drag.Ignore;
            return;
        }
        var point = _surface.ToImage(view);
        _dragStart = point;
        var tolerance = _surface.ImageLength(6);
        var hit = Marks.LastOrDefault(a => AnnotationRenderer.Contains(a, point, tolerance));
        if (hit is not null)
        {
            SelectedId = hit.Id;
            _moveOriginal = hit;
            _drag = Drag.Move;
            _surface.CaptureMouse();
            _surface.InvalidateVisual();
            e.Handled = true;
            return;
        }
        SelectedId = null;
        switch (_tool)
        {
            case AnnotationTool.Text or AnnotationTool.Callout:
                _drag = Drag.Tap;
                break;
            case AnnotationTool.Draw:
                _surface.Draft = Annotation.Create(new StrokeFigure([point]), _color, _lineWidth);
                _drag = Drag.Create;
                break;
            case AnnotationTool.Arrow:
                _surface.Draft = Annotation.Create(new ArrowFigure(point, point), _color, _lineWidth);
                _drag = Drag.Create;
                break;
            case AnnotationTool.Box:
                _surface.Draft = Annotation.Create(new BoxFigure(new Rect(point, new Size(0, 0))), _color, _lineWidth);
                _drag = Drag.Create;
                break;
            case AnnotationTool.Highlight:
                _surface.Draft = Annotation.Create(new HighlightFigure(new Rect(point, new Size(0, 0))), _color, _lineWidth);
                _drag = Drag.Create;
                break;
        }
        _surface.CaptureMouse();
        _surface.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag is Drag.None or Drag.Tap or Drag.Ignore) return;
        var point = _surface.ToImage(e.GetPosition(_surface));
        if (_drag == Drag.Move && _moveOriginal is { } original)
        {
            var moved = original.OffsetBy(point - _dragStart);
            var list = Marks.ToList();
            var index = list.FindIndex(a => a.Id == original.Id);
            if (index < 0) return;
            list[index] = moved;
            // Moving is one undo step, recorded on release, so the live state is set without a history entry.
            _marks.Current = list;
        }
        else if (_drag == Drag.Create && _surface.Draft is { } draft)
        {
            _surface.Draft = Extend(draft, point);
        }
    }

    private Annotation Extend(Annotation draft, Point point)
    {
        switch (draft.Figure)
        {
            case StrokeFigure stroke:
                if ((point - stroke.Points[^1]).Length < _surface.ImageLength(1)) return draft;
                return draft with { Figure = new StrokeFigure(stroke.Points.Append(point).ToList()) };
            case ArrowFigure arrow:
                return draft with { Figure = new ArrowFigure(arrow.From, point) };
            case BoxFigure:
                return draft with { Figure = new BoxFigure(new Rect(_dragStart, point)) };
            case HighlightFigure:
                return draft with { Figure = new HighlightFigure(new Rect(_dragStart, point)) };
            default:
                return draft;
        }
    }

    private bool IsMeaningful(Annotation a)
    {
        var minimum = _surface.ImageLength(4);
        return a.Figure switch
        {
            ArrowFigure arrow => (arrow.To - arrow.From).Length >= minimum,
            BoxFigure box => box.Rect.Width >= minimum && box.Rect.Height >= minimum,
            HighlightFigure h => h.Rect.Width >= minimum && h.Rect.Height >= minimum,
            _ => true,
        };
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        _surface.ReleaseMouseCapture();
        switch (drag)
        {
            case Drag.Create:
                if (_surface.Draft is { } draft && IsMeaningful(draft))
                {
                    History.Record(Marks);
                    SetMarks(Marks.Append(draft).ToList());
                }
                _surface.Draft = null;
                break;
            case Drag.Tap:
                Place(_surface.ToImage(e.GetPosition(_surface)));
                break;
            case Drag.Move when _moveOriginal is { } original:
                // Record the move as one step, from where it started.
                var index = Marks.ToList().FindIndex(a => a.Id == original.Id);
                if (index >= 0 && Marks[index] != original)
                {
                    var before = Marks.ToList();
                    before[index] = original;
                    History.Record(before);
                    OnMarksChanged();
                }
                break;
        }
        _moveOriginal = null;
    }

    private void CancelDrag()
    {
        if (_drag == Drag.Move && _moveOriginal is { } original)
        {
            var list = Marks.ToList();
            var index = list.FindIndex(a => a.Id == original.Id);
            if (index >= 0) { list[index] = original; _marks.Current = list; }
        }
        _drag = Drag.None;
        _surface.Draft = null;
    }

    private void Place(Point point)
    {
        switch (_tool)
        {
            case AnnotationTool.Text:
                BeginText(point);
                break;
            case AnnotationTool.Callout:
                var next = Marks.Select(a => a.Figure as CalloutFigure).Where(c => c is not null).Select(c => c!.Number).DefaultIfEmpty(0).Max() + 1;
                var image = _images[Index];
                var diameter = Math.Max(24, Math.Max(image.Width, image.Height) * 0.035);
                var callout = Annotation.Create(new CalloutFigure(next, point, diameter), _color, _lineWidth);
                History.Record(Marks);
                SetMarks(Marks.Append(callout).ToList());
                SelectedId = callout.Id;
                break;
        }
    }

    // ---------------------------------------------------------------- text entry

    protected override bool EscapeIsHandledByEditor => _textBox is not null;

    private void BeginText(Point origin)
    {
        _textOrigin = origin;
        var view = _surface.ToView(origin);
        var size = AnnotationRenderer.FontSize(_lineWidth) * _surface.Scale;
        _textBox = new TextBox
        {
            Background = Brushes.Transparent,
            BorderBrush = ToolKit.Brush("Accent"),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = size,
            Padding = new Thickness(2, 0, 2, 0),
            Height = double.NaN,
            MinWidth = 140,
            Width = Math.Max(160, _surface.ActualWidth - view.X - 10),
            Template = TextEntryTemplate,
        };
        System.Windows.Controls.Canvas.SetLeft(_textBox, view.X - 3);
        System.Windows.Controls.Canvas.SetTop(_textBox, view.Y);
        _textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { e.Handled = true; CommitText(); }
        };
        _textBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitText);
        _overlay.Children.Add(_textBox);
        OnMarksChanged();
        Dispatcher.BeginInvoke(() => { _textBox?.Focus(); Keyboard.Focus(_textBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static readonly ControlTemplate TextEntryTemplate = BuildTextTemplate();

    private static ControlTemplate BuildTextTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        host.SetValue(Control.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        border.AppendChild(host);
        template.VisualTree = border;
        return template;
    }

    private void CommitText()
    {
        if (_textBox is not { } box) return;
        _textBox = null;
        _overlay.Children.Remove(box);
        var text = box.Text.Trim();
        if (text.Length > 0)
        {
            var annotation = Annotation.Create(new TextFigure(text, _textOrigin), _color, _lineWidth);
            History.Record(Marks);
            SetMarks(Marks.Append(annotation).ToList());
            SelectedId = annotation.Id;
        }
        else OnMarksChanged();
        Keyboard.ClearFocus();
        Focus();
    }

    // ---------------------------------------------------------------- undo, delete, reset

    private void Undo()
    {
        if (_textBox is not null || !History.CanUndo) return;
        Restore(History.Undo(Marks)!);
    }

    private void Redo()
    {
        if (_textBox is not null || !History.CanRedo) return;
        Restore(History.Redo(Marks)!);
    }

    private void Restore(IReadOnlyList<Annotation> snapshot)
    {
        _surface.Draft = null;
        if (SelectedId is { } id && snapshot.All(a => a.Id != id)) SelectedId = null;
        SetMarks(snapshot);
    }

    private void DeleteSelection()
    {
        if (SelectedId is not { } id || Marks.All(a => a.Id != id)) return;
        History.Record(Marks);
        SetMarks(Marks.Where(a => a.Id != id).ToList());
        SelectedId = null;
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (_textBox is not null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key is Key.Delete or Key.Back && SelectedId is not null) { DeleteSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
    }

    protected override string PrimaryTitle => _marks.IsBatch ? _marks.PrimaryTitle("Save") : "Save";
    protected override bool CanApply => _textBox is null && _marks.EditedCount > 0;
    protected override bool HasReset => true;

    /// <summary>Clears the shown image's marks.</summary>
    protected override void ResetEdits()
    {
        CommitText();
        _surface.Draft = null;
        SelectedId = null;
        if (Marks.Count == 0) return;
        History.Record(Marks);
        SetMarks(Array.Empty<Annotation>());
    }

    protected override bool PrepareToSave()
    {
        CommitText();
        return true;
    }

    protected override bool NeedsStaThread => true;

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        AnnotateOperation.Run(_images.Select((image, i) => (image.Path, _marks[i])).ToList(), progress, cancel);

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "marks":
                    var image = _images[Index];
                    double w = image.Width, h = image.Height;
                    var width = Math.Max(3, Math.Max(w, h) / 200);
                    var list = new List<Annotation>
                    {
                        Annotation.Create(new BoxFigure(new Rect(w * 0.52, h * 0.30, w * 0.36, h * 0.30)), PresetColors[0], width),
                        Annotation.Create(new ArrowFigure(new Point(w * 0.18, h * 0.22), new Point(w * 0.50, h * 0.40)), PresetColors[4], width),
                        Annotation.Create(new HighlightFigure(new Rect(w * 0.08, h * 0.62, w * 0.40, h * 0.10)), PresetColors[2], width),
                        Annotation.Create(new TextFigure("Golden hour", new Point(w * 0.10, h * 0.10)), PresetColors[6], width * 1.4),
                        Annotation.Create(new CalloutFigure(1, new Point(w * 0.57, h * 0.34), Math.Max(24, Math.Max(w, h) * 0.035)), PresetColors[0], width),
                        Annotation.Create(new CalloutFigure(2, new Point(w * 0.74, h * 0.52), Math.Max(24, Math.Max(w, h) * 0.035)), PresetColors[0], width),
                        Annotation.Create(new StrokeFigure([new Point(w * 0.12, h * 0.80), new Point(w * 0.22, h * 0.76), new Point(w * 0.32, h * 0.82), new Point(w * 0.42, h * 0.77)]), PresetColors[3], width),
                    };
                    SetMarks(list);
                    SelectedId = list[0].Id;
                    break;
                case "text":
                    _toolPicker.SelectedIndex = (int)AnnotationTool.Text;
                    BeginText(new Point(_images[Index].Width * 0.3, _images[Index].Height * 0.4));
                    _textBox!.Text = "Look here";
                    break;
                case "second":
                    _marks.Index = Math.Min(1, _images.Count - 1);
                    break;
            }
    }
}

/// <summary>The picture with its marks on it.</summary>
internal sealed class AnnotateSurface : ImageSurface
{
    private IReadOnlyList<Annotation> _annotations = [];
    private Annotation? _draft;
    private Guid? _selected;

    public AnnotateSurface() => Inset = 8;

    public IReadOnlyList<Annotation> Annotations
    {
        get => _annotations;
        set { _annotations = value; InvalidateVisual(); }
    }

    public Annotation? Draft
    {
        get => _draft;
        set { _draft = value; InvalidateVisual(); }
    }

    public Guid? SelectedId
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    public void SetImage(System.Windows.Media.Imaging.BitmapSource source, Size size)
    {
        ImageSize = size;
        Source = source;
    }

    protected override void DrawOverlay(DrawingContext dc, Rect imageRect)
    {
        var scale = Scale;
        dc.PushClip(new RectangleGeometry(imageRect));
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, imageRect.X, imageRect.Y));
        foreach (var annotation in _annotations) AnnotationRenderer.Draw(dc, annotation);
        if (_draft is not null) AnnotationRenderer.Draw(dc, _draft);
        if (_annotations.FirstOrDefault(a => a.Id == _selected) is { } selected)
        {
            var outline = AnnotationRenderer.Bounds(selected);
            outline.Inflate(ImageLength(4), ImageLength(4));
            var pen = new Pen(ToolKit.Brush("Accent"), ImageLength(1.5)) { DashStyle = new DashStyle([3, 2.2], 0) };
            dc.DrawRectangle(null, pen, outline);
        }
        dc.Pop();
        dc.Pop();
    }
}
