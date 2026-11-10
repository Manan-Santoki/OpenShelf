using System.IO;
using OpenShelf.Shelf;

namespace OpenShelf.Shelf.Tests;

public class EdgeDockingTests
{
    static readonly Box Screen = new(0, 0, 1440, 900);

    [Fact]
    public void DocksLeftWhenClose()
    {
        var (f, edge) = EdgeDocking.Settle(new Box(30, 400, 140, 160), Screen);
        Assert.Equal(ShelfEdge.Left, edge);
        Assert.Equal(0, f.X);
    }

    [Fact]
    public void DocksRightWhenClose()
    {
        var (f, edge) = EdgeDocking.Settle(new Box(1270, 400, 140, 160), Screen);
        Assert.Equal(ShelfEdge.Right, edge);
        Assert.Equal(1440, f.Right);
    }

    [Fact]
    public void StaysFloatingInTheMiddle()
    {
        var frame = new Box(600, 400, 140, 160);
        var (f, edge) = EdgeDocking.Settle(frame, Screen);
        Assert.Null(edge);
        Assert.Equal(frame, f);
    }

    [Fact]
    public void KeepsShelfOnScreenVertically() =>
        Assert.Equal(900, EdgeDocking.Settle(new Box(600, 850, 140, 160), Screen).Frame.Bottom);

    [Theory]
    [InlineData(40), InlineData(180), InlineData(395), InlineData(610), InlineData(740)]
    public void DockingKeepsTheHeight(double y)
    {
        foreach (var x in new double[] { -20, 30, 1270, 1320 })
        {
            var frame = new Box(x, y, 140, 160);
            var (f, edge) = EdgeDocking.Settle(frame, Screen);
            Assert.NotNull(edge);
            Assert.Equal(y, f.Y);
            Assert.Equal(frame.W, f.W);
        }
    }

    [Fact]
    public void DockingNearTopOrBottomOnlyMovesItBackOnScreen()
    {
        var low = EdgeDocking.Settle(new Box(10, -60, 140, 160), Screen);
        Assert.Equal(ShelfEdge.Left, low.Edge);
        Assert.Equal(0, low.Frame.Y);
        var high = EdgeDocking.Settle(new Box(1290, 800, 140, 160), Screen);
        Assert.Equal(ShelfEdge.Right, high.Edge);
        Assert.Equal(900, high.Frame.Bottom);
    }

    [Fact]
    public void SecondMonitorWithNegativeOrigin()
    {
        var visible = new Box(-1920, 40, 1920, 1000);
        var (f, edge) = EdgeDocking.Settle(new Box(-1900, 500, 140, 160), visible);
        Assert.Equal(ShelfEdge.Left, edge);
        Assert.Equal(-1920, f.X);
        Assert.Equal(500, f.Y);
    }

    [Fact]
    public void ThresholdScalesWithDpi()
    {
        Assert.Null(EdgeDocking.Settle(new Box(60, 100, 200, 200), Screen, 48).Edge);
        Assert.Equal(ShelfEdge.Left, EdgeDocking.Settle(new Box(60, 100, 200, 200), Screen, 72).Edge);
    }

    [Fact]
    public void ClampDoesNotDock()
    {
        var f = EdgeDocking.Clamp(new Box(5, 400, 140, 160), Screen);
        Assert.Equal(5, f.X);
        Assert.Equal(1300, EdgeDocking.Clamp(new Box(1400, 0, 140, 160), Screen).X);
    }

    [Fact]
    public void HiddenFrameSitsJustPastTheEdge()
    {
        var shown = new Box(0, 300, 328, 400);
        Assert.Equal(-328, EdgeHiding.HiddenFrame(shown, ShelfEdge.Left, Screen).X);
        var right = new Box(1112, 300, 328, 400);
        Assert.Equal(1440, EdgeHiding.HiddenFrame(right, ShelfEdge.Right, Screen).X);
    }

