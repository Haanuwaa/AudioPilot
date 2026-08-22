using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class WindowLayoutPolicyTests
{
    [Fact]
    public void PartialSizeEditsPreserveTheOtherBoundsOnANegativeCoordinateMonitor()
    {
        var current = new WindowLayoutSnapshot(-1800, 100, 600, 500, 96, "normal", true, "live", true);
        var monitor = new WindowLayoutMonitor(-1920, 0, -1920, 40, 1920, 1040, 96);
        var result = WindowPlacementHelper.ResolveLayout(current, new(Width: 800), monitor);
        Assert.Equal((-1800, 100, 800, 500), (result.X, result.Y, result.Width, result.Height));
    }

    [Fact]
    public void ResetUsesDefaultLogicalSizeAtTheTargetDpiAndCentersInsideItsWorkArea()
    {
        var result = WindowPlacementHelper.ResolveLayout(null, new(Reset: true), new(0, 0, 0, 40, 1920, 1040, 144));
        Assert.Equal((613, 230, 693, 660), (result.X, result.Y, result.Width, result.Height));
        Assert.Equal(144, result.Dpi);
    }

    [Fact]
    public void MovingBetweenDpiScalesPreservesOmittedLogicalDimensions()
    {
        var current = new WindowLayoutSnapshot(100, 100, 600, 400, 96, "normal", true, "live", true);
        var result = WindowPlacementHelper.ResolveLayout(current, new(X: 2000), new(1920, 0, 1920, 0, 2560, 1440, 144));
        Assert.Equal((900, 600), (result.Width, result.Height));
        Assert.Equal(2000, result.X);
    }

    [Theory]
    [InlineData(1, 1, 645, 480)]
    [InlineData(32768, 32768, 1600, 900)]
    public void RequestedSizesRespectScaledMinimumsAndTheAvailableWorkArea(int width, int height, int expectedWidth, int expectedHeight)
    {
        var result = WindowPlacementHelper.ResolveLayout(null, new(X: -1000000, Y: 1000000, Width: width, Height: height), new(0, 0, 0, 40, 1600, 900, 144));
        Assert.Equal((expectedWidth, expectedHeight), (result.Width, result.Height));
        Assert.Equal(0, result.X);
        Assert.Equal(940 - result.Height, result.Y);
    }

    [Fact]
    public void ScreenCoordinatesAreConvertedToWorkspaceCoordinatesForLeftAndTopTaskbars()
    {
        var saved = MainWindowPlacementService.ToSaved(new(-1800, 100, 600, 500, 144, "normal", false, "saved", true),
            new(-1920, 0, -1880, 40, 1880, 1040, 144));
        Assert.Equal(new SavedWindowPlacement(-1840, 60, -1240, 560, 144, false), saved);
    }
}
