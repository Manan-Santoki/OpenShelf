using OpenShelf.Core;

namespace OpenShelf.Tests;

public class WheelGeometryTests
{
    private readonly WheelGeometry _geometry = new(4, 50, 130);
    private const double Cx = 200, Cy = 200;

    // y grows DOWNWARDS (screen space): "above" the centre means a smaller y.
    [Fact]
    public void WedgesFollowClockwiseFromTwelveOClock()
    {
        Assert.Equal(0, _geometry.WedgeAt(200, 100, Cx, Cy)); // above
        Assert.Equal(1, _geometry.WedgeAt(300, 200, Cx, Cy)); // right
        Assert.Equal(2, _geometry.WedgeAt(200, 300, Cx, Cy)); // below
        Assert.Equal(3, _geometry.WedgeAt(100, 200, Cx, Cy)); // left
    }

    [Fact]
    public void JustLeftOfTwelveOClockWrapsToFirstWedge() =>
        Assert.Equal(0, _geometry.WedgeAt(190, 100, Cx, Cy));

    [Fact]
    public void HubAndOutsideHaveNoWedge()
    {
        Assert.Null(_geometry.WedgeAt(210, 190, Cx, Cy));
        Assert.Null(_geometry.WedgeAt(200, 60, Cx, Cy));
    }

    [Fact]
    public void EmptyWheelHasNoWedges() =>
        Assert.Null(new WheelGeometry(0).WedgeAt(200, 100, Cx, Cy));

    [Fact]
    public void FewWedgesUseTheFullLabelWidth() =>
        Assert.Equal(74, new WheelGeometry(4).LabelWidth(94));

    [Fact]
    public void CrowdedWheelsNarrowLabelsToFitTheirWedge()
    {
        var width = new WheelGeometry(11).LabelWidth(94);
        var chord = 2 * 94 * Math.Sin(2 * Math.PI / 11 / 2);
        Assert.True(width < 74);
        Assert.True(width <= chord - 7);
    }

    [Fact]
    public void CenterAngleIsClockwiseFromTwelve()
    {
        var g = new WheelGeometry(4);
        Assert.Equal(0, g.CenterAngle(0));
        Assert.Equal(Math.PI / 2, g.CenterAngle(1), 9);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(11)]
    public void EveryWedgeCentreHitsItsOwnWedge(int count)
    {
        var g = new WheelGeometry(count);
        for (var i = 0; i < count; i++)
        {
            var a = g.CenterAngle(i);
            var x = Cx + 93 * Math.Sin(a);
            var y = Cy - 93 * Math.Cos(a);
            Assert.Equal(i, g.WedgeAt(x, y, Cx, Cy));
        }
    }
}
