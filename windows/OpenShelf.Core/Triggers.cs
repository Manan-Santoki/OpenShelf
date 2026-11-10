namespace OpenShelf.Core;

public enum WheelKind { Convert, Tools, Actions }

/// <summary>Trigger keys. Win is tracked so a chord containing it never matches by accident.</summary>
[Flags]
public enum Modifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Win = 8 }

/// <summary>Maps the currently held modifier keys to the wheel they trigger. Port of TriggerMatcher.swift.</summary>
public sealed class TriggerMatcher(IReadOnlyDictionary<WheelKind, Modifiers> triggers)
{
    public static TriggerMatcher Default { get; } = new(new Dictionary<WheelKind, Modifiers>
    {
        [WheelKind.Convert] = Modifiers.Shift,
        [WheelKind.Tools] = Modifiers.Shift | Modifiers.Alt,
        [WheelKind.Actions] = Modifiers.Control,
    });

    /// <summary>The wheel whose trigger exactly equals the held modifiers, or null.</summary>
    public WheelKind? WheelFor(Modifiers held)
    {
        if (held == Modifiers.None) return null;
        foreach (var pair in triggers)
            if (pair.Value == held) return pair.Key;
        return null;
    }
}

/// <summary>
/// Turns modifier readings taken during a drag into the combinations that should
/// open a wheel. A quick tap counts the moment a key is let go, with every key
/// that was down; a held combination counts once it has been steady for
/// <see cref="SettleDelay"/>, so pressing Shift on the way to Shift+Alt doesn't
/// flash Convert. Port of ModifierChord in TriggerMatcher.swift.
/// </summary>
public sealed class ModifierChord(TimeSpan settleDelay)
{
    /// <summary>The Mac app's value.</summary>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(70);

    public TimeSpan SettleDelay { get; } = settleDelay;

    private Modifiers _chord;      // every key held since all were last up
    private Modifiers _held;
    private TimeSpan? _heldSince;
    private Modifiers? _lastReported;

    /// <summary>Records the keys held at <paramref name="now"/> (any monotonic clock); returns a combination when one is chosen.</summary>
    public Modifiers? Update(Modifiers flags, TimeSpan now)
    {
        Modifiers? chosen = null;
        if (flags != _held)
        {
            var didRelease = (_held & ~flags) != 0;
            _held = flags;
            _heldSince = now;
            if (didRelease && _chord != Modifiers.None && _chord != _lastReported) chosen = _chord;
            _chord |= flags;
        }
        else if (_held != Modifiers.None && _held == _chord && _held != _lastReported
                 && _heldSince is { } since && now - since >= SettleDelay)
        {
            chosen = _held;
        }
        if (chosen is not null) _lastReported = chosen;
        if (flags == Modifiers.None)
        {
            _chord = Modifiers.None;
            _lastReported = null;
        }
        return chosen;
    }

    public void Reset()
    {
        _chord = Modifiers.None; _held = Modifiers.None; _heldSince = null; _lastReported = null;
    }
}
