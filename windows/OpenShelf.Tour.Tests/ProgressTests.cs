using OpenShelf.Core;
using static OpenShelf.Tour.Tests.Events;

namespace OpenShelf.Tour.Tests;

public class ProgressTests
{
    private static (TourProgress Progress, FakePreferences Prefs) Make()
    {
        var prefs = new FakePreferences();
        return (new TourProgress(prefs) { IsActive = true }, prefs);
    }

    [Fact]
    public void Starts_on_Welcome_with_nothing_done()
    {
        var (p, _) = Make();
        Assert.Equal(TourStep.Welcome, p.Current);
        Assert.Empty(p.Completed);
        Assert.False(p.CanGoBack);
    }

    [Fact]
    public void Events_are_ignored_while_the_window_is_closed()
    {
        var (p, _) = Make();
        p.IsActive = false;
        p.Show(TourStep.Convert);
        Assert.False(p.Record(Ran(WheelKind.Convert, "convert.png", "a.jpg")));
        Assert.Empty(p.Completed);
    }

    [Fact]
    public void Opening_the_right_wheel_marks_the_step_under_way_then_the_drop_completes_it()
    {
        var (p, _) = Make();
        p.Show(TourStep.Convert);
        Assert.False(p.IsUnderWay);
        p.Record(Opened(WheelKind.Convert));
        Assert.True(p.IsUnderWay);
        Assert.True(p.Record(Ran(WheelKind.Convert, "convert.png", "Mountain Lake.jpg")));
        Assert.False(p.IsUnderWay);
        Assert.Contains(TourStep.Convert, p.Completed);
        Assert.Equal(1, p.Celebration);
    }

    [Fact]
    public void The_wrong_wheel_opening_does_not_mark_under_way()
    {
        var (p, _) = Make();
        p.Show(TourStep.Convert);
        p.Record(Opened(WheelKind.Tools));
        Assert.False(p.IsUnderWay);
    }

    [Fact]
    public void Only_the_current_step_is_completed_by_an_event()
    {
        var (p, _) = Make();
        p.Show(TourStep.Tools);
        p.Record(Ran(WheelKind.Convert, "convert.png", "a.jpg"));   // that is the Convert step's
        Assert.Empty(p.Completed);
    }

    [Fact]
    public void Completing_twice_celebrates_once()
    {
        var (p, _) = Make();
        p.Show(TourStep.Shelf);
        p.Record(Ran(WheelKind.Actions, "action.shelf", "a.md"));
        p.Record(Ran(WheelKind.Actions, "action.shelf", "a.md"));
        Assert.Equal(1, p.Celebration);
        Assert.Equal(1, p.PracticeDoneCount);
    }

    [Fact]
    public void Next_and_Previous_walk_the_steps_and_stop_at_the_ends()
    {
        var (p, _) = Make();
        p.Previous();
        Assert.Equal(TourStep.Welcome, p.Current);
        for (var i = 0; i < 20; i++) p.Next();
        Assert.Equal(TourStep.Finish, p.Current);
        Assert.False(p.CanGoNext);
        p.Previous();
        Assert.Equal(TourStep.Options, p.Current);
        p.Show(TourStep.Welcome);
        p.Next();
        Assert.Equal(TourStep.Convert, p.Current);
    }

    [Fact]
    public void Moving_to_another_step_clears_under_way()
    {
        var (p, _) = Make();
        p.Show(TourStep.Pdf);
        p.Record(Opened(WheelKind.Tools));
        Assert.True(p.IsUnderWay);
        p.Next();
        Assert.False(p.IsUnderWay);
    }

    [Fact]
    public void Done_steps_are_remembered_across_launches()
    {
        var (p, prefs) = Make();
        p.Show(TourStep.Markdown);
        p.Record(Ran(WheelKind.Convert, "convert.pdf", "Trip Notes.md"));
        p.Show(TourStep.Keyboard);
        p.Record(Opened(WheelKind.Convert, keyboard: true));

        var later = new TourProgress(prefs);
        Assert.Equal(new[] { TourStep.Markdown, TourStep.Keyboard }, later.Completed.OrderBy(s => (int)s));
        Assert.Equal(TourStep.Welcome, later.Current);   // the tour always opens on Welcome
    }

    [Fact]
    public void Start_Over_in_Settings_clears_the_saved_value_and_Reload_picks_it_up()
    {
        var (p, prefs) = Make();
        p.Show(TourStep.Shelf);
        p.Record(Ran(WheelKind.Actions, "action.shelf", "a.md"));
        prefs.SetJson(TourProgress.ProgressKey, null);
        p.Reload();
        Assert.Empty(p.Completed);
    }

    [Fact]
    public void Reset_forgets_everything_and_goes_to_Welcome()
    {
        var (p, prefs) = Make();
        p.Show(TourStep.Shelf);
        p.Record(Ran(WheelKind.Actions, "action.shelf", "a.md"));
        p.Reset();
        Assert.Empty(p.Completed);
        Assert.Equal(TourStep.Welcome, p.Current);
        Assert.Empty(new TourProgress(prefs).Completed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Completed\":42}")]
    [InlineData("[1,2]")]
    public void Unreadable_saved_progress_means_nothing_done(string? json)
    {
        var prefs = new FakePreferences();
        if (json is not null) prefs.SetJson(TourProgress.ProgressKey, json);
        Assert.Empty(new TourProgress(prefs).Completed);
    }

    [Fact]
    public void Unknown_welcome_finish_and_duplicate_entries_are_ignored()
    {
        var steps = TourProgress.Decode("{\"Completed\":[\"convert\",\"bogus\",\"welcome\",\"finish\",\"convert\",\"PDF\"]}");
        Assert.Equal(new[] { TourStep.Convert, TourStep.Pdf }, steps);
    }

    [Fact]
    public void Changed_is_raised_for_every_change()
    {
        var (p, _) = Make();
        var count = 0;
        p.Changed += () => count++;
        p.Next();
        p.Record(Opened(WheelKind.Convert));
        p.Record(Ran(WheelKind.Convert, "convert.png", "a.jpg"));
        Assert.Equal(3, count);
    }
}
