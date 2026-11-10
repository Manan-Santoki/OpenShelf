using System.Runtime.InteropServices;
using OpenShelf.Core;

namespace OpenShelf.Settings;

/// <summary>Display names for modifiers and virtual keys, shown on keycaps.</summary>
public static class KeyNames
{
    /// <summary>Ctrl, Shift, Alt, Win: in that order, whatever was pressed first.</summary>
    public static IReadOnlyList<string> ModifierNames(Modifiers m)
    {
        var names = new List<string>(4);
        if (m.HasFlag(Modifiers.Control)) names.Add("Ctrl");
        if (m.HasFlag(Modifiers.Shift)) names.Add("Shift");
        if (m.HasFlag(Modifiers.Alt)) names.Add("Alt");
        if (m.HasFlag(Modifiers.Win)) names.Add("Win");
        return names;
    }

    /// <summary>Keycap labels for a shortcut: its modifiers, then its key.</summary>
    public static IReadOnlyList<string> Keycaps(HotKey hotKey) => [.. ModifierNames(hotKey.Modifiers), KeyName(hotKey.VirtualKey)];

    public static string KeyName(int vk)
    {
        switch (vk)
        {
            case 0x08: return "Backspace";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "Page Up";
            case 0x22: return "Page Down";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "Left";
            case 0x26: return "Up";
            case 0x27: return "Right";
            case 0x28: return "Down";
            case 0x2C: return "Print Screen";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
        }
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        var ch = MapVirtualKeyW((uint)vk, 2) & 0x7FFF_FFFF;   // MAPVK_VK_TO_CHAR
        return ch > 32 ? char.ToUpperInvariant((char)ch).ToString() : $"Key {vk:X2}";
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}

/// <summary>
/// Records a modifier combination from successive readings of the held modifiers: the combination is
/// every key that was down since all were last up, and is chosen the moment they are all released
/// (the way the wheels' own trigger chords work), so Shift then Alt records Shift + Alt.
/// </summary>
public sealed class ModifierRecorder
{
    private Modifiers _chord;

    /// <summary>The combination so far, for showing while keys are down.</summary>
    public Modifiers Current => _chord;

    /// <summary>Feeds the modifiers held now; returns the recorded combination once everything is released.</summary>
    public Modifiers? Update(Modifiers held)
    {
        if (held != Modifiers.None) { _chord |= held; return null; }
        if (_chord == Modifiers.None) return null;
        var done = _chord;
        _chord = Modifiers.None;
        return done;
    }

    public void Reset() => _chord = Modifiers.None;
}
