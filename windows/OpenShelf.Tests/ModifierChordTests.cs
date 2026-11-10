using OpenShelf.Core;

namespace OpenShelf.Tests;

public class ModifierChordTests
{
    private const Modifiers None = Modifiers.None;
    private const Modifiers Shift = Modifiers.Shift;
    private const Modifiers Alt = Modifiers.Alt;

    /// <summary>Feeds (milliseconds, flags) readings and collects what was chosen.</summary>
    private static List<Modifiers> Run(params (int Ms, Modifiers Flags)[] readings)
    {
        var chord = new ModifierChord(TimeSpan.FromMilliseconds(70));
        var chosen = new List<Modifiers>();
        foreach (var (ms, flags) in readings)
            if (chord.Update(flags, TimeSpan.FromMilliseconds(ms)) is { } m) chosen.Add(m);
        return chosen;
    }

    [Fact] public void QuickTapCountsOnRelease() =>
        Assert.Equal([Shift], Run((0, None), (25, Shift), (50, Shift), (75, None)));

    [Fact] public void HeldKeyCountsAfterSettling() =>
        Assert.Equal([Shift], Run((0, Shift), (25, Shift), (50, Shift), (75, Shift), (100, Shift)));

    [Fact] public void HeldKeyIsNotRepeatedOnRelease() =>
        Assert.Equal([Shift], Run((0, Shift), (100, Shift), (125, None)));

    [Fact] public void ShiftOnTheWayToShiftAltDoesNotFlashConvert() =>
        Assert.Equal([Shift | Alt], Run((0, Shift), (30, Shift | Alt), (60, Shift | Alt), (110, Shift | Alt)));

    [Fact] public void TappedChordCountsWhenTheFirstKeyIsReleased() =>
        Assert.Equal([Shift | Alt], Run((0, Shift), (20, Shift | Alt), (50, Alt), (60, None)));

    [Fact] public void AddingAKeyToAHeldOneSwitches() =>
        Assert.Equal([Shift, Shift | Alt], Run((0, Shift), (100, Shift), (125, Shift | Alt), (200, Shift | Alt)));

    [Fact] public void ReleasingPartOfAChordDoesNotSwitchBack() =>
        Assert.Equal([Shift | Alt], Run((0, Shift | Alt), (100, Shift | Alt), (125, Shift), (300, Shift), (325, None)));

    [Fact] public void ANewTapAfterReleasingCountsAgain() =>
        Assert.Equal([Shift, Shift], Run((0, Shift), (100, Shift), (125, None), (150, Shift), (175, None)));

    [Fact] public void ResetForgetsTheChord()
    {
        var chord = new ModifierChord(TimeSpan.FromMilliseconds(70));
        chord.Update(Shift, TimeSpan.FromMilliseconds(0));
        chord.Reset();
        Assert.Null(chord.Update(None, TimeSpan.FromMilliseconds(25)));
    }
}

public class TriggerMatcherTests
{
    [Fact] public void DefaultsMapExactChords()
    {
        var m = TriggerMatcher.Default;
        Assert.Equal(WheelKind.Convert, m.WheelFor(Modifiers.Shift));
        Assert.Equal(WheelKind.Tools, m.WheelFor(Modifiers.Shift | Modifiers.Alt));
        Assert.Equal(WheelKind.Actions, m.WheelFor(Modifiers.Control));
    }

    [Fact] public void NoneAndUnmappedAndSupersetsMatchNothing()
    {
        var m = TriggerMatcher.Default;
        Assert.Null(m.WheelFor(Modifiers.None));
        Assert.Null(m.WheelFor(Modifiers.Alt));
        Assert.Null(m.WheelFor(Modifiers.Shift | Modifiers.Win));
        Assert.Null(m.WheelFor(Modifiers.Shift | Modifiers.Control));
    }

    [Fact] public void CustomTriggersAreHonoured()
    {
        var m = new TriggerMatcher(new Dictionary<WheelKind, Modifiers> { [WheelKind.Actions] = Modifiers.Alt });
        Assert.Equal(WheelKind.Actions, m.WheelFor(Modifiers.Alt));
        Assert.Null(m.WheelFor(Modifiers.Shift));
    }
}