    [Fact]
    public void TabIsCentredOnTheShelfAndKeptOnScreen()
    {
        var tab = EdgeHiding.TabFrame(new Box(0, 300, 328, 400), ShelfEdge.Left, Screen);
        Assert.Equal(0, tab.X);
        Assert.Equal(500 - 36, tab.Y);
        var rightTab = EdgeHiding.TabFrame(new Box(1112, 0, 328, 60), ShelfEdge.Right, Screen);
        Assert.Equal(1440 - 14, rightTab.X);
        Assert.Equal(0, rightTab.Y);
        Assert.Equal(900 - 72, EdgeHiding.TabFrame(new Box(0, 850, 328, 100), ShelfEdge.Left, Screen).Y);
    }

    [Fact]
    public void PointerNearHasSlack()
    {
        var shelf = new Box(0, 100, 300, 300);
        Assert.True(EdgeHiding.IsPointerNear(305, 200, shelf));
        Assert.False(EdgeHiding.IsPointerNear(320, 200, shelf));
    }
}

public class LayoutTests
{
    [Fact]
    public void SizesPerState()
    {
        Assert.Equal(ShelfLayout.EmptySize, ShelfLayout.Size(0, true, ShelfViewMode.Grid));
        Assert.Equal(ShelfLayout.CollapsedSize, ShelfLayout.Size(5, false, ShelfViewMode.Grid));
        var (w, h) = ShelfLayout.Size(5, true, ShelfViewMode.Grid);
        Assert.Equal(ShelfLayout.ExpandedWidth, w);
        Assert.Equal(ShelfLayout.HeaderHeight + 2 * ShelfLayout.CellHeight + 2 * ShelfLayout.BodyPadV, h);
    }

    [Fact]
    public void ManyItemsCapTheHeightAndScroll()
    {
        Assert.Equal(ShelfLayout.MaxCardHeight, ShelfLayout.Size(25, true, ShelfViewMode.Grid).H);
        Assert.True(ShelfLayout.NeedsScrolling(25, ShelfViewMode.Grid));
        Assert.False(ShelfLayout.NeedsScrolling(5, ShelfViewMode.Grid));
    }

    [Fact]
    public void GridFramesAreThreeAcross()
    {
        var f = ShelfLayout.ItemFrames(4, ShelfViewMode.Grid);
        Assert.Equal(f[0].Y, f[2].Y);
        Assert.True(f[3].Y > f[0].Y);
        Assert.Equal(f[0].X, f[3].X);
        Assert.False(f[0].Intersects(f[1]));
    }

    [Fact]
    public void ListFramesStack()
    {
        var f = ShelfLayout.ItemFrames(3, ShelfViewMode.List);
        Assert.Equal(f[0].Bottom, f[1].Y);
    }
}

public class TypeSelectTests
{
    [Fact]
    public void PrefixBuildsWithinTimeoutAndResetsAfter()
    {
        var t = new TypeSelect();
        Assert.Equal("b", t.Append("b", 0));
        Assert.Equal("be", t.Append("e", 400));
        Assert.Equal("be", t.Prefix);
        Assert.Equal("c", t.Append("c", 1500));
    }

    [Fact]
    public void MatchIgnoresCaseAndAccents()
    {
        var names = new[] { "Beach.jpg", "\u00C9clair.png", "notes.md" };
        Assert.Equal(1, TypeSelect.Match("ec", names));
        Assert.Equal(0, TypeSelect.Match("BEA", names));
        Assert.Equal(2, TypeSelect.Match("N", names));
        Assert.Null(TypeSelect.Match("zz", names));
        Assert.Null(TypeSelect.Match("", names));
    }

    [Fact]
    public void FirstMatchWins() => Assert.Equal(0, TypeSelect.Match("a", new[] { "alpha", "apple" }));

    [Theory]
    [InlineData("a", true), InlineData("7", true), InlineData("-", true), InlineData("\u00E9", true)]
    [InlineData(" ", false), InlineData("\r", false), InlineData("ab", false), InlineData("", false)]
    public void TypingCharacters(string text, bool expected) => Assert.Equal(expected, TypeSelect.IsTypingText(text));
}

public class SelectionTests
{
    static readonly string[] Items = { "a", "b", "c", "d", "e" };

