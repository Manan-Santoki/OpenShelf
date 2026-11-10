using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>The progress card of SPEC 2.3: "Converting to JPG", "1 of 3 saved", a thin bar and a round cancel button.
/// Glass like the toast, non-activating, but clickable (the cancel button).</summary>
public sealed class ProgressService : IProgressService
{
    readonly IToastService _toasts;

    public ProgressService(IToastService toasts) => _toasts = toasts;

    public IProgressCard Begin(string title, int total, ScreenPoint near, CancellationTokenSource cancel)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => Begin(title, total, near, cancel));
        var card = new ProgressCard(dispatcher, _toasts, title, Math.Max(1, total), near, cancel);
        card.Open();
        return card;
    }
}

internal sealed class ProgressCard : IProgressCard
{
    readonly Dispatcher _dispatcher;
    readonly IToastService _toasts;
    readonly ScreenPoint _near;
    readonly ProgressWindow _window;
    readonly object _gate = new();
    int _completed;
    double? _fraction;
    string? _detail;
    bool _pending;
    bool _closed;

    public ProgressCard(Dispatcher dispatcher, IToastService toasts, string title, int total, ScreenPoint near, CancellationTokenSource cancel)
    {
        _dispatcher = dispatcher;
        _toasts = toasts;
        _near = near;
        _window = new ProgressWindow(title, total, cancel);
    }

    public void Open()
    {
        try { _window.ShowNear(_near); }
        catch (Exception ex) { Log.Error("Progress open", ex); }
    }

    public void Report(int completed, double? fraction = null, string? detail = null)
    {
        lock (_gate)
        {
            _completed = completed;
            _fraction = fraction;
            _detail = detail;
            if (_pending || _closed) return;
            _pending = true;
        }
        _dispatcher.BeginInvoke(() =>
        {
            int c; double? f; string? d;
            lock (_gate) { c = _completed; f = _fraction; d = _detail; _pending = false; if (_closed) return; }
            try { _window.Update(c, f, d); }
            catch (Exception ex) { Log.Error("Progress update", ex); }
        });
    }

    public void Finish(string message)
    {
        lock (_gate) { if (_closed) return; _closed = true; }
        _dispatcher.BeginInvoke(() =>
        {
            try { _window.CloseCard(); } catch (Exception ex) { Log.Error("Progress close", ex); }
            try { _toasts.Show(message, _near); } catch (Exception ex) { Log.Error("Progress toast", ex); }
        });
    }

    public void Dispose()
    {
        lock (_gate) { if (_closed) return; _closed = true; }
        _dispatcher.BeginInvoke(() =>
        {
            try { _window.CloseCard(); } catch (Exception ex) { Log.Error("Progress close", ex); }
        });
    }
}

internal sealed class ProgressWindow : Window
{
    const double Pad = GlassCard.Pad;
    const double CardW = 340;
    const double CardH = 82;
    const double BarWidth = CardW - 2 - 32;

    readonly int _total;
    readonly CancellationTokenSource _cancel;
    readonly TextBlock _title = new();
    readonly TextBlock _detail = new();
    readonly Border _fill = new();
    readonly Grid _host = new();
    readonly TranslateTransform _rise = new(0, 6);
    readonly Border _cancelBack = new();
    readonly TextBlock _cancelGlyph = new();
    IntPtr _hwnd;
    bool _cancelled;
    bool _closed;

    public ProgressWindow(string title, int total, CancellationTokenSource cancel)
    {
        _total = total;
        _cancel = cancel;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = CardW + 2 * Pad;
        Height = CardH + 2 * Pad;
        FontFamily = Theme.TextFont;
        Focusable = false;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        _title.Text = title;
        _detail.Text = $"0 of {total} saved";

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
    }

