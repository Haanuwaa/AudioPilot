using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

public sealed class WasapiAudioEndpointTestSessionTests
{
    private static readonly AudioEndpointReference Output = new("output", "Output");
    private static readonly AudioEndpointReference Input = new("input", "Input");

    [Fact]
    public async Task OutputStartFailure_DisposesEveryOwnedResourceAndPreservesStartFailure()
    {
        using var logger = TestLoggerScope.CreateInMemory("output-start-failure.log");
        var player = new FakePlayer { PlayFailure = new InvalidOperationException("start failed") };
        var device = new FakeDisposable();
        var enumerator = new FakeDisposable { Failure = new InvalidOperationException("enumerator dispose") };
        var session = new WasapiOutputTestSession(Output, enumerator, device, player, logger.Logger);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AudioEndpointTestResourceDisposer.StartOrDisposeAsync(
                session,
                session.Start,
                static _ => { }));

        Assert.Equal("start failed", failure.Message);
        Assert.Equal(1, player.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public async Task OutputDisposeFailure_DoesNotSkipDeviceOrEnumeratorCleanup()
    {
        using var logger = TestLoggerScope.CreateInMemory("output-dispose-failure.log");
        var player = new FakePlayer { DisposeFailure = new InvalidOperationException("player dispose") };
        var device = new FakeDisposable { Failure = new InvalidOperationException("device dispose") };
        var enumerator = new FakeDisposable { Failure = new InvalidOperationException("enumerator dispose") };
        var session = new WasapiOutputTestSession(Output, enumerator, device, player, logger.Logger);

        await Assert.ThrowsAnyAsync<Exception>(async () => await session.DisposeAsync());

        Assert.Equal(1, player.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public async Task InputStartFailure_DisposesRecorderDeviceEnumeratorAndGate()
    {
        using var logger = TestLoggerScope.CreateInMemory("input-start-failure.log");
        var recorder = new FakeRecorder { StartFailure = new InvalidOperationException("capture start") };
        var device = new FakeDisposable();
        var enumerator = new FakeDisposable();
        var session = new WasapiInputTestSession(
            Input,
            null,
            enumerator,
            device,
            recorder,
            new FakeMonitorFactory(),
            logger.Logger);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AudioEndpointTestResourceDisposer.StartOrDisposeAsync(
                session,
                session.Start,
                static _ => { }));

        Assert.Equal("capture start", failure.Message);
        Assert.Equal(1, recorder.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public async Task InputDisposeFailures_DoNotSkipAnyCaptureOrMonitorResource()
    {
        using var logger = TestLoggerScope.CreateInMemory("input-dispose-failures.log");
        var recorder = new FakeRecorder { DisposeFailure = new InvalidOperationException("recorder dispose") };
        var captureDevice = new FakeDisposable { Failure = new InvalidOperationException("capture device dispose") };
        var captureEnumerator = new FakeDisposable { Failure = new InvalidOperationException("capture enumerator dispose") };
        var monitorPlayer = new FakePlayer { DisposeFailure = new InvalidOperationException("monitor player dispose") };
        var monitorDevice = new FakeDisposable { Failure = new InvalidOperationException("monitor device dispose") };
        var monitorEnumerator = new FakeDisposable { Failure = new InvalidOperationException("monitor enumerator dispose") };
        var monitorFactory = new FakeMonitorFactory();
        monitorFactory.Results.Enqueue(new AudioTestMonitorPlaybackResources(monitorPlayer, monitorEnumerator, monitorDevice));
        var session = new WasapiInputTestSession(
            Input,
            null,
            captureEnumerator,
            captureDevice,
            recorder,
            monitorFactory,
            logger.Logger);
        await session.ConfigureMonitoringAsync(true, Output, 0.5f, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(async () => await session.DisposeAsync());

        Assert.Equal(1, monitorPlayer.DisposeCount);
        Assert.Equal(1, monitorDevice.DisposeCount);
        Assert.Equal(1, monitorEnumerator.DisposeCount);
        Assert.Equal(1, recorder.DisposeCount);
        Assert.Equal(1, captureDevice.DisposeCount);
        Assert.Equal(1, captureEnumerator.DisposeCount);
    }

    [Fact]
    public async Task MonitorReplacementCleanupFailure_StillDisposesAllOldResourcesAndKeepsNewMonitorOwned()
    {
        using var logger = TestLoggerScope.CreateInMemory("monitor-replacement-failure.log");
        var firstPlayer = new FakePlayer { DisposeFailure = new InvalidOperationException("old player dispose") };
        var firstDevice = new FakeDisposable { Failure = new InvalidOperationException("old device dispose") };
        var firstEnumerator = new FakeDisposable();
        var secondPlayer = new FakePlayer();
        var secondDevice = new FakeDisposable();
        var secondEnumerator = new FakeDisposable();
        var monitorFactory = new FakeMonitorFactory();
        monitorFactory.Results.Enqueue(new AudioTestMonitorPlaybackResources(firstPlayer, firstEnumerator, firstDevice));
        monitorFactory.Results.Enqueue(new AudioTestMonitorPlaybackResources(secondPlayer, secondEnumerator, secondDevice));
        var session = new WasapiInputTestSession(
            Input,
            null,
            new FakeDisposable(),
            new FakeDisposable(),
            new FakeRecorder(),
            monitorFactory,
            logger.Logger);
        await session.ConfigureMonitoringAsync(true, Output, 0.5f, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => session.ConfigureMonitoringAsync(
            true,
            new AudioEndpointReference("output-2", "Output 2"),
            0.6f,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, firstPlayer.DisposeCount);
        Assert.Equal(1, firstDevice.DisposeCount);
        Assert.Equal(1, firstEnumerator.DisposeCount);
        Assert.True(session.MonitoringEnabled);
        Assert.Equal("output-2", session.MonitorEndpoint?.Id);

        await session.DisposeAsync();
        Assert.Equal(1, secondPlayer.DisposeCount);
        Assert.Equal(1, secondDevice.DisposeCount);
        Assert.Equal(1, secondEnumerator.DisposeCount);
    }

    [Fact]
    public async Task RepeatedMonitorReconfiguration_DisposesEveryReplacedResourceExactlyOnce()
    {
        using var logger = TestLoggerScope.CreateInMemory("monitor-reconfiguration.log");
        var monitorFactory = new FakeMonitorFactory();
        var resources = Enumerable.Range(0, 20)
            .Select(_ => new AudioTestMonitorPlaybackResources(
                new FakePlayer(),
                new FakeDisposable(),
                new FakeDisposable()))
            .ToArray();
        foreach (AudioTestMonitorPlaybackResources resource in resources)
        {
            monitorFactory.Results.Enqueue(resource);
        }

        var session = new WasapiInputTestSession(
            Input,
            null,
            new FakeDisposable(),
            new FakeDisposable(),
            new FakeRecorder(),
            monitorFactory,
            logger.Logger);

        for (int index = 0; index < resources.Length; index++)
        {
            await session.ConfigureMonitoringAsync(
                true,
                new AudioEndpointReference($"monitor-{index}", $"Monitor {index}"),
                0.5f,
                TestContext.Current.CancellationToken);
        }

        await session.ConfigureMonitoringAsync(false, null, 0.5f, TestContext.Current.CancellationToken);
        await session.DisposeAsync();
        await session.DisposeAsync();

        foreach (AudioTestMonitorPlaybackResources resource in resources)
        {
            Assert.Equal(1, Assert.IsType<FakePlayer>(resource.Player).DisposeCount);
            Assert.Equal(1, Assert.IsType<FakeDisposable>(resource.Device).DisposeCount);
            Assert.Equal(1, Assert.IsType<FakeDisposable>(resource.Enumerator).DisposeCount);
        }
    }

    private sealed class FakePlayer : IAudioTestPlayerAdapter
    {
        private EventHandler<StoppedEventArgs>? _playbackStopped;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped
        {
            add => _playbackStopped += value;
            remove => _playbackStopped -= value;
        }

        public bool LowLatencyActive { get; init; }
        public int LatencyMilliseconds { get; init; } = 50;
        public float Volume { get; set; }
        public Exception? PlayFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int DisposeCount { get; private set; }
        public void Init(IWaveProvider source) { }
        public void Play()
        {
            if (PlayFailure != null) throw PlayFailure;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailure == null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class FakeRecorder : IAudioTestRecorderAdapter
    {
        private CaptureDataAvailableHandler? _dataAvailable;
        private EventHandler<StoppedEventArgs>? _recordingStopped;

        public event CaptureDataAvailableHandler? DataAvailable
        {
            add => _dataAvailable += value;
            remove => _dataAvailable -= value;
        }

        public event EventHandler<StoppedEventArgs>? RecordingStopped
        {
            add => _recordingStopped += value;
            remove => _recordingStopped -= value;
        }

        public WaveFormat WaveFormat { get; } = new WaveFormat(48_000, 16, 1);
        public bool LowLatencyActive { get; init; }
        public int LatencyMilliseconds { get; init; } = 50;
        public Exception? StartFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int DisposeCount { get; private set; }
        public void StartRecording()
        {
            if (StartFailure != null) throw StartFailure;
        }
        public void RaiseData(byte[] data) => _dataAvailable?.Invoke(data, AudioClientBufferFlags.None, 0, 0);
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailure == null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class FakeMonitorFactory : IAudioTestMonitorPlayerFactory
    {
        public Queue<AudioTestMonitorPlaybackResources> Results { get; } = [];

        public Task<AudioTestMonitorPlaybackResources> CreateAsync(
            AudioEndpointReference? monitorEndpoint,
            IWaveProvider source,
            float volume,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioTestMonitorPlaybackResources resources = Results.Dequeue();
            resources.Player.Volume = volume;
            return Task.FromResult(resources);
        }
    }

    private sealed class FakeDisposable : IDisposable
    {
        public Exception? Failure { get; init; }
        public int DisposeCount { get; private set; }
        public void Dispose()
        {
            DisposeCount++;
            if (Failure != null) throw Failure;
        }
    }
}
