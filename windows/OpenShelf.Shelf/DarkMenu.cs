using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

internal sealed record MenuEntry(string Label, Action? Click = null, string? Glyph = null, string? Hint = null,
    bool Enabled = true, bool Separator = false)
{
    public static MenuEntry Line() => new("", Separator: true);
}

/// <summary>The dark, rounded popup menu used by the shelf's context menu and the tray icon. Takes keyboard focus
/// while open (Up/Down, Return, Esc) and closes when it loses it.</summary>
internal sealed class DarkMenu : Window
{
    const double Pad = 20;
    const double Radius = 12;
    const double ItemHeight = 32;

    static readonly Brush HoverFill = Theme.White(0.10);
    static readonly Brush Label = Theme.White(0.94);
    static readonly Brush Dim = Theme.White(0.38);
    static readonly Brush Soft = Theme.White(0.60);

    /// <summary>The menu currently open, if any (shelves don't tuck away while one is showing).</summary>
    public static DarkMenu? Current { get; private set; }

    readonly List<(MenuEntry Entry, Border Row)> _rows = new();
    readonly Border _card = new();
    int _index = -1;
    bool _done;

    DarkMenu(IReadOnlyList<MenuEntry> entries)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        var stack = new StackPanel { Margin = new Thickness(6) };
        bool anyGlyph = entries.Any(e => e.Glyph != null);
        foreach (var entry in entries)
        {
            if (entry.Separator)
            {
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5), Background = Theme.White(0.12) });
                continue;
            }
            var row = BuildRow(entry, anyGlyph);
            stack.Children.Add(row);
            _rows.Add((entry, row));
        }

        _card.Margin = new Thickness(Pad);
        _card.MinWidth = 220;
        _card.CornerRadius = new CornerRadius(Radius);
        _card.Background = Theme.Brush(Theme.SurfaceRaised, 0.98);
        _card.BorderBrush = Theme.White(0.16);
        _card.BorderThickness = new Thickness(1);
        _card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.4, Color = Colors.Black };
        _card.Child = stack;
        Content = _card;

        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += OnKey;
        MouseLeave += (_, _) => Highlight(-1);
        Closed += (_, _) => { if (ReferenceEquals(Current, this)) Current = null; };
    }

    Border BuildRow(MenuEntry entry, bool anyGlyph)
    {
        var grid = new Grid { Height = ItemHeight };
        if (anyGlyph) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int c = 0;
        if (anyGlyph)
        {
            grid.Children.Add(new IconView(entry.Glyph, 16, entry.Enabled ? Soft : Dim));
            c = 1;
        }
        var text = new TextBlock
        {
            Text = entry.Label, FontSize = 13.5, Foreground = entry.Enabled ? Label : Dim,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(anyGlyph ? 0 : 10, 0, 16, 0),
        };
        Grid.SetColumn(text, c);
        grid.Children.Add(text);
        if (entry.Hint != null)
        {
            var hint = new TextBlock
            {
                Text = entry.Hint, FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(hint, c + 1);
            grid.Children.Add(hint);
        }

        var row = new Border { CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Child = grid };
        row.MouseEnter += (_, _) => Highlight(_rows.FindIndex(r => ReferenceEquals(r.Row, row)));
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(_rows.FindIndex(r => ReferenceEquals(r.Row, row))); };
        return row;
    }

    void Highlight(int index)
    {
        _index = index;
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Row.Background = i == index && _rows[i].Entry.Enabled ? HoverFill : Brushes.Transparent;
    }

    void Choose(int index)
    {
        if (index < 0 || index >= _rows.Count || !_rows[index].Entry.Enabled) return;
        var click = _rows[index].Entry.Click;
        CloseMenu();
        // Run once the menu has gone, so focus has returned to whoever opened it.
        if (click != null) Dispatcher.BeginInvoke(() =>
        {
            try { click(); } catch (Exception ex) { Log.Error("Menu action", ex); }
        });
    }

    void Move(int step)
    {
        if (_rows.Count == 0) return;
        int i = _index;
        for (int n = 0; n < _rows.Count; n++)
        {
            i = ((i < 0 ? (step > 0 ? -1 : 0) : i) + step + _rows.Count) % _rows.Count;
            if (_rows[i].Entry.Enabled) { Highlight(i); return; }
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Home: Highlight(-1); Move(1); break;
            case Key.End: Highlight(0); Move(-1); break;
            case Key.Enter: case Key.Space: Choose(_index); break;
            case Key.Escape: CloseMenu(); break;
            default: return;
        }
        e.Handled = true;
    }

    void CloseMenu()
    {
        if (_done) return;
        _done = true;
        try { Close(); } catch (Exception) { }
    }

    /// <summary>Opens a menu with its top-left corner at <paramref name="at"/> (physical pixels), moved to stay on screen.</summary>
    public static DarkMenu Open(IReadOnlyList<MenuEntry> entries, ScreenPoint at)
    {
        Current?.CloseMenu();
        var menu = new DarkMenu(entries);
        Current = menu;
        try
        {
            var (scale, work) = Native.MonitorAt(at);
            menu._card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var card = menu._card.DesiredSize;
            double w = card.Width + 2 * Pad, h = card.Height + 2 * Pad;     // _card includes its Pad margin
            int pw = (int)Math.Round(w * scale), ph = (int)Math.Round(h * scale);
            int padPx = (int)Math.Round(Pad * scale);
            int ix = at.X, iy = at.Y, iw = pw - 2 * padPx, ih = ph - 2 * padPx;
            // Open upwards/leftwards when there is no room below/right, like a native menu.
            if (ix + iw > work.Right) ix = at.X - iw;
            if (iy + ih > work.Bottom) iy = at.Y - ih;
            (ix, iy) = Native.Clamp(ix, iy, iw, ih, work);
            int x = ix - padPx, y = iy - padPx;

            menu.Left = x / scale; menu.Top = y / scale;      // provisional; corrected below once the HWND exists
            menu.Show();
            var hwnd = new WindowInteropHelper(menu).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            Native.SetForegroundWindow(hwnd);
            menu.Activate();
            menu.Focus();
        }
        catch (Exception ex) { Log.Error("Menu open", ex); }

        Motion.Animate(menu._card, OpacityProperty, 1, 110, Motion.EaseOut, from: 0);
        return menu;
    }

    public void Dismiss() => CloseMenu();
}
