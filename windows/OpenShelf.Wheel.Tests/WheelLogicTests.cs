using OpenShelf.Core;
using OpenShelf.Wheel;

namespace OpenShelf.Wheel.Tests;

public class WheelPagingTests
{
    private static List<WheelSlot> Items(int from, int to) => Enumerable.Range(from, to - from).Select(WheelSlot.ForItem).ToList();

    [Fact]
    public void SevenItemsFitOnOnePage()
    {
        Assert.Equal(1, WheelPaging.PageCount(7));
        Assert.Equal(Items(0, 7), WheelPaging.Slots(7, 0));
    }

    [Fact]
    public void EightItemsMakeTwoPagesWithBackWhereMoreWas()
    {
        Assert.Equal(2, WheelPaging.PageCount(8));
        Assert.Equal(Items(0, 6).Append(WheelSlot.More(1, 2)), WheelPaging.Slots(8, 0));
        Assert.Equal(Items(6, 8).Concat(Enumerable.Repeat(WheelSlot.Empty, 4)).Append(WheelSlot.Back(0, 2)), WheelPaging.Slots(8, 1));
    }

    [Fact]
    public void ElevenItemsLeaveOneEmptyWedge()
    {
        Assert.Equal(Items(0, 6).Append(WheelSlot.More(1, 2)), WheelPaging.Slots(11, 0));
        Assert.Equal(Items(6, 11).Append(WheelSlot.Empty).Append(WheelSlot.Back(0, 2)), WheelPaging.Slots(11, 1));
    }

    [Fact]
    public void TwelveItemsFillBothPages()
    {
        Assert.Equal(Items(6, 12).Append(WheelSlot.Back(0, 2)), WheelPaging.Slots(12, 1));
    }

    [Fact]
    public void LongerWheelsHaveMoreOnEveryPageButTheLast()
    {
        Assert.Equal(3, WheelPaging.PageCount(14));
        Assert.Equal(WheelSlot.More(1, 3), WheelPaging.Slots(14, 0)[^1]);
        Assert.Equal(WheelSlot.More(2, 3), WheelPaging.Slots(14, 1)[^1]);
        Assert.Equal(Items(12, 14).Concat(Enumerable.Repeat(WheelSlot.Empty, 4)).Append(WheelSlot.Back(0, 3)), WheelPaging.Slots(14, 2));
        Assert.Equal(WheelPaging.SlotCount, WheelPaging.Slots(14, 2).Count);
    }

    [Fact]
    public void EmptyWheelHasNoSlots()
    {
        Assert.Equal(1, WheelPaging.PageCount(0));
        Assert.Empty(WheelPaging.Slots(0, 0));
    }

    [Fact]
    public void PageTurnSlotsDescribeTheirPage()
    {
        Assert.Equal("More · 2 of 2", WheelSlot.More(1, 2).PageLabel);
        Assert.Equal("Back · 1 of 2", WheelSlot.Back(0, 2).PageLabel);
        Assert.Null(WheelSlot.Empty.PageLabel);
        Assert.Null(WheelSlot.ForItem(0).PageLabel);
    }

    [Fact]
    public void PageIsClamped()
    {
        Assert.Equal(WheelPaging.Slots(8, 1), WheelPaging.Slots(8, 9));
        Assert.Equal(WheelPaging.Slots(8, 0), WheelPaging.Slots(8, -3));
    }

    // y grows downwards in WheelGeometry.WedgeAt; a point at `degrees` clockwise from 12 o'clock.
    private static (double x, double y) Point(double degrees, double distance = 90)
    {
        var r = degrees * Math.PI / 180;
        return (200 + distance * Math.Sin(r), 200 - distance * Math.Cos(r));
    }

