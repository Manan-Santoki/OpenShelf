using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OpenShelf.Ui;

/// <summary>
/// Draws one <see cref="Icons"/> geometry as a stroked outline with round caps. The size is rounded to whole device pixels so the icon stays on
/// the pixel grid at 100 to 200 % scaling; the colour is the inherited text colour unless <see cref="Foreground"/> is set.
/// </summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty NameProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IconView() { SnapsToDevicePixels = true; UseLayoutRounding = true; IsHitTestVisible = false; }

    public IconView(string? icon, double size = 16, Brush? foreground = null) : this()
    {
        Icon = icon;
        Size = size;
        if (foreground is not null) Foreground = foreground;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The Lucide name, such as "crop".</summary>
    public string? Icon { get => (string?)GetValue(NameProperty); set => SetValue(NameProperty, value); }

    /// <summary>Width and height in device-independent pixels.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Line width in device-independent pixels; NaN picks the width for the size class (<see cref="Icons.StrokeFor"/>).</summary>
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    double DeviceSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Max(1, Math.Round(Size * dpi)) / dpi;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = DeviceSize();
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geometry = Icons.Find(Icon);
        if (geometry is null) return;
        var size = DeviceSize();
        var thickness = double.IsNaN(StrokeThickness) ? Icons.StrokeFor(Size) : StrokeThickness;
        var scale = size / Icons.Grid;
        var pen = new Pen(Foreground, thickness / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze) pen.Freeze();   // a brush that is being animated (the wheel labels) keeps the pen live
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    /// <summary>Draws an icon straight into a <see cref="DrawingContext"/> at <paramref name="x"/>, <paramref name="y"/>.</summary>
    public static void Draw(DrawingContext dc, string icon, Brush brush, double x, double y, double size, double thickness = double.NaN)
    {
        var geometry = Icons.Find(icon);
        if (geometry is null) return;
        var scale = size / Icons.Grid;
        var pen = new Pen(brush, (double.IsNaN(thickness) ? Icons.StrokeFor(size) : thickness) / scale)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
        dc.Pop();
    }
}
