using OpenShelf.Core;
using static OpenShelf.Tour.Tests.Events;

namespace OpenShelf.Tour.Tests;

public class ServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "os-tour-svc-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private TourService Make(FakePreferences prefs, FakeWheels wheels) =>
        new(prefs, wheels, new FakeShelves(), new FakeToasts()) { PracticeFolder = _folder };

    [Fact]
    public void The_first_launch_shows_the_tour_once_and_remembers_it()
    {
        Sta.Run(() =>
        {
            var prefs = new FakePreferences();
            var wheels = new FakeWheels();
            using (var first = Make(prefs, wheels))
            {
                first.ShowOnFirstRunIfNeeded();
                Assert.NotNull(first.Window);
                Assert.True(first.Window!.IsVisible);
                Assert.Equal("true", prefs.GetJson(TourService.ShownKey));
            }

            using var second = Make(prefs, wheels);
            second.ShowOnFirstRunIfNeeded();
            Assert.Null(second.Window);

            second.Show();                       // the menu and Settings always work
            Assert.NotNull(second.Window);
        });
    }

    [Fact]
    public void Show_twice_reuses_the_one_window()
    {
        Sta.Run(() =>
        {
            using var tour = Make(new FakePreferences(), new FakeWheels());
            tour.Show();
            var window = tour.Window;
            tour.Show();
            Assert.Same(window, tour.Window);
        });
    }

    [Fact]
    public void Wheel_activity_moves_the_steps_only_while_the_window_is_open()
    {
        Sta.Run(() =>
        {
            var prefs = new FakePreferences();
            var wheels = new FakeWheels();
            using var tour = Make(prefs, wheels);

            wheels.Raise(Ran(WheelKind.Convert, "convert.png", "a.jpg"));     // not open yet
            tour.Show();
            tour.Progress.Show(TourStep.Convert);
            wheels.Raise(Opened(WheelKind.Convert));
            Assert.True(tour.Progress.IsUnderWay);
            wheels.Raise(Ran(WheelKind.Convert, "convert.png", "Mountain Lake.jpg"));
            Assert.Contains(TourStep.Convert, tour.Progress.Completed);
            Assert.Contains("convert", prefs.GetJson(TourProgress.ProgressKey)!);

            tour.Window!.Close();
            tour.Progress.Show(TourStep.Shelf);
            wheels.Raise(Ran(WheelKind.Actions, "action.shelf", "a.md"));     // closed again
            Assert.DoesNotContain(TourStep.Shelf, tour.Progress.Completed);
        });
    }

    [Fact]
    public void Start_Over_from_Settings_returns_an_open_tour_to_Welcome()
    {
        Sta.Run(() =>
        {
            var prefs = new FakePreferences();
            var wheels = new FakeWheels();
            using var tour = Make(prefs, wheels);
            tour.Show();
            tour.Progress.Show(TourStep.Shelf);
            wheels.Raise(Ran(WheelKind.Actions, "action.shelf", "a.md"));
            Assert.NotEmpty(tour.Progress.Completed);

            prefs.SetJson("tour.progress", null);     // what Settings does before calling TakeTour
            tour.Show();
            Assert.Empty(tour.Progress.Completed);
            Assert.Equal(TourStep.Welcome, tour.Progress.Current);
        });
    }

    [Fact]
    public void Dispose_stops_listening_and_closes_the_window()
    {
        Sta.Run(() =>
        {
            var wheels = new FakeWheels();
            var tour = Make(new FakePreferences(), wheels);
            tour.Show();
            var window = tour.Window!;
            Assert.True(wheels.HasSubscribers);
            tour.Dispose();
            Assert.False(wheels.HasSubscribers);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void The_window_is_an_ordinary_window_that_does_not_block_or_cover_the_wheel()
    {
        Sta.Run(() =>
        {
            using var tour = Make(new FakePreferences(), new FakeWheels());
            tour.Show();
            var window = tour.Window!;
            Assert.False(window.Topmost);               // the wheel (topmost, no-activate) always draws above it
            Assert.True(window.ShowActivated);
            Assert.Equal(System.Windows.ResizeMode.CanMinimize, window.ResizeMode);
        });
    }
}
