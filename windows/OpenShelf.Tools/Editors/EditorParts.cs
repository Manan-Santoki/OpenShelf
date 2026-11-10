using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Editors;

/// <summary>Small pieces several editors share.</summary>
internal static class EditorParts
{
    public static IReadOnlyList<ImageSource?> Thumbnails(IReadOnlyList<EditableImage> images) =>
        images.Select(i => (ImageSource?)i.Thumbnail).ToList();

    public static IReadOnlyList<string> Names(IReadOnlyList<EditableImage> images) => images.Select(i => i.Name).ToList();

    public static FilmstripView Filmstrip(IReadOnlyList<EditableImage> images, bool sameForAll) =>
        new(Thumbnails(images), Names(images), sameForAll);

    /// <summary>A rounded picture, filled to a square.</summary>
    public static Border Thumb(ImageSource source, double size = 52, int? badge = null)
    {
        var picture = new Image { Source = source, Stretch = Stretch.UniformToFill, Width = size, Height = size };
        var clip = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = picture };
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(clip);
        if (badge is { } count)
        {
            var label = ToolKit.Text(count.ToString(), 10, "Text", FontWeights.Bold);
            label.Foreground = Brushes.White;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var pill = new Border
            {
                Child = label,
                Background = ToolKit.Brush("Accent"),
                CornerRadius = new CornerRadius(9),
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(5, 1, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -5, -5),
            };
            grid.Children.Add(pill);
        }
        return new Border { Child = grid, Width = size, Height = size };
    }

    /// <summary>Thumbnail, name and a second line: the head of Resize, Compress and Metadata.</summary>
    public static Grid Header(ImageSource picture, string title, string subtitle, int? badge = null, UIElement? trailing = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var thumb = Thumb(picture, 52, badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(ToolKit.Text(title, 13, "Text", FontWeights.SemiBold));
        text.Children.Add(ToolKit.Text(subtitle, 11, "Text3"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(thumb);
        grid.Children.Add(text);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>An undo/redo pair of icon buttons.</summary>
    public static (StackPanel Panel, Button Undo, Button Redo) UndoRedo(Action undo, Action redo)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var u = ToolKit.IconButton("undo-2", "Undo (Ctrl+Z)", (_, _) => undo(), 28);
        var r = ToolKit.IconButton("redo-2", "Redo (Ctrl+Shift+Z)", (_, _) => redo(), 28);
        panel.Children.Add(u);
        panel.Children.Add(r);
        return (panel, u, r);
    }
}