    [Fact]
    public void MoreSitsInTheLastWedgeOfSeven()
    {
        var g = new WheelGeometry(WheelPaging.SlotCount, 50, 130);
        var (x, y) = Point(360.0 * WheelPaging.PageTurnSlot / 7);
        Assert.Equal(WheelPaging.PageTurnSlot, g.WedgeAt(x, y, 200, 200));
        // about 10 o'clock
        Assert.InRange(360.0 * WheelPaging.PageTurnSlot / 7, 300, 320);
    }

    [Fact]
    public void SevenSlotGeometry()
    {
        var g = new WheelGeometry(7, 50, 130);
        for (var slot = 0; slot < 7; slot++)
        {
            var (x, y) = Point(slot * 360.0 / 7);
            Assert.Equal(slot, g.WedgeAt(x, y, 200, 200));
        }
        const double half = 180.0 / 7;
        Assert.Equal(0, g.WedgeAt(Point(half - 1).x, Point(half - 1).y, 200, 200));
        Assert.Equal(1, g.WedgeAt(Point(half + 1).x, Point(half + 1).y, 200, 200));
        Assert.Equal(0, g.WedgeAt(Point(360 - half + 1).x, Point(360 - half + 1).y, 200, 200));
        Assert.Equal(6, g.WedgeAt(Point(360 - half - 1).x, Point(360 - half - 1).y, 200, 200));
        Assert.Null(g.WedgeAt(Point(300, 30).x, Point(300, 30).y, 200, 200));
        Assert.Null(g.WedgeAt(Point(300, 140).x, Point(300, 140).y, 200, 200));
    }
}

public class PageTurnDwellTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void TurnsAfterResting350Milliseconds()
    {
        var d = new PageTurnDwell();
        Assert.False(d.Update(true, Ms(0)));
        Assert.True(d.IsPending);
        Assert.False(d.Update(true, Ms(340)));
        Assert.True(d.Update(true, Ms(350)));
        Assert.False(d.IsPending);
    }

    [Fact]
    public void RestingOnBackDoesNotTurnAgainUntilThePointerLeaves()
    {
        var d = new PageTurnDwell();
        Assert.False(d.Update(true, Ms(0)));
        Assert.True(d.Update(true, Ms(350)));
        Assert.False(d.Update(true, Ms(800)));
        Assert.False(d.Update(true, Ms(2000)));
        Assert.False(d.Update(false, Ms(2100)));
        Assert.False(d.Update(true, Ms(2200)));
        Assert.False(d.Update(true, Ms(2540)));
        Assert.True(d.Update(true, Ms(2550)));
    }

    [Fact]
    public void LeavingCancels()
    {
        var d = new PageTurnDwell();
        Assert.False(d.Update(true, Ms(0)));
        Assert.False(d.Update(false, Ms(200)));
        Assert.False(d.IsPending);
        Assert.False(d.Update(true, Ms(300)));
        Assert.False(d.Update(true, Ms(640)));
        Assert.True(d.Update(true, Ms(650)));
    }

    [Fact]
    public void TurningByKeyWhileRestingWaitsForTheNextVisit()
    {
        var d = new PageTurnDwell();
        Assert.False(d.Update(true, Ms(0)));
        d.PageDidTurn(true, Ms(100));
        Assert.False(d.IsPending);
        Assert.False(d.Update(true, Ms(600)));
        Assert.False(d.Update(false, Ms(700)));
        Assert.False(d.Update(true, Ms(800)));
        Assert.True(d.Update(true, Ms(1150)));
    }

    [Fact]
    public void TurningByKeyElsewhereLeavesTheDwellReady()
    {
        var d = new PageTurnDwell();
        d.PageDidTurn(false, Ms(0));
        Assert.False(d.Update(true, Ms(100)));
        Assert.True(d.Update(true, Ms(450)));
    }
}

public class WheelOrderingTests
{
    [Fact]
    public void UnlistedElementsFollowInTheirOriginalOrder() =>
        Assert.Equal([2, 1, 5, 9, 7], WheelOrdering.Ordered([5, 1, 9, 2, 7], [2, 1]));

