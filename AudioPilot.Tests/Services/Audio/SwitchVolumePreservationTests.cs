using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Collection("AudioHardwareStressIsolation")]
public sealed class SwitchVolumePreservationTests
{
    [AudioHardwareFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    public void RoutineMuteIntentSupersedesSwitchPreservationEvenWhenAlreadyApplied()
    {
        if (!TestExecutionGuards.RequireDefaultAudioEndpoints(nameof(RoutineMuteIntentSupersedesSwitchPreservationEvenWhenAlreadyApplied),
            (DataFlow.Render, Role.Multimedia), (DataFlow.Capture, Role.Console))) return;
        using var audio = new AudioDeviceService();
        ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            foreach (bool playback in TargetChangeCases)
            {
                using var endpoint = playback ? audio.TryGetPlaybackDeviceForRoutine(null) : audio.TryGetRecordingDeviceForRoutine(null);
                Assert.NotNull(endpoint);
                bool muted = endpoint.AudioEndpointVolume.Mute;
                long revision = audio.GetEndpointMuteIntentRevision(playback);
                var result = AudioPilot.Services.Routines.RoutineEndpointMuteService.Apply(audio,
                    AudioPilot.Logging.Logger.Instance, playback, endpoint.ID, muted, false, "same-state-routine", null);
                Assert.True(result.Success);
                Assert.Equal(muted, endpoint.AudioEndpointVolume.Mute);
                Assert.Equal(revision + 1, audio.GetEndpointMuteIntentRevision(playback));
            }
        });
    }

    [Fact]
    public async Task DelayedInputSnapshotDoesNotOverwriteNewerVolume()
    {
        var snapshot = new TaskCompletionSource<SessionVolumeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        float currentVolume = 30;
        var guard = new EndpointVolumePreservationState(.3f);
        Task restore = PostSwitchCoordinator.RestoreInputStateAsync(snapshot.Task, "input", (_, volume, _) =>
        {
            if (volume.HasValue) currentVolume = volume.Value;
        }, () => true, TestContext.Current.CancellationToken, canRestoreVolume: () => guard.CanRestore(currentVolume / 100));
        currentVolume = 35;
        snapshot.SetResult(new SessionVolumeSnapshot { MicVolumePercent = 30 });
        await restore;
        Assert.Equal(35, currentVolume);
    }

    private static readonly bool[] TargetChangeCases = [true, false];

    [AudioHardwareFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    public async Task NativeEndpointGuardPreservesNewerChoicesAndAllowsUnchangedTargets()
    {
        if (!TestExecutionGuards.RequireDefaultAudioEndpoints(nameof(NativeEndpointGuardPreservesNewerChoicesAndAllowsUnchangedTargets),
            (DataFlow.Render, Role.Multimedia), (DataFlow.Capture, Role.Multimedia))) return;
        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var volume = new VolumeControlService(new UnusedEnumerator(), _ => null, _ => true);
            foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                float original = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                float newer = original > .9f ? original - .01f : original + .01f;
                foreach (bool changeTarget in TargetChangeCases)
                {
                    using var guard = EndpointStatePreservationGuard.Capture(device.ID, AudioPilot.Logging.Logger.Instance);
                    Assert.NotNull(guard);
                    try
                    {
                        float source = changeTarget ? original : newer;
                        var snapshot = new SessionVolumeSnapshot { MasterVolumePercent = source * 100, MicVolumePercent = source * 100 };
                        if (changeTarget) device.AudioEndpointVolume.MasterVolumeLevelScalar = newer;
                        if (flow == DataFlow.Render)
                            volume.ApplySessionVolumesSimpleAsync(snapshot, device.ID, null, () => true, applyMicVolume: false,
                                canRestoreMasterVolume: guard.CanRestore).GetAwaiter().GetResult();
                        else
                            PostSwitchCoordinator.RestoreInputStateAsync(Task.FromResult(snapshot), device.ID,
                                (_, level, _) => device.AudioEndpointVolume.MasterVolumeLevelScalar = level!.Value / 100,
                                () => true, TestContext.Current.CancellationToken, canRestoreVolume: guard.CanRestore).GetAwaiter().GetResult();
                        Assert.Equal(newer, device.AudioEndpointVolume.MasterVolumeLevelScalar, 4);
                        Assert.True(guard.CanRestoreMute());
                        guard.Dispose();
                        Assert.False(guard.CanRestore());
                        Assert.False(guard.CanRestoreMute());
                    }
                    finally { device.AudioEndpointVolume.MasterVolumeLevelScalar = original; }
                }
                bool originalMute = device.AudioEndpointVolume.Mute;
                if (flow == DataFlow.Render || !originalMute)
                {
                    using var guard = EndpointStatePreservationGuard.Capture(device.ID, AudioPilot.Logging.Logger.Instance);
                    Assert.NotNull(guard);
                    try
                    {
                        device.AudioEndpointVolume.Mute = !originalMute;
                        Assert.False(guard.CanRestoreMute());
                        Assert.True(guard.CanRestore());
                    }
                    finally { device.AudioEndpointVolume.Mute = originalMute; }
                }
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumeNotificationPreventsRestorationEvenWhenLevelReturns(bool returnToInitial)
    {
        var guard = new EndpointVolumePreservationState(.3f);
        guard.Observe(.4f);
        if (returnToInitial) guard.Observe(.3f);
        Assert.False(guard.CanRestore(returnToInitial ? .3f : .4f));
    }

    [Fact]
    public async Task InputVolumeChangeDoesNotPreventIndependentMutePreservation()
    {
        var guard = new EndpointVolumePreservationState(.3f);
        guard.Observe(.4f);
        var writes = new List<(float? Volume, bool? Muted)>();
        await PostSwitchCoordinator.RestoreInputStateAsync(Task.FromResult(new SessionVolumeSnapshot { MicVolumePercent = 30 }),
            "input", (_, volume, muted) => writes.Add((volume, muted)), () => true, TestContext.Current.CancellationToken,
            muted: true, canRestoreVolume: () => guard.CanRestore(.4f));
        Assert.Equal(((float?)null, (bool?)true), Assert.Single(writes));
    }

    [Fact]
    public async Task UnchangedInputStillPreservesSourceVolume()
    {
        var guard = new EndpointVolumePreservationState(.7f);
        float current = 70;
        guard.Observe(.7f);
        await PostSwitchCoordinator.RestoreInputStateAsync(Task.FromResult(new SessionVolumeSnapshot { MicVolumePercent = 30 }),
            "input", (_, volume, _) => current = volume!.Value, () => true, TestContext.Current.CancellationToken,
            canRestoreVolume: () => guard.CanRestore(current / 100));
        Assert.Equal(30, current);
    }

    [Fact]
    public void InvalidEndpointVolumeFailsClosed()
    {
        Assert.False(new EndpointVolumePreservationState(float.NaN).CanRestore(.3f));
        Assert.False(new EndpointVolumePreservationState(.3f).CanRestore(float.NaN));
    }

    private sealed class UnusedEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }
}
