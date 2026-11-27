using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRefreshCoordinatorTests
{
    [Fact]
    public void TryBeginRefreshCycle_AllowsSingleActiveCycle()
    {
        var coordinator = new AppRefreshCoordinator();

        bool first = coordinator.TryBeginRefreshCycle();
        bool second = coordinator.TryBeginRefreshCycle();

        Assert.True(first);
        Assert.False(second);
        Assert.True(coordinator.IsRefreshing);
    }

    [Fact]
    public void EndRefreshCycle_ClearsRefreshingFlag()
    {
        var coordinator = new AppRefreshCoordinator();
        Assert.True(coordinator.TryBeginRefreshCycle());

        coordinator.EndRefreshCycle();

        Assert.False(coordinator.IsRefreshing);
        Assert.True(coordinator.TryBeginRefreshCycle());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndRefreshCycle_DiscardsPendingWorkAndReleasesOwnership(bool hasPendingRefresh)
    {
        var coordinator = new AppRefreshCoordinator();
        Assert.True(coordinator.TryBeginRefreshCycle());

        if (hasPendingRefresh)
        {
            coordinator.MarkPendingRefresh();
        }

        coordinator.EndRefreshCycle();

        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.TryConsumePendingRefresh());
        Assert.True(coordinator.TryBeginRefreshCycle());
    }

    [Fact]
    public void TryConsumePendingRefresh_ReturnsTrueOncePerPendingSignal()
    {
        var coordinator = new AppRefreshCoordinator();

        coordinator.MarkPendingRefresh();

        Assert.True(coordinator.TryConsumePendingRefresh());
        Assert.False(coordinator.TryConsumePendingRefresh());
    }
}
