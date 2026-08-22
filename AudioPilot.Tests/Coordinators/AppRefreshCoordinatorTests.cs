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
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void EndRefreshCycleAndTryRestart_ReturnsExpectedState(bool hasPendingRefresh, bool expectedRestarted, bool expectedIsRefreshing)
    {
        var coordinator = new AppRefreshCoordinator();
        Assert.True(coordinator.TryBeginRefreshCycle());

        if (hasPendingRefresh)
        {
            coordinator.MarkPendingRefresh();
        }

        bool restarted = coordinator.EndRefreshCycleAndTryRestart();

        Assert.Equal(expectedRestarted, restarted);
        Assert.Equal(expectedIsRefreshing, coordinator.IsRefreshing);
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
