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
        bool showCalled = false;
        bool refreshCacheCalled = false;
        bool refreshMixerCalled = false;
        bool updateMuteCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-deferred.log");

        await AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState,
            () =>
            {
                showCalled = true;
                return Task.FromResult(true);
            },
            static () => Task.CompletedTask,
            () => refreshCacheCalled = true,
            () =>
            {
                refreshMixerCalled = true;
                return Task.CompletedTask;
            },
            () =>
            {
                updateMuteCalled = true;
                return Task.CompletedTask;
            },
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.True(windowState.HasInteractiveShowRequest);
        Assert.False(showCalled);
        Assert.False(refreshCacheCalled);
        Assert.False(refreshMixerCalled);
        Assert.False(updateMuteCalled);
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
    public async Task ShowWindow_ShowsImmediatelyAfterStartupVisibilityResolves()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        bool showCalled = false;
        bool refreshCollectionsCalled = false;
        bool refreshCacheCalled = false;
        bool refreshMixerCalled = false;
        bool updateMuteCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-ready.log");

        await AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState,
            () =>
            {
                showCalled = true;
                return Task.FromResult(true);
            },
            () =>
            {
                refreshCollectionsCalled = true;
                return Task.CompletedTask;
            },
            () => refreshCacheCalled = true,
            () =>
            {
                refreshMixerCalled = true;
                return Task.CompletedTask;
            },
            () =>
            {
                updateMuteCalled = true;
                return Task.CompletedTask;
            },
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.True(showCalled);
        Assert.True(refreshCollectionsCalled);
        Assert.True(refreshCacheCalled);
        Assert.True(refreshMixerCalled);
        Assert.True(updateMuteCalled);
    }

    [Fact]
    public async Task ShowWindow_WhenShellTransitionFails_DoesNotRefreshInteractiveState()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        bool refreshCollectionsCalled = false;
        bool refreshCacheCalled = false;
        bool refreshMixerCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-failed.log");

        bool succeeded = await AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState,
            static () => Task.FromResult(false),
            () =>
            {
                refreshCollectionsCalled = true;
                return Task.CompletedTask;
            },
            () => refreshCacheCalled = true,
            () =>
            {
                refreshMixerCalled = true;
                return Task.CompletedTask;
            },
            static () => Task.CompletedTask,
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.False(succeeded);
        Assert.False(refreshCollectionsCalled);
        Assert.False(refreshCacheCalled);
        Assert.False(refreshMixerCalled);
    }

    [Fact]
    public async Task ShowWindowAsync_WaitsForPresentationBeforePublishingShownStateOrRefreshing()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        var presentationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool refreshCollectionsCalled = false;
        bool refreshCacheCalled = false;
        using var loggerScope = new TestLoggerScope(nameof(AppWindowVisibilityCoordinatorTests), "window-show-awaits-presentation.log");

        Task<bool> show = AppWindowVisibilityCoordinator.ShowWindowAsync(
            windowState,
            () => presentationCompletion.Task,
            () =>
            {
                refreshCollectionsCalled = true;
                return Task.CompletedTask;
            },
            () => refreshCacheCalled = true,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask,
            loggerScope.Logger,
            DateTime.UtcNow);

        Assert.False(show.IsCompleted);
        Assert.False(refreshCollectionsCalled);
        Assert.False(refreshCacheCalled);

        presentationCompletion.SetResult(true);
        Assert.True(await show);
        Assert.True(refreshCollectionsCalled);
        Assert.True(refreshCacheCalled);
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

    [Fact]
    public async Task ShowWindow_CompletesWhileDeviceEnumerationIsPending()
    {
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        var enumeration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool mixerRefreshCalled = false;
        bool muteRefreshCalled = false;
        using var loggerScope = TestLoggerScope.CreateInMemory("window-show-pending-refresh.log");

        try
        {
            Task<bool> show = AppWindowVisibilityCoordinator.ShowWindowAsync(
                windowState,
                static () => Task.FromResult(true),
                () => enumeration.Task,
                static () => { },
                () =>
                {
                    mixerRefreshCalled = true;
                    return Task.CompletedTask;
                },
                () =>
                {
                    muteRefreshCalled = true;
                    return Task.CompletedTask;
                },
                loggerScope.Logger,
                DateTime.UtcNow);

            Assert.True(show.IsCompletedSuccessfully);
            Assert.True(await show);
            Assert.True(mixerRefreshCalled);
            Assert.True(muteRefreshCalled);
        }
        finally
        {
            enumeration.TrySetResult();
        }
    }

    [Fact]
    public async Task ObserveShowRefreshAsync_ObservesFailuresFromAllRefreshOperations()
    {
        int deviceRefreshCalls = 0;
        int mixerRefreshCalls = 0;
        int muteRefreshCalls = 0;
        using var loggerScope = TestLoggerScope.CreateInMemory("window-show-refresh-failed.log");

        await AppWindowVisibilityCoordinator.ObserveShowRefreshAsync(
            () =>
            {
                deviceRefreshCalls++;
                return Task.FromException(new InvalidOperationException("device enumeration failed"));
            },
            () =>
            {
                mixerRefreshCalls++;
                throw new InvalidOperationException("mixer refresh failed");
            },
            () =>
            {
                muteRefreshCalls++;
                return Task.FromException(new InvalidOperationException("mute refresh failed"));
            },
            loggerScope.Logger);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Equal(1, deviceRefreshCalls);
        Assert.Equal(1, mixerRefreshCalls);
        Assert.Equal(1, muteRefreshCalls);
        Assert.Contains("window-show-refresh-failed", logText, StringComparison.Ordinal);
    }
}
