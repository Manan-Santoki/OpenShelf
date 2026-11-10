using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>What a shelf window needs from the service that owns it.</summary>
internal interface IShelfHost
{
    IPreferences Preferences { get; }
    PreviewController Preview { get; }
    void Toast(string message, ScreenPoint near);
    /// <summary>The user interacted with this shelf: it becomes the most recent.</summary>
    void Interacted(ShelfWindow window);
    /// <summary>Something worth saving changed.</summary>
    void Changed(ShelfWindow window);
}

/// <summary>
/// A floating shelf. The window has a fixed size (the biggest card plus a transparent margin for the shadow) and
/// the card moves and resizes inside it, so resizing never resizes the HWND. The card sits at a canonical offset for
/// each docking edge; the window itself only ever moves.
/// </summary>
internal sealed class ShelfWindow : Window
{
    const double Pad = ShelfLayout.Pad;
    const double WinW = ShelfLayout.ExpandedWidth + 2 * Pad;
    const double WinH = ShelfLayout.MaxCardHeight + 2 * Pad;
    const double R = ShelfLayout.CardRadius;

    static readonly Brush Clear = Brushes.Transparent;
    static readonly Brush AccentBrush = Theme.Brush(Theme.Accent);
    static readonly Brush HoverBrush = Theme.White(0.06);
    static readonly Brush SelectBrush = Theme.White(0.12);
    static readonly Brush ThumbBorder = Theme.White(0.10);
    static readonly Brush NameBrush = Theme.White(0.92);
    static readonly Brush Placeholder = Theme.White(0.08);

    sealed class Tile
    {
        public required ShelfItem Item;
        public required ShelfViewMode Mode;
        public required FrameworkElement Root;
        public required ScaleTransform RootScale;
        public required TranslateTransform Shift;
        public required Border HoverFill;
        public required Border SelectFill;
        public Border? Pill;
        public ScaleTransform? PillScale;
        public TextBlock? Name;
        public required Image Image;
        public bool Selected;
    }

    readonly ShelfModel _model;
    readonly IShelfHost _env;
    readonly Dictionary<ShelfItem, Tile> _tiles = new();
    readonly TypeSelect _typeSelect = new();

    // Visual tree.
    readonly Canvas _stage = new();
    readonly Grid _shadow = new();
    readonly List<(Border Layer, double Grow)> _shadowLayers = new();
    readonly Grid _contact = new();
    readonly Border _card = new();
    readonly Grid _clip = new();
    readonly Grid _content = new();
    readonly Border _highlight = new();
    readonly Border _dropRing = new();
    readonly TextBlock _title = new();
    readonly Grid _body = new();
    readonly Grid _empty = new();
    readonly Grid _stackHost = new();
    readonly Grid _stackLayer = new();
    readonly ScrollViewer _scroll = new();
    readonly Canvas _canvas = new();
    readonly Border _scrollThumb = new();
    readonly Border _bandVisual = new();
    readonly DropTargetHelper _dropHelper = new();
    readonly ScaleTransform _stageScale = new(1, 1);
    readonly TranslateTransform _stageShift = new();
    readonly TextBlock _modeGlyph = new();
    readonly TextBlock _expandGlyph = new();
    Grid _modeButton = null!;
    Grid _expandButton = null!;
    readonly EdgeTab _edgeTab = new();
    readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // State.
    ShelfEdge? _edge;
    bool _expanded;
    ShelfViewMode _mode = ShelfViewMode.Grid;
    double _cx = Pad, _cy = Pad, _cw = ShelfLayout.CollapsedSize.W, _ch = ShelfLayout.CollapsedSize.H;   // card in window DIPs
    IntPtr _hwnd;
    bool _placed, _shown, _closing, _closeFinished, _hidden, _holdOpen, _dragging, _stackHover;
    DateTime? _awaySince;
    Tween? _layoutTween, _slideTween, _moveTween, _scrollTween;
    double _hideShift;

    // Pointer state for clicks, drags and the rubber band.
    ShelfItem? _pressItem;
    bool _pressIsStack, _collapseOnRelease;
    Point _pressPoint;
    Point? _bandStart;
    bool _bandActive;
    IReadOnlyCollection<ShelfItem> _selectionBeforeBand = Array.Empty<ShelfItem>();

