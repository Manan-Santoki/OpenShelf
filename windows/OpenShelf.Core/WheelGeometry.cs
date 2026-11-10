namespace OpenShelf.Core;

/// <summary>
/// Layout of a wheel: a hub surrounded by equal wedges, wedge 0 centred at
/// 12 o'clock and the rest following clockwise. Port of WheelGeometry.swift.
/// Units are device-independent pixels; points use a y-DOWN space (screen/WPF).
/// </summary>
public readonly record struct WheelGeometry(int WedgeCount, double InnerRadius = WheelGeometry.DefaultInnerRadius, double OuterRadius = WheelGeometry.DefaultOuterRadius)
{
    public const double DefaultOuterRadius = 132;
    public const double DefaultInnerRadius = 50;
    /// <summary>Room around the wheel for its rim, shadow and the label above it.</summary>
    public const double Margin = 76;
    public static double PanelSide => (DefaultOuterRadius + Margin) * 2;

    /// <summary>Angular width of one wedge, in radians.</summary>
    public double Sweep => WedgeCount > 0 ? 2 * Math.PI / WedgeCount : 0;

    /// <summary>Angle of a wedge's centre, in radians clockwise from 12 o'clock.</summary>
    public double CenterAngle(int index) => Sweep * index;

    /// <summary>How wide a wedge's label can be at <paramref name="radius"/> without reaching its neighbours.</summary>
    public double LabelWidth(double radius, double maximum = 74)
    {
        if (WedgeCount <= 1) return maximum;
        var chord = 2 * radius * Math.Sin(Sweep / 2);
        return Math.Max(0, Math.Min(maximum, chord - 8));
    }

    /// <summary>The wedge under the point (x, y), or null over the hub or outside the wheel. y grows downwards.</summary>
    public int? WedgeAt(double x, double y, double centerX, double centerY)
    {
        if (WedgeCount <= 0) return null;
        var dx = x - centerX;
        var dy = centerY - y; // flip to y-up so the maths matches the Swift original
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < InnerRadius || distance > OuterRadius) return null;
        var angle = Math.Atan2(dx, dy); // clockwise from 12 o'clock
        if (angle < 0) angle += 2 * Math.PI;
        return (int)((angle + Sweep / 2) / Sweep) % WedgeCount;
    }
}
