using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenShelf.Tools;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>A slim rounded progress bar in the editors' accent colour. Determinate with <see cref="Value"/> (0 to 1), or
/// <see cref="Indeterminate"/> with a bar sliding along the track.</summary>
internal sealed class ThinProgress : FrameworkElement
{
    private double _value;
    private bool _indeterminate;
    private readonly TranslateTransform _slide = new();

    public ThinProgress()
    {
        Height = 6;
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            if (value)
            {
                var animation = new DoubleAnimation(-0.4, 1.0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
                _slide.BeginAnimation(TranslateTransform.XProperty, animation);
                CompositionTarget.Rendering += Tick;
            }
            else
            {
                _slide.BeginAnimation(TranslateTransform.XProperty, null);
                CompositionTarget.Rendering -= Tick;
            }
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(bounds, 3, 3));
        dc.DrawRectangle(ToolKit.Brush("Fill"), null, bounds);
        if (_indeterminate)
            dc.DrawRoundedRectangle(ToolKit.Brush("Accent"), null, new Rect(_slide.X * ActualWidth, 0, ActualWidth * 0.4, ActualHeight), 3, 3);
        else if (_value > 0)
            dc.DrawRoundedRectangle(ToolKit.Brush("Accent"), null, new Rect(0, 0, ActualWidth * _value, ActualHeight), 3, 3);
        dc.Pop();
    }

    // The animation moves the transform; repaint with it while the bar is on screen.
    private void Tick(object? sender, EventArgs e)
    {
        if (!_indeterminate || !IsVisible) return;
        InvalidateVisual();
    }
}

/// <summary>The grey-and-white squares that show transparency, filling their whole area (the Mac's Checkerboard).</summary>
internal sealed class CheckerboardBackdrop : FrameworkElement
{
    private static readonly Brush Light = Frozen(new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xEB)));
    private static readonly Brush Dark = Frozen(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private readonly double _cell;

    public CheckerboardBackdrop(double cell = 10)
    {
        _cell = cell;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Light, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var y = 0; y * _cell < ActualHeight; y++)
            for (var x = y % 2; x * _cell < ActualWidth; x += 2)
                dc.DrawRectangle(Dark, null, new Rect(x * _cell, y * _cell, _cell, _cell));
    }
}

/// <summary>Lets the demo executable put an editor into a named state for screenshots.</summary>
internal interface IEditorDemo
{
    void ApplyDemoState(string state);
}