    [Fact]
    public void NothingIsLostAndDuplicatesInPriorityUseTheFirstRank() =>
        Assert.Equal(["b", "a", "c"], WheelOrdering.Ordered(["a", "b", "c"], ["b", "a", "b"]));
}

public class WheelKeyboardTests
{
    [Fact]
    public void FirstPressPicksAnEndOfTheWheel()
    {
        Assert.Equal(0, WheelKeyboardNavigator.Step(null, 7, StepDirection.Clockwise));
        Assert.Equal(6, WheelKeyboardNavigator.Step(null, 7, StepDirection.CounterClockwise));
    }

    [Fact]
    public void StepsAndWrapsAround()
    {
        Assert.Equal(3, WheelKeyboardNavigator.Step(2, 7, StepDirection.Clockwise));
        Assert.Equal(0, WheelKeyboardNavigator.Step(6, 7, StepDirection.Clockwise));
        Assert.Equal(6, WheelKeyboardNavigator.Step(0, 7, StepDirection.CounterClockwise));
    }

    [Fact]
    public void EmptyWheelOrStaleIndex()
    {
        Assert.Null(WheelKeyboardNavigator.Step(null, 0, StepDirection.Clockwise));
        Assert.Equal(0, WheelKeyboardNavigator.Step(9, 4, StepDirection.Clockwise));
    }

    [Fact]
    public void ArrowsReachMoreOnTheFirstPage()
    {
        var first = WheelPaging.Slots(8, 0);
        Assert.Equal(6, WheelKeyboardNavigator.Step(5, first, StepDirection.Clockwise));
        Assert.Equal(0, WheelKeyboardNavigator.Step(6, first, StepDirection.Clockwise));
        Assert.Equal(6, WheelKeyboardNavigator.Step(null, first, StepDirection.CounterClockwise));
    }

    [Fact]
    public void ArrowsSkipEmptyWedges()
    {
        var second = WheelPaging.Slots(8, 1);
        Assert.Equal(6, WheelKeyboardNavigator.Step(1, second, StepDirection.Clockwise));
        Assert.Equal(1, WheelKeyboardNavigator.Step(6, second, StepDirection.CounterClockwise));
        Assert.Equal(0, WheelKeyboardNavigator.Step(6, second, StepDirection.Clockwise));
        Assert.Equal(6, WheelKeyboardNavigator.Step(0, second, StepDirection.CounterClockwise));
    }

    [Fact]
    public void NothingSelectableMeansNoStep() =>
        Assert.Null(WheelKeyboardNavigator.Step(null, [WheelSlot.Empty, WheelSlot.Empty], StepDirection.Clockwise));

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void KeyRepeatFiresOncePerPress()
    {
        var r = new KeyRepeat();
        Assert.True(r.Update(true, Ms(0)));
        Assert.False(r.Update(true, Ms(25)));
        Assert.False(r.Update(false, Ms(50)));
        Assert.True(r.Update(true, Ms(75)));
    }

    [Fact]
    public void KeyRepeatRepeatsAfterInitialDelayThenAtInterval()
    {
        var r = new KeyRepeat();
        Assert.True(r.Update(true, Ms(0)));
        Assert.False(r.Update(true, Ms(340)));
        Assert.True(r.Update(true, Ms(360)));
        Assert.False(r.Update(true, Ms(470)));
        Assert.True(r.Update(true, Ms(490)));
        Assert.True(r.Update(true, Ms(615)));
    }

    [Fact]
    public void ReleasingResetsTheDelay()
    {
        var r = new KeyRepeat();
        Assert.True(r.Update(true, Ms(0)));
        Assert.True(r.Update(true, Ms(400)));
        Assert.False(r.Update(false, Ms(425)));
        Assert.True(r.Update(true, Ms(450)));
        Assert.False(r.Update(true, Ms(600)));
    }

