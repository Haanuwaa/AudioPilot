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
