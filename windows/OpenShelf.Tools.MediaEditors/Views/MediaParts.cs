using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Lets the demo executable put an editor into a named state for screenshots.</summary>
internal interface IMediaEditorDemo
{
    void ApplyDemoState(string state);
}

/// <summary>Glyphs the media tools use (Segoe Fluent Icons / MDL2 code points).</summary>
internal static class Glyphs
{
    public const string Trim = "";
    public const string Speed = "";
    public const string Crop = "";
    public const string Rotate = "";
    public const string Split = "";
    public const string Merge = "";
    public const string Compress = "";
    public const string Mute = "";
    public const string Film = "";
    public const string Waveform = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Warning = "";
    public const string RotateLeft = "";
    public const string RotateRight = "";
    public const string Upside = "";
    public const string Flip = "";
    public const string Remove = "";
    public const string Grip = "";
}

/// <summary>Pictures and small controls the media editors share.</summary>
internal static class MediaParts
{
    /// <summary>Decodes PNG bytes from ffmpeg into a frozen picture that any thread can hand to the UI.</summary>
    public static BitmapSource Picture(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The head of Speed, Split and Compress: an accent tile with an icon, a name and a second line.</summary>
    public static Grid IconHeader(string glyph, string title, out TextBlock subtitle)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = ToolKit.Glyph(glyph, 20, "Accent");
        var tile = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x3D, 0x7B, 0xFD)),
            Child = icon,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(ToolKit.Text(title, 13, "Text", FontWeights.SemiBold));
        subtitle = ToolKit.Text(" ", 11, "Text3");
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 1);
        grid.Children.Add(tile);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A message with an icon, centred in a canvas: why a file will not load, or that ffmpeg is needed.</summary>
    public static UIElement Notice(string message, string glyph = Glyphs.Warning)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        var icon = ToolKit.Glyph(glyph, 22, "Text3");
        icon.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(icon);
        var text = ToolKit.Text(message, 12, "Text2", wrap: true);
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = 380;
        panel.Children.Add(text);
        return panel;
    }

    /// <summary>A button in a row of choices, filled with the accent when selected (Speed's presets, Rotate's turns).</summary>
    public static Button Choice(object content, bool selected, RoutedEventHandler click, string name)
    {
        var button = new Button { Content = content, Style = ToolKit.Style(selected ? "AccentBtn" : "Btn"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += click;
        ToolKit.Name(button, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? "Selected" : "");
        return button;
    }
}

/// <summary>A "m:ss.s" field that commits on Enter or when focus leaves it.</summary>
internal sealed class TimeField : TextBox
{
    private double _seconds;
    private bool _silent;

    public TimeField(string name)
    {
        Style = ToolKit.Style("Field");
        Width = 76;
        TextAlignment = TextAlignment.Center;
        FontFeatures();
        ToolKit.Name(this, name);
        LostKeyboardFocus += (_, _) => Commit();
        GotKeyboardFocus += (_, _) => SelectAll();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        Show();
    }

    private void FontFeatures() => System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);

    public double Seconds
    {
        get => _seconds;
        set { _seconds = value; if (!IsKeyboardFocused) Show(); }
    }

    /// <summary>Raised with the time that was typed, when it reads as one.</summary>
    public event Action<double>? ValueCommitted;

    private void Show()
    {
        _silent = true;
        Text = MediaTime.Format(_seconds);
        _silent = false;
    }

    public void Commit()
    {
        if (_silent) return;
        if (MediaTime.Parse(Text) is { } parsed) ValueCommitted?.Invoke(parsed);
        Show();
    }
}

/// <summary>Export status under an editor while it writes files: a bar, "Exporting 2 of 3… 40%" and a Cancel button.</summary>
internal sealed class ExportCard : Border
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly TextBlock _text = ToolKit.Text("", 11, "Text2");
    private readonly Button _cancel;

    public ExportCard(Action cancel)
    {
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 12, 0, 0);
        _cancel = ToolKit.Button("Cancel", (_, _) => cancel(), "GhostBtn");
        _cancel.MinHeight = 26;
        _cancel.Padding = new Thickness(10, 0, 10, 0);
        _cancel.FontSize = 12;
        ToolKit.Name(_cancel, "Cancel export");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _bar.Margin = new Thickness(0, 0, 0, 5);
        left.Children.Add(_bar);
        left.Children.Add(_text);
        _cancel.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_cancel, 1);
        grid.Children.Add(left);
        grid.Children.Add(_cancel);
        Child = grid;
        System.Windows.Automation.AutomationProperties.SetName(this, "Export progress");
    }

    public double Fraction => _bar.Value;
    public string Text => _text.Text;

    public void Show(double fraction, string? detail = null)
    {
        Visibility = Visibility.Visible;
        fraction = Math.Clamp(fraction, 0, 1);
        _bar.Value = fraction;
        _text.Text = detail ?? $"Exporting… {(int)(fraction * 100)}%";
        System.Windows.Automation.AutomationProperties.SetHelpText(this, $"{(int)(fraction * 100)} percent");
    }

    public void Hide() => Visibility = Visibility.Collapsed;
}
