namespace OpenShelf.Shelf;

/// <summary>A rectangle in any one coordinate space (physical pixels or DIPs, the caller decides).</summary>
internal readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
    public bool Intersects(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public Box Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public static Box FromPoints(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public Box Intersection(Box o)
    {
        double x = Math.Max(X, o.X), y = Math.Max(Y, o.Y);
        double r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= x || b <= y ? new Box(x, y, 0, 0) : new Box(x, y, r - x, b - y);
    }
}

internal enum ShelfEdge { Left, Right }

internal enum ShelfViewMode { Grid, List }

/// <summary>Where a shelf settles after the user moves it: flush against the left or right edge of the
/// work area when released close to it, otherwise where it was dropped. Always kept fully on screen;
/// docking only ever moves it sideways (SPEC 3, "Edge docking").</summary>
internal static class EdgeDocking
{
    /// <summary>Snap distance in DIPs (points on the Mac).</summary>
    public const double ThresholdDip = 48;

    /// <param name="threshold">Snap distance in the same units as the boxes.</param>
    public static (Box Frame, ShelfEdge? Edge) Settle(Box frame, Box visible, double threshold = ThresholdDip)
    {
        double y = ClampAxis(frame.Y, frame.H, visible.Y, visible.Bottom);
        if (frame.X - visible.X <= threshold)
            return (frame with { X = visible.X, Y = y }, ShelfEdge.Left);
        if (visible.Right - frame.Right <= threshold)
            return (frame with { X = visible.Right - frame.W, Y = y }, ShelfEdge.Right);
        return (frame with { X = ClampAxis(frame.X, frame.W, visible.X, visible.Right), Y = y }, null);
    }

    /// <summary>Keeps the frame inside the visible area without docking it.</summary>
    public static Box Clamp(Box frame, Box visible) => frame with
    {
        X = ClampAxis(frame.X, frame.W, visible.X, visible.Right),
        Y = ClampAxis(frame.Y, frame.H, visible.Y, visible.Bottom),
    };

    // If the box is larger than the area its start wins, so the header stays reachable.
    static double ClampAxis(double start, double length, double min, double max) =>
        Math.Max(min, Math.Min(start, max - length));
}

/// <summary>Geometry for docked shelves that tuck away behind the screen edge, leaving a slim tab.</summary>
internal static class EdgeHiding
{
    public const double TabWidthDip = 14;
    public const double TabHeightDip = 72;
    /// <summary>How long the pointer must stay away before a docked shelf hides.</summary>
    public const double HideDelaySeconds = 1.5;
    /// <summary>Slack around the shelf so brushing past its edge doesn't count as leaving (DIPs).</summary>
    public const double HoverMarginDip = 8;

    /// <summary>Where the shelf sits while hidden: just past the edge it is docked to.</summary>
    public static Box HiddenFrame(Box shown, ShelfEdge edge, Box visible) => edge == ShelfEdge.Left
        ? shown with { X = visible.X - shown.W }
        : shown with { X = visible.Right };

    /// <summary>The tab: against the docked edge, centred on the shelf, kept on screen.</summary>
    public static Box TabFrame(Box shown, ShelfEdge edge, Box visible, double scale = 1)
    {
        double w = TabWidthDip * scale, h = TabHeightDip * scale;
        double x = edge == ShelfEdge.Left ? visible.X : visible.Right - w;
        double y = Math.Max(visible.Y, Math.Min(shown.MidY - h / 2, visible.Bottom - h));
        return new Box(x, y, w, h);
    }

    public static bool IsPointerNear(double x, double y, Box shelf, double margin = HoverMarginDip) =>
        shelf.Inflate(margin).Contains(x, y);
}
