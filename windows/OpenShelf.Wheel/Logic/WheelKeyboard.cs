using OpenShelf.Core;

namespace OpenShelf.Wheel;

public enum StepDirection { Clockwise, CounterClockwise }

/// <summary>Moves a wheel's highlight one wedge at a time, wrapping around. Port of WheelKeyboardNavigator.</summary>
public static class WheelKeyboardNavigator
{
    /// <summary>The wedge after one step from <paramref name="current"/>, passing over unselectable ones. With nothing
    /// highlighted, clockwise starts at wedge 0 and counter-clockwise at the last. Null when nothing can be chosen.</summary>
    public static int? Step(int? current, int count, StepDirection direction, Func<int, bool>? isSelectable = null)
    {
        if (count <= 0) return null;
        var offset = direction == StepDirection.Clockwise ? 1 : -1;
        var index = direction == StepDirection.Clockwise ? -1 : count;
        if (current is { } c && c >= 0 && c < count) index = c;
        for (var i = 0; i < count; i++)
        {
            index = (index + offset + count) % count;
            if (isSelectable?.Invoke(index) ?? true) return index;
        }
        return null;
    }

    public static int? Step(int? current, IReadOnlyList<WheelSlot> slots, StepDirection direction) =>
        Step(current, slots.Count, direction, i => !slots[i].IsEmpty);
}

/// <summary>Turns "key is down" readings into presses with auto-repeat: one press on key-down, then repeats after
/// <see cref="InitialDelay"/>, every <see cref="Interval"/>, while it stays down. Port of KeyRepeat.</summary>
public sealed class KeyRepeat
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(350);
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(120);

    private TimeSpan? _pressedAt, _lastFiredAt;

    public bool Update(bool isDown, TimeSpan now)
    {
        if (!isDown) { _pressedAt = null; _lastFiredAt = null; return false; }
        if (_pressedAt is not { } pressed || _lastFiredAt is not { } last)
        {
            _pressedAt = now; _lastFiredAt = now;
            return true;
        }
        var wait = last == pressed ? InitialDelay : Interval;
        if (now - last < wait) return false;
        _lastFiredAt = now;
        return true;
    }
}

/// <summary>Number keys: a page holds at most seven wedges, so entries are 1 to 7 clockwise from 12 o'clock and More or Back is 0.</summary>
public static class WheelBadges
{
    public static IReadOnlyDictionary<int, string> Keys(IReadOnlyList<WheelSlot> slots)
    {
        var keys = new Dictionary<int, string>();
        var number = 1;
        for (var i = 0; i < slots.Count; i++)
        {
            switch (slots[i].Kind)
            {
                case SlotKind.Item: keys[i] = (number++).ToString(); break;
                case SlotKind.More or SlotKind.Back: keys[i] = "0"; break;
            }
        }
        return keys;
    }

    /// <summary>The wedge a digit picks, or null.</summary>
    public static int? SlotForKey(string key, IReadOnlyList<WheelSlot> slots) =>
        Keys(slots).Where(p => p.Value == key).Select(p => (int?)p.Key).FirstOrDefault();
}

/// <summary>The wheels a keyboard session can show, and how Tab moves between them.</summary>
public static class WheelCycle
{
    public static readonly WheelKind[] Order = [WheelKind.Convert, WheelKind.Tools, WheelKind.Actions];

    public static List<WheelKind> Available(Func<WheelKind, bool> hasItems) => Order.Where(hasItems).ToList();

    public static WheelKind? Next(WheelKind kind, IReadOnlyList<WheelKind> available, bool backwards = false)
    {
        var index = -1;
        for (var i = 0; i < available.Count; i++) if (available[i] == kind) index = i;
        if (available.Count <= 1 || index < 0) return null;
        return available[(index + (backwards ? -1 : 1) + available.Count) % available.Count];
    }

    public static string Title(WheelKind kind) => kind switch
    {
        WheelKind.Convert => "Convert", WheelKind.Tools => "Tools", _ => "Actions",
    };
}

/// <summary>Recognises a tap of modifier keys (pressed and released with no other key between), e.g. Shift+Alt to switch to
/// the Tools wheel while it is open. Port of ModifierTapTracker.</summary>
public sealed class ModifierTapTracker
{
    private const Modifiers Relevant = Modifiers.Shift | Modifiers.Control | Modifiers.Alt;
    private Modifiers _pressed;
    private bool _interrupted;
    private Modifiers _heldOver;

    /// <param name="alreadyHeld">Keys down when tracking began, such as the Ctrl+Alt of the shortcut; letting go of them is not a tap.</param>
    public ModifierTapTracker(Modifiers alreadyHeld = Modifiers.None) => _heldOver = alreadyHeld & Relevant;

    /// <summary>Feed every modifier change; returns the combination tapped when the last key comes up.</summary>
    public Modifiers? FlagsChanged(Modifiers flags)
    {
        flags &= Relevant;
        _heldOver &= flags;
        if (_heldOver != Modifiers.None) _interrupted = true;
        if (flags != Modifiers.None) { _pressed |= flags; return null; }
        var result = _interrupted || _pressed == Modifiers.None ? (Modifiers?)null : _pressed;
        _pressed = Modifiers.None;
        _interrupted = false;
        return result;
    }

    /// <summary>A regular key while modifiers are held means a shortcut, not a tap.</summary>
    public void KeyPressed()
    {
        if (_pressed != Modifiers.None) _interrupted = true;
    }

    /// <summary>The keys of <paramref name="flags"/> pressed on purpose (not held over from before).</summary>
    public Modifiers Chosen(Modifiers flags) => (flags & Relevant) & ~_heldOver;
}

/// <summary>What a key does on an open wheel.</summary>
public enum WheelKeyCommand { None, CounterClockwise, Clockwise, Run, Digit, NextWheel, PreviousWheel, Close }

public static class WheelKeys
{
    public const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    /// <summary>Classifies a virtual-key code. Digits are the top row and the numpad (0 to 7); <paramref name="digit"/> is the number.
    /// Tab and Esc only count with <paramref name="withoutDrag"/>: mid-drag Esc must reach the drag source.</summary>
    public static WheelKeyCommand Classify(int vk, bool shift, bool withoutDrag, out int digit)
    {
        digit = -1;
        if (vk is >= 0x30 and <= 0x37) { digit = vk - 0x30; return WheelKeyCommand.Digit; }
        if (vk is >= 0x60 and <= 0x67) { digit = vk - 0x60; return WheelKeyCommand.Digit; }
        switch (vk)
        {
            case VK_LEFT or VK_UP: return WheelKeyCommand.CounterClockwise;
            case VK_RIGHT or VK_DOWN: return WheelKeyCommand.Clockwise;
            case VK_RETURN: return WheelKeyCommand.Run;
            case VK_TAB when withoutDrag: return shift ? WheelKeyCommand.PreviousWheel : WheelKeyCommand.NextWheel;
            case VK_ESCAPE when withoutDrag: return WheelKeyCommand.Close;
        }
        return WheelKeyCommand.None;
    }
}

/// <summary>What the label above a keyboard wheel says as it opens (Windows key names).</summary>
public static class KeyboardWheelHint
{
    public const string Options = "Alt+Return for options";

    public static string Opening(bool canSwitch, bool hasOptions) => (canSwitch, hasOptions) switch
    {
        (true, false) => "Arrows choose · Return runs · Tab switches wheel",
        (false, false) => "Arrows choose · Return runs",
        (true, true) => $"Return runs · {Options} · Tab switches wheel",
        (false, true) => $"Arrows choose · Return runs · {Options}",
    };
}
