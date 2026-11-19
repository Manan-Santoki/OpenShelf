using OpenShelf.Core;
using OpenShelf.Shelf;
using Xunit;

namespace OpenShelf.Shelf.Tests;

public class PopupPlacementTests
{
    static NativeRect At(ScreenPoint p) => new() { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 1 };

    [Fact]
    public void ATrayMenuOpensAtThePointerLikeANativeOne()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        // Over the hidden-icons panel, near the bottom right: the menu's bottom-left corner sits at the pointer.
        var pointer = new ScreenPoint(work.Right - 500, work.Bottom - 100);
        var (x, y) = PopupPlacement.Place(pointer, 300, 200, At(pointer))!.Value;
        Assert.Equal(pointer.X, x);
        Assert.Equal(pointer.Y - 200, y);
    }

    [Fact]
    public void ItFlipsWhenThereIsNoRoom()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var pointer = new ScreenPoint(work.Right - 50, work.Top + 50);
        var (x, y) = PopupPlacement.Place(pointer, 300, 200, At(pointer))!.Value;
        Assert.True(x + 300 <= work.Right + 1, "stays on screen to the right");
        Assert.True(y >= work.Top, "stays on screen at the top");
        Assert.True(x <= pointer.X && x + 300 >= pointer.X - 1, "still at the pointer horizontally");
    }
}
