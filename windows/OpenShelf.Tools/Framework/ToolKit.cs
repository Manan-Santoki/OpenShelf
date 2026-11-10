using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenShelf.Ui;

namespace OpenShelf.Tools;

/// <summary>Building blocks for tool editors, all styled from ToolStyles.xaml so every editor matches the Settings window.</summary>
public static class ToolKit
{
    private static ResourceDictionary? _dictionary;

    /// <summary>The palette and control styles; <see cref="EditorWindow"/> merges it into its resources.</summary>
    public static ResourceDictionary Dictionary =>
        _dictionary ??= new ResourceDictionary { Source = new Uri("/OpenShelf.Tools;component/ToolStyles.xaml", UriKind.Relative) };

    public static Brush Brush(string key) => (Brush)Dictionary[key];
    public static Style Style(string key) => (Style)Dictionary[key];
    public static Color Color(string key) => ((SolidColorBrush)Dictionary[key]).Color;

    public static void Name(DependencyObject element, string name) => AutomationProperties.SetName(element, name);

    public static TextBlock Text(string text, double size = 13, string brush = "Text", FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(brush),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Glyph(string glyph, double size, string brush = "Text2") => new()
    {
        Text = glyph,
        FontFamily = Theme.IconFont,
        FontSize = size,
        Foreground = Brush(brush),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A rounded translucent panel, the "well" behind canvases and summaries.</summary>
    public static Border Card(UIElement child, double padding = 10, double radius = 10) => new()
    {
        Child = child,
        Background = Brush("Fill"),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(padding),
    };

    /// <summary>A leading label and a control, as under an editor's canvas.</summary>
    public static Grid Row(string label, UIElement content, double labelWidth = 78)
    {
        var grid = new Grid { MinHeight = 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Text(label, 12, "Text2");
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        Grid.SetColumn((UIElement)content, 1);
        grid.Children.Add(content);
        return grid;
    }

    public static Button Button(string text, RoutedEventHandler click, string style = "Btn")
    {
        var button = new Button { Content = text, Style = Style(style) };
        button.Click += click;
        Name(button, text);
        return button;
    }

    /// <summary>A small button showing an icon glyph.</summary>
    public static Button IconButton(string glyph, string tip, RoutedEventHandler click, double size = 28)
    {
        var button = new Button { Content = glyph, Style = Style("IconBtn"), ToolTip = tip, Width = size, Height = size };
        button.Click += click;
        Name(button, tip);
        return button;
    }

    /// <summary>A full-width preset button ("1080 px").</summary>
    public static Button Preset(string text, RoutedEventHandler click)
    {
        var button = Button(text, click);
        button.MinHeight = 28;
        button.Padding = new Thickness(6, 0, 6, 0);
        button.FontSize = 12;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    /// <summary>A status message under an editor's controls, in a row of fixed height so it appearing never moves a control.</summary>
    public static TextBlock Status(int lines = 2) => new()
    {
        FontSize = 11,
        Foreground = Brush("Text3"),
        TextWrapping = TextWrapping.Wrap,
        Height = lines * 15.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}

/// <summary>A switch with its label: "Keep colour profile".</summary>
public sealed class SwitchRow : Grid
{
    private readonly CheckBox _box = new() { Style = ToolKit.Style("Switch"), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, LayoutTransform = new ScaleTransform(0.85, 0.85) };
    private readonly TextBlock _label;

    public SwitchRow(string label, bool isChecked = false, string? tip = null)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _label = ToolKit.Text(label, 12, "Text");
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.Margin = new Thickness(8, 0, 0, 0);
        _label.MouseLeftButtonUp += (_, _) => _box.IsChecked = _box.IsChecked != true;
        SetColumn(_label, 1);
        Children.Add(_box);
        Children.Add(_label);
        _box.IsChecked = isChecked;
        _box.Checked += (_, _) => Toggled?.Invoke();
        _box.Unchecked += (_, _) => Toggled?.Invoke();
        ToolKit.Name(_box, label);
        if (tip is not null) ToolTip = tip;
    }

    public bool IsChecked
    {
        get => _box.IsChecked == true;
        set => _box.IsChecked = value;
    }

    /// <summary>Changes the state without raising <see cref="Toggled"/>.</summary>
    public void SetSilently(bool value)
    {
        if (IsChecked == value) return;
        var handler = Toggled;
        Toggled = null;
        _box.IsChecked = value;
        Toggled = handler;
    }

    public event Action? Toggled;
}

/// <summary>A slider with a label and a live value readout, e.g. "Padding ——o—— 160 px".</summary>
public sealed class LabeledSlider : Grid
{
    private readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Focusable = true };
    private readonly TextBlock _readout = ToolKit.Text("", 11, "Text2");
    private readonly string _unit;
    private readonly TextBlock _label;
    private Func<double, double> _display;
    private bool _silent;

    public LabeledSlider(string label, double minimum, double maximum, double value, string unit = "px", Func<double, double>? display = null, double labelWidth = 78)
    {
        _unit = unit;
        _display = display ?? (v => v);
        MinHeight = 28;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var text = _label = ToolKit.Text(label, 12, "Text2");
        text.VerticalAlignment = VerticalAlignment.Center;
        _readout.VerticalAlignment = VerticalAlignment.Center;
        _readout.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(_slider, 1);
        SetColumn(_readout, 2);
        Children.Add(text);
        Children.Add(_slider);
        Children.Add(_readout);
        _slider.Minimum = minimum;
        _slider.Maximum = maximum;
        _slider.SmallChange = (maximum - minimum) / 100;
        _slider.LargeChange = (maximum - minimum) / 10;
        _slider.Value = Math.Clamp(value, minimum, maximum);
        _slider.ValueChanged += (_, e) =>
        {
            UpdateReadout();
            if (!_silent) ValueChanged?.Invoke(e.NewValue);
        };
        _slider.PreviewMouseLeftButtonUp += (_, _) => DragCompleted?.Invoke();
        _slider.LostKeyboardFocus += (_, _) => DragCompleted?.Invoke();
        ToolKit.Name(_slider, label);
        UpdateReadout();
    }

    public double Value
    {
        get => _slider.Value;
        set => _slider.Value = Math.Clamp(value, _slider.Minimum, _slider.Maximum);
    }

    public double Maximum
    {
        get => _slider.Maximum;
        set { _slider.Maximum = value; UpdateReadout(); }
    }

    /// <summary>Changes how the value is shown (text size shows points, say).</summary>
    public void SetDisplay(Func<double, double> display)
    {
        _display = display;
        UpdateReadout();
    }

    /// <summary>Changes the label and range without raising <see cref="ValueChanged"/>.</summary>
    public void Configure(string label, double minimum, double maximum, double value)
    {
        _silent = true;
        try
        {
            _label.Text = label;
            _slider.Minimum = minimum;
            _slider.Maximum = maximum;
            _slider.SmallChange = (maximum - minimum) / 100;
            _slider.LargeChange = (maximum - minimum) / 10;
            _slider.Value = Math.Clamp(value, minimum, maximum);
        }
        finally { _silent = false; }
        UpdateReadout();
    }

    public void SetSilently(double value)
    {
        _silent = true;
        try { Value = value; }
        finally { _silent = false; }
        UpdateReadout();
    }

    private void UpdateReadout()
    {
        var shown = Math.Round(_display(_slider.Value), MidpointRounding.AwayFromZero);
        _readout.Text = (_unit.Length == 0 ? "" : "") + shown.ToString("0", CultureInfo.CurrentCulture) + (_unit.Length > 0 ? " " + _unit : "");
    }

    public event Action<double>? ValueChanged;
    /// <summary>The pointer was released after a drag (or the slider lost focus): a coalesced undo step ends here.</summary>
    public event Action? DragCompleted;
}

/// <summary>A segmented control: one item selected at a time.</summary>
public sealed class Segmented : Border
{
    private readonly List<RadioButton> _buttons = [];
    private readonly string _group = Guid.NewGuid().ToString("N");
    private bool _silent;
    private int _selected = -1;

    public Segmented(IReadOnlyList<string> titles, int selected = 0, double itemPadding = 10, IReadOnlyList<string>? glyphs = null)
    {
        Background = ToolKit.Brush("Well");
        BorderBrush = ToolKit.Brush("Hairline");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        var grid = new UniformGrid { Rows = 1 };
        for (var i = 0; i < titles.Count; i++)
        {
            var index = i;
            object content = titles[i];
            if (glyphs is not null)
            {
                var g = ToolKit.Glyph(glyphs[i], 14, "Text2");
                g.ClearValue(TextBlock.ForegroundProperty);
                content = g;
            }
            var button = new RadioButton { Content = content, GroupName = _group, Style = ToolKit.Style("Seg"), Margin = new Thickness(1, 0, 1, 0), ToolTip = titles[i], Padding = new Thickness(itemPadding, 0, itemPadding, 0) };
            ToolKit.Name(button, titles[i]);
            button.Checked += (_, _) =>
            {
                _selected = index;
                if (!_silent) SelectionChanged?.Invoke(index);
            };
            _buttons.Add(button);
            grid.Children.Add(button);
        }
        Child = grid;
        SetSilently(selected);
    }

    public int SelectedIndex
    {
        get => _selected;
        set { if (value >= 0 && value < _buttons.Count) _buttons[value].IsChecked = true; }
    }

    public void SetSilently(int index)
    {
        _silent = true;
        try { SelectedIndex = index; }
        finally { _silent = false; }
    }

    public event Action<int>? SelectionChanged;
}

/// <summary>A whole-number text field. Edits commit when focus leaves or Enter is pressed; letters are refused.</summary>
public sealed class NumberBox : TextBox
{
    private int? _value;
    private bool _silent;

    public NumberBox(double width = 72, int? value = null)
    {
        Style = ToolKit.Style("Field");
        Width = width;
        TextAlignment = TextAlignment.Right;
        InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
        _value = value;
        SetText();
        PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.Text) is not string text || !text.All(char.IsDigit)) e.CancelCommand();
        });
        LostKeyboardFocus += (_, _) => Commit();
        GotKeyboardFocus += (_, _) => SelectAll();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
    }

    public int? Value
    {
        get => _value;
        set { _value = value; SetText(); }
    }

    private void SetText()
    {
        _silent = true;
        Text = _value?.ToString(CultureInfo.InvariantCulture) ?? "";
        _silent = false;
    }

    /// <summary>Reads the text; raises <see cref="ValueCommitted"/> when it holds a number, and puts the accepted value back.</summary>
    public void Commit()
    {
        if (_silent) return;
        if (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            _value = parsed;
            ValueCommitted?.Invoke(parsed);
        }
        SetText();
    }

    public event Action<int>? ValueCommitted;
}

