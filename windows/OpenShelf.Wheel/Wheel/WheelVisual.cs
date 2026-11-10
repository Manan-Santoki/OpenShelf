using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Wheel;

/// <summary>
/// The wheel drawing in a Side x Side canvas: frosted disc, wedges, labels, hub and name pill.
/// Each wedge owns its layers (base, accent overlay, flash) so hover only animates opacity and a
/// render transform; nothing is re-laid-out or re-brushed while the pointer moves.
/// </summary>
internal sealed class WheelVisual
{
    public const double Side = 416; // WheelGeometry.PanelSide, checked in WheelWindow
    public const double DiscRadius = 141, WedgeInner = 50, WedgeOuter = 132, NameCentreAbove = 161, NameHeight = 26;
    private const double Gap = 6, Corner = 14, LabelRadius = 93, HubRadius = 44, Push = 3, BackdropPad = 40;

    private static readonly Color Accent = Theme.Accent;
    private static readonly Brush WedgeFill = Theme.White(0.12);
    private static readonly Brush WedgeEdge = Theme.White(0.40);
    private static readonly Brush WedgeSheen = Theme.Vertical((Colors.White, 0.09, 0), (Colors.White, 0, 0.5));
    private static readonly Brush HotFill = Theme.Vertical((Accent, 0.95, 0), (Accent, 0.78, 1));
    private static readonly Brush HotEdge = Theme.White(0.55);
    private static readonly Brush HotSheen = Theme.Vertical((Colors.White, 0.28, 0), (Colors.White, 0, 0.5));
    private static readonly Brush PillIdle = Theme.Black(0.30);
    private static readonly Color Tint = Color.FromRgb(0x1C, 0x1C, 0x20);

    private sealed class Wedge
    {
        public required Canvas Container;
        public required ScaleTransform Scale;
        public required TranslateTransform Move;
        public required Canvas Hot;
        public required Path Flash;
        public required SolidColorBrush LabelBrush;
        public required Vector Direction;
    }

    private readonly List<Wedge> _wedges = [];
    private readonly WheelModel _model;
    private readonly string _hubDefault;
    private readonly double _dpi;
    private readonly Ellipse _hubActive;
    private readonly Border _pill, _pillAccent, _nameRow;
    private readonly TextBlock[] _hubText = new TextBlock[2];
    private readonly TranslateTransform _nameMove = new(0, 6);
    private int _hubFront;
    private string _hubShown;
    private int? _hover;

    public Canvas Root { get; }

