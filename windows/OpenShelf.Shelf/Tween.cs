using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>A per-frame tween for things WPF animations can't drive directly (window positions, scroll offsets,
/// the card rectangle). Call <see cref="Cancel"/> to stop; the completion callback then does not run.</summary>
internal sealed class Tween
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly double _ms;
    readonly IEasingFunction _ease;
    readonly Action<double> _step;
    readonly Action? _done;
    bool _running;

    Tween(double ms, IEasingFunction ease, Action<double> step, Action? done)
    {
        _ms = ms; _ease = ease; _step = step; _done = done;
    }

    /// <summary>Runs <paramref name="step"/> with eased progress 0..1 over <paramref name="ms"/>. With motion off the
    /// end state is applied at once.</summary>
    public static Tween Run(double ms, IEasingFunction? ease, Action<double> step, Action? done = null, bool respectMotion = true)
    {
        var tween = new Tween(ms, ease ?? Motion.EaseInOut, step, done);
        if ((respectMotion && !Motion.Enabled) || ms <= 0)
        {
            step(1);
            done?.Invoke();
            return tween;
        }
        tween._running = true;
        step(0);
        CompositionTarget.Rendering += tween.OnFrame;
        return tween;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!_running) return;
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _ms);
        try { _step(_ease.Ease(t)); }
        catch (Exception ex) { Log.Error("Tween step", ex); Stop(); return; }
        if (t < 1) return;
        Stop();
        try { _done?.Invoke(); }
        catch (Exception ex) { Log.Error("Tween done", ex); }
    }

    void Stop()
    {
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    public bool IsRunning => _running;

    public void Cancel() => Stop();

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
