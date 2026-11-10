using OpenShelf.Core;
using OpenShelf.Ui;
using Xunit;

namespace OpenShelf.Tools.Tests;

public class IconTests
{
    [Fact]
    public void Every_icon_parses_to_a_geometry_inside_the_24_grid()
    {
        Assert.NotEmpty(Icons.Names);
        foreach (var name in Icons.Names)
        {
            var geometry = Icons.Find(name);
            Assert.NotNull(geometry);
            var bounds = geometry!.Bounds;
            Assert.False(bounds.IsEmpty, name);
            Assert.InRange(bounds.Left, -0.5, 24.5);
            Assert.InRange(bounds.Right, -0.5, 24.5);
            Assert.InRange(bounds.Top, -0.5, 24.5);
            Assert.InRange(bounds.Bottom, -0.5, 24.5);
        }
    }

    [Fact]
    public void Unknown_names_are_not_found_so_callers_can_fall_back()
    {
        Assert.Null(Icons.Find("no-such-icon"));
        Assert.Null(Icons.Find(""));
        Assert.Null(Icons.Find(null));
    }

    [Fact]
    public void Stroke_gets_heavier_with_size_in_whole_classes()
    {
        Assert.Equal(Icons.StrokeFor(16), Icons.StrokeFor(18));
        Assert.True(Icons.StrokeFor(20) > Icons.StrokeFor(16));
        Assert.True(Icons.StrokeFor(40) > Icons.StrokeFor(24));
    }

    [Fact]
    public void Every_built_in_tool_icon_exists()
    {
        var catalog = new ToolsCatalog(new FakeToasts(), new FakeProgress(), new FakeShelves());
        Assert.All(catalog.Tools, t => Assert.NotNull(Icons.Find(t.Glyph)));
    }

    [Fact]
    public void The_app_icon_has_clean_small_frames()
    {
        var sizes = AppIcons.Frames().Select(f => f.PixelWidth).ToList();
        foreach (var size in new[] { 16, 20, 24, 32, 48, 256 }) Assert.Contains(size, sizes);
    }
}
