using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Ui;

[assembly: InternalsVisibleTo("OpenShelf.Settings.Tests")]
[assembly: InternalsVisibleTo("OpenShelf.Settings.Demo")]

namespace OpenShelf.Settings;

/// <summary>Reusable building blocks of the settings window, all styled from Styles.xaml.</summary>
internal static class Kit
{
    private static ResourceDictionary? _dictionary;

    /// <summary>The window's styles and palette; merged into the window's resources.</summary>
    public static ResourceDictionary Dictionary =>
        _dictionary ??= new ResourceDictionary { Source = new Uri("/OpenShelf.Settings;component/Styles.xaml", UriKind.Relative) };

    public static Brush Brush(string key) => (Brush)Dictionary[key];
    public static Style Style(string key) => (Style)Dictionary[key];

    public static TextBlock Text(string text, double size = 13, string brush = "Text", FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(brush),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
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

    public static void Name(DependencyObject element, string name) => AutomationProperties.SetName(element, name);

    /// <summary>A heading with its caption line, then the content.</summary>
    public static StackPanel Section(string heading, string? caption, UIElement content)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(heading, 15, weight: FontWeights.SemiBold));
        if (caption is not null)
        {
            var c = Text(caption, 12, "Text3", wrap: true);
            c.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(c);
        }
        if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>Small print under a card.</summary>
    public static TextBlock Footnote(string text) =>
        new() { Text = text, FontSize = 12, Foreground = Brush("Text3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0) };

    public static Border Keycap(string text, bool dim = false)
    {
        var label = Text(text, 12, dim ? "Text3" : "Text", FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = label,
            Background = Brush("Keycap"),
            BorderBrush = Brush("KeycapBorder"),
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 10, 0),
            MinWidth = 36,
            Height = 28,
            SnapsToDevicePixels = true,
        };
    }

    public static StackPanel Keycaps(IEnumerable<string> names)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var first = true;
        foreach (var name in names)
        {
            var cap = Keycap(name);
            if (!first) cap.Margin = new Thickness(6, 0, 0, 0);
            first = false;
            strip.Children.Add(cap);
        }
        return strip;
    }

    public static Button Button(string text, RoutedEventHandler click, string style = "Btn", string? name = null)
    {
        var button = new Button { Content = text, Style = Style(style) };
        button.Click += click;
        Name(button, name ?? text);
        return button;
    }

    public static Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var button = new Button { Content = glyph, Style = Style("IconBtn"), ToolTip = tip };
        button.Click += click;
        Name(button, tip);
        return button;
    }

    public static CheckBox Switch(string name)
    {
        var toggle = new CheckBox { Style = Style("Switch"), VerticalAlignment = VerticalAlignment.Center };
        Name(toggle, name);
        return toggle;
    }
}

/// <summary>A label with an optional caption on the left and a control on the right.</summary>
internal sealed class SettingRow : Border
{
    private readonly TextBlock _title;
    private readonly TextBlock? _caption;
    private readonly TextBlock _note = Kit.Text("", 12, "Danger", wrap: true);
    private readonly ContentPresenter _trailing = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _grid = new();

    public SettingRow(string title, string? caption = null, UIElement? trailing = null, UIElement? leading = null)
    {
        Padding = new Thickness(16, 10, 16, 10);
        MinHeight = 52;
        Background = Brushes.Transparent;
        Child = _grid;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (leading is not null)
        {
            if (leading is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 12, 0); fe.VerticalAlignment = VerticalAlignment.Center; }
            Grid.SetColumn(leading, 0);
            _grid.Children.Add(leading);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title = Kit.Text(title, 13);
        text.Children.Add(_title);
        if (caption is not null)
        {
            _caption = Kit.Text(caption, 12, "Text3", wrap: true);
            _caption.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(_caption);
        }
        Grid.SetColumn(text, 1);
        _grid.Children.Add(text);

        _trailing.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(_trailing, 2);
        _grid.Children.Add(_trailing);
        if (trailing is not null) _trailing.Content = trailing;

        _note.Visibility = Visibility.Collapsed;
        _note.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_note, 1);
        Grid.SetColumnSpan(_note, 3);
        _grid.Children.Add(_note);
    }

    public string Title { get => _title.Text; set => _title.Text = value; }
    public TextBlock? Caption => _caption;

    public UIElement? Trailing { get => (UIElement?)_trailing.Content; set => _trailing.Content = value; }

    /// <summary>A line of text under the row (an inline error); null hides it.</summary>
    public void SetNote(string? text, string brush = "Danger")
    {
        _note.Text = text ?? "";
        _note.Foreground = Kit.Brush(brush);
        _note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>A rounded container of rows separated by hairlines.</summary>
internal sealed class Card : Border
{
    private readonly StackPanel _stack = new();

    public Card(params Border[] rows)
    {
        Background = Kit.Brush("Card");
        BorderBrush = Kit.Brush("Hairline");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        SnapsToDevicePixels = true;
        Child = _stack;
        SetRows(rows);
    }

    /// <summary>Rows are Borders so each can carry its own hairline above it (and vanish with it when collapsed).</summary>
    public void SetRows(IEnumerable<Border> rows)
    {
        _stack.Children.Clear();
        foreach (var row in rows)
        {
            var first = _stack.Children.Count == 0;
            row.BorderBrush = Kit.Brush("Hairline");
            row.BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0);
            _stack.Children.Add(row);
        }
    }
}

