using System.Diagnostics;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>Polls button, pointer and modifiers at ~60 Hz to detect a possible drag and the chosen trigger chord.</summary>
internal sealed class DragMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ModifierChord _chord = new(ModifierChord.DefaultSettleDelay);
    private bool _down, _active;
    private Native.POINT _origin;

    /// <summary>A possible drag began (button down and moved past the drag threshold).</summary>
    public event Action<ScreenPoint>? DragStarted;
    /// <summary>The left button came up after a possible drag.</summary>
    public event Action? DragEnded;
    /// <summary>A modifier chord was chosen during a possible drag, with the current pointer.</summary>
    public event Action<WheelKind, ScreenPoint>? ChordChosen;

    public DragMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Dispose() => _timer.Stop();

    private void Tick()
    {
        try
        {
            var down = Native.IsDown(Native.VK_LBUTTON);
            Native.GetCursorPos(out var p);

            if (!down)
            {
                if (_down)
                {
                    _down = false;
                    if (_active)
                    {
                        _active = false;
                        _chord.Reset();
                        Log.Write("possible drag ended");
                        DragEnded?.Invoke();
                    }
                }
                return;
            }

            if (!_down)
            {
                _down = true;
                _active = false;
                _origin = p;
                _chord.Reset();
            }

            if (!_active)
            {
                var cx = Math.Max(Native.GetSystemMetrics(Native.SM_CXDRAG), 1);
                var cy = Math.Max(Native.GetSystemMetrics(Native.SM_CYDRAG), 1);
                if (Math.Abs(p.X - _origin.X) <= cx && Math.Abs(p.Y - _origin.Y) <= cy) return;
                _active = true;
                Log.Write($"possible drag started at {p.X},{p.Y}");
                DragStarted?.Invoke(new ScreenPoint(p.X, p.Y));
            }

            var mods = Modifiers.None;
            if (Native.IsDown(Native.VK_SHIFT)) mods |= Modifiers.Shift;
            if (Native.IsDown(Native.VK_CONTROL)) mods |= Modifiers.Control;
            if (Native.IsDown(Native.VK_MENU)) mods |= Modifiers.Alt;
            if (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN)) mods |= Modifiers.Win;

            if (_chord.Update(mods, _clock.Elapsed) is { } chosen && TriggerMatcher.Default.WheelFor(chosen) is { } kind)
            {
                Log.Write($"chord chosen: {chosen} -> {kind}");
                ChordChosen?.Invoke(kind, new ScreenPoint(p.X, p.Y));
            }
        }
        catch (Exception ex) { Log.Error("DragMonitor.Tick", ex); }
    }
}