    public ShelfWindow(ShelfModel model, IShelfHost env)
    {
        _model = model;
        _env = env;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        FontFamily = Theme.TextFont;
        Width = WinW;
        Height = WinH;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        Content = BuildVisualTree();

        SourceInitialized += (_, _) =>
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            }
            catch (Exception ex) { Log.Error("Shelf SourceInitialized", ex); }
        };

        _model.Items.CollectionChanged += OnItemsChanged;
        _model.SelectionChanged += RefreshSelection;
        foreach (var item in _model.Items) item.PropertyChanged += OnItemPropertyChanged;
        PreviewMouseDown += (_, _) => { EnsureFocus(); _env.Interacted(this); };
        PreviewKeyDown += OnKeyDown;
        PreviewTextInput += OnTextInput;
        Activated += (_, _) => { _env.Interacted(this); PruneMissing(); };
        Deactivated += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && !_closing) Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
        };
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        _edgeTab.RevealRequested += () => BringForward();
        _edgeTimer.Tick += (_, _) => CheckEdgeHiding();
        _edgeTimer.Start();
        Closed += (_, _) =>
        {
            _edgeTimer.Stop();
            _edgeTab.Close();
            _model.Items.CollectionChanged -= OnItemsChanged;
            _model.SelectionChanged -= RefreshSelection;
            foreach (var item in _model.Items) item.PropertyChanged -= OnItemPropertyChanged;
            _dropHelper.Dispose();
        };

        RebuildTiles(animate: false);
        RefreshBody();
        ApplyShape();
        SetCard(CanonX(_cw), Pad, _cw, _ch);
    }

    // ---------- public state ----------

    public ShelfModel Model => _model;
    public IntPtr Handle => _hwnd;
    public bool IsDragging => _dragging;
    public bool IsHidden => _hidden;
    public ShelfEdge? DockedEdge => _edge;
    public bool IsExpanded => _expanded;
    public ShelfViewMode ViewMode => _mode;

    /// <summary>Raised when the user closes the shelf from inside (header button, Esc, Ctrl+W, last item removed).</summary>
    double S => _shown ? VisualTreeHelper.GetDpi(this).DpiScaleX : _initialScale;
    double _initialScale = 1;

    /// <summary>The card's frame on screen, in physical pixels, wherever the window currently is.</summary>
    public Box CardScreen()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return new Box(0, 0, _cw, _ch);
        double s = S;
        return new Box(r.Left + _cx * s, r.Top + _cy * s, _cw * s, _ch * s);
    }

    public (int Count, IReadOnlyList<string> Paths) SelectionPaths() =>
        (_model.Selection.Count, _model.Selected.Select(i => i.Path).ToList());

    public void SetState(bool expanded, ShelfViewMode mode)
    {
        _expanded = expanded;
        if (_mode != mode) _tiles.Clear();
        _mode = mode;
        RebuildTiles(animate: false);
        RefreshBody();
        var (w, h) = ShelfLayout.Size(_model.Items.Count, _expanded, _mode);
        _cw = w; _ch = h;
        SetCard(CanonX(w), Pad, w, h);
    }

    // ---------- visual tree ----------

    double CanonX(double cardW) => _edge switch
    {
        ShelfEdge.Left => Pad,
        ShelfEdge.Right => WinW - Pad - cardW,
        _ => (WinW - cardW) / 2,
    };

    CornerRadius Radii() => _edge switch
    {
        ShelfEdge.Left => new CornerRadius(0, R, R, 0),
        ShelfEdge.Right => new CornerRadius(R, 0, 0, R),
        _ => new CornerRadius(R),
    };

    UIElement BuildVisualTree()
    {
        // Soft shadow: stacked translucent rounded rectangles (cheap to animate, unlike a blur effect).
        const int layers = 30;
        const double step = 1.15;
        for (int i = 0; i < layers; i++)
        {
            var layer = new Border { Margin = new Thickness(-(i * step)), Background = Theme.Black(0.0095), IsHitTestVisible = false };
            _shadow.Children.Add(layer);
            _shadowLayers.Add((layer, i * step));
        }
        _shadow.RenderTransform = new TranslateTransform(0, 8);
        _shadow.IsHitTestVisible = false;
        // A tight contact shadow right under the edge (not offset).
        foreach (var (m, a) in new[] { (2.0, 0.035), (1.0, 0.05), (0.0, 0.07) })
        {
            var contact = new Border { Margin = new Thickness(-m), Background = Theme.Black(a), IsHitTestVisible = false };
            _contact.Children.Add(contact);
            _shadowLayers.Add((contact, m));
        }
        _contact.IsHitTestVisible = false;

        _card.Background = Theme.Vertical((Color.FromRgb(0x2C, 0x2C, 0x30), 0.98, 0), (Theme.Surface, 0.98, 1));
        _card.BorderBrush = Theme.White(0.14);
        _card.BorderThickness = new Thickness(1);
        _card.Child = _clip;

        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ShelfLayout.HeaderHeight) });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.VerticalAlignment = VerticalAlignment.Top;

        _highlight.BorderThickness = new Thickness(1);
        _highlight.BorderBrush = Theme.Vertical((Colors.White, 0.06, 0), (Colors.White, 0.0, 0.35));
        _highlight.IsHitTestVisible = false;
        _dropRing.BorderThickness = new Thickness(1.5);
        _dropRing.BorderBrush = Theme.Brush(Theme.Accent, 0.7);
        _dropRing.Background = Theme.White(0.03);
        _dropRing.Opacity = 0;
        _dropRing.IsHitTestVisible = false;

        _clip.Children.Add(_content);
        _clip.Children.Add(_highlight);
        _clip.Children.Add(_dropRing);

        _content.Children.Add(BuildHeader());
        BuildBody();
        Grid.SetRow(_body, 1);
        _content.Children.Add(_body);

        _stage.Children.Add(_shadow);
        _stage.Children.Add(_contact);
        _stage.Children.Add(_card);
        var group = new TransformGroup();
        group.Children.Add(_stageScale);
        group.Children.Add(_stageShift);
        _stage.RenderTransform = group;
        _stage.Width = WinW;
        _stage.Height = WinH;
        return _stage;
    }

    Grid HeaderButton(string glyph, TextBlock glyphBlock, Action click)
    {
        var hover = new Border { CornerRadius = new CornerRadius(14), Background = Theme.White(0.10), Opacity = 0 };
        glyphBlock.Text = glyph;
        glyphBlock.FontFamily = Theme.IconFont;
        glyphBlock.FontSize = 11;
        glyphBlock.Foreground = Theme.White(0.62);
        glyphBlock.HorizontalAlignment = HorizontalAlignment.Center;
        glyphBlock.VerticalAlignment = VerticalAlignment.Center;
        var button = new Grid { Width = 28, Height = 28, Background = Clear, Cursor = Cursors.Arrow };
        button.Children.Add(hover);
        button.Children.Add(glyphBlock);
        button.MouseEnter += (_, _) => Motion.Animate(hover, OpacityProperty, 1, 90);
        button.MouseLeave += (_, _) => Motion.Animate(hover, OpacityProperty, 0, 90);
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { click(); } catch (Exception ex) { Log.Error("Shelf header button", ex); }
        };
        return button;
    }

    UIElement BuildHeader()
    {
        var header = new Grid { Background = Clear };
        header.MouseLeftButtonDown += OnHeaderDown;

        _title.FontFamily = Theme.Font;
        _title.FontSize = 15;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.White(0.62);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.IsHitTestVisible = false;
        header.Children.Add(_title);

        var close = HeaderButton("", new TextBlock(), () => Close());
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Margin = new Thickness(10, 0, 0, 0);
        header.Children.Add(close);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _modeButton = HeaderButton("", _modeGlyph, () => SetViewMode(_mode == ShelfViewMode.Grid ? ShelfViewMode.List : ShelfViewMode.Grid));
        _expandButton = HeaderButton("", _expandGlyph, () => SetExpanded(!_expanded));
        right.Children.Add(_modeButton);
        right.Children.Add(_expandButton);
        header.Children.Add(right);
        return header;
    }

    void BuildBody()
    {
        // Empty.
        var emptyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        emptyStack.Children.Add(new TextBlock
        {
            Text = "", FontFamily = Theme.IconFont, FontSize = 24, Foreground = Theme.White(0.42),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
        });
        emptyStack.Children.Add(new TextBlock { Text = "Drop files here", FontSize = 13, Foreground = Theme.White(0.5), HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(emptyStack);
        _empty.IsHitTestVisible = false;

        // Collapsed stack: a transparent layer takes the pointer for dragging, double-clicking and the menu.
        _stackHost.Background = Clear;
        _stackLayer.IsHitTestVisible = false;
        _stackLayer.HorizontalAlignment = HorizontalAlignment.Center;
        _stackLayer.VerticalAlignment = VerticalAlignment.Center;
        _stackLayer.Width = 120;
        _stackLayer.Height = 120;
        _stackLayer.Margin = new Thickness(0, 0, 0, 14);
        _stackHost.Children.Add(_stackLayer);
        _stackHost.MouseEnter += (_, _) => SetStackSpread(true);
        _stackHost.MouseLeave += (_, _) => SetStackSpread(false);
        _stackHost.MouseLeftButtonDown += OnStackDown;
        _stackHost.MouseMove += OnStackMove;
        _stackHost.MouseLeftButtonUp += OnStackUp;
        _stackHost.MouseRightButtonUp += (_, e) => { e.Handled = true; if (_model.Items.Count > 0) ShowMenu(_model.Items.ToList()); };

        // Expanded: a scrolling canvas of tiles.
        _canvas.Background = Clear;
        _canvas.Width = ShelfLayout.ExpandedWidth;
        _canvas.MouseLeftButtonDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseLeftButtonUp += OnCanvasUp;
        _bandVisual.CornerRadius = new CornerRadius(3);
        _bandVisual.Background = Theme.Brush(Theme.Accent, 0.15);
        _bandVisual.BorderBrush = Theme.Brush(Theme.Accent, 0.8);
        _bandVisual.BorderThickness = new Thickness(1);
        _bandVisual.IsHitTestVisible = false;
        _bandVisual.Visibility = Visibility.Collapsed;
        Panel.SetZIndex(_bandVisual, 1000);
        _canvas.Children.Add(_bandVisual);

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Background = Clear;
        _scroll.Content = _canvas;
        _scroll.VerticalAlignment = VerticalAlignment.Top;
        _scroll.ScrollChanged += (_, _) => UpdateScrollThumb();

        _scrollThumb.Width = 4;
        _scrollThumb.CornerRadius = new CornerRadius(2);
        _scrollThumb.Background = Theme.White(0.28);
        _scrollThumb.HorizontalAlignment = HorizontalAlignment.Right;
        _scrollThumb.VerticalAlignment = VerticalAlignment.Top;
        _scrollThumb.Margin = new Thickness(0, 0, 3, 0);
        _scrollThumb.IsHitTestVisible = false;
        _scrollThumb.Visibility = Visibility.Collapsed;

        _body.VerticalAlignment = VerticalAlignment.Top;
        _body.Children.Add(_empty);
        _body.Children.Add(_stackHost);
        _body.Children.Add(_scroll);
        _body.Children.Add(_scrollThumb);
    }

    void UpdateScrollThumb()
    {
        double extent = _scroll.ExtentHeight, view = _scroll.ViewportHeight;
        if (_scroll.Visibility != Visibility.Visible || extent <= view + 0.5)
        {
            _scrollThumb.Visibility = Visibility.Collapsed;
            return;
        }
        double track = view - 12;
        double h = Math.Max(24, track * view / extent);
        double top = 6 + (track - h) * (_scroll.VerticalOffset / (extent - view));
        _scrollThumb.Height = h;
        _scrollThumb.Margin = new Thickness(0, top, 3, 0);
        _scrollThumb.Visibility = Visibility.Visible;
    }

    static Geometry RoundRect(double w, double h, CornerRadius r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.TopLeft, 0), true, true);
            c.LineTo(new Point(w - r.TopRight, 0), true, false);
            if (r.TopRight > 0) c.ArcTo(new Point(w, r.TopRight), new Size(r.TopRight, r.TopRight), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(w, h - r.BottomRight), true, false);
            if (r.BottomRight > 0) c.ArcTo(new Point(w - r.BottomRight, h), new Size(r.BottomRight, r.BottomRight), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.BottomLeft, h), true, false);
            if (r.BottomLeft > 0) c.ArcTo(new Point(0, h - r.BottomLeft), new Size(r.BottomLeft, r.BottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(0, r.TopLeft), true, false);
            if (r.TopLeft > 0) c.ArcTo(new Point(r.TopLeft, 0), new Size(r.TopLeft, r.TopLeft), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Corner radii follow the docked edge: the corners touching the screen edge are square.</summary>
    void ApplyShape()
    {
        var radii = Radii();
        _card.CornerRadius = radii;
        var inner = new CornerRadius(Math.Max(0, radii.TopLeft - 1), Math.Max(0, radii.TopRight - 1), Math.Max(0, radii.BottomRight - 1), Math.Max(0, radii.BottomLeft - 1));
        _highlight.CornerRadius = inner;
        _dropRing.CornerRadius = inner;
        foreach (var (layer, grow) in _shadowLayers)
        {
            double Grow(double r) => r > 0 ? r + grow : 0;
            layer.CornerRadius = new CornerRadius(Grow(radii.TopLeft), Grow(radii.TopRight), Grow(radii.BottomRight), Grow(radii.BottomLeft));
        }
        SetCard(_cx, _cy, _cw, _ch);
    }

    /// <summary>Positions the card inside the window (DIPs).</summary>
    void SetCard(double x, double y, double w, double h)
    {
        _cx = x; _cy = y; _cw = w; _ch = h;
        foreach (UIElement e in new UIElement[] { _shadow, _contact, _card })
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
        }
        _shadow.Width = _contact.Width = _card.Width = w;
        _shadow.Height = _contact.Height = _card.Height = h;
        double iw = Math.Max(0, w - 2), ih = Math.Max(0, h - 2);
        var radii = Radii();
        _clip.Clip = RoundRect(iw, ih, new CornerRadius(Math.Max(0, radii.TopLeft - 1), Math.Max(0, radii.TopRight - 1), Math.Max(0, radii.BottomRight - 1), Math.Max(0, radii.BottomLeft - 1)));
        _content.HorizontalAlignment = _edge switch
        {
            ShelfEdge.Left => HorizontalAlignment.Left,
            ShelfEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
    }

    // ---------- placement ----------

    void EnsureHandle()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Opens the shelf near a point (physical pixels): the card's top centre just below it.</summary>
    public void PlaceNear(ScreenPoint near)
    {
        var (scale, _) = Native.MonitorAt(near);
        var (w, h) = ShelfLayout.Size(_model.Items.Count, _expanded, _mode);
        var desired = new Box(near.X - w * scale / 2, near.Y + 12 * scale, w * scale, h * scale);
        PlaceCard(desired, dock: true, pop: true);
    }

    /// <summary>Puts the card at a saved or chosen spot, keeping it on screen and docking it when it is close to an edge.</summary>
    public void PlaceAt(Box desiredCardPx, ShelfEdge? savedEdge, bool pop)
    {
        var (scale, _) = Native.MonitorAt(new ScreenPoint((int)desiredCardPx.MidX, (int)desiredCardPx.MidY));
        var (w, h) = ShelfLayout.Size(_model.Items.Count, _expanded, _mode);
        double nw = w * scale, nh = h * scale;
        // The saved frame may be from another state or monitor size: keep its top and its docked side.
        double x = savedEdge switch
        {
            ShelfEdge.Left => desiredCardPx.X,
            ShelfEdge.Right => desiredCardPx.Right - nw,
            _ => desiredCardPx.MidX - nw / 2,
        };
        PlaceCard(new Box(x, desiredCardPx.Y, nw, nh), dock: true, pop);
    }

    void PlaceCard(Box desired, bool dock, bool pop)
    {
        EnsureHandle();
        var center = new ScreenPoint((int)desired.MidX, (int)desired.MidY);
        var (scale, work) = Native.MonitorAt(center);
        _initialScale = scale;
        var workBox = new Box(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        var (settled, edge) = dock ? EdgeDocking.Settle(desired, workBox, EdgeDocking.ThresholdDip * scale)
                                   : (EdgeDocking.Clamp(desired, workBox), (ShelfEdge?)null);
        _edge = edge;
        var (w, h) = ShelfLayout.Size(_model.Items.Count, _expanded, _mode);
        ApplyShape();
        SetCard(CanonX(w), Pad, w, h);

        _stage.Opacity = 0;
        if (!_shown)
        {
            Show();
            _shown = true;
        }
        int wx = (int)Math.Round(settled.X - _cx * scale), wy = (int)Math.Round(settled.Y - _cy * scale);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, wx, wy, (int)Math.Round(WinW * scale), (int)Math.Round(WinH * scale), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Native.BringToTop(_hwnd);
        _placed = true;
        _stage.Opacity = 1;
        if (pop) PopIn();
        _env.Changed(this);
    }

    /// <summary>Moves the card so its top-left is at the given physical pixel position (no docking), for demos.</summary>
    public void MoveCardTo(double x, double y, bool settle)
    {
        var card = CardScreen();
        var target = card with { X = x, Y = y };
        var work = Native.WorkAt(target.MidX, target.MidY);
        var (settled, edge) = settle ? EdgeDocking.Settle(target, work, EdgeDocking.ThresholdDip * S) : (EdgeDocking.Clamp(target, work), (ShelfEdge?)null);
        Rehome(edge);
        var c2 = CardScreen();
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(settled.X - _cx * S), (int)Math.Round(settled.Y - _cy * S), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _ = c2;
        _env.Changed(this);
    }

    /// <summary>Changes the docked edge, keeping the card where it is on screen.</summary>
    void Rehome(ShelfEdge? edge)
    {
        if (edge == _edge) return;
        var cur = CardScreen();
        _edge = edge;
        double s = S;
        ApplyShape();
        SetCard(CanonX(_cw), _cy, _cw, _ch);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(cur.X - _cx * s), (int)Math.Round(cur.Y - _cy * s), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void PopIn()
    {
        _stageScale.CenterX = _cx + _cw / 2;
        _stageScale.CenterY = _cy + _ch / 2;
        _stage.Opacity = 0;
        Motion.Animate(_stage, OpacityProperty, 1, Motion.Enabled ? 200 : 0, Motion.EaseOut, from: 0);
        Motion.Animate(_stageScale, ScaleTransform.ScaleXProperty, 1, 320, Motion.Spring, from: 0.94);
        Motion.Animate(_stageScale, ScaleTransform.ScaleYProperty, 1, 320, Motion.Spring, from: 0.94);
    }

    public void BringToTop()
    {
        if (_hwnd != IntPtr.Zero && !_hidden) Native.BringToTop(_hwnd);
    }

    /// <summary>Brings the shelf forward, sliding it out when it is hidden at the edge. It then stays out until the pointer has visited it.</summary>
    public void BringForward()
    {
        _holdOpen = true;
        _awaySince = null;
        if (_hidden) Reveal(); else BringToTop();
    }

    // ---------- state changes ----------

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded || _model.Items.Count == 0) return;
        _expanded = expanded;
        _typeSelect.Reset();
        ApplyLayout(animate: true);
        _env.Changed(this);
    }

    public void SetViewMode(ShelfViewMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _tiles.Clear();
        RebuildTiles(animate: false);
        ApplyLayout(animate: true);
        _env.Changed(this);
    }

    /// <summary>Re-lays out after a change of state or item count. The body swaps at once, laid out at its final size;
    /// the card then grows or shrinks over it in 0.25 s with no bounce, anchored at the top and at the docked side.</summary>
    void ApplyLayout(bool animate)
    {
        RefreshBody();
        var (w, h) = ShelfLayout.Size(_model.Items.Count, _expanded, _mode);
        if (!_placed || _hwnd == IntPtr.Zero)
        {
            SetCard(CanonX(w), Pad, w, h);
            return;
        }

        _layoutTween?.Cancel();
        double s = S;
        var cur = CardScreen();
        double nw = w * s, nh = h * s;
        double x = _edge switch { ShelfEdge.Left => cur.X, ShelfEdge.Right => cur.Right - nw, _ => cur.MidX - nw / 2 };
        var target = EdgeDocking.Clamp(new Box(x, cur.Y, nw, nh), Native.WorkAt(cur.MidX, cur.MidY));
        double newCx = CanonX(w);
        Native.GetWindowRect(_hwnd, out var rect);
        double fromX = rect.Left, fromY = rect.Top;
        double toX = target.X - newCx * s, toY = target.Y - Pad * s;
        double fcx = _cx, fcy = _cy, fcw = _cw, fch = _ch;

        void Step(double t)
        {
            SetCard(Tween.Lerp(fcx, newCx, t), Tween.Lerp(fcy, Pad, t), Tween.Lerp(fcw, w, t), Tween.Lerp(fch, h, t));
            int wx = (int)Math.Round(Tween.Lerp(fromX, toX, t)), wy = (int)Math.Round(Tween.Lerp(fromY, toY, t));
            if (wx != rect.Left || wy != rect.Top)
                Native.SetWindowPos(_hwnd, IntPtr.Zero, wx, wy, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        void Done()
        {
            UpdateScrollThumb();
            if (_hidden) PositionTab();
        }

        if (!animate || _hidden || !_shown || !IsVisible)
        {
            Step(1);
            Done();
            return;
        }
        _layoutTween = Tween.Run(250, Motion.EaseInOut, Step, Done);
    }

    /// <summary>Which of the three bodies is showing, and the header buttons.</summary>
    void RefreshBody()
    {
        int n = _model.Items.Count;
        var (w, h) = ShelfLayout.Size(n, _expanded, _mode);
        double bodyH = h - ShelfLayout.HeaderHeight;
        _content.Width = w;
        _content.Height = h;
        _body.Width = w;
        _body.Height = bodyH;
        foreach (var e in new FrameworkElement[] { _empty, _stackHost }) { e.Width = w; e.Height = bodyH; }
        _scroll.Width = w;
        _scroll.Height = bodyH;
        _empty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        _stackHost.Visibility = n > 0 && !_expanded ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = n > 0 && _expanded ? Visibility.Visible : Visibility.Collapsed;
        _modeButton.Visibility = _expanded && n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _expandButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _modeGlyph.Text = _mode == ShelfViewMode.Grid ? "" : "";
        _expandGlyph.Text = _expanded ? "" : "";
        _title.Text = n == 0 ? "Shelf" : n == 1 ? "1 file" : $"{n} files";
        _canvas.Height = Math.Max(ShelfLayout.ContentHeight(n, _mode), bodyH);
        if (!_expanded) _scroll.ScrollToVerticalOffset(0);
        if (_scroll.Visibility == Visibility.Visible) { _scroll.UpdateLayout(); UpdateScrollThumb(); } else _scrollThumb.Visibility = Visibility.Collapsed;
        RebuildStack();
    }

    // ---------- close ----------

    protected override void OnClosing(CancelEventArgs e)
    {
        bool animate = !_closeFinished && _shown && IsVisible && Motion.Enabled && !_hidden &&
                       Application.Current is { } app && !app.Dispatcher.HasShutdownStarted;
        if (animate)
        {
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            IsHitTestVisible = false;
            _stageScale.CenterX = _cx + _cw / 2;
            _stageScale.CenterY = _cy + _ch / 2;
            Motion.Animate(_stageScale, ScaleTransform.ScaleXProperty, 0.96, 140, Motion.EaseOut);
            Motion.Animate(_stageScale, ScaleTransform.ScaleYProperty, 0.96, 140, Motion.EaseOut);
            Motion.Animate(_stage, OpacityProperty, 0, 140, Motion.EaseOut, completed: () =>
            {
                _closeFinished = true;
                try { Close(); } catch (Exception ex) { Log.Error("Shelf close", ex); }
            });
            return;
        }
        _closing = true;
        _layoutTween?.Cancel();
        _slideTween?.Cancel();
        base.OnClosing(e);
    }

    // ---------- items and tiles ----------

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            if (e.NewItems != null) foreach (ShelfItem i in e.NewItems) i.PropertyChanged += OnItemPropertyChanged;
            if (e.OldItems != null) foreach (ShelfItem i in e.OldItems) i.PropertyChanged -= OnItemPropertyChanged;
            RebuildTiles(animate: true);
            if (_model.Items.Count == 0 && _model.HasHadItems && !_closing)
            {
                Log.Info("Shelf emptied, closing");
                Dispatcher.BeginInvoke(() => { try { Close(); } catch (Exception ex) { Log.Error("Shelf close", ex); } });
                return;
            }
            if (_model.Items.Count == 0) _expanded = false;
            ApplyLayout(animate: true);
            _env.Changed(this);
            if (_hidden) _edgeTab.SetCount(_model.Items.Count);
        }
        catch (Exception ex) { Log.Error("Shelf items changed", ex); }
    }

    void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ShelfItem item) return;
        if (_tiles.TryGetValue(item, out var tile) && tile.Image.Source != item.Thumbnail) tile.Image.Source = item.Thumbnail;
        if (!_expanded && _model.Items.TakeLast(3).Contains(item)) RebuildStack();
    }

    void PruneMissing()
    {
        try { _model.RemoveMissing(); } catch (Exception ex) { Log.Error("Shelf prune", ex); }
    }

    void RebuildTiles(bool animate)
    {
        var old = new Dictionary<ShelfItem, Tile>(_tiles);
        _tiles.Clear();
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(_canvas.Children[i], _bandVisual)) _canvas.Children.RemoveAt(i);

        var frames = ShelfLayout.ItemFrames(_model.Items.Count, _mode);
        int newIndex = 0;
        for (int i = 0; i < _model.Items.Count; i++)
        {
            var item = _model.Items[i];
            var f = frames[i];
            bool isNew = !old.Remove(item, out var tile);
            tile ??= _mode == ShelfViewMode.Grid ? CreateGridTile(item) : CreateRowTile(item);
            double oldLeft = isNew ? f.X : Canvas.GetLeft(tile.Root), oldTop = isNew ? f.Y : Canvas.GetTop(tile.Root);
            Canvas.SetLeft(tile.Root, f.X);
            Canvas.SetTop(tile.Root, f.Y);
            _canvas.Children.Add(tile.Root);
            _tiles[item] = tile;
            UpdateTile(tile, animate: false);
            if (isNew)
            {
                if (animate && _shown) AnimateIn(tile, newIndex++);
            }
            else if (animate && _shown && Motion.Enabled && (Math.Abs(oldLeft - f.X) > 0.5 || Math.Abs(oldTop - f.Y) > 0.5))
            {
                tile.Shift.X = oldLeft - f.X;
                tile.Shift.Y = oldTop - f.Y;
                Motion.Animate(tile.Shift, TranslateTransform.XProperty, 0, 250, Motion.EaseInOut);
                Motion.Animate(tile.Shift, TranslateTransform.YProperty, 0, 250, Motion.EaseInOut);
            }
        }
        _canvas.Height = Math.Max(ShelfLayout.ContentHeight(_model.Items.Count, _mode), _scroll.Height > 0 ? _scroll.Height : 0);
    }

    static void AnimateIn(Tile t, int index)
    {
        if (!Motion.Enabled) return;
        int delay = Math.Min(index, 8) * 35;
        t.Root.Opacity = 0;
        t.RootScale.ScaleX = t.RootScale.ScaleY = 0.9;
        Motion.Animate(t.Root, OpacityProperty, 1, 200, Motion.EaseOut, delay, 0);
        Motion.Animate(t.RootScale, ScaleTransform.ScaleXProperty, 1, 260, Motion.Spring, delay, 0.9);
        Motion.Animate(t.RootScale, ScaleTransform.ScaleYProperty, 1, 260, Motion.Spring, delay, 0.9);
    }

    void RefreshSelection()
    {
        foreach (var t in _tiles.Values) UpdateTile(t, animate: true);
    }

    void UpdateTile(Tile t, bool animate)
    {
        bool sel = _model.Selection.Contains(t.Item);
        if (t.Image.Source != t.Item.Thumbnail) t.Image.Source = t.Item.Thumbnail;
        if (animate && t.Selected == sel) return;
        t.Selected = sel;
        int ms = animate ? 120 : 0;
        Motion.Animate(t.SelectFill, OpacityProperty, sel ? 1 : 0, ms, Motion.EaseOut);
        if (t.Mode == ShelfViewMode.List) return;
        if (sel)
        {
            Motion.Animate(t.Pill!, OpacityProperty, 1, ms, Motion.EaseOut);
            Motion.Animate(t.PillScale!, ScaleTransform.ScaleXProperty, 1, ms, Motion.EaseOut, from: animate ? 0.9 : null);
            Motion.Animate(t.PillScale!, ScaleTransform.ScaleYProperty, 1, ms, Motion.EaseOut, from: animate ? 0.9 : null);
        }
        else Motion.Animate(t.Pill!, OpacityProperty, 0, animate ? 100 : 0, Motion.EaseOut);
    }

    Tile CreateGridTile(ShelfItem item)
    {
        var image = new Image
        {
            Width = 66, Height = 54, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, 66, 54), 7, 7),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var thumb = new Border
        {
            Width = 68, Height = 56, CornerRadius = new CornerRadius(8),
            Background = Placeholder, BorderBrush = ThumbBorder, BorderThickness = new Thickness(1),
            Child = image,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.25, Color = Colors.Black },
        };

        var hoverFill = new Border { CornerRadius = new CornerRadius(12), Background = HoverBrush, Opacity = 0 };
        var selectFill = new Border { CornerRadius = new CornerRadius(12), Background = SelectBrush, Opacity = 0 };
        var backdrop = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        backdrop.Children.Add(hoverFill);
        backdrop.Children.Add(selectFill);
        backdrop.Children.Add(new Border { Padding = new Thickness(6), Child = thumb });

        var name = new TextBlock
        {
            Text = item.Name, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = NameBrush,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 32,
            MaxWidth = 80, Margin = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Center,
            UseLayoutRounding = true,
        };
        var pillScale = new ScaleTransform(1, 1);
        var pillFill = new Border
        {
            CornerRadius = new CornerRadius(9), Background = AccentBrush, Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pillScale,
        };
        var pill = new Grid { Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 92 };
        pill.Children.Add(pillFill);
        pill.Children.Add(name);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(backdrop);
        stack.Children.Add(pill);
        return FinishTile(item, ShelfViewMode.Grid, TileWidth, TileHeight, stack, hoverFill, selectFill, image, pillFill, pillScale, name);
    }

    const double TileWidth = ShelfLayout.TileWidth;
    const double TileHeight = ShelfLayout.TileHeight;

    Tile CreateRowTile(ShelfItem item)
    {
        var image = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, Clip = new RectangleGeometry(new Rect(0, 0, 24, 24), 5, 5) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var hoverFill = new Border { CornerRadius = new CornerRadius(9), Background = HoverBrush, Opacity = 0 };
        var selectFill = new Border { CornerRadius = new CornerRadius(9), Background = AccentBrush, Opacity = 0 };
        var name = new TextBlock
        {
            Text = item.Name, FontSize = 13, Foreground = NameBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(44, 0, 12, 0),
        };
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Center;
        image.Margin = new Thickness(10, 0, 0, 0);
        var grid = new Grid();
        grid.Children.Add(hoverFill);
        grid.Children.Add(selectFill);
        grid.Children.Add(image);
        grid.Children.Add(name);
        return FinishTile(item, ShelfViewMode.List, ShelfLayout.ExpandedWidth - 2 * ShelfLayout.BodyPadH, ShelfLayout.RowHeight, grid,
            hoverFill, selectFill, image, null, null, name);
    }

    Tile FinishTile(ShelfItem item, ShelfViewMode mode, double w, double h, UIElement child, Border hoverFill, Border selectFill,
        Image image, Border? pill, ScaleTransform? pillScale, TextBlock name)
    {
        var rootScale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(rootScale);
        group.Children.Add(shift);
        var root = new Border
        {
            Width = w, Height = h, Background = Clear, Child = child,
            Padding = mode == ShelfViewMode.Grid ? new Thickness(0, 2, 0, 0) : new Thickness(0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = group,
        };
        root.MouseEnter += (_, _) => Motion.Animate(hoverFill, OpacityProperty, 1, 90);
        root.MouseLeave += (_, _) => Motion.Animate(hoverFill, OpacityProperty, 0, 90);
        root.MouseLeftButtonDown += (_, e) => OnTileDown(item, root, e);
        root.MouseMove += (_, e) => OnTileMove(item, root, e);
        root.MouseLeftButtonUp += (_, e) => OnTileUp(item, root, e);
        root.MouseRightButtonUp += (_, e) => OnTileRightClick(item, e);
        return new Tile
        {
            Item = item, Mode = mode, Root = root, RootScale = rootScale, Shift = shift, HoverFill = hoverFill, SelectFill = selectFill,
            Pill = pill, PillScale = pillScale, Name = name, Image = image,
        };
    }

    // ---------- collapsed stack ----------

    readonly List<(RotateTransform Rot, TranslateTransform Move, int Depth)> _stackThumbs = new();

    void RebuildStack()
    {
        _stackLayer.Children.Clear();
        _stackThumbs.Clear();
        var top = _model.Items.TakeLast(3).ToList();
        for (int i = 0; i < top.Count; i++)
        {
            int depth = top.Count - 1 - i;
            var image = new Image { Source = top[i].Thumbnail, Width = 88, Height = 88, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1.5, Direction = 270, Opacity = 0.35, Color = Colors.Black };
            var rot = new RotateTransform(-7 * depth);
            var move = new TranslateTransform(-5 * depth, -3 * depth);
            var g = new TransformGroup();
            g.Children.Add(rot);
            g.Children.Add(move);
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = g;
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
            _stackLayer.Children.Add(image);
            _stackThumbs.Add((rot, move, depth));
        }
        if (_model.Items.Count > 1)
        {
            var badge = new Border
            {
                MinWidth = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 8), Padding = new Thickness(7, 0, 7, 0),
                Child = new TextBlock
                {
                    Text = _model.Items.Count.ToString(), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                },
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.3, Color = Colors.Black },
            };
            _stackLayer.Children.Add(badge);
        }
        if (_stackHover) SetStackSpread(true, animate: false);
    }

    void SetStackSpread(bool spread, bool animate = true)
    {
        _stackHover = spread;
        double k = spread ? 1.8 : 1;
        foreach (var (rot, move, depth) in _stackThumbs)
        {
            int ms = animate ? 150 : 0;
            Motion.Animate(rot, RotateTransform.AngleProperty, -7 * depth * k, ms, Motion.EaseOut);
            Motion.Animate(move, TranslateTransform.XProperty, -5 * depth * k, ms, Motion.EaseOut);
            Motion.Animate(move, TranslateTransform.YProperty, -3 * depth * k, ms, Motion.EaseOut);
        }
    }

    void OnStackDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (e.ClickCount == 2) { _pressItem = null; _pressIsStack = false; SetExpanded(true); return; }
            _pressIsStack = true;
            _pressItem = _model.Items.LastOrDefault();
            _pressPoint = e.GetPosition(this);
            _stackHost.CaptureMouse();
        }
        catch (Exception ex) { Log.Error("Shelf stack down", ex); }
    }

    void OnStackMove(object sender, MouseEventArgs e)
    {
        try
        {
            if (!_pressIsStack || e.LeftButton != MouseButtonState.Pressed || !PastDragThreshold(e)) return;
            _pressIsStack = false;
            _pressItem = null;
            _stackHost.ReleaseMouseCapture();
            StartDrag(_model.Items.ToList());
        }
        catch (Exception ex) { Log.Error("Shelf stack move", ex); }
    }

    void OnStackUp(object sender, MouseButtonEventArgs e)
    {
        _stackHost.ReleaseMouseCapture();
        _pressIsStack = false;
        _pressItem = null;
        e.Handled = true;
    }

    bool PastDragThreshold(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return Math.Abs(p.X - _pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(p.Y - _pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    // ---------- tile clicks, drags ----------

    List<ShelfItem> Targets(ShelfItem item) => _model.Selection.Contains(item) ? _model.Selected.ToList() : new List<ShelfItem> { item };

    void OnTileDown(ShelfItem item, UIElement tile, MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                _pressItem = null;
                FileActions.Open(Targets(item).Select(i => i.Path));
                return;
            }
            bool ctrl = Native.IsDown(0x11), shift = Native.IsDown(0x10);
            _collapseOnRelease = false;
            _pressIsStack = false;
            if (ctrl || shift)
            {
                _model.Click(item, ctrl, shift);
                _pressItem = ctrl && _model.Selection.Contains(item) ? item : null;
            }
            else
            {
                if (_model.Selection.Contains(item)) _collapseOnRelease = true;   // may be the start of a multi-file drag
                else _model.SelectOnly(item);
                _pressItem = item;
            }
            _pressPoint = e.GetPosition(this);
            if (_pressItem != null) tile.CaptureMouse();
        }
        catch (Exception ex) { Log.Error("Shelf tile down", ex); }
    }

    void OnTileMove(ShelfItem item, UIElement tile, MouseEventArgs e)
    {
        try
        {
            if (_pressItem == null || !ReferenceEquals(_pressItem, item) || e.LeftButton != MouseButtonState.Pressed || !PastDragThreshold(e)) return;
            _pressItem = null;
            _collapseOnRelease = false;
            tile.ReleaseMouseCapture();
            StartDrag(Targets(item));
        }
        catch (Exception ex) { Log.Error("Shelf tile move", ex); }
    }

    void OnTileUp(ShelfItem item, UIElement tile, MouseButtonEventArgs e)
    {
        try
        {
            tile.ReleaseMouseCapture();
            if (_collapseOnRelease && ReferenceEquals(_pressItem, item)) _model.SelectOnly(item);
            _collapseOnRelease = false;
            _pressItem = null;
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("Shelf tile up", ex); }
    }

    void OnTileRightClick(ShelfItem item, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_model.Selection.Contains(item)) _model.SelectOnly(item);
        ShowMenu(Targets(item));
    }

    // ---------- rubber band on empty space ----------

    void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        _bandStart = e.GetPosition(_canvas);
        _bandActive = false;
        _selectionBeforeBand = _model.Selected.ToList();
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (_bandStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        if (!_bandActive && Math.Abs(p.X - start.X) < 3 && Math.Abs(p.Y - start.Y) < 3) return;
        _bandActive = true;
        var rect = Box.FromPoints(start.X, start.Y, p.X, p.Y).Intersection(new Box(0, 0, _canvas.Width, _canvas.Height));
        _bandVisual.Visibility = Visibility.Visible;
        Canvas.SetLeft(_bandVisual, rect.X);
        Canvas.SetTop(_bandVisual, rect.Y);
        _bandVisual.Width = rect.W;
        _bandVisual.Height = rect.H;

        var frames = ShelfLayout.ItemFrames(_model.Items.Count, _mode);
        var byItem = new Dictionary<ShelfItem, Box>();
        for (int i = 0; i < frames.Length; i++) byItem[_model.Items[i]] = frames[i];
        _model.SetSelection(RubberBand.Selection(byItem, rect, _selectionBeforeBand, Native.IsDown(0x11), Native.IsDown(0x10)));
        AutoScrollTo(p.Y);
    }

    void AutoScrollTo(double contentY)
    {
        double y = contentY - _scroll.VerticalOffset;
        if (y < 14) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 10);
        else if (y > _scroll.ViewportHeight - 14) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 10);
    }

    void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (_bandStart == null) return;
        _canvas.ReleaseMouseCapture();
        if (!_bandActive && !Native.IsDown(0x11) && !Native.IsDown(0x10)) _model.ClearSelection();
        _bandStart = null;
        _bandActive = false;
        _bandVisual.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    // ---------- header drag and docking ----------

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            e.Handled = true;
            Native.ReleaseCapture();
            // Runs the system move loop; returns when the button is released.
            Native.SendMessage(_hwnd, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            SettleAfterMove();
        }
        catch (Exception ex) { Log.Error("Shelf header drag", ex); }
    }

    /// <summary>Docks or clamps the shelf once the user lets go of it.</summary>
    void SettleAfterMove()
    {
        var card = CardScreen();
        double s = S;
        var work = Native.WorkAt(card.MidX, card.MidY);
        var (settled, edge) = EdgeDocking.Settle(card, work, EdgeDocking.ThresholdDip * s);
        Rehome(edge);
        Native.GetWindowRect(_hwnd, out var r);
        int tx = (int)Math.Round(settled.X - _cx * s), ty = (int)Math.Round(settled.Y - _cy * s);
        _moveTween?.Cancel();
        int fx = r.Left, fy = r.Top;
        if (fx != tx || fy != ty)
            _moveTween = Tween.Run(250, Motion.EaseInOut, t =>
                Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(Tween.Lerp(fx, tx, t)), (int)Math.Round(Tween.Lerp(fy, ty, t)), 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE));
        _awaySince = null;
        _env.Changed(this);
    }

    // ---------- hiding at the edge ----------

    Box WorkOfCard()
    {
        var c = CardScreen();
        return Native.WorkAt(c.MidX, c.MidY);
    }

    void PositionTab()
    {
        if (_edge is not { } edge) return;
        _edgeTab.ShowTab(EdgeHiding.TabFrame(CardScreen(), edge, WorkOfCard(), S), edge, _model.Items.Count);
    }

    void CheckEdgeHiding()
    {
        try
        {
            if (_closing || !_placed || !_shown) return;
            if (_edge == null || !_env.Preferences.HideDockedShelves)
            {
                _awaySince = null;
                if (_hidden) Reveal();
                return;
            }
            if (_hidden) return;
            var p = Native.CursorPosition();
            bool near = EdgeHiding.IsPointerNear(p.X, p.Y, CardScreen(), EdgeHiding.HoverMarginDip * S);
            if (_holdOpen)
            {
                if (near) _holdOpen = false;
                return;
            }
            bool busy = Native.AnyMouseButtonDown() || _model.Items.Count == 0 || _env.Preview.IsOpen || DarkMenu.Current != null ||
                        _dragging || (_layoutTween != null && _layoutTween.IsRunning) || (_moveTween != null && _moveTween.IsRunning);
            if (busy || near) { _awaySince = null; return; }
            _awaySince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _awaySince.Value).TotalSeconds >= EdgeHiding.HideDelaySeconds) TuckAway();
        }
        catch (Exception ex) { Log.Error("Shelf edge check", ex); }
    }

    /// <summary>Slides the shelf behind its docked edge and leaves the tab (fades instead when motion is off).</summary>
    public void TuckAway()
    {
        if (_hidden || _edge is not { } edge || !_shown) return;
        _hidden = true;
        _awaySince = null;
        _slideTween?.Cancel();
        bool slide = Motion.Enabled;
        _hideShift = (_cw + 44) * (edge == ShelfEdge.Left ? -1 : 1);
        _slideTween = Tween.Run(slide ? 250 : 150, Motion.EaseInOut, t =>
        {
            if (slide)
            {
                _stageShift.X = _hideShift * t;
                // Stays solid for most of the slide, fades as it reaches the edge.
                _stage.Opacity = 1 - Math.Clamp((t - 0.45) / 0.55, 0, 1);
            }
            else _stage.Opacity = 1 - t;
        }, () =>
        {
            if (!_hidden) return;
            base.Hide();
            PositionTab();
        }, respectMotion: false);
    }

    /// <summary>Slides a hidden shelf back out from its edge.</summary>
    public void Reveal()
    {
        if (!_hidden) return;
        _hidden = false;
        _awaySince = null;
        _holdOpen = true;
        _slideTween?.Cancel();
        _edgeTab.HideTab();
        bool slide = Motion.Enabled;
        double from = slide ? _hideShift : 0;
        _stageShift.X = from;
        _stage.Opacity = 0;
        Show();
        BringToTop();
        _slideTween = Tween.Run(slide ? 250 : 150, Motion.EaseOut, t =>
        {
            _stageShift.X = from * (1 - t);
            _stage.Opacity = slide ? Math.Min(1, t / 0.4) : t;
        }, () => { _stageShift.X = 0; _stage.Opacity = 1; }, respectMotion: false);
    }

    // ---------- keyboard ----------

    void EnsureFocus()
    {
        try
        {
            if (_hwnd == IntPtr.Zero || _closing) return;
            if (!IsActive)
            {
                Native.RemoveExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
                Native.SetForegroundWindow(_hwnd);
                Activate();
            }
            Keyboard.Focus(this);
        }
        catch (Exception ex) { Log.Error("Shelf focus", ex); }
    }

    /// <summary>Focuses this shelf's window (used after the preview closes).</summary>
    public void Focus2() => EnsureFocus();

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            bool plain = !ctrl && !alt;
            switch (e.Key)
            {
                case Key.Space when plain: ShowPreview(); break;
                case Key.Delete when plain:
                case Key.Back when plain: RemoveSelected(); break;
                case Key.Return when plain: FileActions.Open(_model.SelectedOrAll.Select(i => i.Path)); break;
                case Key.Escape:
                    if (_expanded) SetExpanded(false); else Close();
                    break;
                case Key.A when ctrl && !alt: _model.SelectAll(); break;
                case Key.W when ctrl && !alt: Close(); break;
                default: return;
            }
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("Shelf key", ex); }
    }

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        try
        {
            if (!TypeSelect.IsTypingText(e.Text)) return;
            var prefix = _typeSelect.Append(e.Text, Environment.TickCount64);
            if (TypeSelect.Match(prefix, _model.Items.Select(i => i.Name).ToList()) is { } index)
            {
                var item = _model.Items[index];
                _model.SelectOnly(item);
                if (_expanded) ScrollTo(index);
            }
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("Shelf type-select", ex); }
    }

    void ScrollTo(int index)
    {
        var frame = ShelfLayout.ItemFrames(_model.Items.Count, _mode)[index];
        double max = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        double target = Math.Clamp(frame.MidY - _scroll.ViewportHeight / 2, 0, max);
        double from = _scroll.VerticalOffset;
        _scrollTween?.Cancel();
        _scrollTween = Tween.Run(250, Motion.EaseInOut, t => _scroll.ScrollToVerticalOffset(Tween.Lerp(from, target, t)));
    }

    void RemoveSelected()
    {
        var sel = _model.Selected;
        if (sel.Count > 0) _model.Remove(sel);
    }

    // ---------- menu, preview ----------

    void ShowPreview()
    {
        var paths = _model.SelectedOrAll.Select(i => i.Path).ToList();
        _env.Preview.Toggle(paths, Native.CursorPosition(), () => EnsureFocus());
    }

    public void ShowMenu(IReadOnlyList<ShelfItem> targets, ScreenPoint? at = null)
    {
        var paths = targets.Select(t => t.Path).ToList();
        var near = at ?? Native.CursorPosition();
        DarkMenu.Open(new[]
        {
            new MenuEntry("Open", () => FileActions.Open(paths), "", "Enter"),
            new MenuEntry("Preview", () => _env.Preview.Open(paths, 0, near, () => EnsureFocus()), "", "Space"),
            new MenuEntry("Show in Explorer", () => FileActions.Reveal(paths), ""),
            MenuEntry.Line(),
            new MenuEntry("Copy", () => FileActions.CopyFiles(paths), "", "Ctrl+C"),
            new MenuEntry("Copy Path", () => { if (FileActions.CopyPaths(paths)) _env.Toast(paths.Count == 1 ? "Copied path" : "Copied paths", near); }, ""),
            new MenuEntry("Share…", () => { if (!FileActions.Share(_hwnd, paths)) _env.Toast("Sharing isn't available", near); }, ""),
            MenuEntry.Line(),
            new MenuEntry("Remove from Shelf", () => _model.Remove(targets), "", "Del"),
        }, near);
    }

    /// <summary>The screen centre of a tile, in physical pixels (for demos).</summary>
    public ScreenPoint TileCentre(int index)
    {
        var f = ShelfLayout.ItemFrames(_model.Items.Count, _mode)[index];
        var c = CardScreen();
        double s = S;
        return new ScreenPoint((int)(c.X + (f.MidX) * s), (int)(c.Y + (ShelfLayout.HeaderHeight + f.MidY - _scroll.VerticalOffset) * s));
    }

    public void SelectIndices(params int[] indices) => _model.SetSelection(indices.Select(i => _model.Items[i]));

    public void ScrollToIndexNow(int index)
    {
        var frame = ShelfLayout.ItemFrames(_model.Items.Count, _mode)[index];
        _scroll.UpdateLayout();
        double max = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        _scroll.ScrollToVerticalOffset(Math.Clamp(frame.MidY - _scroll.ViewportHeight / 2, 0, max));
    }

    // ---------- drag out / drop in ----------

    void StartDrag(IReadOnlyList<ShelfItem> dragged)
    {
        var paths = dragged.Select(i => i.Path).ToArray();
        Log.Info($"Drag out started: {paths.Length} file(s)");
        _dragging = true;
        uint effect = 0;
        bool native = false;
        try { native = ShellDrag.TryDrag(_hwnd, paths, out effect); }
        catch (Exception ex) { Log.Error("Shell drag", ex); }

        if (!native)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            try
            {
                effect = (uint)DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex) { Log.Error("DoDragDrop", ex); }
        }
        _dragging = false;

        foreach (var t in _tiles.Values) Motion.Animate(t.HoverFill, OpacityProperty, 0, 0);
        int removed = 0;
        try
        {
            if (_env.Preferences.RemoveAfterDragOut && effect != 0) { _model.Remove(dragged); removed += dragged.Count; }
            removed += _model.RemoveMissing();
        }
        catch (Exception ex) { Log.Error("Shelf prune after drag", ex); }
        Log.Info($"Drag out finished ({(native ? "shell" : "wpf")}): effect={effect}, removed {removed}");

        // The destination may finish moving the files after the drag loop has ended.
        var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        again.Tick += (_, _) => { again.Stop(); if (!_closing) PruneMissing(); };
        again.Start();
    }

    void SetDropVisual(bool on) => Motion.Animate(_dropRing, OpacityProperty, on ? 1 : 0, 120, Motion.EaseOut);

    static (int X, int Y) CursorXY() { var p = Native.CursorPosition(); return (p.X, p.Y); }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        try
        {
            _env.Interacted(this);
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (!files) return;
            SetDropVisual(true);
            var (x, y) = CursorXY();
            _dropHelper.DragEnter(_hwnd, e.Data, x, y, 1);
            ShellDrag.SetDropDescription(e.Data, "Add to %1", "Shelf");
        }
        catch (Exception ex) { Log.Error("Shelf drag enter", ex); }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        try
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            var (x, y) = CursorXY();
            _dropHelper.DragOver(x, y, 1);
        }
        catch (Exception ex) { Log.Error("Shelf drag over", ex); }
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        try
        {
            SetDropVisual(false);
            _dropHelper.DragLeave();
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("Shelf drag leave", ex); }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true;
            SetDropVisual(false);
            var (x, y) = CursorXY();
            _dropHelper.Drop(e.Data, x, y, 1);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                int added = _model.Add(files);
                Log.Info($"Dropped {files.Length} file(s) on shelf, added {added}");
            }
            e.Effects = DragDropEffects.Copy;
        }
        catch (Exception ex) { Log.Error("Shelf drop", ex); }
    }
}
