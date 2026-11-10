using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>
/// The strip under the Trim preview: video frames or an audio waveform with a start and an end handle, the cut parts dimmed, and a
/// playhead. Dragging a handle moves that end of the selection, clicking or dragging the track moves the playhead.
/// </summary>
internal sealed class TrimTimeline : FrameworkElement
{
    private const double Radius = 8;

    private double _duration;
    private double _start;
    private double _end;
    private double _playhead;
    private bool _isVideo = true;
    private IReadOnlyList<ImageSource> _frames = [];
    private float[] _wave = [];
    private TimelineMath.Part _drag;
    private double _dragX;
    private double _dragStart;
    private double _dragEnd;

    public TrimTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
        ClipToBounds = false;
        ToolKit.Name(this, "Trim range");
    }

    public double Duration { get => _duration; set { _duration = value; Redraw(); } }
    public double Start { get => _start; set { _start = value; Redraw(); } }
    public double End { get => _end; set { _end = value; Redraw(); } }
    public double Playhead { get => _playhead; set { _playhead = value; InvalidateVisual(); } }
    public bool IsVideo { get => _isVideo; set { _isVideo = value; InvalidateVisual(); } }
    public IReadOnlyList<ImageSource> Frames { get => _frames; set { _frames = value; InvalidateVisual(); } }
    public float[] Waveform { get => _wave; set { _wave = value; InvalidateVisual(); } }

    /// <summary>The start handle was dragged: the time it now points at (not yet snapped or clamped).</summary>
    public event Action<double>? StartDragged;
    public event Action<double>? EndDragged;
    /// <summary>The track was clicked or scrubbed.</summary>
    public event Action<double>? Seeked;

    private void Redraw()
    {
        InvalidateVisual();
        System.Windows.Automation.AutomationProperties.SetHelpText(this,
            $"From {OpenShelf.Tools.Media.MediaTime.Format(_start)} to {OpenShelf.Tools.Media.MediaTime.Format(_end)}");
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 58 : availableSize.Height);

    private double Scale => _duration > 0 ? ActualWidth / _duration : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var area = new Rect(0, 0, width, height);
        dc.DrawRectangle(Brushes.Transparent, null, area);   // hit-testable
        if (width <= 0 || height <= 0) return;

        var clip = new RectangleGeometry(area, Radius, Radius);
        dc.PushClip(clip);
        dc.DrawRectangle(ToolKit.Brush("Fill"), null, area);
        if (_isVideo) DrawFrames(dc, area);
        else DrawWave(dc, area);

        if (_duration > 0)
        {
            var startX = TimelineMath.XAt(_start, width, _duration);
            var endX = TimelineMath.XAt(_end, width, _duration);
            var dim = Dim;
            if (startX > 0) dc.DrawRectangle(dim, null, new Rect(0, 0, Math.Min(startX, width), height));
            if (endX < width) dc.DrawRectangle(dim, null, new Rect(Math.Max(endX, 0), 0, width - Math.Max(endX, 0), height));
        }
        dc.Pop();

        if (_duration <= 0) return;
        var sx = TimelineMath.XAt(_start, width, _duration);
        var ex = TimelineMath.XAt(_end, width, _duration);
        var accent = ToolKit.Brush("Accent");
        var frame = new Rect(sx, 0, Math.Max(ex - sx, TimelineMath.HandleWidth * 2), height);
        dc.DrawRoundedRectangle(null, new Pen(accent, 3), Inset(frame, 1.5), 6, 6);

        DrawHandle(dc, new Rect(sx, 0, TimelineMath.HandleWidth, height), isStart: true);
        DrawHandle(dc, new Rect(Math.Max(ex, sx + TimelineMath.HandleWidth * 2) - TimelineMath.HandleWidth, 0, TimelineMath.HandleWidth, height), isStart: false);

        var px = Math.Clamp(TimelineMath.XAt(_playhead, width, _duration) - 1, 0, Math.Max(width - 2, 0));
        dc.DrawRectangle(Shadow, null, new Rect(px - 0.5, -3, 3, height + 6));
        dc.DrawRectangle(Brushes.White, null, new Rect(px, -3, 2, height + 6));
    }

    private static Rect Inset(Rect rect, double by) => new(rect.X + by, rect.Y + by, Math.Max(rect.Width - 2 * by, 0), Math.Max(rect.Height - 2 * by, 0));

    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
    private static readonly Brush Grip = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)));
    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
    private static readonly Brush Shadow = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)));

    private void DrawHandle(DrawingContext dc, Rect rect, bool isStart)
    {
        var geometry = new PathGeometry();
        var r = 6.0;
        var figure = isStart
            ? new PathFigure(new Point(rect.Right, rect.Top), [
                new LineSegment(new Point(rect.Left + r, rect.Top), true),
                new ArcSegment(new Point(rect.Left, rect.Top + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true),
                new LineSegment(new Point(rect.Left, rect.Bottom - r), true),
                new ArcSegment(new Point(rect.Left + r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true),
                new LineSegment(new Point(rect.Right, rect.Bottom), true)], true)
            : new PathFigure(new Point(rect.Left, rect.Top), [
                new LineSegment(new Point(rect.Right - r, rect.Top), true),
                new ArcSegment(new Point(rect.Right, rect.Top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(rect.Right, rect.Bottom - r), true),
                new ArcSegment(new Point(rect.Right - r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(rect.Left, rect.Bottom), true)], true);
        geometry.Figures.Add(figure);
        dc.DrawGeometry(ToolKit.Brush("Accent"), null, geometry);
        var gripHeight = Math.Min(16, rect.Height - 8);
        dc.DrawRoundedRectangle(Grip, null, new Rect(rect.X + rect.Width / 2 - 1, rect.Y + (rect.Height - gripHeight) / 2, 2, gripHeight), 1, 1);
    }

    private void DrawFrames(DrawingContext dc, Rect area)
    {
        if (_frames.Count == 0) return;
        var cell = area.Width / _frames.Count;
        for (var i = 0; i < _frames.Count; i++)
        {
            var slot = new Rect(i * cell, 0, cell + 0.5, area.Height);
            var picture = _frames[i];
            var scale = Math.Max(slot.Width / picture.Width, slot.Height / picture.Height);
            var w = picture.Width * scale;
            var h = picture.Height * scale;
            dc.PushClip(new RectangleGeometry(slot));
            dc.DrawImage(picture, new Rect(slot.X + (slot.Width - w) / 2, slot.Y + (slot.Height - h) / 2, w, h));
            dc.Pop();
        }
    }

    private void DrawWave(DrawingContext dc, Rect area)
    {
        if (_wave.Length == 0) return;
        var accent = ToolKit.Brush("Accent").Clone();
        accent.Opacity = 0.85;
        accent.Freeze();
        var barWidth = area.Width / _wave.Length;
        for (var i = 0; i < _wave.Length; i++)
        {
            var h = Math.Max(1, _wave[i] * area.Height * 0.9);
            dc.DrawRoundedRectangle(accent, null, new Rect(i * barWidth, (area.Height - h) / 2, Math.Max(barWidth - 0.5, 0.5), h), barWidth / 2, barWidth / 2);
        }
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0 || ActualWidth <= 0) return;
        var x = e.GetPosition(this).X;
        var startX = TimelineMath.XAt(_start, ActualWidth, _duration);
        var endX = TimelineMath.XAt(_end, ActualWidth, _duration);
        _drag = TimelineMath.HitTest(x, startX, endX);
        _dragX = x;
        _dragStart = _start;
        _dragEnd = _end;
        CaptureMouse();
        e.Handled = true;
        if (_drag == TimelineMath.Part.Track) Seek(x);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_drag == TimelineMath.Part.None || !IsMouseCaptured)
        {
            var part = _duration > 0 ? TimelineMath.HitTest(x, TimelineMath.XAt(_start, ActualWidth, _duration), TimelineMath.XAt(_end, ActualWidth, _duration)) : TimelineMath.Part.Track;
            Cursor = part is TimelineMath.Part.Start or TimelineMath.Part.End ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        var scale = Scale;
        if (scale <= 0) return;
        var delta = (x - _dragX) / scale;
        switch (_drag)
        {
            case TimelineMath.Part.Start: StartDragged?.Invoke(_dragStart + delta); break;
            case TimelineMath.Part.End: EndDragged?.Invoke(_dragEnd + delta); break;
            case TimelineMath.Part.Track: Seek(x); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = TimelineMath.Part.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Seek(double x) =>
        Seeked?.Invoke(Math.Clamp(TimelineMath.TimeAt(x, ActualWidth, _duration), _start, _end));
}
