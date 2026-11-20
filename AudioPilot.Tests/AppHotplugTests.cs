namespace AudioPilot.Tests;

public sealed class AppHotplugTests
{
    [Theory]
    [InlineData(1, 350, true, 120)]
    [InlineData(1, 120, true, 120)]
    [InlineData(1, 80, true, 80)]
    [InlineData(2, 350, true, 350)]
    [InlineData(3, 350, true, 350)]
    [InlineData(2, 350, false, 120)]
    [InlineData(3, 350, false, 120)]
    public void ResolveDebounceMs_UsesFastPathForSingleSignalOrHiddenWindow(
        int pendingSignals,
        int configuredDebounceMs,
        bool isWindowVisible,
        int expected)
    {
        int result = AppHotplugRefreshHelper.ResolveDebounceMs(
            pendingSignals,
            configuredDebounceMs,
            isWindowVisible);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task WaitForSettlementAsync_CompletesImmediately_WhenRefreshIsIdle()
    {
        int waitCalls = 0;

        await AppHotplugRefreshHelper.WaitForSettlementAsync(
            waitForRefreshSettlementAsync: _ =>
            {
                waitCalls++;
                return Task.CompletedTask;
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, waitCalls);
    }

    [Fact]
    public async Task WaitForSettlementAsync_WaitsUntilRefreshStops()
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task settlementTask = AppHotplugRefreshHelper.WaitForSettlementAsync(
            waitForRefreshSettlementAsync: _ => completionSource.Task,
            cancellationToken: CancellationToken.None);

        Assert.False(settlementTask.IsCompleted);

        completionSource.SetResult();
        await settlementTask;

        Assert.True(settlementTask.IsCompletedSuccessfully);
    }
}
