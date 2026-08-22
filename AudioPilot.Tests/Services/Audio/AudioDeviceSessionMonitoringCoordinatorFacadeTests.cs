using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionMonitoringCoordinatorFacadeTests
{
    [Fact]
    public void AcquireAndRelease_TrackConsumerCounts()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        var facade = CreateFacade(sessionService, _ => { });

        facade.Acquire(AudioMixerMode.Output);
        Assert.Equal(1, facade.GetConsumerCountForTests(AudioMixerMode.Output));
        Assert.True(facade.IsMonitoringActiveForTests(AudioMixerMode.Output));

        facade.Release(AudioMixerMode.Output);
        Assert.Equal(0, facade.GetConsumerCountForTests(AudioMixerMode.Output));
        Assert.False(facade.IsMonitoringActiveForTests(AudioMixerMode.Output));
    }

    [Fact]
    public void Update_LeavesInactiveMonitoringInactive_WhenThereAreNoConsumers()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        var facade = CreateFacade(sessionService, _ => { });

        facade.Update();

        Assert.False(facade.IsMonitoringActiveForTests(AudioMixerMode.Output));
        Assert.False(facade.IsMonitoringActiveForTests(AudioMixerMode.Input));
    }

    [Fact]
    public void OnEndpointVolumeChanged_EmitsLifecycleSignal()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        AudioSessionLifecycleSignal? received = null;
        var facade = CreateFacade(sessionService, signal => received = signal);

        facade.OnEndpointVolumeChanged(AudioMixerMode.Input, "capture-1", 44f, isMuted: true);

        Assert.NotNull(received);
        Assert.Equal(AudioSessionLifecycleSignalKind.EndpointVolumeChanged, received.Value.Kind);
        Assert.Equal("capture-1", received.Value.EndpointId);
        Assert.Null(received.Value.VolumePercent);
        Assert.Null(received.Value.IsMuted);
    }

    [Fact]
    public void OnEndpointVolumeChanged_CarriesExactStateOnlyForCachedPrimaryEndpoint()
    {
        using var sessionService = new AudioSessionService(new StubEnumerator());
        sessionService.SeedEndpointSnapshotForTests(
            AudioMixerMode.Input,
            new AudioSessionRecentSnapshotCache.EndpointSnapshotEntry(
                "capture-primary",
                "Microphone",
                20f,
                IsMuted: false,
                TimestampTicks: DateTime.UtcNow.Ticks));
        var received = new List<AudioSessionLifecycleSignal>();
        var facade = CreateFacade(sessionService, received.Add);

        facade.OnEndpointVolumeChanged(AudioMixerMode.Input, "capture-secondary", 70f, isMuted: true);
        facade.OnEndpointVolumeChanged(AudioMixerMode.Input, "capture-primary", 44f, isMuted: true);

        Assert.Equal(2, received.Count);
        Assert.Null(received[0].VolumePercent);
        Assert.Null(received[0].IsMuted);
        Assert.Equal(44f, received[1].VolumePercent);
        Assert.True(received[1].IsMuted);
    }

    private static AudioDeviceSessionMonitoringCoordinatorFacade CreateFacade(
        AudioSessionService sessionService,
        Action<AudioSessionLifecycleSignal> onLifecycleChanged)
    {
        var logger = Logger.Instance;
        var playbackCoordinator = new SessionMonitorCoordinator(
            logger,
            AudioMixerMode.Output,
            static () => [],
            static (_, _, _) => { },
            static (_, _, _, _) => { },
            static _ => { },
            static (_, _) => { },
            static () => false);
        var recordingCoordinator = new SessionMonitorCoordinator(
            logger,
            AudioMixerMode.Input,
            static () => [],
            static (_, _, _) => { },
            static (_, _, _, _) => { },
            static _ => { },
            static (_, _) => { },
            static () => false);

        return new AudioDeviceSessionMonitoringCoordinatorFacade(
            logger,
            sessionService,
            playbackCoordinator,
            recordingCoordinator,
            () => false,
            onLifecycleChanged);
    }

    private sealed class StubEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }
}