    FrameworkElement BuildContent()
    {
        var grid = new Grid { Width = CardW - 2, Height = CardH - 2 };

        _cancelBack.Width = _cancelBack.Height = 26;
        _cancelBack.CornerRadius = new CornerRadius(13);
        _cancelBack.Background = Theme.White(0.12);
        _cancelGlyph.Text = "";
        _cancelGlyph.FontFamily = Theme.IconFont;
        _cancelGlyph.FontSize = 10;
        _cancelGlyph.Foreground = Theme.White(0.72);
        _cancelGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _cancelGlyph.VerticalAlignment = VerticalAlignment.Center;
        var cancel = new Grid
        {
            Width = 26, Height = 26, Margin = new Thickness(14, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Cursor = Cursors.Arrow,
        };
        cancel.Children.Add(_cancelBack);
        cancel.Children.Add(_cancelGlyph);
        cancel.MouseEnter += (_, _) => { if (!_cancelled) _cancelBack.Background = Theme.White(0.22); };
        cancel.MouseLeave += (_, _) => { if (!_cancelled) _cancelBack.Background = Theme.White(0.12); };
        cancel.MouseLeftButtonUp += (_, e) => { e.Handled = true; Cancel(); };
        grid.Children.Add(cancel);

        _title.FontFamily = Theme.Font;
        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.White(0.96);
        _title.Margin = new Thickness(52, 11, 14, 0);
        _title.VerticalAlignment = VerticalAlignment.Top;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        grid.Children.Add(_title);

        _detail.FontSize = 12.5;
        _detail.Foreground = Theme.White(0.55);
        _detail.Margin = new Thickness(52, 31, 14, 0);
        _detail.VerticalAlignment = VerticalAlignment.Top;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        grid.Children.Add(_detail);

        var track = new Border
        {
            Height = 5, Margin = new Thickness(16, 0, 16, 14), VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(2.5), Background = Theme.White(0.12), ClipToBounds = true,
        };
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.CornerRadius = new CornerRadius(2.5);
        _fill.Background = Theme.White(0.66);
        _fill.Width = 0;
        track.Child = _fill;
        grid.Children.Add(track);
        return grid;
    }

    void Cancel()
    {
        if (_cancelled) return;
        _cancelled = true;
        _cancelBack.Background = Theme.White(0.06);
        _cancelGlyph.Foreground = Theme.White(0.3);
        _detail.Text = "Cancelling…";
        try { _cancel.Cancel(); } catch (Exception ex) { Log.Error("Progress cancel", ex); }
    }

    public void ShowNear(ScreenPoint near)
    {
        double scale = 1;
        try
        {
            var (s, work) = Native.MonitorAt(near);
            scale = s;
            int winW = (int)Math.Round((CardW + 2 * Pad) * scale);
            int winH = (int)Math.Round((CardH + 2 * Pad) * scale);
            int x = near.X + (int)Math.Round((14 - Pad) * scale);
            int y = near.Y + (int)Math.Round((18 - Pad) * scale);
            (x, y) = Native.Clamp(x, y, winW, winH, work);

            _host.Opacity = 0;
            _host.RenderTransform = _rise;
            if (!Motion.Enabled) _rise.Y = 0;
            GlassCard.Fill(_host, BuildContent(), CardW, CardH, x, y, scale, hitTestable: true);
            Content = _host;

            Show();
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, winW, winH, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.BringToTop(_hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Progress placement", ex);
            if (!IsVisible) { try { Show(); } catch (Exception) { } }
        }
        Motion.Animate(_host, OpacityProperty, 1, Motion.Enabled ? 160 : 0, Motion.EaseOut, from: 0);
        Motion.Animate(_rise, TranslateTransform.YProperty, 0, 260, Motion.Spring, from: 6);
    }

    public void Update(int completed, double? fraction, string? detail)
    {
        if (_closed || _cancelled) return;
        completed = Math.Clamp(completed, 0, _total);
        _detail.Text = detail ?? $"{completed} of {_total} saved";
        double value = (completed + Math.Clamp(fraction ?? 0, 0, 1)) / _total;
        Motion.Animate(_fill, WidthProperty, Math.Clamp(value, 0, 1) * BarWidth, 180, Motion.EaseOut);
    }

    public void CloseCard()
    {
        if (_closed) return;
        _closed = true;
        try { _fill.BeginAnimation(WidthProperty, null); _host.BeginAnimation(OpacityProperty, null); Close(); }
        catch (Exception ex) { Log.Error("Progress close", ex); }
    }
}
