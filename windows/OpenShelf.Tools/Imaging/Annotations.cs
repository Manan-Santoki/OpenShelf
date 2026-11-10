using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ImageMagick;

namespace OpenShelf.Tools.Imaging;

public enum AnnotationTool { Draw, Arrow, Box, Highlight, Text, Callout }

public static class AnnotationTools
{
    public static readonly IReadOnlyList<AnnotationTool> All = Enum.GetValues<AnnotationTool>();

    public static string Title(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Draw => "Draw",
        AnnotationTool.Arrow => "Arrow",
        AnnotationTool.Box => "Box",
        AnnotationTool.Highlight => "Highlight",
        AnnotationTool.Text => "Text",
        _ => "Callout",
    };

    /// <summary>Segoe MDL2 glyph.</summary>
    public static string Glyph(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Draw => "",
        AnnotationTool.Arrow => "",
        AnnotationTool.Box => "",
        AnnotationTool.Highlight => "",
        AnnotationTool.Text => "",
        _ => "",
    };

    public static string Hint(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Draw => "Drag on the image to draw.",
        AnnotationTool.Arrow => "Drag from the tail toward the arrowhead.",
        AnnotationTool.Box => "Drag to draw a box.",
        AnnotationTool.Highlight => "Drag over the image to highlight.",
        AnnotationTool.Text => "Click where the text should go.",
        _ => "Click to add numbered callouts.",
    };
}

/// <summary>What a mark is. Coordinates are image pixels, origin top-left.</summary>
public abstract record Figure;
public sealed record StrokeFigure(IReadOnlyList<Point> Points) : Figure;
public sealed record ArrowFigure(Point From, Point To) : Figure;
public sealed record BoxFigure(Rect Rect) : Figure;
public sealed record HighlightFigure(Rect Rect) : Figure;
public sealed record TextFigure(string Text, Point Origin) : Figure;
/// <summary>Callouts keep the size they were created with, whatever the stroke width.</summary>
public sealed record CalloutFigure(int Number, Point Center, double Diameter) : Figure;

/// <summary>One mark on the image.</summary>
public sealed record Annotation(Guid Id, Figure Figure, Color Color, double LineWidth)
{
    public static Annotation Create(Figure figure, Color color, double lineWidth) => new(Guid.NewGuid(), figure, color, lineWidth);

    public Annotation OffsetBy(Vector delta)
    {
        Point Move(Point p) => p + delta;
        var moved = Figure switch
        {
            StrokeFigure s => new StrokeFigure(s.Points.Select(Move).ToList()),
            ArrowFigure a => new ArrowFigure(Move(a.From), Move(a.To)),
            BoxFigure b => new BoxFigure(Rect.Offset(b.Rect, delta)),
            HighlightFigure h => new HighlightFigure(Rect.Offset(h.Rect, delta)),
            TextFigure t => new TextFigure(t.Text, Move(t.Origin)),
            CalloutFigure c => new CalloutFigure(c.Number, Move(c.Center), c.Diameter),
            var other => other,
        };
        return this with { Figure = moved };
    }
}

