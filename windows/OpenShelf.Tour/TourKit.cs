using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenShelf.Ui;

namespace OpenShelf.Tour;

/// <summary>The tour window's palette and small building blocks, drawn from <see cref="Theme"/> and Settings' look.</summary>
internal static class TourKit
{
    public static readonly Brush Window = B(0x1F, 0x1F, 0x22);
    public static readonly Brush Sidebar = Theme.Brush(Theme.Surface);
    public static readonly Brush Card = Theme.Brush(Theme.SurfaceRaised);
    public static readonly Brush Hairline = Theme.White(0.11);
    public static readonly Brush Text = B(0xF4, 0xF4, 0xF6);
    public static readonly Brush Text2 = Theme.White(0.69);
    public static readonly Brush Text3 = Theme.White(0.5);
    public static readonly Brush Fill = Theme.White(0.08);
    public static readonly Brush FillHover = Theme.White(0.14);
    public static readonly Brush FillPressed = Theme.White(0.2);
    public static readonly Brush Accent = Theme.Brush(Theme.Accent);
    public static readonly Brush AccentSoft = Theme.Brush(Theme.Accent, 0.16);
    public static readonly Brush AccentHover = B(0x5A, 0x90, 0xFE);
    public static readonly Brush AccentPressed = B(0x2F, 0x68, 0xE0);
    public static readonly Brush Success = B(0x6C, 0xD4, 0x9A);
    public static readonly Brush SuccessSoft = Theme.Brush(Color.FromRgb(0x6C, 0xD4, 0x9A), 0.13);
    public static readonly Brush Danger = B(0xFF, 0x7B, 0x7B);
    public static readonly Brush Keycap = B(0x3C, 0x3C, 0x42);
    public static readonly Brush KeycapBorder = Theme.White(0.33);
    public static readonly Brush KeycapEdge = Theme.Black(0.45);