/// <summary>Pick one of a few options, shown side by side. Arrow keys move the choice.</summary>
internal sealed class Segmented<T> : Border where T : notnull
{
    private readonly List<(T Value, RadioButton Button)> _items = [];
    private bool _silent;

    public Segmented(IReadOnlyList<(string Title, T Value)> options, Action<T> picked, string name)
    {
        Background = Kit.Brush("Well");
        BorderBrush = Kit.Brush("Hairline");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
        var group = Guid.NewGuid().ToString();
        foreach (var (title, value) in options)
        {
            var button = new RadioButton { Content = title, Style = Kit.Style("Seg"), GroupName = group };
            Kit.Name(button, $"{name}: {title}");
            button.Checked += (_, _) => { if (!_silent) picked(value); };
            _items.Add((value, button));
            panel.Children.Add(button);
        }
        Child = panel;
        Kit.Name(this, name);
    }

    public T Selected
    {
        set
        {
            _silent = true;
            foreach (var (v, b) in _items) b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            _silent = false;
        }
    }
}

/// <summary>A themed drop-down list.</summary>
internal sealed class Dropdown<T> : ComboBox
{
    private readonly List<T> _values = [];
    private bool _silent;
    private readonly Action<T> _picked;

    public Dropdown(IReadOnlyList<(string Title, T Value)> options, Action<T> picked, string name, double width = 180)
    {
        _picked = picked;
        Style = (Style)Kit.Dictionary[typeof(ComboBox)];   // implicit styles don't reach subclasses
        Width = width;
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var (title, value) in options)
        {
            Items.Add(new ComboBoxItem { Content = title });
            _values.Add(value);
        }
        Kit.Name(this, name);
        SelectionChanged += (_, _) =>
        {
            if (!_silent && SelectedIndex >= 0) _picked(_values[SelectedIndex]);
        };
    }

    /// <summary>Selects without raising the picked callback; adds the value when it isn't offered.</summary>
    public T Selected
    {
        set
        {
            _silent = true;
            var index = _values.FindIndex(v => EqualityComparer<T>.Default.Equals(v, value));
            if (index < 0)
            {
                Items.Add(new ComboBoxItem { Content = value?.ToString() ?? "" });
                _values.Add(value);
                index = _values.Count - 1;
            }
            SelectedIndex = index;
            _silent = false;
        }
    }
}

/// <summary>Bitrate slider on a logarithmic scale (0.1 to 200 Mb/s) with its value beside it.</summary>
internal sealed class MbpsSlider : StackPanel
{
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 1, Width = 200, SmallChange = 0.004, LargeChange = 0.05, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _value = Kit.Text("", 13, "Text");
    private bool _silent;

    public MbpsSlider(Action<double> picked)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _value.Width = 68;
        _value.TextAlignment = TextAlignment.Right;
        _value.Margin = new Thickness(12, 0, 0, 0);
        _value.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_slider);
        Children.Add(_value);
        Kit.Name(_slider, "Bitrate in megabits per second");
        _slider.ValueChanged += (_, e) =>
        {
            if (_silent) return;
            var mbps = Round(FromPosition(e.NewValue));
            _value.Text = Label(mbps);
            picked(mbps);
        };
    }

    public double Mbps
    {
        set
        {
            _silent = true;
            _slider.Value = ToPosition(value);
            _value.Text = Label(value);
            _silent = false;
        }
    }

    private static readonly double Span = Math.Log(VideoOutput.CustomMbpsMax / VideoOutput.CustomMbpsMin);
    internal static double ToPosition(double mbps) => Math.Log(VideoOutput.ClampedMbps(mbps) / VideoOutput.CustomMbpsMin) / Span;
    internal static double FromPosition(double p) => VideoOutput.CustomMbpsMin * Math.Exp(Math.Clamp(p, 0, 1) * Span);
    internal static double Round(double mbps) => VideoOutput.ClampedMbps(mbps < 10 ? Math.Round(mbps, 1) : Math.Round(mbps));
    internal static string Label(double mbps) => (mbps < 10 ? mbps.ToString("0.0") : mbps.ToString("0")) + " Mb/s";
}
