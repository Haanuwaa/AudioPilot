using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Stress)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceServiceResumeRecoveryStressTests
{
    [StressFact]
    public async Task RecoverAfterSystemResumeAsync_RepeatedCalls_DoesNotDeadlock_AndSwitchGatesRemainAvailable()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(RecoverAfterSystemResumeAsync_RepeatedCalls_DoesNotDeadlock_AndSwitchGatesRemainAvailable)))
        {
            return;
        }

        using var service = CreateAudioService();

        int iterations = 64;
        var recoveryTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                await service.RecoverAfterSystemResumeAsync();
            }
        });

        await recoveryTask.WaitAsync(TimeSpan.FromMilliseconds(
            AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs + 2000));
        bool outputGateEntered = service.TryEnterOutputSwitchGateForTests();
        bool inputGateEntered = service.TryEnterInputSwitchGateForTests();

        try
        {
            Assert.True(outputGateEntered);
            Assert.True(inputGateEntered);
        }
        finally
        {
            if (outputGateEntered)
            {
                service.ExitOutputSwitchGateForTests();
            }

            if (inputGateEntered)
            {
                service.ExitInputSwitchGateForTests();
            }
        }
    }

    [StressFact]
    public async Task RecoverAfterSystemResumeAsync_DisposeWhileWaitingForSemaphore_CompletesWithoutShutdownFault()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(RecoverAfterSystemResumeAsync_DisposeWhileWaitingForSemaphore_CompletesWithoutShutdownFault)))
        {
            return;
        }

        using var service = CreateAudioService();
        SemaphoreSlim semaphore = service.ResumeRecoverySemaphoreForTests;
        bool semaphoreReleased = false;

        await semaphore.WaitAsync();
        try
        {
            Task recoveryTask = service.RecoverAfterSystemResumeAsync();

            var waitForBlock = Stopwatch.StartNew();
            bool observedSemaphoreWait = false;
            while (waitForBlock.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (!recoveryTask.IsCompleted && service.IsResumeRecoveryWaitingOnSemaphoreForTests)
                {
                    observedSemaphoreWait = true;
                    break;
                }

                await Task.Delay(10);
            }

            Assert.True(observedSemaphoreWait);
            int finalActiveRecoveryCount = service.ActiveResumeRecoveryCountForTests;
            Assert.False(recoveryTask.IsCompleted);
            Assert.True(finalActiveRecoveryCount > 0);

            Task disposeTask = service.DisposeAsync().AsTask();
            await service.WaitForDisposeStartedForTestsAsync().WaitAsync(TimeSpan.FromSeconds(2));

            try
            {
                semaphore.Release();
                semaphoreReleased = true;
            }
            catch (ObjectDisposedException)
            {
                semaphoreReleased = true;
            }

            await service.WaitForResumeRecoveryDrainedForTestsAsync().WaitAsync(TimeSpan.FromMilliseconds(
                AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs + 5000));
            await service.WaitForDisposeCleanupBarrierForTestsAsync().WaitAsync(TimeSpan.FromMilliseconds(
                AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs + 5000));
            Assert.False(recoveryTask.IsFaulted);
            Assert.False(disposeTask.IsFaulted);
        }
        finally
        {
            if (!semaphoreReleased && semaphore.CurrentCount == 0)
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }
}