    private static Brush B(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    public static TextBlock Text_(string text, double size = 13, Brush? brush = null, FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? Text,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    /// <summary>Text where words between ** ** are bold and brighter.</summary>
    public static TextBlock Rich(string text, double size = 13, Brush? brush = null, Brush? strong = null, FontWeight? weight = null)
    {
        var block = new TextBlock { FontSize = size, Foreground = brush ?? Text, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new System.Windows.Documents.Run(parts[i]);
            if (i % 2 == 1) { run.FontWeight = FontWeights.SemiBold; run.Foreground = strong ?? Text; }
            block.Inlines.Add(run);
        }
        return block;
    }

    /// <summary>A Lucide icon. <paramref name="size"/> is the size the old font glyph had; outline icons need a little more room to read.</summary>
    public static IconView Glyph(string icon, double size, Brush? brush = null) =>
        new(icon, size switch { <= 10 => 12, <= 13 => 14, <= 14 => 16, _ => size }, brush ?? Text2);

    public static void Name(DependencyObject element, string name) => AutomationProperties.SetName(element, name);

    /// <summary>A key on the keyboard.</summary>
    public static Border Keycap_(string label)
    {
        var cap = new Border
        {
            Background = Keycap,
            BorderBrush = KeycapBorder,
            BorderThickness = new Thickness(1, 1, 1, 2.5),
            CornerRadius = new CornerRadius(6),
            MinWidth = 30,
            Height = 27,
            Padding = new Thickness(9, 0, 9, 0),
            Child = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
        };
        Name(cap, label);
        return cap;
    }

    public static StackPanel Keycaps(IEnumerable<string> labels)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var first = true;
        foreach (var label in labels)
        {
            var cap = Keycap_(label);
            if (!first)
            {
                strip.Children.Add(new TextBlock { Text = "+", Foreground = Text3, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
            }
            first = false;
            strip.Children.Add(cap);
        }
        return strip;
    }

    /// <summary>Fades and slides a freshly built page in, unless animations are off.</summary>
    public static void SlideIn(FrameworkElement page)
    {
        var shift = new TranslateTransform(24, 0);
        page.RenderTransform = shift;
        page.Opacity = 0;
        Motion.Animate(page, UIElement.OpacityProperty, 1, 220, from: 0);
        Motion.Animate(shift, TranslateTransform.XProperty, 0, 320, Motion.EaseOut, from: 24);
    }

    /// <summary>A quick pop with a little overshoot, to celebrate.</summary>
    public static void Pop(FrameworkElement element)
    {
        var scale = new ScaleTransform(0.6, 0.6);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        Motion.Animate(scale, ScaleTransform.ScaleXProperty, 1, 420, Motion.Spring, from: 0.6);
        Motion.Animate(scale, ScaleTransform.ScaleYProperty, 1, 420, Motion.Spring, from: 0.6);
    }
}

internal enum ButtonStyle { Primary, Secondary, Link }

/// <summary>A button drawn to match the window: rounded, hover and press states, keyboard focus ring.</summary>
internal sealed class TourButton : Border
{
    private readonly ButtonStyle _style;
    private readonly TextBlock _label;
    private bool _over, _down;

    public event Action? Click;

    public TourButton(string text, ButtonStyle style = ButtonStyle.Secondary, string? tip = null)
    {
        _style = style;
        Focusable = true;
        Cursor = Cursors.Hand;
        CornerRadius = new CornerRadius(7);
        BorderThickness = new Thickness(1.5);
        Height = style == ButtonStyle.Link ? 24 : 32;
        Padding = new Thickness(style == ButtonStyle.Link ? 4 : 16, 0, style == ButtonStyle.Link ? 4 : 16, 0);
        _label = new TextBlock
        {
            Text = text,
            FontSize = style == ButtonStyle.Link ? 12 : 13,
            FontWeight = style == ButtonStyle.Primary ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Child = _label;
        if (tip is not null) ToolTip = tip;
        TourKit.Name(this, text);
        AutomationProperties.SetItemType(this, "button");
        Paint();
        MouseEnter += (_, _) => { _over = true; Paint(); };
        MouseLeave += (_, _) => { _over = false; _down = false; Paint(); };
        MouseLeftButtonDown += (_, e) => { _down = true; Focus(); CaptureMouse(); Paint(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            var hit = _down && IsMouseOver;
            _down = false; ReleaseMouseCapture(); Paint();
            if (hit) Click?.Invoke();
            e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Click?.Invoke(); e.Handled = true; } };
        GotKeyboardFocus += (_, _) => Paint();
        LostKeyboardFocus += (_, _) => Paint();
    }

    public string Text { get => _label.Text; set { _label.Text = value; TourKit.Name(this, value); } }

    public void Invoke() => Click?.Invoke();

    private void Paint()
    {
        var focused = IsKeyboardFocused;
        switch (_style)
        {
            case ButtonStyle.Primary:
                Background = _down ? TourKit.AccentPressed : _over ? TourKit.AccentHover : TourKit.Accent;
                _label.Foreground = Brushes.White;
                break;
            case ButtonStyle.Link:
                Background = _over ? TourKit.Fill : Brushes.Transparent;
                _label.Foreground = _over ? TourKit.Text : TourKit.Text2;
                break;
            default:
                Background = _down ? TourKit.FillPressed : _over ? TourKit.FillHover : TourKit.Fill;
                _label.Foreground = TourKit.Text;
                break;
        }
        BorderBrush = focused ? TourKit.Accent : Brushes.Transparent;
    }
}

/// <summary>A small on/off switch with a label, in the style of Settings.</summary>
internal sealed class TourSwitch : Border
{
    private readonly Border _thumb = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
    private readonly Border _track = new() { Width = 40, Height = 22, CornerRadius = new CornerRadius(11) };
    private readonly TranslateTransform _shift = new();
    private bool _on;

    public event Action<bool>? Toggled;

    public TourSwitch(string label)
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        _thumb.RenderTransform = _shift;
        _track.Child = _thumb;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_track);
        var text = TourKit.Text_(label, 13);
        text.Margin = new Thickness(10, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Child = row;
        TourKit.Name(this, label);
        MouseLeftButtonUp += (_, e) => { Flip(); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Flip(); e.Handled = true; } };
        Paint(animate: false);
    }

    public bool IsOn
    {
        get => _on;
        set { if (_on == value) return; _on = value; Paint(animate: IsLoaded); }
    }

    private void Flip() { _on = !_on; Paint(animate: true); Toggled?.Invoke(_on); }

    private void Paint(bool animate)
    {
        _track.Background = _on ? TourKit.Accent : TourKit.FillPressed;
        var target = _on ? 18.0 : 0.0;
        if (animate) Motion.Animate(_shift, TranslateTransform.XProperty, target, 160, Motion.Snappy);
        else { _shift.BeginAnimation(TranslateTransform.XProperty, null); _shift.X = target; }
        AutomationProperties.SetHelpText(this, _on ? "On" : "Off");
    }
}
