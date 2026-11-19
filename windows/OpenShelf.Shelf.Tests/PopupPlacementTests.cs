using OpenShelf.Core;
using OpenShelf.Shelf;
using Xunit;

namespace OpenShelf.Shelf.Tests;

public class PopupPlacementTests
{
    static bool Overlaps(int x, int y, int w, int h, NativeRect r) => x < r.Right && x + w > r.Left && y < r.Bottom && y + h > r.Top;

    [Fact]
    public void ATrayMenuStaysOffTheHiddenIconsPanel()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        // A hidden-icons panel just above the taskbar, opened from an icon inside it.
        var panel = new NativeRect { Left = work.Right - 400, Top = work.Bottom - 260, Right = work.Right - 200, Bottom = work.Bottom - 10 };
        var anchor = new ScreenPoint(panel.Left + 100, panel.Top + 100);
        var placed = PopupPlacement.Place(anchor, 300, 200, panel);
        Assert.NotNull(placed);
        var (x, y) = placed!.Value;
        Assert.False(Overlaps(x, y, 300, 200, panel), $"menu at {x},{y} covers the panel");
        Assert.InRange(x, work.Left, work.Right - 300);
        Assert.InRange(y, work.Top, work.Bottom - 200);
    }
}
