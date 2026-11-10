namespace OpenShelf.Wheel;

/// <summary>
/// Detects a shake of the pointer during a possible drag: at least <see cref="Reversals"/> horizontal direction
/// reversals, each after travelling <see cref="MinTravel"/> physical pixels, within <see cref="Window"/>.
/// </summary>
public sealed class ShakeDetector
{
    public int Reversals { get; init; } = 3;
    public int MinTravel { get; init; } = 40;
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(600);

    private int _direction;          // 0 = not yet moving far enough, +1 right, -1 left
    private int _extreme;
    private bool _started;
    private readonly List<TimeSpan> _turns = [];

    public void Reset() { _started = false; _direction = 0; _turns.Clear(); }

    /// <summary>Feed the pointer's x at <paramref name="now"/>; true when a shake has just completed.</summary>
    public bool Update(int x, TimeSpan now)
    {
        if (!_started) { _started = true; _extreme = x; _direction = 0; return false; }
        if (_direction == 0)
        {
            if (x - _extreme >= MinTravel) { _direction = 1; _extreme = x; }
            else if (_extreme - x >= MinTravel) { _direction = -1; _extreme = x; }
            return false;
        }
        if (_direction > 0 ? x > _extreme : x < _extreme) { _extreme = x; return false; }
        if (Math.Abs(_extreme - x) < MinTravel) return false;

        _direction = -_direction;
        _extreme = x;
        _turns.Add(now);
        _turns.RemoveAll(t => now - t > Window);
        if (_turns.Count < Reversals) return false;
        _turns.Clear();
        return true;
    }
}
