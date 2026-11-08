using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchRetryCoordinatorTests
{
    [Fact]
    public async Task ContainedRetry_WhenSwitchThrows_LogsAndReleasesRetryGate()
    {
        using var loggerScope = new TestLoggerScope(nameof(ContainedRetry_WhenSwitchThrows_LogsAndReleasesRetryGate), "switch-retry-throws.log", LogLevel.Info);
        var coordinator = new AppSwitchRetryCoordinator(loggerScope.Logger);
        int endCount = 0;

        await coordinator.RunContainedCoalescedRetryBackgroundAsync(
            retryDelayMs: 0,
            static () => throw new InvalidOperationException("injected switch failure"),
            () => Interlocked.Increment(ref endCount),
            static () => CancellationToken.None,
            static token => CancellationTokenSource.CreateLinkedTokenSource(token),
            "output-switch-skip",
            "op-retry-contained",
            CancellationToken.None);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Equal(1, Volatile.Read(ref endCount));
        Assert.Contains("output-switch-skip", logText, StringComparison.Ordinal);
        Assert.Contains("opId=op-retry-contained", logText, StringComparison.Ordinal);
        Assert.Contains("reason=coalesced-retry-failed", logText, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected switch failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainedRetry_WhenCancelled_ReleasesRetryGateWithoutFailureLog()
    {
        using var loggerScope = new TestLoggerScope(nameof(ContainedRetry_WhenCancelled_ReleasesRetryGateWithoutFailureLog), "switch-retry-cancel.log", LogLevel.Info);
        var coordinator = new AppSwitchRetryCoordinator(loggerScope.Logger);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        int endCount = 0;

        await coordinator.RunContainedCoalescedRetryBackgroundAsync(
            retryDelayMs: 10,
            static () => Task.CompletedTask,
            () => Interlocked.Increment(ref endCount),
            () => cancellationSource.Token,
            static token => CancellationTokenSource.CreateLinkedTokenSource(token),
            "input-switch-skip",
            "op-retry-cancelled",
            cancellationSource.Token);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Equal(1, Volatile.Read(ref endCount));
        Assert.DoesNotContain("coalesced-retry-failed", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainedRetry_WhenGateReleaseThrows_ContainsAndLogsFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(ContainedRetry_WhenGateReleaseThrows_ContainsAndLogsFailure), "switch-retry-release-throws.log", LogLevel.Info);
        var coordinator = new AppSwitchRetryCoordinator(loggerScope.Logger);

        await coordinator.RunContainedCoalescedRetryBackgroundAsync(
            retryDelayMs: 0,
            static () => Task.CompletedTask,
            static () => throw new InvalidOperationException("injected gate release failure"),
            static () => CancellationToken.None,
            static token => CancellationTokenSource.CreateLinkedTokenSource(token),
            "output-switch-skip",
            "op-retry-release-contained",
            CancellationToken.None);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("opId=op-retry-release-contained", logText, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected gate release failure", logText, StringComparison.Ordinal);
    }
}
