using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>The wheel drawing: rim, rounded wedges, labels, hub and name pill, in a Side x Side canvas.</summary>
internal sealed class WheelVisual
{
    public const double Side = 416; // WheelGeometry.PanelSide, checked in WheelWindow
    private const double Rim = 132, WedgeInner = 57, WedgeOuter = 126, Gap = 3, Corner = 10, LabelRadius = 93, HubRadius = 50;

    private static readonly Color Accent = Color.FromRgb(0x3D, 0x7B, 0xFD);
    private static readonly FontFamily Font = new("Segoe UI");
    private static readonly Brush WedgeFill = Frozen(Color.FromArgb(0x1C, 255, 255, 255));      // 11%
    private static readonly Brush WedgeBorder = Frozen(Color.FromArgb(0x38, 255, 255, 255));    // 22%
    private static readonly Brush HotFill = Frozen(Color.FromArgb(0xF2, Accent.R, Accent.G, Accent.B)); // 95%
    private static readonly Brush HubIdle = Frozen(Color.FromArgb(0x26, 255, 255, 255));
    private static readonly Brush AccentBrush = Frozen(Accent);

    private readonly List<System.Windows.Shapes.Path> _wedges = [];
    private readonly TextBlock _hubText;
    private readonly Border _hubPill;
    private readonly WheelModel _model;
    private readonly string _hubDefault;
    private int? _hover;

    public Canvas Root { get; }

    public WheelVisual(WheelModel model, string hubDefault)
    {
        _model = model;
        _hubDefault = hubDefault;
        var geo = new WheelGeometry(model.Items.Count);
        var c = new Point(Side / 2, Side / 2);
        Root = new Canvas { Width = Side, Height = Side, IsHitTestVisible = false };

        // Rim
        var rim = new Ellipse
        {
            Width = Rim * 2, Height = Rim * 2,
            Fill = Frozen(Color.FromArgb(0xF2, 0x2A, 0x2A, 0x2E)),                        // #2A2A2E at 95% (no blur on Windows, so nearly opaque)
            Stroke = Frozen(Color.FromArgb(0x47, 255, 255, 255)),                          // 28%
            StrokeThickness = 1.2,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45 },
        };
        Place(rim, c.X - Rim, c.Y - Rim);

