using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchRequestCoordinatorTests
{
    [Fact]
    public void TryBeginOutputSwitchRequest_ReturnsInProgress_WhenPreviousOutputSwitchIsStillActive()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);

        bool firstAccepted = coordinator.TryBeginOutputSwitchRequest("output-1", out AppSwitchRequestRejectionReason firstReason);
        bool secondAccepted = coordinator.TryBeginOutputSwitchRequest("output-2", out AppSwitchRequestRejectionReason secondReason);

        Assert.True(firstAccepted);
        Assert.Equal(AppSwitchRequestRejectionReason.None, firstReason);
        Assert.False(secondAccepted);
        Assert.Equal(AppSwitchRequestRejectionReason.InProgress, secondReason);
    }

    [Fact]
    public void TryBeginOutputSwitchRequest_ReturnsDebounced_WhenPreviousRequestIsWithinDebounceWindow()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);
        TestPrivateAccess.SetField(coordinator, "_lastOutputSwitchRequestTicks", DateTime.UtcNow.Ticks);

        bool accepted = coordinator.TryBeginOutputSwitchRequest("output-1", out AppSwitchRequestRejectionReason rejectionReason);

        Assert.False(accepted);
        Assert.Equal(AppSwitchRequestRejectionReason.Debounced, rejectionReason);
    }

    [Fact]
    public void EndOutputSwitchRequest_AllowsNextOutputSwitchAttempt()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);

        Assert.True(coordinator.TryBeginOutputSwitchRequest("output-1", out _));

        coordinator.EndOutputSwitchRequest();
        TestPrivateAccess.SetField(coordinator, "_lastOutputSwitchRequestTicks", DateTime.UtcNow.AddMinutes(-1).Ticks);

        bool accepted = coordinator.TryBeginOutputSwitchRequest("output-2", out AppSwitchRequestRejectionReason rejectionReason);

        Assert.True(accepted);
        Assert.Equal(AppSwitchRequestRejectionReason.None, rejectionReason);
    }

    [Fact]
    public void TryBeginInputSwitchRequest_ReturnsDebounced_WhenPreviousInputRequestIsWithinDebounceWindow()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);
        TestPrivateAccess.SetField(coordinator, "_lastInputSwitchRequestTicks", DateTime.UtcNow.Ticks);

        bool accepted = coordinator.TryBeginInputSwitchRequest("input-1", out AppSwitchRequestRejectionReason rejectionReason);

        Assert.False(accepted);
        Assert.Equal(AppSwitchRequestRejectionReason.Debounced, rejectionReason);
    }

    [Fact]
    public void TryBeginOutputCoalescedRetry_PreventsDuplicatePendingRetryUntilEnded()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);

        bool firstQueued = coordinator.TryBeginOutputCoalescedRetry();
        bool secondQueued = coordinator.TryBeginOutputCoalescedRetry();

        coordinator.EndOutputCoalescedRetry();

        bool thirdQueued = coordinator.TryBeginOutputCoalescedRetry();

        Assert.True(firstQueued);
        Assert.False(secondQueued);
        Assert.True(thirdQueued);
    }

    [Fact]
    public void TryBeginInputCoalescedRetry_PreventsDuplicatePendingRetryUntilEnded()
    {
        var coordinator = new AppSwitchRequestCoordinator(Logger.Instance);

        bool firstQueued = coordinator.TryBeginInputCoalescedRetry();
        bool secondQueued = coordinator.TryBeginInputCoalescedRetry();

        coordinator.EndInputCoalescedRetry();

        bool thirdQueued = coordinator.TryBeginInputCoalescedRetry();

        Assert.True(firstQueued);
        Assert.False(secondQueued);
        Assert.True(thirdQueued);
    }

    [Fact]
    public void ResolveCoalescedRetryDelayMs_ReturnsFallback_WhenRequestWasNotDebounced()
    {
        int delayMs = AppSwitchRequestCoordinator.ResolveCoalescedRetryDelayMs(
            wasDebounced: false,
            nowTicks: DateTime.UtcNow.Ticks,
            lastRequestTicks: DateTime.UtcNow.AddMilliseconds(-20).Ticks,
            debounceWindowTicks: TimeSpan.FromMilliseconds(100).Ticks,
            fallbackDelayMs: 100);

        Assert.Equal(100, delayMs);
    }

    [Fact]
    public void ResolveCoalescedRetryDelayMs_ReturnsRemainingWindow_WhenRequestWasDebounced()
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long lastRequestTicks = nowTicks - TimeSpan.FromMilliseconds(40).Ticks;

        int delayMs = AppSwitchRequestCoordinator.ResolveCoalescedRetryDelayMs(
            wasDebounced: true,
            nowTicks: nowTicks,
            lastRequestTicks: lastRequestTicks,
            debounceWindowTicks: TimeSpan.FromMilliseconds(100).Ticks,
            fallbackDelayMs: 100);

        Assert.InRange(delayMs, 60, 62);
    }

    [Fact]
    public void ResolveCoalescedRetryDelayMs_ReturnsMinimalDelay_WhenDebounceWindowHasElapsed()
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long lastRequestTicks = nowTicks - TimeSpan.FromMilliseconds(140).Ticks;

        int delayMs = AppSwitchRequestCoordinator.ResolveCoalescedRetryDelayMs(
            wasDebounced: true,
            nowTicks: nowTicks,
            lastRequestTicks: lastRequestTicks,
            debounceWindowTicks: TimeSpan.FromMilliseconds(100).Ticks,
            fallbackDelayMs: 100);

        Assert.Equal(1, delayMs);
    }
}
