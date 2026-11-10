using System.Windows.Media;

namespace OpenShelf.Ui;

/// <summary>Shared look: colours, fonts and icon glyphs. One place so every surface matches.</summary>
public static class Theme
{
    public static readonly Color Accent = Color.FromRgb(0x3D, 0x7B, 0xFD);
    public static readonly Color Surface = Color.FromRgb(0x26, 0x26, 0x29);
    public static readonly Color SurfaceRaised = Color.FromRgb(0x2A, 0x2A, 0x2E);

    /// <summary>Segoe UI Variable on Windows 11, Segoe UI on Windows 10.</summary>
    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>Icon font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10. Both share code points.</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush White(double opacity) => Brush(Colors.White, opacity);
    public static SolidColorBrush Black(double opacity) => Brush(Colors.Black, opacity);

    /// <summary>Top-to-bottom gradient, frozen.</summary>
    public static LinearGradientBrush Vertical(params (Color color, double opacity, double offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
        foreach (var (color, opacity, offset) in stops)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B), offset));
        brush.Freeze();
        return brush;
    }
}
