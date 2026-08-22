using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed class AppHotplugRefreshHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedHotplugBurst_OnlyLatestRefreshRuns_AndShutdownCancelsTheQueue(bool cancelBeforeDispatch)
    {
        using var logScope = TestLoggerScope.CreateInMemory("queued-hotplug-burst.log");
        int refreshes = 0;
        int postRefreshes = 0;
        int routines = 0;
        var dependencies = new AppHotplugRefreshDependencies(
            IsShutdownRequested: static () => false,
            DelayAsync: static (delay, token) => Task.Delay(delay, token),
            ConsumePendingSignals: static () => 150,
            AddCoalescedEvents: static _ => { },
            RefreshDevicesForHotplugAsync: () => { refreshes++; return Task.CompletedTask; },
            WaitForHotplugRefreshSettlementAsync: static _ => Task.CompletedTask,
            ProcessPostRefresh: () => postRefreshes++,
            ExecuteDeviceChangeTriggeredRoutinesAsync: _ => { routines++; return Task.CompletedTask; },
            IncrementAppliedRefreshes: static () => 1,
            ReadCoalescedEvents: static () => 149,
            ReadSuppressedRefreshes: static () => 149,
            DiagnosticsInterval: 5);

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            CancellationTokenSource? current = null;
            var queued = new List<Task>();
            for (int index = 0; index < 150; index++)
            {
                CancellationTokenSource owned = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref current);
                queued.Add(AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                    owned,
                    finished => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref current, finished),
                    token => AppDispatcherHelper.InvokeAsync(
                        Dispatcher.CurrentDispatcher,
                        logScope.Logger,
                        () => AppHotplugRefreshHelper.ExecuteAsync(0, dependencies, logScope.Logger, "burst", token),
                        "Failed to schedule hotplug refresh",
                        "burst"),
                    TestContext.Current.CancellationToken));
            }

            if (cancelBeforeDispatch) AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref current);
            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(queued));
            Assert.Null(current);
        });

        Assert.Equal(cancelBeforeDispatch ? 0 : 1, refreshes);
        Assert.Equal(refreshes, postRefreshes);
        Assert.Equal(refreshes, routines);
        string log = logScope.DisposeAndReadLogText();
        Assert.DoesNotContain("[Error]", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[Warning]", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsImmediately_WhenShutdownAlreadyRequested()
    {
        List<string> calls = [];

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => true,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 1,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: _ => Task.CompletedTask,
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ => Task.CompletedTask,
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 0,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            Logger.Instance,
            nameof(ExecuteAsync_ReturnsImmediately_WhenShutdownAlreadyRequested),
            CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAfterDelay_WhenShutdownIsRequestedBeforeRefresh()
    {
        List<string> calls = [];
        bool shutdownRequested = false;

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: () => shutdownRequested,
                DelayAsync: (_, _) =>
                {
                    shutdownRequested = true;
                    return Task.CompletedTask;
                },
                ConsumePendingSignals: static () => 2,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: _ => Task.CompletedTask,
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ => Task.CompletedTask,
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 0,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            Logger.Instance,
            nameof(ExecuteAsync_ReturnsAfterDelay_WhenShutdownIsRequestedBeforeRefresh),
            CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteAsync_RunsRefreshSequence_AndTracksCoalescedExtras()
    {
        List<string> calls = [];
        int pendingSignals = 3;
        int coalescedEvents = 0;
        int appliedRefreshes = 0;

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => false,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: () => pendingSignals,
                AddCoalescedEvents: extras => coalescedEvents += extras,
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: _ =>
                {
                    calls.Add("settle");
                    return Task.CompletedTask;
                },
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ =>
                {
                    calls.Add("routines");
                    return Task.CompletedTask;
                },
                IncrementAppliedRefreshes: () => ++appliedRefreshes,
                ReadCoalescedEvents: () => coalescedEvents,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            Logger.Instance,
            nameof(ExecuteAsync_RunsRefreshSequence_AndTracksCoalescedExtras),
            CancellationToken.None);

        Assert.Equal(["refresh", "settle", "overlay", "routines"], calls);
        Assert.Equal(2, coalescedEvents);
        Assert.Equal(1, appliedRefreshes);
    }

    [Fact]
    public async Task ExecuteAsync_LogsDiagnostics_OnFirstAppliedRefresh()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppHotplugRefreshHelperTests), "hotplug-diagnostics.log", LogLevel.Debug);

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => false,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 3,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: static () => Task.CompletedTask,
                WaitForHotplugRefreshSettlementAsync: static _ => Task.CompletedTask,
                ProcessPostRefresh: static () => { },
                ExecuteDeviceChangeTriggeredRoutinesAsync: static _ => Task.CompletedTask,
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 7,
                ReadSuppressedRefreshes: static () => 2,
                DiagnosticsInterval: 5),
            loggerScope.Logger,
            nameof(ExecuteAsync_LogsDiagnostics_OnFirstAppliedRefresh),
            CancellationToken.None);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppConstants.Audio.LogEvents.Diagnostics.HotplugDiagnostics, logText, StringComparison.Ordinal);
        Assert.Contains("coalescedEvents=7", logText, StringComparison.Ordinal);
        Assert.Contains("suppressedRefreshes=2", logText, StringComparison.Ordinal);
        Assert.Contains("appliedRefreshes=1", logText, StringComparison.Ordinal);
        Assert.Contains("lastBatchSignals=3", logText, StringComparison.Ordinal);
        Assert.Contains("debounceMs=0", logText, StringComparison.Ordinal);
        Assert.Contains("delayElapsedMs=", logText, StringComparison.Ordinal);
        Assert.Contains("refreshElapsedMs=", logText, StringComparison.Ordinal);
        Assert.Contains("settlementElapsedMs=", logText, StringComparison.Ordinal);
        Assert.Contains("settlementTimedOut=False", logText, StringComparison.Ordinal);
        Assert.Contains("postRefreshElapsedMs=", logText, StringComparison.Ordinal);
        Assert.Contains("routinesElapsedMs=", logText, StringComparison.Ordinal);
        Assert.Contains("totalElapsedMs=", logText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 5, true)]
    [InlineData(5, 5, true)]
    [InlineData(4, 5, false)]
    public void ShouldLogDiagnostics_ReturnsExpectedValue(int appliedRefreshes, int interval, bool expected)
    {
        bool actual = AppHotplugRefreshHelper.ShouldLogDiagnostics(appliedRefreshes, interval);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRoutines_WhenShutdownStartsAfterRefresh()
    {
        List<string> calls = [];
        bool shutdownRequested = false;

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: () => shutdownRequested,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 1,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    shutdownRequested = true;
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: _ => Task.CompletedTask,
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ =>
                {
                    calls.Add("routines");
                    return Task.CompletedTask;
                },
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 0,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            Logger.Instance,
            nameof(ExecuteAsync_SkipsRoutines_WhenShutdownStartsAfterRefresh),
            CancellationToken.None);

        Assert.Equal(["refresh"], calls);
    }

    [Fact(Timeout = 30000)]
    public async Task ExecuteAsync_ContinuesAfterSettlementTimeout_AndRunsPostRefreshFlow()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppHotplugRefreshHelperTests), "hotplug-settlement-timeout.log", LogLevel.Warning);
        List<string> calls = [];

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => false,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 1,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task,
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ =>
                {
                    calls.Add("routines");
                    return Task.CompletedTask;
                },
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 0,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            loggerScope.Logger,
            nameof(ExecuteAsync_ContinuesAfterSettlementTimeout_AndRunsPostRefreshFlow),
            TestContext.Current.CancellationToken);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(["refresh", "overlay", "routines"], calls);
        Assert.Contains("hotplug-refresh-settlement-timeout", logText, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=", logText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_CanceledSettlement_DoesNotReportTimeoutOrContinueStaleRefresh(bool cancelDuringRefresh)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppHotplugRefreshHelperTests), "hotplug-settlement-canceled.log", LogLevel.Warning);
        using var cancellation = new CancellationTokenSource();
        List<string> calls = [];

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => false,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 1,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: () =>
                {
                    calls.Add("refresh");
                    if (cancelDuringRefresh) cancellation.Cancel();
                    return Task.CompletedTask;
                },
                WaitForHotplugRefreshSettlementAsync: _ =>
                {
                    if (!cancelDuringRefresh) cancellation.Cancel();
                    return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                },
                ProcessPostRefresh: () => calls.Add("overlay"),
                ExecuteDeviceChangeTriggeredRoutinesAsync: _ =>
                {
                    calls.Add("routines");
                    return Task.CompletedTask;
                },
                IncrementAppliedRefreshes: static () => 1,
                ReadCoalescedEvents: static () => 0,
                ReadSuppressedRefreshes: static () => 0,
                DiagnosticsInterval: 5),
            loggerScope.Logger,
            nameof(ExecuteAsync_CanceledSettlement_DoesNotReportTimeoutOrContinueStaleRefresh),
            cancellation.Token);

        Assert.Equal(["refresh"], calls);
        Assert.DoesNotContain("hotplug-refresh-settlement-timeout", loggerScope.DisposeAndReadLogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotReadDiagnosticsCounters_WhenDebugLoggingIsDisabled()
    {
        int counterReads = 0;

        await AppHotplugRefreshHelper.ExecuteAsync(
            debounceDelayMs: 0,
            new AppHotplugRefreshDependencies(
                IsShutdownRequested: static () => false,
                DelayAsync: static (_, _) => Task.CompletedTask,
                ConsumePendingSignals: static () => 1,
                AddCoalescedEvents: static _ => { },
                RefreshDevicesForHotplugAsync: static () => Task.CompletedTask,
                WaitForHotplugRefreshSettlementAsync: static _ => Task.CompletedTask,
                ProcessPostRefresh: static () => { },
                ExecuteDeviceChangeTriggeredRoutinesAsync: static _ => Task.CompletedTask,
                IncrementAppliedRefreshes: static () => 5,
                ReadCoalescedEvents: () =>
                {
                    counterReads++;
                    return 0;
                },
                ReadSuppressedRefreshes: () =>
                {
                    counterReads++;
                    return 0;
                },
                DiagnosticsInterval: 5),
            Logger.Instance,
            nameof(ExecuteAsync_DoesNotReadDiagnosticsCounters_WhenDebugLoggingIsDisabled),
            CancellationToken.None);

        Assert.Equal(0, counterReads);
    }
}
