using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppMixerRefreshCoordinatorTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void ShouldRefreshForNewSession_RequiresVisibleAndNotCleaning(
        bool visible,
        bool cleaning,
        bool expected)
    {
        bool result = AppMixerRefreshGuardHelper.ShouldRefreshForNewSession(visible, cleaning);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, false, false, (int)SessionCreatedMixerRefreshOutcome.Refreshed)]
    [InlineData(false, false, false, (int)SessionCreatedMixerRefreshOutcome.SkippedWindowHidden)]
    [InlineData(true, true, false, (int)SessionCreatedMixerRefreshOutcome.SkippedWindowHidden)]
    [InlineData(true, false, true, (int)SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress)]
    public void ResolveSessionCreatedRefreshOutcome_ReturnsExpectedValue(
        bool isWindowVisible,
        bool isCleaningUp,
        bool isRefreshInProgress,
        int expected)
    {
        SessionCreatedMixerRefreshOutcome outcome = AppMixerRefreshCoordinator.ResolveSessionCreatedRefreshOutcome(
            isWindowVisible,
            isCleaningUp,
            isRefreshInProgress);

        Assert.Equal((SessionCreatedMixerRefreshOutcome)expected, outcome);
    }

    [Theory]
    [InlineData(4, 0, false, (int)SessionCreatedMixerRefreshOutcome.Refreshed, 1, true)]
    [InlineData(1, 2, true, (int)SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress, 0, null)]
    public async Task ExecuteSessionCreatedRefreshAsync_HandlesImmediateOutcomes(
        int queuedSignals,
        int drainedSignals,
        bool refreshAlreadyInProgress,
        int expectedOutcome,
        int expectedRefreshCalls,
        bool? expectedInteractive)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppMixerRefreshCoordinatorTests), "mixer-refresh-immediate.log", LogLevel.Info);
        int refreshCalls = 0;
        bool? interactive = null;

        SessionCreatedMixerRefreshOutcome outcome = await AppMixerRefreshCoordinator.ExecuteSessionCreatedRefreshAsync(
            new SessionCreatedMixerRefreshInput(QueuedSignals: queuedSignals, DebounceMs: 0, SignalLabel: "Session-created", Target: MixerRefreshTarget.Output),
            new SessionCreatedMixerRefreshDependencies(
                DrainPendingSignals: _ => new SessionCreatedMixerRefreshDrainResult(drainedSignals, MixerRefreshTarget.Output),
                IsWindowVisible: static () => true,
                IsCleaningUp: static () => false,
                IsRefreshInProgress: _ => refreshAlreadyInProgress,
                WaitForRefreshSettlementAsync: static (_, _) => Task.CompletedTask,
                RefreshSettlementTimeoutMs: 0,
                RefreshMixerAsync: (_, isInteractive) =>
                {
                    refreshCalls++;
                    interactive = isInteractive;
                    return Task.CompletedTask;
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal((SessionCreatedMixerRefreshOutcome)expectedOutcome, outcome);
        Assert.Equal(expectedRefreshCalls, refreshCalls);
        Assert.Equal(expectedInteractive, interactive);
    }

    [Fact]
    public async Task ExecuteSessionCreatedRefreshAsync_WaitsForActiveRefreshToSettle_ThenRefreshes()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppMixerRefreshCoordinatorTests), "mixer-refresh-wait.log", LogLevel.Debug);
        int refreshCalls = 0;
        int refreshChecks = 0;
        int waitCalls = 0;

        SessionCreatedMixerRefreshOutcome outcome = await AppMixerRefreshCoordinator.ExecuteSessionCreatedRefreshAsync(
            new SessionCreatedMixerRefreshInput(QueuedSignals: 2, DebounceMs: 0, SignalLabel: "Session-created", Target: MixerRefreshTarget.Input),
            new SessionCreatedMixerRefreshDependencies(
                DrainPendingSignals: static _ => new SessionCreatedMixerRefreshDrainResult(0, MixerRefreshTarget.Input),
                IsWindowVisible: static () => true,
                IsCleaningUp: static () => false,
                IsRefreshInProgress: _ => refreshChecks++ == 0,
                WaitForRefreshSettlementAsync: (_, _) =>
                {
                    waitCalls++;
                    return Task.CompletedTask;
                },
                RefreshSettlementTimeoutMs: 100,
                RefreshMixerAsync: (_, _) =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(SessionCreatedMixerRefreshOutcome.Refreshed, outcome);
        Assert.Equal(1, waitCalls);
        Assert.Equal(1, refreshCalls);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("signal=Session-created", logText, StringComparison.Ordinal);
        Assert.Contains("coalescedSignals=2", logText, StringComparison.Ordinal);
        Assert.Contains("outcome=Refreshed", logText, StringComparison.Ordinal);
        Assert.Contains("waitCount=1", logText, StringComparison.Ordinal);
        Assert.Contains("waitMs=", logText, StringComparison.Ordinal);
        Assert.Contains("totalMs=", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSessionCreatedRefreshAsync_SkipsWaitTiming_WhenDebugLoggingIsDisabled()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppMixerRefreshCoordinatorTests), "mixer-refresh-info.log", LogLevel.Info);
        int waitCalls = 0;
        int refreshChecks = 0;

        SessionCreatedMixerRefreshOutcome outcome = await AppMixerRefreshCoordinator.ExecuteSessionCreatedRefreshAsync(
            new SessionCreatedMixerRefreshInput(QueuedSignals: 1, DebounceMs: 0, SignalLabel: "Session-created", Target: MixerRefreshTarget.Output),
            new SessionCreatedMixerRefreshDependencies(
                DrainPendingSignals: static _ => new SessionCreatedMixerRefreshDrainResult(1, MixerRefreshTarget.Output),
                IsWindowVisible: static () => true,
                IsCleaningUp: static () => false,
                IsRefreshInProgress: _ => refreshChecks++ == 0,
                WaitForRefreshSettlementAsync: (_, _) =>
                {
                    waitCalls++;
                    return Task.CompletedTask;
                },
                RefreshSettlementTimeoutMs: 100,
                RefreshMixerAsync: static (_, _) => Task.CompletedTask),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(SessionCreatedMixerRefreshOutcome.Refreshed, outcome);
        Assert.Equal(1, waitCalls);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.Diagnostics.MixerRefreshCoordination, logText, StringComparison.Ordinal);
    }
}
