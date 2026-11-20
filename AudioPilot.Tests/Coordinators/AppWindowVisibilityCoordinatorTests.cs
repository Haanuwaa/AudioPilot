using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppWindowVisibilityCoordinatorTests
{
    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 1)]
    public async Task ToggleWindowVisibility_ChoosesActionFromAuthoritativeShellVisibility(
        bool isWindowVisible,
        int expectedShowCalls,
        int expectedMinimizeCalls)
    {
        int showCalls = 0;
        int minimizeCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-visibility-toggle.log");

        await AppWindowVisibilityCoordinator.ToggleWindowVisibilityAsync(
            isWindowVisible,
            () =>
            {
                showCalls++;
                return Task.FromResult(true);
            },
            () => minimizeCalls++,
            loggerScope.Logger);

        Assert.Equal(expectedShowCalls, showCalls);
        Assert.Equal(expectedMinimizeCalls, minimizeCalls);
    }

    [Fact]
    public async Task ShowWindow_DefersUntilStartupVisibilityResolves()
    {
        var windowState = new AppWindowStateCoordinator();
        using var loggerScope = TestLoggerScope.CreateInMemory("window-show-deferred.log");

        Assert.False(await AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState, static () => throw new InvalidOperationException("Window must remain deferred."),
            loggerScope.Logger, DateTime.UtcNow));

        Assert.True(windowState.HasInteractiveShowRequest);
    }

    [Fact]
    public void BuildMinimizePlan_CombinesFirstRunAndSaveBalloonState()
    {
        var windowState = new AppWindowStateCoordinator
        {
            ShowBalloonOnFirstMinimize = true,
        };

        windowState.MarkShown(DateTime.UtcNow.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10));

        MinimizeWindowPlan plan = AppWindowVisibilityCoordinator.BuildMinimizePlan(
            windowState,
            showBalloonAfterSave: true,
            DateTime.UtcNow);

        Assert.Equal(MinimizeAttemptResult.Started, plan.AttemptResult);
        Assert.True(plan.ShowBalloon);
        Assert.True(plan.ConsumeFirstRunBalloon);
        Assert.True(plan.ConsumeSaveBalloon);
    }

    [Fact]
    public void ApplyMinimizePlan_ClearsConsumedBalloonFlags_AndCompletesMinimize()
    {
        var windowState = new AppWindowStateCoordinator
        {
            ShowBalloonOnFirstMinimize = true,
        };

        bool saveBalloonCleared = false;
        bool minimizeCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-visibility.log");

        AppWindowVisibilityCoordinator.ApplyMinimizePlan(
            windowState,
            new MinimizeWindowPlan(MinimizeAttemptResult.Started, ShowBalloon: true, ConsumeFirstRunBalloon: true, ConsumeSaveBalloon: true),
            (_, _) =>
            {
                minimizeCalled = true;
                return true;
            },
            () => saveBalloonCleared = true,
            loggerScope.Logger);

        Assert.True(minimizeCalled);
        Assert.False(windowState.ShowBalloonOnFirstMinimize);
        Assert.True(saveBalloonCleared);

        Assert.Equal(MinimizeAttemptResult.Started, windowState.TryBeginMinimize(DateTime.UtcNow.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10)));
    }

    [Fact]
    public void ApplyMinimizePlan_WhenShellTransitionFails_PreservesBalloonFlags_AndAbortsMinimize()
    {
        var windowState = new AppWindowStateCoordinator
        {
            ShowBalloonOnFirstMinimize = true,
        };
        bool saveBalloonCleared = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-visibility-failed.log");

        AppWindowVisibilityCoordinator.ApplyMinimizePlan(
            windowState,
            new MinimizeWindowPlan(MinimizeAttemptResult.Started, ShowBalloon: true, ConsumeFirstRunBalloon: true, ConsumeSaveBalloon: true),
            static (_, _) => false,
            () => saveBalloonCleared = true,
            loggerScope.Logger);

        Assert.True(windowState.ShowBalloonOnFirstMinimize);
        Assert.False(saveBalloonCleared);
        Assert.Equal(MinimizeAttemptResult.Started, windowState.TryBeginMinimize(DateTime.UtcNow.AddMilliseconds(-AppConstants.Timing.ShowCooldownMs - 10)));
    }

    [Fact]
    public async Task ShowWindow_WhenShellTransitionFails_DoesNotStartCooldown()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        using var loggerScope = TestLoggerScope.CreateInMemory("window-show-failed.log");
        DateTime now = DateTime.UtcNow;

        Assert.False(await AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState, static () => Task.FromResult(false), loggerScope.Logger, now));
        Assert.Equal(MinimizeAttemptResult.Started, windowState.TryBeginMinimize(now));
    }


    [Fact]
    public async Task ShowWindowAsync_WaitsForPresentationBeforeStartingCooldown()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        var presentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loggerScope = TestLoggerScope.CreateInMemory("window-show-awaits-presentation.log");
        DateTime now = DateTime.UtcNow;

        Task<bool> show = AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState, () => presentation.Task, loggerScope.Logger, now);

        Assert.False(show.IsCompleted);
        Assert.Equal(MinimizeAttemptResult.Started, windowState.TryBeginMinimize(now));
        windowState.AbortMinimize();
        presentation.SetResult(true);
        Assert.True(await show);
        Assert.Equal(MinimizeAttemptResult.Cooldown, windowState.TryBeginMinimize(now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartHiddenToTray_ReturnsAuthoritativeShellResult(bool shellResult)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-start-hidden.log");

        bool result = AppWindowVisibilityCoordinator.StartHiddenToTray(() => shellResult, loggerScope.Logger);

        Assert.Equal(shellResult, result);
    }


}
