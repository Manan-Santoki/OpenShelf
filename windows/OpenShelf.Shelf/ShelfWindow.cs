using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenShelf.Core;

namespace OpenShelf.Shelf;

/// <summary>A floating, non-activating shelf window showing a ShelfModel as a thumbnail grid.</summary>
internal sealed class ShelfWindow : Window
{
    const double Pad = 20;        // transparent margin for the drop shadow
    const double CardWidth = 324;
    const double HeaderHeight = 36;
    const double RowHeight = 110;
    const double BodyPadding = 8;
    const int Columns = 3;
    const int MaxRows = 3;

    static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFD)));
    static readonly Brush SelectedBackdrop = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));
    static readonly Brush Placeholder = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)));
    static readonly Brush Clear = Brushes.Transparent;

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    sealed class Tile
    {
        public required ShelfItem Item;
        public required FrameworkElement Root;
        public required Border Backdrop;
        public required Border Pill;
        public required Image Image;
    }

    readonly ShelfModel _model;
    readonly WrapPanel _panel = new() { Width = Columns * 100, Margin = new Thickness(12, BodyPadding, 0, BodyPadding) };
    readonly TextBlock _title = new();
    readonly TextBlock _hint = new();
    readonly ScrollViewer _scroll = new();
    readonly Dictionary<ShelfItem, Tile> _tiles = new();
    IntPtr _hwnd;

    ShelfItem? _anchor;
    ShelfItem? _pressItem;
    Point _pressPoint;
    bool _collapseOnRelease;
    bool _closing;

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
        FontFamily = new FontFamily("Segoe UI");
        Width = CardWidth + 2 * Pad;
        Height = ComputeHeight();
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

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
        DragEnter += (_, _) => Interacted?.Invoke(this);
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += (_, _) => { _closing = true; };
        Closed += (_, _) => _model.Items.CollectionChanged -= OnItemsChanged;

        RebuildTiles();
    }

    // ---------- layout ----------

    int Rows => Math.Max(1, Math.Min(MaxRows, (_model.Items.Count + Columns - 1) / Columns));

    double ComputeHeight() => 2 * Pad + HeaderHeight + Rows * RowHeight + 2 * BodyPadding;

    UIElement BuildVisualTree()
    {
        var card = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(20),
            Background = Frozen(new SolidColorBrush(Color.FromArgb(0xF7, 0x26, 0x26, 0x29))),
            BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x29, 255, 255, 255))),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black
            },
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: drag handle, centred title, close button on the left.
        var header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += OnHeaderDown;

        _title.FontSize = 14;
        _title.FontWeight = FontWeights.Bold;
        _title.Foreground = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)));
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.IsHitTestVisible = false;
        header.Children.Add(_title);

        var closeText = new TextBlock
        {
            Text = "✕", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255))),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = Clear, Child = closeText, Cursor = Cursors.Arrow,
        };
        close.MouseEnter += (_, _) => close.Background = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)));
        close.MouseLeave += (_, _) => close.Background = Clear;
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
        _hint.Foreground = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)));
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.IsHitTestVisible = false;
        body.Children.Add(_hint);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        card.Child = root;
        return card;
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

    // ---------- placement ----------

    /// <summary>Shows the window next to a physical-pixel point, clamped inside that monitor's work area.</summary>
    public void ShowNear(ScreenPoint near)
    {
        Opacity = 0;
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
        Opacity = 1;
    }

    public void BringToTop()
    {
        if (_hwnd != IntPtr.Zero) Native.BringToTop(_hwnd);
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
        foreach (var t in _tiles.Values) t.Item.PropertyChanged -= OnItemPropertyChanged;
        _tiles.Clear();
        _panel.Children.Clear();

        foreach (var item in _model.Items)
        {
            var tile = CreateTile(item);
            _tiles[item] = tile;
            _panel.Children.Add(tile.Root);
            item.PropertyChanged += OnItemPropertyChanged;
            UpdateTile(tile);
        }

        if (_anchor != null && !_model.Items.Contains(_anchor)) _anchor = null;
        int n = _model.Items.Count;
        _title.Text = n == 0 ? "Shelf" : n == 1 ? "1 file" : $"{n} files";
        _hint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        Height = ComputeHeight();
    }

    void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ShelfItem item && _tiles.TryGetValue(item, out var tile)) UpdateTile(tile);
    }

    static void UpdateTile(Tile t)
    {
        t.Backdrop.Background = t.Item.IsSelected ? SelectedBackdrop : Clear;
        t.Pill.Background = t.Item.IsSelected ? Accent : Clear;
        t.Image.Source = t.Item.Thumbnail;
    }

    Tile CreateTile(ShelfItem item)
    {
        var image = new Image
        {
            Width = 92, Height = 62, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, 92, 62), 6, 6),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var thumb = new Border
        {
            Width = 92, Height = 62, CornerRadius = new CornerRadius(6),
            Background = Placeholder, Child = image,
        };
        var backdrop = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(4),
            Background = Clear, Child = thumb, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = item.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 30,
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 2, 0, 0), Background = Clear, Child = name,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 96,
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(backdrop);
        stack.Children.Add(pill);
        var root = new Border
        {
            Width = 100, Height = RowHeight, Background = Brushes.Transparent, Child = stack,
            Padding = new Thickness(0, 2, 0, 0), ToolTip = item.Path,
        };

        root.MouseLeftButtonDown += (_, e) => OnTileDown(item, root, e);
        root.MouseMove += (_, e) => OnTileMove(item, root, e);
        root.MouseLeftButtonUp += (_, e) => OnTileUp(item, root, e);

        return new Tile { Item = item, Root = root, Backdrop = backdrop, Pill = pill, Image = image };
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

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        DragDropEffects result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(this, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex) { Log.Error("DoDragDrop", ex); }

        int removed = 0;
        try { removed = _model.RemoveMissing(); }
        catch (Exception ex) { Log.Error("Shelf prune after drag", ex); }
        Log.Info($"Drag out finished: effect={result}, removed {removed} missing file(s)");
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true;
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
