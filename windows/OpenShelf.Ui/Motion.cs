using System.Windows;
using System.Windows.Media.Animation;

namespace OpenShelf.Ui;

/// <summary>Animation helpers. Everything respects the system "show animations" setting (Reduce Motion).</summary>
public static class Motion
{
    /// <summary>False when the user turned animations off in Windows; callers then jump to the end state.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Animates a double property, or sets it at once when motion is off. Returns the animation (null when skipped).</summary>
    public static DoubleAnimation? Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds,
        IEasingFunction? easing = null, int delayMilliseconds = 0, double? from = null, Action? completed = null)
    {
        if (!Enabled || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            completed?.Invoke();
            return null;
        }
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = easing ?? EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (from is { } start) animation.From = start;
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
        return animation;
    }

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>A soft spring with a little overshoot, like SwiftUI's .spring(response: 0.32, dampingFraction: 0.72).</summary>
    public static readonly IEasingFunction Spring = Freeze(new SpringEase { Damping = 0.72, Oscillations = 1.15 });

    /// <summary>A stiffer spring with no visible bounce, for hover and selection changes.</summary>
    public static readonly IEasingFunction Snappy = Freeze(new SpringEase { Damping = 0.9, Oscillations = 0.8 });

    private static T Freeze<T>(T easing) where T : Freezable { easing.Freeze(); return easing; }
}

/// <summary>Damped-oscillation easing: overshoots the target slightly and settles on it.</summary>
public sealed class SpringEase : EasingFunctionBase
{
    /// <summary>0..1; higher settles faster with less overshoot.</summary>
    public double Damping { get; set; } = 0.72;
    /// <summary>How many half-swings happen over the duration.</summary>
    public double Oscillations { get; set; } = 1.15;

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseInCore is used as written

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var decay = Math.Exp(-Damping * 7.5 * t);
        return 1 - decay * Math.Cos(Oscillations * Math.PI * t);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
