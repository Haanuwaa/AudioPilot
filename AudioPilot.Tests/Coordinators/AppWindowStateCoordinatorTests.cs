using AudioPilot.Constants;
using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppWindowStateCoordinatorTests
{
    [Fact]
    public void RequestInteractiveShow_RecordsPendingShowBeforeStartupResolves()
    {
        var coordinator = new AppWindowStateCoordinator();

        coordinator.RequestInteractiveShow();

        Assert.True(coordinator.HasInteractiveShowRequest);
        Assert.False(coordinator.IsStartupVisibilityResolved);
    }

    [Fact]
    public void TryBeginMinimize_ReturnsCooldown_ImmediatelyAfterShow()
    {
        var coordinator = new AppWindowStateCoordinator();
        var shownAt = DateTime.UtcNow;
        coordinator.MarkShown(shownAt);

        var result = coordinator.TryBeginMinimize(shownAt.AddMilliseconds(10));

        Assert.Equal(MinimizeAttemptResult.Cooldown, result);
    }

    [Fact]
    public void TryBeginMinimize_ReturnsAlreadyMinimizing_OnDuplicateCall()
    {
        var coordinator = new AppWindowStateCoordinator();
        var shownAt = DateTime.UtcNow;
        coordinator.MarkShown(shownAt.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10));

        var first = coordinator.TryBeginMinimize(DateTime.UtcNow);
        var second = coordinator.TryBeginMinimize(DateTime.UtcNow);

        Assert.Equal(MinimizeAttemptResult.Started, first);
        Assert.Equal(MinimizeAttemptResult.AlreadyMinimizing, second);
    }

    [Fact]
    public void CompleteMinimize_AllowsNextMinimizeAttempt()
    {
        var coordinator = new AppWindowStateCoordinator();
        var shownAt = DateTime.UtcNow;
        coordinator.MarkShown(shownAt.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10));

        var first = coordinator.TryBeginMinimize(DateTime.UtcNow);
        coordinator.CompleteMinimize();
        var second = coordinator.TryBeginMinimize(DateTime.UtcNow);

        Assert.Equal(MinimizeAttemptResult.Started, first);
        Assert.Equal(MinimizeAttemptResult.Started, second);
    }

    [Fact]
    public void MarkStartupVisibilityResolved_SetsResolvedFlag()
    {
        var coordinator = new AppWindowStateCoordinator();

        coordinator.MarkStartupVisibilityResolved();

        Assert.True(coordinator.IsStartupVisibilityResolved);
    }
}
