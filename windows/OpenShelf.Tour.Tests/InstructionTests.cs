using OpenShelf.Core;

namespace OpenShelf.Tour.Tests;

public class InstructionTests
{
    private static readonly IReadOnlyDictionary<WheelKind, Modifiers> Defaults = new FakePreferences().Triggers;

    [Fact]
    public void Wheel_steps_show_the_keys_that_are_set_not_fixed_ones()
    {
        var custom = new Dictionary<WheelKind, Modifiers>(Defaults)
        {
            [WheelKind.Convert] = Modifiers.Control | Modifiers.Shift,
            [WheelKind.Tools] = Modifiers.Alt,
        };
        Assert.Equal(new[] { "Ctrl", "Shift" }, TourInstructions.Steps(TourStep.Convert, custom, null)[1].Keys);
        Assert.Equal(new[] { "Alt" }, TourInstructions.Steps(TourStep.Tools, custom, null)[1].Keys);
        Assert.Equal(new[] { "Ctrl" }, TourInstructions.Steps(TourStep.Shelf, Defaults, null)[1].Keys);
        Assert.Equal(new[] { "Shift", "Alt" }, TourInstructions.Steps(TourStep.Pdf, Defaults, null)[1].Keys);
    }

    [Fact]
    public void Key_names_are_in_Ctrl_Shift_Alt_order_and_never_mention_Win()
    {
        Assert.Equal(new[] { "Ctrl", "Shift", "Alt" }, KeyLabels.Modifiers(Modifiers.Alt | Modifiers.Shift | Modifiers.Control));
        Assert.Equal(new[] { "Ctrl", "Alt", "Space" }, KeyLabels.HotKey(new HotKey(Modifiers.Control | Modifiers.Alt, 0x20)));
        Assert.DoesNotContain("Win", KeyLabels.Modifiers(Modifiers.Win | Modifiers.Shift));
        Assert.Equal("F5", KeyLabels.KeyName(0x74));
        Assert.Equal("K", KeyLabels.KeyName(0x4B));
    }

    [Fact]
    public void Text_uses_Windows_words()
    {
        var all = string.Join("\n",
            TourSteps.All.SelectMany(s => TourInstructions.Steps(s, Defaults, new HotKey(Modifiers.Control, 0x20)).Select(i => i.Text))
                .Concat(TourSteps.All.Select(s => s.Summary()))
                .Concat(TourSteps.All.Select(s => TourInstructions.Tip(s) ?? "")));
        Assert.DoesNotContain("Finder", all);
        Assert.DoesNotContain("menu bar", all);
        Assert.DoesNotContain("⌥", all);
        Assert.DoesNotContain("⌘", all);
        Assert.DoesNotContain("Option", all);
        Assert.Contains("Explorer", all);
    }

    [Fact]
    public void Keyboard_step_shows_the_shortcut_or_offers_to_set_one()
    {
        var withKey = TourInstructions.Steps(TourStep.Keyboard, Defaults, new HotKey(Modifiers.Control | Modifiers.Alt, 0x20));
        Assert.Equal(3, withKey.Count);
        Assert.Equal(InstructionAction.ShowInExplorer, withKey[0].Action);
        Assert.Equal(new[] { "Ctrl", "Alt", "Space" }, withKey[1].Keys);

        var without = TourInstructions.Steps(TourStep.Keyboard, Defaults, null);
        Assert.Empty(without[1].Keys);
        Assert.Equal(InstructionAction.SetShortcut, without[1].Action);
    }

    [Fact]
    public void Options_step_asks_for_Alt_on_the_drop()
    {
        var steps = TourInstructions.Steps(TourStep.Options, Defaults, null);
        Assert.Equal(new[] { "Shift" }, steps[1].Keys);
        Assert.Contains("**Alt**", steps[2].Text);
        Assert.Contains("MP4 or GIF", steps[2].Text);
    }

    [Fact]
    public void Status_goes_from_waiting_to_under_way_to_done()
    {
        Assert.Equal("Waiting for you to try it…", TourInstructions.Status(TourStep.Convert, false, false));
        Assert.Equal("The Convert wheel is open. Now drop on **PNG**.", TourInstructions.Status(TourStep.Convert, false, true));
        Assert.Equal("Nicely done! The new file is next to the original.", TourInstructions.Status(TourStep.Convert, true, true));
    }

    [Fact]
    public void Finish_summary_counts_what_was_tried()
    {
        Assert.StartsWith("You’ve tried 3 of 8.", TourInstructions.FinishSummary(3, 8));
        Assert.StartsWith("You’ve tried everything.", TourInstructions.FinishSummary(8, 8));
    }
}
