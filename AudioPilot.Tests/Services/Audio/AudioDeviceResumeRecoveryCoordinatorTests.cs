using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceResumeRecoveryCoordinatorTests
{
    [Fact]
    public async Task RecoverAfterSystemResumeAsync_InvokesRecoveryCallbacks()
    {
        using var coordinator = new AudioDeviceResumeRecoveryCoordinator(Logger.Instance, () => false);
        bool snapshotsInvalidated = false;
        bool switchTimesReset = false;
        bool queued = false;

        await coordinator.RecoverAfterSystemResumeAsync(
            () => null,
            () => snapshotsInvalidated = true,
            () => switchTimesReset = true,
            () =>
            {
                queued = true;
                return true;
            },
            CancellationToken.None);

        Assert.True(snapshotsInvalidated);
        Assert.True(switchTimesReset);
        Assert.True(queued);
    }

    [Fact]
    public async Task RecoverAfterSystemResumeAsync_CoalescesWaitingCalls_WithoutSuppressingNextCycle()
    {
        using var coordinator = new AudioDeviceResumeRecoveryCoordinator(Logger.Instance, () => false);
        int invalidations = 0;
        Task RecoverAsync() => coordinator.RecoverAfterSystemResumeAsync(
            () => null, () => invalidations++, () => { }, () => true, TestContext.Current.CancellationToken);

        await coordinator.SemaphoreForTests.WaitAsync(TestContext.Current.CancellationToken);
        Task first = RecoverAsync();
        Task duplicate = RecoverAsync();
        coordinator.SemaphoreForTests.Release();
        await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, invalidations);

        await RecoverAsync();
        Assert.Equal(2, invalidations);
        Assert.Equal(0, coordinator.ActiveRecoveryCountForTests);
    }

    [Fact]
    public async Task SignalShutdown_CompletesActiveRecoveryWaiter()
    {
        using var coordinator = new AudioDeviceResumeRecoveryCoordinator(Logger.Instance, () => false);
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SetStateForTests(completionSource, activeCount: 1);

        coordinator.SignalShutdown();
        await coordinator.WaitForActiveResumeRecoveryAsync().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(completionSource.Task.IsCompleted);
    }
}