    [Fact]
    public void BadgesNumberWedgesInOrder()
    {
        var slots = WheelPaging.Slots(5, 0);
        Assert.Equal(new Dictionary<int, string> { [0] = "1", [1] = "2", [2] = "3", [3] = "4", [4] = "5" }, WheelBadges.Keys(slots));
        Assert.Equal(2, WheelBadges.SlotForKey("3", slots));
        Assert.Null(WheelBadges.SlotForKey("6", slots));
    }

    [Fact]
    public void PagesNumberTheirOwnWedgesAndMoreIsZero()
    {
        Assert.Equal(new Dictionary<int, string> { [0] = "1", [1] = "2", [2] = "3", [3] = "4", [4] = "5", [5] = "6", [6] = "0" }, WheelBadges.Keys(WheelPaging.Slots(8, 0)));
        var second = WheelPaging.Slots(8, 1);
        Assert.Equal(new Dictionary<int, string> { [0] = "1", [1] = "2", [6] = "0" }, WheelBadges.Keys(second));
        Assert.Equal(7, second[WheelBadges.SlotForKey("2", second)!.Value].Item);
    }

    [Fact]
    public void KeysClassify()
    {
        Assert.Equal(WheelKeyCommand.Clockwise, WheelKeys.Classify(WheelKeys.VK_RIGHT, false, false, out _));
        Assert.Equal(WheelKeyCommand.Clockwise, WheelKeys.Classify(WheelKeys.VK_DOWN, false, false, out _));
        Assert.Equal(WheelKeyCommand.CounterClockwise, WheelKeys.Classify(WheelKeys.VK_LEFT, false, false, out _));
        Assert.Equal(WheelKeyCommand.Run, WheelKeys.Classify(WheelKeys.VK_RETURN, false, false, out _));
        Assert.Equal(WheelKeyCommand.Digit, WheelKeys.Classify(0x33, false, false, out var d));
        Assert.Equal(3, d);
        Assert.Equal(WheelKeyCommand.Digit, WheelKeys.Classify(0x60, false, false, out d));
        Assert.Equal(0, d);
        Assert.Equal(WheelKeyCommand.None, WheelKeys.Classify(0x38, false, false, out _));
        // Esc reaches the drag source; Tab and Esc work only without a drag.
        Assert.Equal(WheelKeyCommand.None, WheelKeys.Classify(WheelKeys.VK_ESCAPE, false, false, out _));
        Assert.Equal(WheelKeyCommand.Close, WheelKeys.Classify(WheelKeys.VK_ESCAPE, false, true, out _));
        Assert.Equal(WheelKeyCommand.NextWheel, WheelKeys.Classify(WheelKeys.VK_TAB, false, true, out _));
        Assert.Equal(WheelKeyCommand.PreviousWheel, WheelKeys.Classify(WheelKeys.VK_TAB, true, true, out _));
        Assert.Equal(WheelKeyCommand.None, WheelKeys.Classify(WheelKeys.VK_TAB, false, false, out _));
    }

    [Fact]
    public void WheelCycleWraps()
    {
        var all = new[] { WheelKind.Convert, WheelKind.Tools, WheelKind.Actions };
        Assert.Equal(WheelKind.Tools, WheelCycle.Next(WheelKind.Convert, all));
        Assert.Equal(WheelKind.Convert, WheelCycle.Next(WheelKind.Actions, all));
        Assert.Equal(WheelKind.Actions, WheelCycle.Next(WheelKind.Convert, all, backwards: true));
        Assert.Null(WheelCycle.Next(WheelKind.Convert, [WheelKind.Convert]));
        Assert.Equal([WheelKind.Convert, WheelKind.Actions], WheelCycle.Available(k => k != WheelKind.Tools));
    }

    [Fact]
    public void HintsFitTheState()
    {
        Assert.Equal("Arrows choose · Return runs · Tab switches wheel", KeyboardWheelHint.Opening(true, false));
        Assert.Equal("Return runs · Alt+Return for options · Tab switches wheel", KeyboardWheelHint.Opening(true, true));
        Assert.Equal("Arrows choose · Return runs", KeyboardWheelHint.Opening(false, false));
    }
}

