using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceServiceSwitchOrchestrationTests
{
    [Fact]
    public void OutputSwitchGate_RejectsConcurrentEntry_ThenRecoversAfterRelease()
    {
        using var service = CreateAudioService();

        bool firstEnter = service.TryEnterOutputSwitchGateForTests();
        bool secondEnter = service.TryEnterOutputSwitchGateForTests();

        try
        {
            Assert.True(firstEnter);
            Assert.False(secondEnter);
        }
        finally
        {
            if (firstEnter)
            {
                service.ExitOutputSwitchGateForTests();
            }
        }

        bool thirdEnter = service.TryEnterOutputSwitchGateForTests();
        try
        {
            Assert.True(thirdEnter);
        }
        finally
        {
            if (thirdEnter)
            {
                service.ExitOutputSwitchGateForTests();
            }
        }
    }

    [Fact]
    public void InputSwitchGate_RejectsConcurrentEntry_ThenRecoversAfterRelease()
    {
        using var service = CreateAudioService();

        bool firstEnter = service.TryEnterInputSwitchGateForTests();
        bool secondEnter = service.TryEnterInputSwitchGateForTests();

        try
        {
            Assert.True(firstEnter);
            Assert.False(secondEnter);
        }
        finally
        {
            if (firstEnter)
            {
                service.ExitInputSwitchGateForTests();
            }
        }

        bool thirdEnter = service.TryEnterInputSwitchGateForTests();
        try
        {
            Assert.True(thirdEnter);
        }
        finally
        {
            if (thirdEnter)
            {
                service.ExitInputSwitchGateForTests();
            }
        }
    }

    [Fact]
    public void ShouldRegisterPreserveSnapshot_RequiresBothFlagAndSnapshot()
    {
        var snapshot = new SessionVolumeSnapshot();

        Assert.True(AudioDeviceService.ShouldRegisterPreserveSnapshot(true, snapshot));
        Assert.False(AudioDeviceService.ShouldRegisterPreserveSnapshot(true, null));
        Assert.False(AudioDeviceService.ShouldRegisterPreserveSnapshot(false, snapshot));
    }

    [Fact]
    public async Task SwitchAudioDeviceAsync_ReturnsFailure_ForMissingDevice()
    {
        using var service = CreateAudioService();

        var (success, _) = await service.SwitchAudioDeviceAsync(
            targetId: "missing-device-2",
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false);

        Assert.False(success);
    }

    [Fact]
    public async Task SwitchAudioDeviceAsync_WhenOutputGateBusy_LeavesHeldGateOwnedByCaller()
    {
        using var service = CreateAudioService();

        bool gateHeld = service.TryEnterOutputSwitchGateForTests();
        Assert.True(gateHeld);

        try
        {
            var (success, _) = await service.SwitchAudioDeviceAsync(
                targetId: "missing-device-2",
                muteMic: false,
                muteSound: false,
                deafen: false,
                preserveAudioLevels: false);

            Assert.False(success);
        }
        finally
        {
            service.ExitOutputSwitchGateForTests();
        }

        bool reentered = service.TryEnterOutputSwitchGateForTests();
        try
        {
            Assert.True(reentered);
        }
        finally
        {
            if (reentered)
            {
                service.ExitOutputSwitchGateForTests();
            }
        }
    }

    [Fact]
    public async Task SwitchAudioDeviceAsync_RepeatedFailures_ReleaseOutputGate()
    {
        using var service = CreateAudioService();

        var (firstSuccess, _) = await service.SwitchAudioDeviceAsync(
            targetId: "missing-device-2",
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false);

        var (secondSuccess, _) = await service.SwitchAudioDeviceAsync(
            targetId: "missing-device-2",
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false);

        Assert.False(firstSuccess);
        Assert.False(secondSuccess);
        Assert.True(service.TryEnterOutputSwitchGateForTests());
        service.ExitOutputSwitchGateForTests();
    }

    [Fact]
    public async Task CompleteOutputSwitchAttempt_DoesNotDeadlock_AndReleasesGate()
    {
        using var service = CreateAudioService();

        bool gateHeld = service.TryEnterOutputSwitchGateForTests();
        Assert.True(gateHeld);

        await Task.Run(service.CompleteOutputSwitchAttemptForTests, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        bool reentered = service.TryEnterOutputSwitchGateForTests();
        try
        {
            Assert.True(reentered);
        }
        finally
        {
            if (reentered)
            {
                service.ExitOutputSwitchGateForTests();
            }
        }
    }

    [Fact]
    public async Task CompleteOutputSwitchAttemptForTests_WhenSessionMonitoringBlocks_ReturnsPromptly_AndReleasesGate()
    {
        using var updateStarted = new ManualResetEventSlim(false);
        using var allowUpdateToFinish = new ManualResetEventSlim(false);
        using var service = new AudioDeviceService(
            new FakeInputListenPropertyWriter(),
            outputSwitchCompletionSessionMonitoringUpdate: () =>
            {
                updateStarted.Set();
                Assert.True(allowUpdateToFinish.Wait(TimeSpan.FromSeconds(5)));
            });

        bool gateHeld = service.TryEnterOutputSwitchGateForTests();
        Assert.True(gateHeld);

        await Task.Run(service.CompleteOutputSwitchAttemptForTests, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(updateStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        bool reentered = service.TryEnterOutputSwitchGateForTests();
        try
        {
            Assert.True(reentered);
        }
        finally
        {
            if (reentered)
            {
                service.ExitOutputSwitchGateForTests();
            }
        }

        allowUpdateToFinish.Set();

        Task[] backgroundTasks = [.. service.BackgroundTasksForTests.Values];
        if (backgroundTasks.Length > 0)
        {
            await Task.WhenAll(backgroundTasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }
}

