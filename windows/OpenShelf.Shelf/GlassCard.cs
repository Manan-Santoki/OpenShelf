using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>The frosted card shared by toasts and the progress card: shadow, blurred photo of the screen behind it,
/// dark tint, hairline border.</summary>
internal static class GlassCard
{
    /// <summary>Transparent margin around the card for its shadow, in DIPs.</summary>
    public const double Pad = 28;
    public const double Radius = 12;
    const int CaptureMargin = 24;   // extra physical pixels captured so the blur has no faded edge

    static Border Shadow(double w, double h, double blur, double opacity, double depth) => new()
    {
        Width = w, Height = h, Margin = new Thickness(Pad),
        CornerRadius = new CornerRadius(Radius),
        Background = Theme.Brush(Theme.Surface),
        Effect = new DropShadowEffect
        {
            BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black,
            RenderingBias = RenderingBias.Quality,
        },
    };

    /// <summary>Fills <paramref name="host"/> with the card around <paramref name="content"/>.
    /// <paramref name="winX"/>/<paramref name="winY"/> are the window's physical top left (for the screen capture).</summary>
    public static void Fill(Grid host, FrameworkElement content, double cardW, double cardH, int winX, int winY, double scale,
        bool hitTestable)
    {
        host.Children.Add(Shadow(cardW, cardH, 28, 0.35, 8));
        host.Children.Add(Shadow(cardW, cardH, 3, 0.25, 1));

        var inner = new Grid { Width = cardW - 2, Height = cardH - 2, IsHitTestVisible = hitTestable };
        inner.Clip = new RectangleGeometry(new Rect(0, 0, cardW - 2, cardH - 2), Radius - 1, Radius - 1);

        // Photograph the screen under the card just before it appears (physical pixels).
        bool captured = false;
        try
        {
            int m = CaptureMargin;
            int px = winX + (int)Math.Round(Pad * scale);
            int py = winY + (int)Math.Round(Pad * scale);
            int pw = (int)Math.Round(cardW * scale);
            int ph = (int)Math.Round(cardH * scale);
            var snap = Glass.Capture(px - m, py - m, pw + 2 * m, ph + 2 * m);
            if (snap != null)
            {
                var backdrop = Glass.Backdrop(snap, (pw + 2 * m) / scale, (ph + 2 * m) / scale, 22);
                var layer = new Canvas { Width = cardW - 2, Height = cardH - 2, IsHitTestVisible = false };
                Canvas.SetLeft(backdrop, -m / scale - 1);
                Canvas.SetTop(backdrop, -m / scale - 1);
                layer.Children.Add(backdrop);
                inner.Children.Add(layer);
                inner.Children.Add(new Border { Background = Theme.Brush(Theme.Surface, 0.60), IsHitTestVisible = false });
                captured = true;
            }
        }
        catch (Exception ex) { Log.Info("Glass capture failed: " + ex.Message); }

        if (!captured) inner.Children.Add(new Border { Background = Theme.Brush(Theme.Surface, 0.97), IsHitTestVisible = false });
        inner.Children.Add(content);

        host.Children.Add(new Border
        {
            Width = cardW, Height = cardH, Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(Radius),
            BorderBrush = Theme.White(0.14), BorderThickness = new Thickness(1),
            Child = inner,
        });
    }
}
