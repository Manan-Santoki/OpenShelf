using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenShelf.Tools;

/// <summary>
/// An image fitted into a canvas area, with conversions between view points and image pixels. Subclasses draw overlays
/// (<see cref="DrawOverlay"/>) and handle the mouse. <see cref="Source"/> may be a downscaled preview of an image whose full size
/// is <see cref="ImageSize"/>; all image coordinates are in full-size pixels.
/// </summary>
public class ImageSurface : FrameworkElement
{
    private BitmapSource? _source;
    private Size _imageSize;
    private bool _checkerboard;

    public ImageSurface()
    {
        SnapsToDevicePixels = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>Space kept free around the picture, in view units.</summary>
    public double Inset { get; set; } = 8;

    public BitmapSource? Source
    {
        get => _source;
        set { _source = value; InvalidateVisual(); }
    }

    /// <summary>Full-size pixel dimensions of the image shown.</summary>
    public Size ImageSize
    {
        get => _imageSize;
        set { _imageSize = value; InvalidateVisual(); }
    }

    /// <summary>Draws a chequerboard behind the picture, to show transparency.</summary>
    public bool Checkerboard
    {
        get => _checkerboard;
        set { _checkerboard = value; InvalidateVisual(); }
    }

    /// <summary>View pixels per image pixel.</summary>
    public double Scale
    {
        get
        {
            if (_imageSize.Width <= 0 || _imageSize.Height <= 0) return 1;
            var room = Room;
            return Math.Max(0.0001, Math.Min(room.Width / _imageSize.Width, room.Height / _imageSize.Height));
        }
    }

    private Size Room => new(Math.Max(1, ActualWidth - 2 * Inset), Math.Max(1, ActualHeight - 2 * Inset));

    /// <summary>The picture's rectangle in view coordinates.</summary>
    public Rect ImageRect
    {
        get
        {
            var scale = Scale;
            var w = _imageSize.Width * scale;
            var h = _imageSize.Height * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }

    /// <summary>Converts a view point to image pixels, kept inside the image when <paramref name="clamp"/> is set.</summary>
    public Point ToImage(Point view, bool clamp = true)
    {
        var rect = ImageRect;
        var scale = Scale;
        var p = new Point((view.X - rect.X) / scale, (view.Y - rect.Y) / scale);
        return clamp ? new Point(Math.Clamp(p.X, 0, _imageSize.Width), Math.Clamp(p.Y, 0, _imageSize.Height)) : p;
    }

    public Point ToView(Point image)
    {
        var rect = ImageRect;
        var scale = Scale;
        return new Point(rect.X + image.X * scale, rect.Y + image.Y * scale);
    }

    public Rect ToView(Rect image)
    {
        var topLeft = ToView(image.TopLeft);
        var scale = Scale;
        return new Rect(topLeft.X, topLeft.Y, image.Width * scale, image.Height * scale);
    }

    /// <summary>A distance of <paramref name="viewLength"/> on screen, expressed in image pixels.</summary>
    public double ImageLength(double viewLength) => viewLength / Math.Max(Scale, 0.0001);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent fill makes the whole area hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_source is null || _imageSize.Width <= 0) return;
        var rect = ImageRect;
        if (_checkerboard) DrawChecker(dc, rect);
        dc.DrawImage(_source, rect);
        DrawOverlay(dc, rect);
    }

    /// <summary>Draws over the picture, in view coordinates.</summary>
    protected virtual void DrawOverlay(DrawingContext dc, Rect imageRect) { }

    private static readonly Brush ChecksLight = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
    private static readonly Brush ChecksDark = Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6)));

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    private static void DrawChecker(DrawingContext dc, Rect rect)
    {
        const double cell = 8;
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawRectangle(ChecksLight, null, rect);
        for (var y = 0; y * cell < rect.Height; y++)
        for (var x = (y % 2); x * cell < rect.Width; x += 2)
            dc.DrawRectangle(ChecksDark, null, new Rect(rect.X + x * cell, rect.Y + y * cell, cell, cell));
        dc.Pop();
    }
}
