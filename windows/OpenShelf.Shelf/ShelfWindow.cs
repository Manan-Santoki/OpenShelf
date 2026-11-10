using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>A floating, non-activating shelf window showing a ShelfModel as a thumbnail grid.</summary>
internal sealed class ShelfWindow : Window
{
    const double Pad = 48;          // transparent margin so the large soft shadow is never clipped
    const double CardRadius = 20;
    const double HeaderHeight = 40;
    const double CellWidth = 104;   // tile (96) + 4 margin each side: 8 px between tiles
    const double CellHeight = 120;
    const double TileWidth = 96;
    const double TileHeight = 112;
    const double BodyPadH = 8;
    const double BodyPadV = 4;
    const int Columns = 3;
    const int MaxRows = 3;
    const double CardWidth = Columns * CellWidth + 2 * BodyPadH;

    static readonly Brush Placeholder = Theme.White(0.08);
    static readonly Brush Clear = Brushes.Transparent;
    static readonly Brush AccentBrush = Theme.Brush(Theme.Accent);
    static readonly Brush HoverBrush = Theme.White(0.06);
    static readonly Brush SelectBrush = Theme.White(0.12);
    static readonly Brush ThumbBorder = Theme.White(0.10);
    static readonly Brush NameBrush = Theme.White(0.92);

    sealed class Tile
    {
        public required ShelfItem Item;
        public required FrameworkElement Root;
        public required ScaleTransform RootScale;
        public required Border HoverFill;
        public required Border SelectFill;
        public required Border Pill;
        public required ScaleTransform PillScale;
        public required Image Image;
        public bool Selected;
    }

    readonly ShelfModel _model;
    readonly WrapPanel _panel = new() { Width = Columns * CellWidth, Margin = new Thickness(BodyPadH, BodyPadV, BodyPadH, BodyPadV) };
    readonly TextBlock _title = new();
    readonly TextBlock _hint = new();
    readonly ScrollViewer _scroll = new();
    readonly Dictionary<ShelfItem, Tile> _tiles = new();
    readonly Grid _host = new();
    readonly ScaleTransform _hostScale = new(1, 1);
    readonly Border _dropRing = new();
    readonly DropTargetHelper _dropHelper = new();
    IntPtr _hwnd;

    ShelfItem? _anchor;
    ShelfItem? _pressItem;
    Point _pressPoint;
    bool _collapseOnRelease;
    bool _closing;
    bool _closeFinished;
    bool _shown;
    int _heightVersion;

    /// <summary>Raised when the user interacts with this shelf (makes it the most recent).</summary>
    public event Action<ShelfWindow>? Interacted;

    public ShelfModel Model => _model;
    public IntPtr Handle => _hwnd;

    public ShelfWindow(ShelfModel model)
    {
        _model = model;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        FontFamily = Theme.TextFont;
        Width = CardWidth + 2 * Pad;
        Height = ComputeHeight();
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
        PreviewMouseDown += (_, _) => Interacted?.Invoke(this);
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        Closed += (_, _) =>
        {
            _model.Items.CollectionChanged -= OnItemsChanged;
            foreach (var t in _tiles.Values) t.Item.PropertyChanged -= OnItemPropertyChanged;
            _dropHelper.Dispose();
        };

        RebuildTiles();
    }

    // ---------- layout ----------

    int Rows => Math.Max(1, Math.Min(MaxRows, (_model.Items.Count + Columns - 1) / Columns));

    double ComputeHeight() => 2 * Pad + HeaderHeight + Rows * CellHeight + 2 * BodyPadV;