/// <summary>A round or square colour/picture swatch with a selection ring.</summary>
public sealed class SwatchButton : Button
{
    private readonly Border _ring = new() { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(9), Padding = new Thickness(2) };
    private readonly Border _face = new() { CornerRadius = new CornerRadius(7), BorderBrush = ToolKit.Brush("Hairline"), BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
    private bool _selected;

    public SwatchButton(Brush? fill, string tip, double size = 26, double radius = 7)
    {
        Style = ToolKit.Style("GhostBtn");
        Padding = new Thickness(0);
        MinHeight = 0;
        Width = size + 8;
        Height = size + 8;
        _face.Width = size;
        _face.Height = size;
        _face.CornerRadius = new CornerRadius(radius);
        _face.Background = fill;
        _ring.CornerRadius = new CornerRadius(radius + 2);
        _ring.Child = _face;
        Content = _ring;
        ToolTip = tip;
        ToolKit.Name(this, tip);
    }

    public Brush? Fill
    {
        get => _face.Background;
        set => _face.Background = value;
    }

    public UIElement? Face
    {
        set => _face.Child = value;
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            _ring.BorderBrush = value ? ToolKit.Brush("Accent") : Brushes.Transparent;
        }
    }
}

/// <summary>A button that opens a small colour chooser: red, green and blue sliders and a hex field.</summary>
public sealed class ColorWell : Grid
{
    private readonly SwatchButton _button;
    private readonly Border _swatch;
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
    private readonly LabeledSlider _r = new("Red", 0, 255, 0, "", labelWidth: 40);
    private readonly LabeledSlider _g = new("Green", 0, 255, 0, "", labelWidth: 40);
    private readonly LabeledSlider _b = new("Blue", 0, 255, 0, "", labelWidth: 40);
    private readonly TextBox _hex = new() { Width = 78, MaxLength = 7, Style = ToolKit.Style("Field") };
    private Color _color;
    private bool _syncing;