    public WheelVisual(WheelModel model, string hubDefault, BitmapSource? backdrop, double dpi, bool animateIn)
    {
        _model = model;
        _hubDefault = hubDefault;
        _dpi = dpi <= 0 ? 1 : dpi;
        var geo = new WheelGeometry(model.Items.Count);
        var c = new Point(Side / 2, Side / 2);
        Root = new Canvas { Width = Side, Height = Side, IsHitTestVisible = false, UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextOptions.SetTextFormattingMode(Root, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(Root, TextRenderingMode.Grayscale);

        BuildDisc(c, backdrop);

        // Wedges (each in its own full-size container so paths keep absolute coordinates)
        for (var i = 0; i < model.Items.Count; i++)
            _wedges.Add(BuildWedge(geo, i, c, model.Items[i], animateIn));

        // Hub
        var hub = new Grid { Width = HubRadius * 2, Height = HubRadius * 2 };
        hub.Children.Add(new Ellipse { Fill = Theme.White(0.08), Stroke = Theme.White(0.5), StrokeThickness = 0.8 });
        _hubActive = new Ellipse { Fill = Theme.Brush(Accent, 0.9), Stroke = Theme.White(0.5), StrokeThickness = 0.8, Opacity = 0 };
        hub.Children.Add(_hubActive);

        _hubShown = hubDefault;
        for (var k = 0; k < 2; k++)
            _hubText[k] = new TextBlock
            {
                Text = k == 0 ? hubDefault : "", FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 70, Opacity = k == 0 ? 1 : 0, UseLayoutRounding = true,
            };
        var pillBack = new Border { CornerRadius = new CornerRadius(11), Background = PillIdle, BorderBrush = Theme.White(0.4), BorderThickness = new Thickness(0.8) };
        _pillAccent = new Border { CornerRadius = new CornerRadius(11), Background = Theme.Brush(Accent), BorderBrush = Theme.White(0.4), BorderThickness = new Thickness(0.8), Opacity = 0 };
        var pillText = new Grid();
        pillText.Children.Add(_hubText[0]);
        pillText.Children.Add(_hubText[1]);
        _pill = new Border
        {
            Height = 22, Width = PillWidth(hubDefault), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { pillBack, _pillAccent, pillText } },
        };
        hub.Children.Add(_pill);
        Place(hub, c.X - HubRadius, c.Y - HubRadius);

        // Name pill above the disc
        _nameRow = new Border
        {
            Height = NameHeight, CornerRadius = new CornerRadius(NameHeight / 2), Padding = new Thickness(12, 0, 12, 0),
            Background = Theme.Brush(Theme.Surface, 0.88), BorderBrush = Theme.White(0.4), BorderThickness = new Thickness(0.8),
            Child = new TextBlock
            {
                Text = model.Name, FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            },
            RenderTransform = _nameMove,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };
        _nameRow.Measure(new Size(double.PositiveInfinity, NameHeight));
        var nameWidth = Math.Ceiling(_nameRow.DesiredSize.Width);
        _nameRow.Width = nameWidth;
        if (animateIn) { _nameRow.Opacity = 0; } else _nameMove.Y = 0;
        Place(_nameRow, Snap(c.X - nameWidth / 2), Snap(c.Y - NameCentreAbove - NameHeight / 2));
    }

    private double Snap(double v) => Math.Round(v * _dpi) / _dpi;

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Root.Children.Add(element);
    }

    private void BuildDisc(Point c, BitmapSource? backdrop)
    {
        var d = DiscRadius * 2;
        var left = c.X - DiscRadius;
        var top = c.Y - DiscRadius;

        // Soft drop shadow, cached so it is rasterised once.
        var shadow = new Ellipse
        {
            Width = d - 2, Height = d - 2, Fill = Theme.Brush(Theme.Surface),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 10, Direction = 270, Opacity = 0.38 },
            CacheMode = new BitmapCache(_dpi),
        };
        Place(shadow, left + 1, top + 1);

