using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>One page in the Pages grid: its picture (turned by the page's rotation), the page number below, and a ring when selected.</summary>
internal sealed class PageCell : Border
{
    public const double BoxSize = 100;

    private readonly Border _frame = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Padding = new Thickness(6), BorderBrush = Brushes.Transparent };
    private readonly Grid _box = new() { Width = BoxSize, Height = BoxSize };
    private readonly Border _page = new() { BorderBrush = ToolKit.Brush("HairlineStrong"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly RotateTransform _rotate = new();
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _number = ToolKit.Text("", 11, "Text2", FontWeights.Medium);

    public PageCell(Guid id, int sourceIndex)
    {
        Id = id;
        SourceIndex = sourceIndex;
        Margin = new Thickness(6);
        Cursor = System.Windows.Input.Cursors.Hand;
        Background = Brushes.Transparent;
        Focusable = false;

        _page.Child = _image;
        _page.RenderTransform = _rotate;
        _page.RenderTransformOrigin = new Point(0.5, 0.5);
        _page.Background = Brushes.White;
        _page.Visibility = Visibility.Collapsed;
        var placeholder = new Border { Background = ToolKit.Brush("Fill"), CornerRadius = new CornerRadius(4), Width = BoxSize * 0.72, Height = BoxSize * 0.92, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        placeholder.Tag = "placeholder";
        _box.Children.Add(placeholder);
        _box.Children.Add(_page);
        _frame.Child = _box;
        _pill.Child = _number;
        var stack = new StackPanel();
        stack.Children.Add(_frame);
        stack.Children.Add(_pill);
        Child = stack;
    }

    public Guid Id { get; }
    public int SourceIndex { get; }

    public void SetPicture(ImageSource? picture)
    {
        if (picture is null) return;
        _image.Source = picture;
        // The picture is scaled to fit the square box, whatever its shape.
        var scale = BoxSize / Math.Max(picture.Width, picture.Height);
        _image.Width = picture.Width * scale;
        _image.Height = picture.Height * scale;
        _page.Visibility = Visibility.Visible;
        _box.Children[0].Visibility = Visibility.Collapsed;
    }

    public void Update(int number, int rotation, bool selected, bool dragged)
    {
        _number.Text = number.ToString();
        _rotate.Angle = rotation;
        _frame.BorderBrush = selected ? ToolKit.Brush("Accent") : Brushes.Transparent;
        _frame.Background = selected ? new SolidColorBrush(Color.FromArgb(0x2E, 0x3D, 0x7B, 0xFD)) : Brushes.Transparent;
        _pill.Background = selected ? ToolKit.Brush("Accent") : Brushes.Transparent;
        _number.Foreground = selected ? Brushes.White : ToolKit.Brush("Text2");
        Opacity = dragged ? 0.4 : 1;
        ToolKit.Name(this, $"Page {number}" + (rotation == 0 ? "" : $", rotated {rotation} degrees"));
    }
}