    UIElement BuildVisualTree()
    {
        // Two soft shadow layers sit behind the card (a wide ambient one and a tight contact one).
        Border Shadow(double blur, double opacity, double depth) => new()
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(CardRadius),
            Background = Theme.Brush(Theme.Surface),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black,
                RenderingBias = RenderingBias.Quality,
            },
        };
        _host.Children.Add(Shadow(34, 0.35, 12));
        _host.Children.Add(Shadow(4, 0.25, 1));

        var surfaceTop = Color.FromRgb(0x2C, 0x2C, 0x30);
        var card = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(CardRadius),
            Background = Theme.Vertical((surfaceTop, 0.98, 0), (Theme.Surface, 0.98, 1)),
            BorderBrush = Theme.White(0.14),
            BorderThickness = new Thickness(1),
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Inner top highlight (1 px, white 6% fading out) and the drag-over ring overlay.
        var highlight = new Border
        {
            CornerRadius = new CornerRadius(CardRadius - 1),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Vertical((Colors.White, 0.06, 0), (Colors.White, 0.0, 0.35)),
            IsHitTestVisible = false,
        };
        Grid.SetRowSpan(highlight, 2);
        _dropRing.CornerRadius = new CornerRadius(CardRadius - 1);
        _dropRing.BorderThickness = new Thickness(1.5);
        _dropRing.BorderBrush = Theme.Brush(Theme.Accent, 0.7);
        _dropRing.Background = Theme.White(0.03);
        _dropRing.Opacity = 0;
        _dropRing.IsHitTestVisible = false;
        Grid.SetRowSpan(_dropRing, 2);

        // Header: drag handle, centred title, close button on the left.
        var header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += OnHeaderDown;

        _title.FontFamily = Theme.Font;
        _title.FontSize = 15;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.White(0.62);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.IsHitTestVisible = false;
        header.Children.Add(_title);

        var closeHover = new Border { CornerRadius = new CornerRadius(14), Background = Theme.White(0.10), Opacity = 0 };
        var closeGlyph = new TextBlock
        {
            Text = "", FontFamily = Theme.IconFont, FontSize = 11,
            Foreground = Theme.White(0.62),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Grid
        {
            Width = 28, Height = 28, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = Clear, Cursor = Cursors.Arrow,
        };
        close.Children.Add(closeHover);
        close.Children.Add(closeGlyph);
        close.MouseEnter += (_, _) => Motion.Animate(closeHover, OpacityProperty, 1, 90);
        close.MouseLeave += (_, _) => Motion.Animate(closeHover, OpacityProperty, 0, 90);
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Close(); } catch (Exception ex) { Log.Error("Shelf close", ex); }
        };
        header.Children.Add(close);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body.
        var body = new Grid();
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Background = Brushes.Transparent;
        _scroll.Content = _panel;
        _scroll.MouseLeftButtonDown += (_, e) =>
        {
            // Tiles mark their clicks handled, so this only fires on empty space.
            ClearSelection();
            e.Handled = true;
        };
        body.Children.Add(_scroll);

        _hint.Text = "Drop files here";
        _hint.FontSize = 13;
        _hint.Foreground = Theme.White(0.5);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.IsHitTestVisible = false;
        body.Children.Add(_hint);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        root.Children.Add(highlight);
        root.Children.Add(_dropRing);
        card.Child = root;
        _host.Children.Add(card);

        _host.RenderTransformOrigin = new Point(0.5, 0.5);
        _host.RenderTransform = _hostScale;
        return _host;
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            e.Handled = true;
            Native.ReleaseCapture();
            Native.SendMessage(_hwnd, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("Shelf header drag", ex); }
    }

    // ---------- placement and window motion ----------

    /// <summary>Shows the window next to a physical-pixel point, clamped inside that monitor's work area.</summary>
    public void ShowNear(ScreenPoint near)
    {
        _host.Opacity = 0;
        _hostScale.ScaleX = _hostScale.ScaleY = Motion.Enabled ? 0.94 : 1;
        Show();
        try
        {
            var (scale, work) = Native.MonitorAt(near);
            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);
            int x = near.X - w / 2;
            int y = near.Y + (int)Math.Round((12 - Pad) * scale);
            (x, y) = Native.Clamp(x, y, w, h, work);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.BringToTop(_hwnd);
        }
        catch (Exception ex) { Log.Error("Shelf placement", ex); }
        _shown = true;

        Motion.Animate(_host, OpacityProperty, 1, Motion.Enabled ? 200 : 0, Motion.EaseOut, from: 0);
        Motion.Animate(_hostScale, ScaleTransform.ScaleXProperty, 1, 320, Motion.Spring, from: 0.94);
        Motion.Animate(_hostScale, ScaleTransform.ScaleYProperty, 1, 320, Motion.Spring, from: 0.94);
    }

    public void BringToTop()
    {
        if (_hwnd != IntPtr.Zero) Native.BringToTop(_hwnd);
    }

    /// <summary>Close plays a short scale and fade first, then really closes.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        bool animate = !_closeFinished && _shown && IsVisible && Motion.Enabled &&
                       Application.Current is { } app && !app.Dispatcher.HasShutdownStarted;
        if (animate)
        {
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            IsHitTestVisible = false;
            Motion.Animate(_hostScale, ScaleTransform.ScaleXProperty, 0.96, 140, Motion.EaseOut);
            Motion.Animate(_hostScale, ScaleTransform.ScaleYProperty, 0.96, 140, Motion.EaseOut);
            Motion.Animate(_host, OpacityProperty, 0, 140, Motion.EaseOut, completed: () =>
            {
                _closeFinished = true;
                try { Close(); } catch (Exception ex) { Log.Error("Shelf close", ex); }
            });
            return;
        }
        _closing = true;
        base.OnClosing(e);
    }

    void SetHeight(double target, bool animate)
    {
        int version = ++_heightVersion;
        if (!animate || !_shown || !Motion.Enabled || Math.Abs(Height - target) < 0.5)
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            if (_shown) ClampToWorkArea();
            return;
        }
        Motion.Animate(this, HeightProperty, target, 200, Motion.EaseInOut, completed: () =>
        {
            if (version != _heightVersion) return;
            BeginAnimation(HeightProperty, null);
            Height = target;
            ClampToWorkArea();
        });
    }

    /// <summary>Moves the window up/left if growing pushed it past the monitor work area.</summary>
    void ClampToWorkArea()
    {
        try
        {
            if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var (_, work) = Native.MonitorAt(new ScreenPoint(r.Left + w / 2, r.Top + h / 2));
            var (x, y) = Native.Clamp(r.Left, r.Top, w, h, work);
            if (x != r.Left || y != r.Top)
                Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("Shelf clamp", ex); }
    }

    // ---------- items and tiles ----------

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            RebuildTiles();
            if (_model.Items.Count == 0 && e.Action != NotifyCollectionChangedAction.Add && !_closing)
            {
                Log.Info("Shelf emptied, closing");
                Dispatcher.BeginInvoke(() => { try { Close(); } catch (Exception ex) { Log.Error("Shelf close", ex); } });
            }
        }
        catch (Exception ex) { Log.Error("Shelf items changed", ex); }
    }

    void RebuildTiles()
    {
        var old = new Dictionary<ShelfItem, Tile>(_tiles);
        _tiles.Clear();
        _panel.Children.Clear();

        int newIndex = 0;
        foreach (var item in _model.Items)
        {
            if (old.Remove(item, out var existing))
            {
                _tiles[item] = existing;
                _panel.Children.Add(existing.Root);
                continue;
            }
            var tile = CreateTile(item);
            _tiles[item] = tile;
            _panel.Children.Add(tile.Root);
            item.PropertyChanged += OnItemPropertyChanged;
            UpdateTile(tile, animate: false);
            if (_shown) AnimateIn(tile, newIndex++);
        }
        foreach (var gone in old.Values) gone.Item.PropertyChanged -= OnItemPropertyChanged;

        if (_anchor != null && !_model.Items.Contains(_anchor)) _anchor = null;
        int n = _model.Items.Count;
        _title.Text = n == 0 ? "Shelf" : n == 1 ? "1 file" : $"{n} files";
        _hint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetHeight(ComputeHeight(), animate: true);
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

    void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ShelfItem item && _tiles.TryGetValue(item, out var tile)) UpdateTile(tile, animate: true);
    }

    static void UpdateTile(Tile t, bool animate)
    {
        bool sel = t.Item.IsSelected;
        if (t.Image.Source != t.Item.Thumbnail) t.Image.Source = t.Item.Thumbnail;
        if (animate && t.Selected == sel) return;
        t.Selected = sel;

        int ms = animate ? 120 : 0;
        Motion.Animate(t.SelectFill, OpacityProperty, sel ? 1 : 0, ms, Motion.EaseOut);
        if (sel)
        {
            Motion.Animate(t.Pill, OpacityProperty, 1, ms, Motion.EaseOut);
            Motion.Animate(t.PillScale, ScaleTransform.ScaleXProperty, 1, ms, Motion.EaseOut, from: animate ? 0.9 : null);
            Motion.Animate(t.PillScale, ScaleTransform.ScaleYProperty, 1, ms, Motion.EaseOut, from: animate ? 0.9 : null);
        }
        else
        {
            Motion.Animate(t.Pill, OpacityProperty, 0, animate ? 100 : 0, Motion.EaseOut);
        }
    }

    Tile CreateTile(ShelfItem item)
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

        var rootScale = new ScaleTransform(1, 1);
        var root = new Border
        {
            Width = TileWidth, Height = TileHeight, Margin = new Thickness(4), Background = Brushes.Transparent, Child = stack,
            Padding = new Thickness(0, 2, 0, 0), ToolTip = item.Path,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rootScale,
        };

        root.MouseEnter += (_, _) => Motion.Animate(hoverFill, OpacityProperty, 1, 90);
        root.MouseLeave += (_, _) => Motion.Animate(hoverFill, OpacityProperty, 0, 90);
        root.MouseLeftButtonDown += (_, e) => OnTileDown(item, root, e);
        root.MouseMove += (_, e) => OnTileMove(item, root, e);
        root.MouseLeftButtonUp += (_, e) => OnTileUp(item, root, e);

        return new Tile
        {
            Item = item, Root = root, RootScale = rootScale, HoverFill = hoverFill, SelectFill = selectFill,
            Pill = pillFill, PillScale = pillScale, Image = image,
        };
    }

    // ---------- selection ----------

    void ClearSelection()
    {
        foreach (var i in _model.Items) i.IsSelected = false;
    }

    void SelectOnly(ShelfItem item)
    {
        foreach (var i in _model.Items) i.IsSelected = ReferenceEquals(i, item);
    }

    List<ShelfItem> Selection() => _model.Items.Where(i => i.IsSelected).ToList();

    void OnTileDown(ShelfItem item, UIElement tile, MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            bool ctrl = Native.IsDown(0x11), shift = Native.IsDown(0x10);
            _collapseOnRelease = false;

            if (shift && _anchor != null && _model.Items.Contains(_anchor))
            {
                int a = _model.Items.IndexOf(_anchor), b = _model.Items.IndexOf(item);
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                for (int i = 0; i < _model.Items.Count; i++) _model.Items[i].IsSelected = i >= lo && i <= hi;
                _pressItem = null;
                return;
            }
            if (ctrl)
            {
                item.IsSelected = !item.IsSelected;
                _anchor = item;
                _pressItem = item.IsSelected ? item : null;
            }
            else
            {
                if (item.IsSelected) _collapseOnRelease = true; // may be the start of a multi-file drag
                else SelectOnly(item);
                _anchor = item;
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
            if (_pressItem == null || !ReferenceEquals(_pressItem, item) || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _pressItem = null;
            _collapseOnRelease = false;
            tile.ReleaseMouseCapture();
            StartDrag(item);
        }
        catch (Exception ex) { Log.Error("Shelf tile move", ex); }
    }

    void OnTileUp(ShelfItem item, UIElement tile, MouseButtonEventArgs e)
    {
        try
        {
            tile.ReleaseMouseCapture();
            if (_collapseOnRelease && ReferenceEquals(_pressItem, item)) SelectOnly(item);
            _collapseOnRelease = false;
            _pressItem = null;
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("Shelf tile up", ex); }
    }

    // ---------- drag out / drop in ----------

    void StartDrag(ShelfItem pressed)
    {
        var dragged = pressed.IsSelected ? Selection() : new List<ShelfItem> { pressed };
        var paths = dragged.Select(i => i.Path).ToArray();
        Log.Info($"Drag out started: {paths.Length} file(s)");

        string result = "None";
        bool native = false;
        try { native = ShellDrag.TryDrag(_hwnd, paths, out var effect) && Describe(effect, out result); }
        catch (Exception ex) { Log.Error("Shell drag", ex); }

        if (!native)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            try
            {
                result = DragDrop.DoDragDrop(this, data,
                    DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link).ToString();
            }
            catch (Exception ex) { Log.Error("DoDragDrop", ex); }
        }

        // The pointer ended somewhere else; make sure no tile keeps a stale hover.
        foreach (var t in _tiles.Values) Motion.Animate(t.HoverFill, OpacityProperty, 0, 0);

        int removed = 0;
        try { removed = _model.RemoveMissing(); }
        catch (Exception ex) { Log.Error("Shelf prune after drag", ex); }
        Log.Info($"Drag out finished ({(native ? "shell" : "wpf")}): effect={result}, removed {removed} missing file(s)");
    }

    static bool Describe(uint effect, out string text)
    {
        text = effect switch { 0 => "None", 1 => "Copy", 2 => "Move", 4 => "Link", _ => effect.ToString() };
        return true;
    }

    static (int X, int Y) CursorXY() { var p = Native.CursorPosition(); return (p.X, p.Y); }

    void SetDropVisual(bool on) => Motion.Animate(_dropRing, OpacityProperty, on ? 1 : 0, 120, Motion.EaseOut);

    void OnDragEnter(object sender, DragEventArgs e)
    {
        try
        {
            Interacted?.Invoke(this);
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