    [Fact]
    public void PlainClickSelectsOnlyThatItem()
    {
        var s = new SelectionState<string>();
        s.Click(Items, "b", false, false);
        s.Click(Items, "d", false, false);
        Assert.Equal(new[] { "d" }, s.Selected);
    }

    [Fact]
    public void CtrlClickToggles()
    {
        var s = new SelectionState<string>();
        s.Click(Items, "a", false, false);
        s.Click(Items, "c", true, false);
        Assert.Equal(2, s.Count);
        s.Click(Items, "a", true, false);
        Assert.Equal(new[] { "c" }, s.Selected);
    }

    [Fact]
    public void ShiftClickSelectsTheRangeFromTheAnchorEitherWay()
    {
        var s = new SelectionState<string>();
        s.Click(Items, "b", false, false);
        s.Click(Items, "d", false, true);
        Assert.Equal(new[] { "b", "c", "d" }, Items.Where(s.Contains));
        s.Click(Items, "a", false, true);
        Assert.Equal(new[] { "a", "b" }, Items.Where(s.Contains));
    }

    [Fact]
    public void ShiftWithoutAnchorSelectsOne()
    {
        var s = new SelectionState<string>();
        s.Click(Items, "c", false, true);
        Assert.Equal(new[] { "c" }, s.Selected);
    }

    [Fact]
    public void SelectAllClearAndPrune()
    {
        var s = new SelectionState<string>();
        s.SelectAll(Items);
        Assert.Equal(5, s.Count);
        s.Prune(new[] { "a", "b" });
        Assert.Equal(2, s.Count);
        s.Clear();
        Assert.Equal(0, s.Count);
    }
}

public class RubberBandTests
{
    static readonly Dictionary<string, Box> Frames = new()
    {
        ["a"] = new Box(0, 0, 100, 100),
        ["b"] = new Box(110, 0, 100, 100),
        ["c"] = new Box(0, 110, 100, 100),
    };

    [Fact]
    public void RectNormalisesAnyDragDirection() =>
        Assert.Equal(new Box(10, 20, 30, 40), Box.FromPoints(40, 60, 10, 20));

    [Fact]
    public void PlainDragSelectsWhatItTouches()
    {
        var sel = RubberBand.Selection(Frames, new Box(90, 90, 30, 30), new[] { "c" }, false, false);
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, sel);
        var tiny = RubberBand.Selection(Frames, new Box(5, 5, 10, 10), new[] { "c" }, false, false);
        Assert.Equal(new HashSet<string> { "a" }, tiny);
    }

    [Fact]
    public void RectInTheGapSelectsNothing() =>
        Assert.Empty(RubberBand.Selection(Frames, new Box(101, 20, 8, 8), Array.Empty<string>(), false, false));

    [Fact]
    public void ShiftAddsToTheInitialSelection()
    {
        var sel = RubberBand.Selection(Frames, new Box(5, 5, 10, 10), new[] { "c" }, false, true);
        Assert.Equal(new HashSet<string> { "a", "c" }, sel);
    }

    [Fact]
    public void CtrlTogglesTouchedItems()
    {
        var sel = RubberBand.Selection(Frames, new Box(5, 5, 200, 10), new[] { "a", "c" }, true, false);
        Assert.Equal(new HashSet<string> { "b", "c" }, sel);
    }
}

public class DedupeTests
{
    [Fact]
    public void IgnoresCaseTrailingSlashesAndRepeats()
    {
        var fresh = ShelfPaths.NewPaths(new[] { @"C:\Temp\A.txt" },
            new[] { @"c:\temp\a.TXT", @"C:\Temp\b.txt", @"C:\Temp\b.txt", @"C:\Temp\sub\", @"C:\Temp\sub", "" });
        Assert.Equal(new[] { @"C:\Temp\b.txt", @"C:\Temp\sub" }, fresh);
    }

    [Fact]
    public void ModelAddSkipsDuplicates()
    {
        ShelfModel.LoadThumbnails = false;
        var model = new ShelfModel();
        Assert.Equal(2, model.Add(new[] { @"C:\Temp\a.txt", @"C:\Temp\b.txt", @"c:\TEMP\A.TXT" }));
        Assert.Equal(0, model.Add(new[] { @"C:\Temp\a.txt" }));
        Assert.Equal(2, model.Items.Count);
        Assert.True(model.HasHadItems);
    }

    [Fact]
    public void RemovingSelectedItemsPrunesTheSelection()
    {
        ShelfModel.LoadThumbnails = false;
        var model = new ShelfModel();
        model.Add(new[] { @"C:\Temp\a.txt", @"C:\Temp\b.txt" });
        model.SelectAll();
        model.Remove(new[] { model.Items[0] });
        Assert.Single(model.Selected);
    }
}

public sealed class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "openshelf-shelftests-" + Guid.NewGuid().ToString("N"));