    public ColorWell(Color color, string tip = "Custom colour")
    {
        _color = color;
        _button = new SwatchButton(null, tip, 20, 10);
        // A rainbow ring marks it as "pick any colour".
        _button.Fill = new LinearGradientBrush(
            [new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.2), new GradientStop(Colors.Lime, 0.4),
             new GradientStop(Colors.Cyan, 0.6), new GradientStop(Colors.Blue, 0.8), new GradientStop(Colors.Magenta, 1)], 45);
        _swatch = new Border { Margin = new Thickness(5), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        _button.Face = _swatch;
        _button.Click += (_, _) => { LoadSliders(); _popup.IsOpen = true; };
        Children.Add(_button);

        var panel = new StackPanel { Width = 220 };
        panel.Children.Add(_r);
        panel.Children.Add(_g);
        panel.Children.Add(_b);
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        hexRow.Children.Add(ToolKit.Text("Hex", 12, "Text2"));
        _hex.Margin = new Thickness(12, 0, 0, 0);
        hexRow.Children.Add(_hex);
        panel.Children.Add(hexRow);
        _popup.Child = new Border
        {
            Child = panel,
            Padding = new Thickness(12),
            Margin = new Thickness(10, 2, 10, 14),
            CornerRadius = new CornerRadius(10),
            Background = ToolKit.Brush("Popup"),
            BorderBrush = ToolKit.Brush("HairlineStrong"),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45, Direction = 270 },
        };
        _popup.PlacementTarget = _button;
        void FromSliders(double _)
        {
            if (_syncing) return;
            Set(Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value), fromSliders: true);
        }
        _r.ValueChanged += FromSliders;
        _g.ValueChanged += FromSliders;
        _b.ValueChanged += FromSliders;
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
    }

    public Color Color
    {
        get => _color;
        set => Set(value, fromSliders: false, raise: false);
    }

    public bool IsSelected
    {
        get => _button.IsSelected;
        set => _button.IsSelected = value;
    }

    /// <summary>The user picked a colour.</summary>
    public event Action<Color>? ColorChosen;

    /// <summary>The button was pressed (to select the custom colour even without changing it).</summary>
    public event Action? Opened
    {
        add => _button.Click += (_, _) => value?.Invoke();
        remove { }
    }

    private void CommitHex()
    {
        var text = _hex.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            Set(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), fromSliders: false);
        else LoadSliders();
    }

    private void Set(Color color, bool fromSliders, bool raise = true)
    {
        _color = color;
        _swatch.Background = new SolidColorBrush(color);
        if (!fromSliders) LoadSliders();
        else _hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (raise) ColorChosen?.Invoke(color);
    }

    private void LoadSliders()
    {
        _syncing = true;
        _r.SetSilently(_color.R);
        _g.SetSilently(_color.G);
        _b.SetSilently(_color.B);
        _hex.Text = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
        _syncing = false;
    }
}