public class ModifierTapTrackerTests
{
    [Fact]
    public void ATapReportsTheKeysWhenTheLastComesUp()
    {
        var t = new ModifierTapTracker();
        Assert.Null(t.FlagsChanged(Modifiers.Shift));
        Assert.Null(t.FlagsChanged(Modifiers.Shift | Modifiers.Alt));
        Assert.Null(t.FlagsChanged(Modifiers.Alt));
        Assert.Equal(Modifiers.Shift | Modifiers.Alt, t.FlagsChanged(Modifiers.None));
    }

    [Fact]
    public void AKeyBetweenMakesItAShortcut()
    {
        var t = new ModifierTapTracker();
        t.FlagsChanged(Modifiers.Control);
        t.KeyPressed();
        Assert.Null(t.FlagsChanged(Modifiers.None));
        Assert.Equal(Modifiers.Control, new Func<Modifiers?>(() => { t.FlagsChanged(Modifiers.Control); return t.FlagsChanged(Modifiers.None); })());
    }

    [Fact]
    public void KeysHeldOverFromTheShortcutDoNotCount()
    {
        var t = new ModifierTapTracker(Modifiers.Control | Modifiers.Alt);
        Assert.Null(t.FlagsChanged(Modifiers.Control | Modifiers.Alt));
        Assert.Null(t.FlagsChanged(Modifiers.Control));
        Assert.Null(t.FlagsChanged(Modifiers.None));
        Assert.Equal(Modifiers.Shift, new Func<Modifiers?>(() => { t.FlagsChanged(Modifiers.Shift); return t.FlagsChanged(Modifiers.None); })());
        Assert.Equal(Modifiers.Shift, new ModifierTapTracker(Modifiers.Control).Chosen(Modifiers.Shift | Modifiers.Control));
    }
}

public class ShakeDetectorTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static bool Run(ShakeDetector d, params (int x, int ms)[] samples)
    {
        var any = false;
        foreach (var (x, ms) in samples) any |= d.Update(x, Ms(ms));
        return any;
    }

    [Fact]
    public void ThreeBigReversalsWithinTheWindowShake() =>
        Assert.True(Run(new ShakeDetector(), (0, 0), (60, 80), (0, 160), (60, 240), (0, 320)));

    [Fact]
    public void TwoReversalsAreNotEnough() =>
        Assert.False(Run(new ShakeDetector(), (0, 0), (60, 80), (0, 160), (60, 240)));

    [Fact]
    public void SlowWigglingIsNotAShake() =>
        Assert.False(Run(new ShakeDetector(), (0, 0), (60, 400), (0, 800), (60, 1200), (0, 1600), (60, 2000)));

    [Fact]
    public void SmallJitterIsNotAShake() =>
        Assert.False(Run(new ShakeDetector(), (0, 0), (30, 20), (0, 40), (30, 60), (0, 80), (30, 100), (0, 120)));

    [Fact]
    public void AStraightDragIsNotAShake() =>
        Assert.False(Run(new ShakeDetector(), Enumerable.Range(0, 60).Select(i => (i * 10, i * 10)).ToArray()));

    [Fact]
    public void CanShakeAgainAfterwards()
    {
        var d = new ShakeDetector();
        Assert.True(Run(d, (0, 0), (60, 80), (0, 160), (60, 240), (0, 320)));
        Assert.True(Run(d, (60, 1000), (0, 1080), (60, 1160), (0, 1240)));
    }

    [Fact]
    public void StartsOverPartWayThroughAMove()
    {
        // Slightly off-axis jitter before the shake does not matter.
        Assert.True(Run(new ShakeDetector(), (500, 0), (505, 10), (560, 90), (500, 170), (560, 250), (500, 330)));
    }
}