    public StoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    string MakeFile(string name, string text = "hello")
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var file = MakeFile("a.txt");
        var shelf = new SavedShelf
        {
            Items = { ShelfStore.Capture(file), new SavedItem { Path = @"C:\nope\x.txt" } },
            X = 400, Y = 300, Width = 328, Height = 408, DockedEdge = "right", Expanded = true, ViewMode = "list",
        };
        var store = Path.Combine(_dir, "shelves.json");
        ShelfStore.Save(store, new[] { shelf });
        var loaded = Assert.Single(ShelfStore.Load(store));
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(file, loaded.Items[0].Path);
        Assert.Equal(shelf.Items[0].FileId, loaded.Items[0].FileId);
        Assert.Equal(shelf.Items[0].Volume, loaded.Items[0].Volume);
        Assert.Null(loaded.Items[1].FileId);
        Assert.Equal((400d, 300d, 328d, 408d), (loaded.X, loaded.Y, loaded.Width, loaded.Height));
        Assert.Equal("right", loaded.DockedEdge);
        Assert.True(loaded.Expanded);
        Assert.Equal("list", loaded.ViewMode);
    }

    [Fact]
    public void LoadOfMissingOrCorruptFileIsEmpty()
    {
        Assert.Empty(ShelfStore.Load(Path.Combine(_dir, "none.json")));
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Empty(ShelfStore.Load(bad));
    }

    [Fact]
    public void CaptureRecordsAFileId()
    {
        var item = ShelfStore.Capture(MakeFile("id.txt"));
        Assert.NotNull(item.FileId);
        Assert.NotNull(item.Volume);
    }

    [Fact]
    public void ResolveFindsAFileThatStayedPut()
    {
        var file = MakeFile("stay.txt");
        Assert.Equal(file, ShelfStore.Resolve(ShelfStore.Capture(file)));
    }

    [Fact]
    public void ResolveFindsARenamedFile()
    {
        var file = MakeFile("old name.txt");
        var item = ShelfStore.Capture(file);
        var renamed = Path.Combine(_dir, "new name.txt");
        File.Move(file, renamed);
        Assert.Equal(renamed, ShelfStore.Resolve(item), ignoreCase: true);
    }

    [Fact]
    public void ResolveFindsAFileMovedToAnotherFolder()
    {
        var file = MakeFile("move me.txt");
        var item = ShelfStore.Capture(file);
        var moved = Path.Combine(_dir, "elsewhere", "deeper", "move me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(file, moved);
        Assert.Equal(moved, ShelfStore.Resolve(item), ignoreCase: true);
    }

    [Fact]
    public void ResolveSkipsADeletedFile()
    {
        var file = MakeFile("gone.txt");
        var item = ShelfStore.Capture(file);
        File.Delete(file);
        Assert.Null(ShelfStore.Resolve(item));
    }

    [Fact]
    public void ResolveFallsBackToThePathWithoutAnId()
    {
        var file = MakeFile("plain.txt");
        Assert.Equal(file, ShelfStore.Resolve(new SavedItem { Path = file }));
        Assert.Null(ShelfStore.Resolve(new SavedItem { Path = Path.Combine(_dir, "missing.txt") }));
    }

    [Fact]
    public void ResolveWorksForFolders()
    {
        var dir = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(dir);
        var item = ShelfStore.Capture(dir);
        var renamed = Path.Combine(_dir, "folder2");
        Directory.Move(dir, renamed);
        Assert.Equal(renamed, ShelfStore.Resolve(item), ignoreCase: true);
    }
}