        if (backdrop is not null)
        {
            var glass = new Canvas { Width = d, Height = d, ClipToBounds = true };
            var photo = Glass.Backdrop(backdrop, d + BackdropPad * 2, d + BackdropPad * 2);
            Canvas.SetLeft(photo, -BackdropPad);
            Canvas.SetTop(photo, -BackdropPad);
            glass.Children.Add(photo);
            glass.Children.Add(new Rectangle { Width = d, Height = d, Fill = Theme.Brush(Tint, 0.62) });
            // Round the square photo with an anti-aliased mask on a wrapper (a plain Clip would have jagged edges).
            var round = new Border
            {
                Width = d, Height = d, Child = glass, CacheMode = new BitmapCache(_dpi),
                OpacityMask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, new EllipseGeometry(new Rect(0, 0, d, d))))
                { Stretch = Stretch.None, Viewport = new Rect(0, 0, d, d), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, d, d), ViewboxUnits = BrushMappingMode.Absolute },
            };
            Place(round, left, top);
        }
        else
        {
            Place(new Ellipse { Width = d, Height = d, Fill = Theme.Brush(Color.FromRgb(0x2A, 0x2A, 0x2E), 0.95) }, left, top);
        }

        // Top-lit sheen and the rim like the edge of glass.
        var sheen = new Ellipse { Width = d, Height = d, Fill = Theme.Vertical((Colors.White, 0.22, 0), (Colors.White, 0.04, 1)), CacheMode = new BitmapCache(_dpi) };
        Place(sheen, left, top);
        var rim = new Ellipse
        {
            Width = d - 1.2, Height = d - 1.2, StrokeThickness = 1.2,
            Stroke = Theme.Vertical((Colors.White, 0.85, 0), (Colors.White, 0.15, 0.5), (Colors.White, 0.45, 1)),
            CacheMode = new BitmapCache(_dpi),
        };
        Place(rim, left + 0.6, top + 0.6);
    }

    private Wedge BuildWedge(WheelGeometry geo, int index, Point c, WheelItem item, bool animateIn)
    {
        var angle = geo.CenterAngle(index);
        var shape = Sector(c, angle, geo.Sweep, WedgeInner, WedgeOuter, Gap, Corner);
        var dir = new Vector(Math.Sin(angle), -Math.Cos(angle));
        var centroid = c + dir * ((WedgeInner + WedgeOuter) / 2);

        var scale = new ScaleTransform(animateIn ? 0.8 : 1, animateIn ? 0.8 : 1);
        var move = new TranslateTransform();
        var container = new Canvas
        {
            Width = Side, Height = Side, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(centroid.X / Side, centroid.Y / Side),
            RenderTransform = new TransformGroup { Children = { scale, move } },
            Opacity = animateIn ? 0 : 1,
        };

        container.Children.Add(new Path { Data = shape, Fill = WedgeFill });
        container.Children.Add(new Path { Data = shape, Fill = WedgeSheen });
        container.Children.Add(new Path { Data = shape, Stroke = WedgeEdge, StrokeThickness = 0.8 });

        // Accent layer: fades in over the base on hover; its glow is baked into one cached bitmap.
        var hot = new Canvas { Opacity = 0, CacheMode = new BitmapCache(_dpi) };
        hot.Children.Add(new Path
        {
            Data = shape, Fill = HotFill, Stroke = HotEdge, StrokeThickness = 0.8,
            Effect = new DropShadowEffect { Color = Accent, BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = 0.45 },
        });
        hot.Children.Add(new Path { Data = shape, Fill = HotSheen });
        container.Children.Add(hot);

        var flash = new Path { Data = shape, Fill = Brushes.White, Opacity = 0 };
        container.Children.Add(flash);

        var brush = new SolidColorBrush(Colors.White) { Opacity = 0.9 };
        AddLabel(container, geo, angle, c, item, brush);
        Root.Children.Add(container);
        return new Wedge { Container = container, Scale = scale, Move = move, Hot = hot, Flash = flash, LabelBrush = brush, Direction = dir };
    }

    private void AddLabel(Canvas parent, WheelGeometry geo, double angle, Point c, WheelItem item, Brush foreground)
    {
        var title = item.Title.ToUpperInvariant();
        var width = geo.LabelWidth(LabelRadius);
        var stack = new StackPanel { Width = width, UseLayoutRounding = true };

        if (item.Glyph is { } glyph)
        {
            stack.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = Theme.IconFont, FontSize = 17, Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3),
            });
            var label = new TextBlock
            {
                Text = title, FontFamily = Theme.Font, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = label.DesiredSize.Width;
            if (natural > width && natural > 0) label.FontSize = Math.Max(9.5 * 0.65, 9.5 * width / natural);
            stack.Children.Add(label);
        }
        else
        {
            var words = item.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var size = words > 1 ? 10.5 : 13;
            stack.Children.Add(new TextBlock
            {
                Text = title, FontFamily = Theme.Font, FontSize = size, FontWeight = FontWeights.Bold, Foreground = foreground,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = size * 1.2, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = size * 1.2 * 2,
            });
        }

        stack.Measure(new Size(width, double.PositiveInfinity));
        var cx = c.X + Math.Sin(angle) * LabelRadius;
        var cy = c.Y - Math.Cos(angle) * LabelRadius;
        Canvas.SetLeft(stack, Snap(cx - width / 2));
        Canvas.SetTop(stack, Snap(cy - stack.DesiredSize.Height / 2));
        parent.Children.Add(stack);
    }

    private double PillWidth(string text)
    {
        var probe = new TextBlock { Text = text, FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold, MaxWidth = 70 };
        TextOptions.SetTextFormattingMode(probe, TextFormattingMode.Ideal);
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(34, Math.Ceiling(probe.DesiredSize.Width) + 22);
    }

    // ---- Motion ----

    /// <summary>Plays the entrance: wedges stagger in clockwise from 12 o'clock, the name pill rises into place.</summary>
    public void PlayOpen(bool bloom)
    {
        for (var i = 0; i < _wedges.Count; i++)
        {
            var w = _wedges[i];
            var delay = i * 22;
            Motion.Animate(w.Container, UIElement.OpacityProperty, 1, 180, Motion.EaseOut, delay, 0);
            Motion.Animate(w.Scale, ScaleTransform.ScaleXProperty, 1, 300, Motion.Spring, delay, 0.8);
            Motion.Animate(w.Scale, ScaleTransform.ScaleYProperty, 1, 300, Motion.Spring, delay, 0.8);
        }
        var nameDelay = bloom ? 60 : 0;
        Motion.Animate(_nameRow, UIElement.OpacityProperty, 1, 180, Motion.EaseOut, nameDelay, 0);
        if (bloom) Motion.Animate(_nameMove, TranslateTransform.YProperty, 0, 260, Motion.EaseOut, nameDelay, 6);
        else _nameMove.Y = 0;
    }

    /// <summary>Highlights a wedge (or none): cross-fades the accent layer, nudges the wedge outward and updates the hub.</summary>
    public void SetHover(int? index)
    {
        if (index is { } i && (i < 0 || i >= _wedges.Count)) index = null;
        if (index == _hover) return;
        if (_hover is { } old)
        {
            var w = _wedges[old];
            Motion.Animate(w.Hot, UIElement.OpacityProperty, 0, 110, Motion.EaseOut);
            Motion.Animate(w.Move, TranslateTransform.XProperty, 0, 160, Motion.EaseOut);
            Motion.Animate(w.Move, TranslateTransform.YProperty, 0, 160, Motion.EaseOut);
            Motion.Animate(w.LabelBrush, Brush.OpacityProperty, 0.9, 110, Motion.EaseOut);
        }
        _hover = index;
        if (index is { } n)
        {
            var w = _wedges[n];
            Motion.Animate(w.Hot, UIElement.OpacityProperty, 1, 110, Motion.EaseOut);
            Motion.Animate(w.Move, TranslateTransform.XProperty, w.Direction.X * Push, 160, Motion.EaseOut);
            Motion.Animate(w.Move, TranslateTransform.YProperty, w.Direction.Y * Push, 160, Motion.EaseOut);
            Motion.Animate(w.LabelBrush, Brush.OpacityProperty, 1, 110, Motion.EaseOut);
        }
        SetHub(index is { } h ? _model.Items[h].Title : _hubDefault, index is not null);
    }

    private void SetHub(string text, bool active)
    {
        Motion.Animate(_pillAccent, UIElement.OpacityProperty, active ? 1 : 0, 110, Motion.EaseOut);
        // The hub itself stays glass; only the pill turns accent, as on the Mac.
        if (text == _hubShown) return;
        _hubShown = text;
        var from = _hubText[_hubFront];
        _hubFront ^= 1;
        var to = _hubText[_hubFront];
        to.Text = text;
        Motion.Animate(from, UIElement.OpacityProperty, 0, 110, Motion.EaseOut);
        Motion.Animate(to, UIElement.OpacityProperty, 1, 110, Motion.EaseOut);
        Motion.Animate(_pill, FrameworkElement.WidthProperty, PillWidth(text), 140, Motion.EaseOut);
    }

    /// <summary>A brief extra brightness on a wedge that was just dropped on.</summary>
    public void Flash(int index)
    {
        if (index < 0 || index >= _wedges.Count || !Motion.Enabled) return;
        var flash = _wedges[index].Flash;
        Motion.Animate(flash, UIElement.OpacityProperty, 0.4, 60, Motion.EaseOut,
            completed: () => Motion.Animate(flash, UIElement.OpacityProperty, 0, 110, Motion.EaseOut));
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
