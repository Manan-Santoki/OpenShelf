using OpenShelf.Core;

namespace OpenShelf.Tour;

/// <summary>What a button inside an instruction does.</summary>
public enum InstructionAction { None, ShowInExplorer, SetShortcut }

/// <summary>One line of how-to. <see cref="Text"/> may mark words in **bold**; <see cref="Keys"/> are drawn as keycaps.</summary>
public sealed record TourInstruction(string Text, IReadOnlyList<string> Keys, InstructionAction Action = InstructionAction.None, string? ActionTitle = null)
{
    public TourInstruction(string text) : this(text, []) { }
}

/// <summary>Key names as shown on keycaps: Ctrl, Shift, Alt, in that order (as Settings shows them).</summary>
public static class KeyLabels
{
    public static IReadOnlyList<string> Modifiers(Modifiers m)
    {
        var names = new List<string>(3);
        if (m.HasFlag(Core.Modifiers.Control)) names.Add("Ctrl");
        if (m.HasFlag(Core.Modifiers.Shift)) names.Add("Shift");
        if (m.HasFlag(Core.Modifiers.Alt)) names.Add("Alt");
        return names;
    }

    public static IReadOnlyList<string> HotKey(HotKey hotKey) => [.. Modifiers(hotKey.Modifiers), KeyName(hotKey.VirtualKey)];

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
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
        }
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return $"Key {vk:X2}";
    }
}

/// <summary>What each step says, with the keys this person has set in Settings.</summary>
public static class TourInstructions
{
    public static IReadOnlyList<TourInstruction> Steps(TourStep step, IReadOnlyDictionary<WheelKind, Modifiers> triggers, HotKey? hotKey)
    {
        var name = step.File() is { } file ? $"**{FileName(file)}**" : "";
        switch (step)
        {
            case TourStep.Keyboard:
                var press = hotKey is { } hk
                    ? new TourInstruction("Press the shortcut. The Convert wheel opens at the pointer.", KeyLabels.HotKey(hk))
                    : new TourInstruction("Set a shortcut to open the wheel, then press it.", [], InstructionAction.SetShortcut, "Set a Shortcut…");
                return
                [
                    new TourInstruction($"Select {name} in Explorer (or files on a shelf).", [], InstructionAction.ShowInExplorer, "Show It in Explorer"),
                    press,
                    new TourInstruction("Use the arrow keys and **Enter** to run a wedge, or **Tab** to switch wheels. **Esc** closes."),
                ];
            case TourStep.Options:
                return
                [
                    new TourInstruction($"Drag {name} and keep holding."),
                    new TourInstruction("Tap the Convert keys.", Keys(WheelKind.Convert, triggers)),
                    new TourInstruction($"Hold **Alt** as you drop on **{step.Target()}**, and choose the quality and size for this one conversion."),
                ];
            default:
                if (step.Wheel() is not { } wheel) return [];
                return
                [
                    new TourInstruction($"Drag {name} from here and keep holding the mouse button."),
                    new TourInstruction("While you’re dragging, tap these keys. No need to keep holding them.", Keys(wheel, triggers)),
                    new TourInstruction($"Drop on **{step.Target()}**."),
                ];
        }
    }

    /// <summary>The keys that open <paramref name="kind"/>'s wheel, as keycap labels.</summary>
    public static IReadOnlyList<string> Keys(WheelKind kind, IReadOnlyDictionary<WheelKind, Modifiers> triggers) =>
        triggers.TryGetValue(kind, out var m) ? KeyLabels.Modifiers(m) : [];

    /// <summary>"Shift + Alt".</summary>
    public static string Describe(WheelKind kind, IReadOnlyDictionary<WheelKind, Modifiers> triggers) =>
        string.Join(" + ", Keys(kind, triggers));

    public static string? Tip(TourStep step) => step switch
    {
        TourStep.Convert => "Results are saved next to the original, and nothing is ever overwritten. Arrow keys and Enter work too, while you’re still dragging; Esc cancels.",
        TourStep.Markdown => "Headings, lists, tables, code and footnotes come out typeset. Try DOCX or HTML too.",
        TourStep.Tools => "Tools open a small editor that saves a copy, like “Mountain Lake Cropped.jpg”. Drag several photos to edit them all at once.",
        TourStep.Pdf => "Drag a PDF and an image together to Combine them into one PDF.",
        TourStep.Video => "Tap the Convert keys instead to make a GIF, or an MP4 from the clip.",
        TourStep.Shelf => "Drag files out of a shelf into any app. Space previews them, and a shelf can dock to the edge of the screen.",
        TourStep.Keyboard => "Press a number key, 1 to 7, to run that wedge, and 0 for More.",
        TourStep.Options => "Settings → Output sets the quality, size and codec for every conversion.",
        _ => null,
    };

    public static string Success(TourStep step) => step switch
    {
        TourStep.Convert => "Nicely done! The new file is next to the original.",
        TourStep.Markdown => "There’s your PDF, properly typeset.",
        TourStep.Tools => "That’s the Tools wheel. Save in the editor to keep a copy.",
        TourStep.Pdf => "PDFs handled.",
        TourStep.Video => "Clip handled.",
        TourStep.Shelf => "Your files are on a shelf. Drag them out whenever you’re ready.",
        TourStep.Keyboard => "That’s the wheel without a drag.",
        TourStep.Options => "Those settings are for this conversion only.",
        _ => "",
    };

    /// <summary>What the status banner says: waiting, the wheel open, or done.</summary>
    public static string Status(TourStep step, bool isDone, bool isUnderWay)
    {
        if (isDone) return Success(step);
        if (isUnderWay && step.Wheel() is { } wheel)
            return $"The {TourSteps.WheelTitle(wheel)} wheel is open. Now drop on **{step.Target()}**.";
        return "Waiting for you to try it…";
    }

    public static string FileName(PracticeFile file) => file switch
    {
        PracticeFile.Photo => "Mountain Lake.jpg",
        PracticeFile.Notes => "Trip Notes.md",
        PracticeFile.Report => "Field Report.pdf",
        PracticeFile.Clip => "Sunset.avi",
        _ => "Letter.txt",
    };

    /// <summary>What a practice file is, under its name in the tray.</summary>
    public static string Caption(PracticeFile file) => file switch
    {
        PracticeFile.Photo => "Photo",
        PracticeFile.Notes => "Markdown",
        PracticeFile.Report => "3-page PDF",
        PracticeFile.Clip => "4-second video",
        _ => "Plain text",
    };

    /// <summary>"Welcome to OpenShelf", or the title for people who saw an earlier welcome guide.</summary>
    public static string WelcomeTitle(bool returning) => returning ? "New: Learn OpenShelf Hands-On" : "Welcome to OpenShelf";

    public static string FinishSummary(int done, int total) => done == total
        ? "You’ve tried everything. OpenShelf lives in the notification area; drag any file and tap a key whenever you need it."
        : $"You’ve tried {done} of {total}. The rest are waiting below, and the tour is in the tray menu whenever you want it.";
}
