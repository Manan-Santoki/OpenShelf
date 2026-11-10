using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>The "Metadata" tool: inspect an image's grouped EXIF/GPS/camera details, with a warning for location, and save clean
/// copies. Several images show a summary list instead.</summary>
public sealed class MetadataEditor : EditorWindow, IEditorDemo
{
    public static readonly Size ContentSize = new(470, 640);

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly IReadOnlyList<MetadataReader.Details> _details;
    private readonly int _identifiable;
    private readonly SwitchRow _keepProfile = new("Keep colour profile", true, "Without it the picture is converted to sRGB first, so it looks the same");
    private readonly TextBlock _explanation = ToolKit.Text("", 11, "Text3", wrap: true);
    private Button? _copy;

    public MetadataEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Metadata", ContentSize)
    {
        _images = images;
        _details = images.Select(i =>
        {
            try { return MetadataReader.Load(i.Path); }
            catch (Exception) { return new MetadataReader.Details(); }
        }).ToList();
        _identifiable = images.Count(i => MetadataStripper.HasIdentifyingMetadata(i.Path));

        CanvasWell = false;
        SetCanvasPadding(0);
        Canvas = images.Count == 1 ? Single(images[0], _details[0]) : Batch(images);

        _keepProfile.Toggled += () => { };
        _explanation.Text = Explanation();
        _explanation.Margin = new Thickness(0, 3, 0, 0);
        var controls = new StackPanel();
        controls.Children.Add(_keepProfile);
        controls.Children.Add(_explanation);
        Controls = controls;
        Refresh();
    }

    private string Explanation()
    {
        if (_identifiable == 0)
            return _images.Count == 1 ? "No camera, date, location or other details to remove." : "None of these have camera, date, location or other details to remove.";
        var clean = _images.Count - _identifiable;
        if (clean == 0) return "Saves a copy without camera, date, location and other details.";
        return $"Saves clean copies of {_identifiable} of {_images.Count}; {clean} {(clean == 1 ? "is" : "are")} already clean.";
    }

    private UIElement Single(EditableImage image, MetadataReader.Details details)
    {
        var sections = MetadataReader.Sections(details);
        var hasLocation = MetadataReader.HasLocation(details);

        _copy = ToolKit.Button("Copy All", (_, _) =>
        {
            try
            {
                Clipboard.SetText(MetadataReader.PlainText(sections, image.Name));
                _copy!.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        _copy.MinHeight = 26;
        _copy.VerticalAlignment = VerticalAlignment.Center;
        _copy.FontSize = 12;
        _copy.IsEnabled = sections.Count > 0;

        var subtitle = new StackPanel { Orientation = Orientation.Horizontal };
        subtitle.Children.Add(ToolKit.Text($"{image.Width} × {image.Height}", 11, "Text3"));
        if (hasLocation)
        {
            var chip = Chip("Has location", "", "Orange");
            chip.Margin = new Thickness(8, 0, 0, 0);
            subtitle.Children.Add(chip);
        }
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(ToolKit.Text(image.Name, 13, "Text", FontWeights.SemiBold));
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_copy, 2);
        head.Children.Add(EditorParts.Thumb(image.Preview, 52));
        head.Children.Add(text);
        head.Children.Add(_copy);

        var body = new StackPanel();
        if (hasLocation)
        {
            var warning = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xA0, 0x30)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = ToolKit.Text("This photo records where it was taken. Anyone you send it to can see that place.", 12, "Text", wrap: true),
            };
            body.Children.Add(warning);
        }
        if (sections.Count == 0)
        {
            var none = ToolKit.Text("No metadata found.", 12, "Text3");
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.Margin = new Thickness(0, 40, 0, 0);
            body.Children.Add(none);
        }
        foreach (var section in sections) body.Children.Add(Section(section));

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 0, 4, 0) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        layout.Children.Add(head);
        layout.Children.Add(scroll);
        return layout;
    }

    private static UIElement Section(MetadataSection section)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = ToolKit.Text(section.Title.ToUpperInvariant(), 10, "Text3", FontWeights.SemiBold);
        title.Margin = new Thickness(2, 0, 0, 5);
        panel.Children.Add(title);
        var rows = new StackPanel();
        for (var i = 0; i < section.Fields.Count; i++)
        {
            var field = section.Fields[i];
            var row = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = ToolKit.Text(field.Label, 12, "Text2");
            var value = ToolKit.Text(field.Value, 12, "Text", wrap: true);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(value, 1);
            row.Children.Add(label);
            row.Children.Add(value);
            if (i > 0) rows.Children.Add(new Border { Height = 1, Background = ToolKit.Brush("Hairline") });
            rows.Children.Add(row);
        }
        panel.Children.Add(ToolKit.Card(rows, 0));
        return panel;
    }

    private UIElement Batch(IReadOnlyList<EditableImage> images)
    {
        var withLocation = _details.Count(MetadataReader.HasLocation);
        var title = ToolKit.Text($"{images.Count} images · {(withLocation == 0 ? "none have" : withLocation == 1 ? "1 has" : $"{withLocation} have")} location data", 13, "Text", FontWeights.SemiBold);
        var list = new StackPanel();
        for (var i = 0; i < images.Count; i++)
        {
            var row = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var thumb = EditorParts.Thumb(images[i].Thumbnail, 28);
            var name = ToolKit.Text(images[i].Name, 12);
            name.Margin = new Thickness(10, 0, 8, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (MetadataReader.HasCameraInfo(_details[i])) chips.Children.Add(Chip("Camera", "", "Text3"));
            if (MetadataReader.HasLocation(_details[i]))
            {
                var chip = Chip("Has location", "", "Orange");
                chip.Margin = new Thickness(6, 0, 0, 0);
                chips.Children.Add(chip);
            }
            Grid.SetColumn(chips, 2);
            row.Children.Add(thumb);
            row.Children.Add(name);
            row.Children.Add(chips);
            if (i > 0) list.Children.Add(new Border { Height = 1, Background = ToolKit.Brush("Hairline") });
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = ToolKit.Card(list, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        layout.Children.Add(title);
        layout.Children.Add(scroll);
        return layout;
    }

    private static Border Chip(string text, string glyph, string brush)
    {
        var tint = brush == "Orange" ? Color.FromRgb(0xFF, 0xA0, 0x30) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = ToolKit.Glyph(glyph, 9, "Text");
        icon.Foreground = new SolidColorBrush(tint);
        icon.Margin = new Thickness(0, 0, 4, 0);
        var label = ToolKit.Text(text, 10, "Text", FontWeights.SemiBold);
        label.Foreground = new SolidColorBrush(tint);
        content.Children.Add(icon);
        content.Children.Add(label);
        return new Border
        {
            Child = content,
            Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    protected override string PrimaryTitle => "Remove Metadata";
    protected override bool CanApply => _identifiable > 0;

    void IEditorDemo.ApplyDemoState(string state) { }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        var keep = false;
        Dispatcher.Invoke(() => keep = _keepProfile.IsChecked);
        return MetadataStripper.Run(_images.Select(i => i.Path).ToList(), keep, progress, cancel);
    }
}