        // Wedges
        for (var i = 0; i < model.Items.Count; i++)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Sector(c, geo.CenterAngle(i), geo.Sweep, WedgeInner, WedgeOuter, Gap, Corner),
                Fill = WedgeFill, Stroke = WedgeBorder, StrokeThickness = 1,
            };
            _wedges.Add(path);
            Root.Children.Add(path);
        }

        // Labels
        for (var i = 0; i < model.Items.Count; i++)
        {
            var angle = geo.CenterAngle(i);
            var title = model.Items[i].Title;
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var size = words > 1 ? 10.5 : 13;
            var width = geo.LabelWidth(LabelRadius);
            var tb = new TextBlock
            {
                Text = title.ToUpperInvariant(), FontFamily = Font, FontSize = size, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Width = width,
                LineHeight = size * 1.2, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = size * 1.2 * 2,
            };
            tb.Measure(new Size(width, double.PositiveInfinity));
            var cx = c.X + Math.Sin(angle) * LabelRadius;
            var cy = c.Y - Math.Cos(angle) * LabelRadius;
            Place(tb, cx - width / 2, cy - tb.DesiredSize.Height / 2);
        }

        // Hub with its text pill
        _hubText = new TextBlock
        {
            Text = hubDefault, FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
        };
        _hubPill = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 8, 3), MaxWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = HubIdle, Child = _hubText,
        };
        var hub = new Grid { Width = HubRadius * 2, Height = HubRadius * 2 };
        hub.Children.Add(new Ellipse
        {
            Fill = Frozen(Color.FromArgb(0x14, 255, 255, 255)),     // 8%
            Stroke = Frozen(Color.FromArgb(0x66, 255, 255, 255)),   // 40%
            StrokeThickness = 1,
        });
        hub.Children.Add(_hubPill);
        Place(hub, c.X - HubRadius, c.Y - HubRadius);

        // Name pill above the rim
        var name = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3),
            Background = Frozen(Color.FromArgb(0xE6, 0x26, 0x26, 0x29)),
            BorderBrush = Frozen(Color.FromArgb(0x33, 255, 255, 255)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = model.Name, FontFamily = Font, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
        };
        var nameRow = new Grid { Width = Side };
        nameRow.Children.Add(name);
        Place(nameRow, 0, c.Y - Rim - 40);
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Root.Children.Add(element);
    }

    /// <summary>Highlights a wedge (or none) and updates the hub text.</summary>
    public void SetHover(int? index)
    {
        if (index is { } i && (i < 0 || i >= _wedges.Count)) index = null;
        if (index == _hover) return;
        if (_hover is { } old) { _wedges[old].Fill = WedgeFill; _wedges[old].Effect = null; }
        _hover = index;
        if (index is { } n)
        {
            _wedges[n].Fill = HotFill;
            _wedges[n].Effect = new DropShadowEffect { Color = Accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.7 };
            _hubText.Text = _model.Items[n].Title;
            _hubPill.Background = AccentBrush;
        }
        else
        {
            _hubText.Text = _hubDefault;
            _hubPill.Background = HubIdle;
        }
    }

    private static Brush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Annular sector with straight sides offset by gap/2 from the wedge boundaries and
    /// fillets of radius <paramref name="corner"/> at the four corners.
    /// Local frame: u along the wedge's centre line (outwards), v clockwise.
    /// </summary>
    private static Geometry Sector(Point c, double theta, double sweep, double r, double R, double gap, double corner)
    {
        if (sweep >= 2 * Math.PI - 1e-6)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(c, R, R));
            ring.Children.Add(new EllipseGeometry(c, r, r));
            ring.Freeze();
            return ring;
        }

        double a = sweep / 2, h = gap / 2, k = corner;
        double dU = Math.Cos(a), dV = Math.Sin(a);   // boundary direction
        double nU = Math.Sin(a), nV = -Math.Cos(a);  // inward normal of that boundary

        // Fillet centres lie at distance (R - k) / (r + k) from the origin and k inside the offset side line.
        var tO = Math.Sqrt(Math.Max(0, (R - k) * (R - k) - (h + k) * (h + k)));
        var tI = Math.Sqrt(Math.Max(0, (r + k) * (r + k) - (h + k) * (h + k)));
        (double u, double v) cO = (tO * dU + (h + k) * nU, tO * dV + (h + k) * nV);
        (double u, double v) cI = (tI * dU + (h + k) * nU, tI * dV + (h + k) * nV);
        var lenO = Math.Sqrt(cO.u * cO.u + cO.v * cO.v);
        var lenI = Math.Sqrt(cI.u * cI.u + cI.v * cI.v);
        (double u, double v) arcO = (cO.u * R / lenO, cO.v * R / lenO);
        (double u, double v) sideO = (tO * dU + h * nU, tO * dV + h * nV);
        (double u, double v) arcI = (cI.u * r / lenI, cI.v * r / lenI);
        (double u, double v) sideI = (tI * dU + h * nU, tI * dV + h * nV);

        double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
        Point P((double u, double v) q, int sigma) =>
            new(c.X + sinT * q.u + cosT * sigma * q.v, c.Y - cosT * q.u + sinT * sigma * q.v);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(arcO, -1), true, true);
            ctx.ArcTo(P(arcO, 1), new Size(R, R), 0, false, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(P(sideO, 1), new Size(k, k), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(sideI, 1), true, false);
            ctx.ArcTo(P(arcI, 1), new Size(k, k), 0, false, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(P(arcI, -1), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            ctx.ArcTo(P(sideI, -1), new Size(k, k), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(sideO, -1), true, false);
            ctx.ArcTo(P(arcO, -1), new Size(k, k), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