/// <summary>Draws annotations with WPF, both for the live preview and the saved full-resolution image, so they match exactly.
/// Drawing contexts take image pixels as units. Port of AnnotationRenderer.</summary>
public static class AnnotationRenderer
{
    private static readonly Typeface Semibold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>Text follows the same size control as lines, scaled so the default (1/200 of the image's long side) gives text about
    /// 3.5% of that side.</summary>
    public static double FontSize(double lineWidth) => Math.Max(14, lineWidth * 7);

    public static double ArrowHead(double lineWidth) => Math.Max(lineWidth * 4, 12);

    public static void Draw(DrawingContext dc, Annotation annotation)
    {
        var brush = new SolidColorBrush(annotation.Color);
        brush.Freeze();
        var pen = new Pen(brush, annotation.LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();

        switch (annotation.Figure)
        {
            case StrokeFigure stroke when stroke.Points.Count > 0:
                if (stroke.Points.Count == 1)
                {
                    dc.DrawEllipse(brush, null, stroke.Points[0], annotation.LineWidth / 2, annotation.LineWidth / 2);
                    break;
                }
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(stroke.Points[0], false, false);
                    ctx.PolyLineTo(stroke.Points.Skip(1).ToList(), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
                break;

            case ArrowFigure arrow:
                var angle = Math.Atan2(arrow.To.Y - arrow.From.Y, arrow.To.X - arrow.From.X);
                var head = ArrowHead(annotation.LineWidth);
                var shaftEnd = new Point(arrow.To.X - Math.Cos(angle) * head * 0.7, arrow.To.Y - Math.Sin(angle) * head * 0.7);
                dc.DrawLine(pen, arrow.From, shaftEnd);
                var tip = new StreamGeometry();
                using (var ctx = tip.Open())
                {
                    ctx.BeginFigure(arrow.To, true, true);
                    ctx.LineTo(new Point(arrow.To.X - Math.Cos(angle - 0.45) * head, arrow.To.Y - Math.Sin(angle - 0.45) * head), true, false);
                    ctx.LineTo(new Point(arrow.To.X - Math.Cos(angle + 0.45) * head, arrow.To.Y - Math.Sin(angle + 0.45) * head), true, false);
                }
                tip.Freeze();
                dc.DrawGeometry(brush, null, tip);
                break;

            case BoxFigure box:
                dc.DrawRectangle(null, pen, box.Rect);
                break;

            case HighlightFigure highlight:
                var wash = new SolidColorBrush(Color.FromArgb((byte)Math.Round(annotation.Color.A * 0.35), annotation.Color.R, annotation.Color.G, annotation.Color.B));
                wash.Freeze();
                dc.DrawRectangle(wash, null, highlight.Rect);
                break;

            case TextFigure text:
                dc.DrawText(Format(text.Text, brush, FontSize(annotation.LineWidth), Semibold), text.Origin);
                break;

            case CalloutFigure callout:
                var radius = callout.Diameter / 2;
                dc.DrawEllipse(brush, null, callout.Center, radius, radius);
                var ring = new Pen(Brushes.White, callout.Diameter * 0.07);
                ring.Freeze();
                dc.DrawEllipse(null, ring, callout.Center, radius - callout.Diameter * 0.035, radius - callout.Diameter * 0.035);
                var label = Format(callout.Number.ToString(CultureInfo.InvariantCulture), Brushes.White, callout.Diameter * 0.55, Bold);
                dc.DrawText(label, new Point(callout.Center.X - label.WidthIncludingTrailingWhitespace / 2, callout.Center.Y - label.Height / 2));
                break;
        }
    }

    private static FormattedText Format(string text, Brush brush, double size, Typeface typeface) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);

    /// <summary>The area an annotation covers, used for selection outlines.</summary>
    public static Rect Bounds(Annotation a)
    {
        var pad = a.LineWidth / 2;
        switch (a.Figure)
        {
            case StrokeFigure s when s.Points.Count > 0:
                var r = new Rect(s.Points[0], s.Points[0]);
                foreach (var p in s.Points) r.Union(p);
                r.Inflate(pad, pad);
                return r;
            case ArrowFigure arrow:
                var rect = new Rect(arrow.From, arrow.To);
                var head = ArrowHead(a.LineWidth);
                rect.Inflate(head / 2, head / 2);
                return rect;
            case BoxFigure box:
                var b = box.Rect;
                b.Inflate(pad, pad);
                return b;
            case HighlightFigure h:
                return h.Rect;
            case TextFigure t:
                var text = Format(t.Text, Brushes.Black, FontSize(a.LineWidth), Semibold);
                return new Rect(t.Origin.X, t.Origin.Y, text.WidthIncludingTrailingWhitespace, text.Height);
            case CalloutFigure c:
                return new Rect(c.Center.X - c.Diameter / 2, c.Center.Y - c.Diameter / 2, c.Diameter, c.Diameter);
            default:
                return Rect.Empty;
        }
    }

    public static bool Contains(Annotation a, Point point, double tolerance)
    {
        var reach = a.LineWidth / 2 + tolerance;
        switch (a.Figure)
        {
            case StrokeFigure s when s.Points.Count > 0:
                if (s.Points.Count == 1) return (point - s.Points[0]).Length <= reach;
                for (var i = 1; i < s.Points.Count; i++)
                    if (DistanceToSegment(point, s.Points[i - 1], s.Points[i]) <= reach) return true;
                return false;
            case ArrowFigure arrow:
                return DistanceToSegment(point, arrow.From, arrow.To) <= Math.Max(reach, Math.Max(a.LineWidth * 2, 6));
            case BoxFigure or HighlightFigure or TextFigure:
                var bounds = Bounds(a);
                bounds.Inflate(tolerance, tolerance);
                return bounds.Contains(point);
            case CalloutFigure c:
                return (point - c.Center).Length <= c.Diameter / 2 + tolerance;
            default:
                return false;
        }
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return (p - a).Length;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return (p - new Point(a.X + t * dx, a.Y + t * dy)).Length;
    }

    /// <summary>Draws <paramref name="annotations"/> over a full-resolution image (in place). Needs a thread that can use WPF drawing.</summary>
    public static void Render(IMagickImage<byte> image, IReadOnlyList<Annotation> annotations)
    {
        if (annotations.Count == 0) return;
        using var layer = MagickWpf.Render((int)image.Width, (int)image.Height, dc =>
        {
            foreach (var annotation in annotations) Draw(dc, annotation);
        });
        image.Composite(layer, 0, 0, CompositeOperator.Over);
    }
}

/// <summary>Saves annotated copies next to the originals ("Name Annotated.png").</summary>
public static class AnnotateOperation
{
    public const string Suffix = "Annotated";

    public static string Save(string path, IReadOnlyList<Annotation> annotations)
    {
        using var image = ImageFiles.ReadUpright(path);
        AnnotationRenderer.Render(image, annotations);
        return ImageFiles.SaveNext(image, path, Suffix);
    }

    /// <summary>Draws on and saves each image; images without marks are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, IReadOnlyList<Annotation> Marks)> jobs, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(jobs, new SaveOutcome { SavedLabel = "annotated", UnchangedLabel = "not annotated" },
            job => job.Marks.Count == 0 ? null : Save(job.Path, job.Marks), progress, cancellation);
}

/// <summary>Snapshot-based undo and redo for editors whose state is a small value, such as a list of annotations.</summary>
public sealed class EditHistory<TSnapshot>
{
    private readonly List<TSnapshot> _undo = [];
    private readonly List<TSnapshot> _redo = [];
    private object? _coalescingKey;
    private readonly int _limit;

    public EditHistory(int limit = 100) => _limit = limit;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call with the state as it was just before a change. Consecutive changes with the same non-null
    /// <paramref name="coalescingKey"/> (e.g. dragging a width slider) undo as one step.</summary>
    public void Record(TSnapshot snapshot, object? coalescingKey = null)
    {
        if (coalescingKey is not null && Equals(coalescingKey, _coalescingKey)) return;
        _coalescingKey = coalescingKey;
        _undo.Add(snapshot);
        if (_undo.Count > _limit) _undo.RemoveRange(0, _undo.Count - _limit);
        _redo.Clear();
    }

    /// <summary>Ends any run of coalesced changes, so the next change gets its own step.</summary>
    public void EndCoalescing() => _coalescingKey = null;

    /// <summary>Returns the state to restore, given the current one; null when there is nothing to undo.</summary>
    public TSnapshot? Undo(TSnapshot current)
    {
        _coalescingKey = null;
        if (_undo.Count == 0) return default;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return previous;
    }

    public TSnapshot? Redo(TSnapshot current)
    {
        _coalescingKey = null;
        if (_redo.Count == 0) return default;
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        return next;
    }
}
