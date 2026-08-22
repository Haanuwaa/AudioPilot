using System.Windows;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class WindowPlacementHelperTests
{
    [Theory]
    [InlineData(-6000, -900, 450, 600, -5280, -418)]
    [InlineData(-1500, 1400, 450, 600, -1890, 998)]
    [InlineData(-5000, -600, 1350, 1800, -5000, -418)]
    [InlineData(-7000, -900, 5000, 2400, -5280, -418)]
    public void InaccessibleWindow_IsRecoveredInsideNegativeCoordinateWorkArea(
        double left, double top, double width, double height, double expectedLeft, double expectedTop)
    {
        var workArea = new Rect(-5280, -418, 3840, 2016);
        Point position = WindowPlacementHelper.GetAccessiblePosition(new Rect(left, top, width, height), workArea, 3);
        Assert.Equal(new Point(expectedLeft, expectedTop), position);
    }

    [Theory]
    [InlineData(40, 40)]
    [InlineData(-100, 40)]
    [InlineData(1600, 40)]
    public void AccessibleWindow_PreservesIntentionalPosition(double left, double top)
    {
        Point position = WindowPlacementHelper.GetAccessiblePosition(
            new Rect(left, top, 450, 600), new Rect(0, 0, 1920, 1040), 1);
        Assert.Equal(new Point(left, top), position);
    }

    [Fact]
    public void WindowBelowTopTaskbar_RecoversItsTitleBar()
    {
        Point position = WindowPlacementHelper.GetAccessiblePosition(
            new Rect(200, 0, 450, 600), new Rect(0, 48, 1920, 1032), 1);
        Assert.Equal(new Point(200, 48), position);
    }
}
