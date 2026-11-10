using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>The slim tab left at the screen edge while a docked shelf is hidden. Hovering it, clicking it,
/// or dragging files onto it brings the shelf back.</summary>
internal sealed class EdgeTab : Window
{
    readonly Border _face = new();
    readonly TextBlock _count = new();
    IntPtr _hwnd;

    public event Action? RevealRequested;

    public EdgeTab()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Focusable = false;
        Width = EdgeHiding.TabWidthDip;
        Height = EdgeHiding.TabHeightDip;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        _count.FontSize = 9.5;
        _count.FontWeight = FontWeights.Bold;
        _count.Foreground = Theme.White(0.62);
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        _count.IsHitTestVisible = false;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border
        {
            Width = 3, Height = 18, CornerRadius = new CornerRadius(1.5), Background = Theme.White(0.45),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5), IsHitTestVisible = false,
        });
        stack.Children.Add(_count);

        _face.Background = Theme.Vertical((Color.FromRgb(0x2C, 0x2C, 0x30), 0.96, 0), (Theme.Surface, 0.96, 1));
        _face.BorderBrush = Theme.White(0.16);
        _face.BorderThickness = new Thickness(1);
        _face.Child = stack;
        Content = _face;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        MouseEnter += (_, _) => RevealRequested?.Invoke();
        MouseLeftButtonDown += (_, e) => { e.Handled = true; RevealRequested?.Invoke(); };
        DragEnter += (_, e) =>
        {
            // The shelf slides out under the pointer and takes the drop from there.
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) RevealRequested?.Invoke();
        };
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
    }

    public void ShowTab(Box px, ShelfEdge edge, int count)
    {
        const double r = 7;
        _face.CornerRadius = edge == ShelfEdge.Left ? new CornerRadius(0, r, r, 0) : new CornerRadius(r, 0, 0, r);
        _face.BorderThickness = edge == ShelfEdge.Left ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
        _count.Text = count.ToString();
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(px.X), (int)Math.Round(px.Y), (int)Math.Round(px.W), (int)Math.Round(px.H),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Native.BringToTop(_hwnd);
        FadeTo(1, 180);
    }

    public void SetCount(int count) => _count.Text = count.ToString();

    public void HideTab()
    {
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    void FadeTo(double to, int ms)
    {
        var a = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(Motion.Enabled ? ms : 0))
        { EasingFunction = Motion.EaseOut };
        BeginAnimation(OpacityProperty, a);
    }
}
