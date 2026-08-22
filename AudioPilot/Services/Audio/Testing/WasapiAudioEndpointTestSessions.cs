using System.Diagnostics.CodeAnalysis;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace AudioPilot.Services.Audio.Testing;

[ExcludeFromCodeCoverage(Justification = "Concrete WASAPI activation requires real Windows audio endpoints and is covered by opt-in hardware tests.")]
internal sealed class WasapiAudioEndpointTestSessionFactory(Logger? logger = null) : IAudioEndpointTestSessionFactory
{
    private readonly Logger _logger = logger ?? Logger.Instance;

    public Task<IAudioOutputTestSession> CreateOutputAsync(
        AudioEndpointReference endpoint,
        CancellationToken cancellationToken)
    {
        return Task.Run<IAudioOutputTestSession>(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(CreateOutputAsync));

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiPlayer? player = null;
            WasapiOutputTestSession? session = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = ResolveActiveDevice(enumerator, endpoint, DataFlow.Render);
                EnsureOutputAudible(device);

                using AudioClient audioClient = device.CreateAudioClient();
                bool stereo = audioClient.MixFormat.Channels >= 2;
                var chime = new AudioTestChimeWaveProvider(stereo);
                player = BuildOutputPlayer(device, chime, endpoint);
                player.Volume = 0.25f;
                cancellationToken.ThrowIfCancellationRequested();

                session = new WasapiOutputTestSession(
                    endpoint,
                    enumerator,
                    device,
                    new WasapiTestPlayerAdapter(player),
                    _logger);
                enumerator = null;
                device = null;
                player = null;
                return await AudioEndpointTestResourceDisposer.StartOrDisposeAsync(
                    session,
                    session.Start,
                    disposeException => LogFailedStartCleanup(endpoint, "output", disposeException));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioEndpointTestException)
            {
                throw AudioEndpointTestFailureClassifier.ClassifyActivation(endpoint, ex);
            }
            finally
            {
                await DisposeUnownedActivationResourcesAsync(
                    player,
                    device,
                    enumerator,
                    endpoint,
                    "output");
            }
        }, cancellationToken);
    }

    public Task<IAudioInputTestSession> CreateInputAsync(
        AudioEndpointReference endpoint,
        AudioEndpointReference? initialMonitorEndpoint,
        CancellationToken cancellationToken)
    {
        return Task.Run<IAudioInputTestSession>(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(CreateInputAsync));

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiRecorder? recorder = null;
            WasapiInputTestSession? session = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = ResolveActiveDevice(enumerator, endpoint, DataFlow.Capture);
                recorder = BuildInputRecorder(device, endpoint);

                session = new WasapiInputTestSession(
                    endpoint,
                    initialMonitorEndpoint,
                    enumerator,
                    device,
                    new WasapiTestRecorderAdapter(recorder),
                    new WasapiAudioTestMonitorPlayerFactory(_logger),
                    _logger);
                enumerator = null;
                device = null;
                recorder = null;
                return await AudioEndpointTestResourceDisposer.StartOrDisposeAsync(
                    session,
                    session.Start,
                    disposeException => LogFailedStartCleanup(endpoint, "input", disposeException));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioEndpointTestException)
            {
                throw AudioEndpointTestFailureClassifier.ClassifyActivation(endpoint, ex);
            }
            finally
            {
                await DisposeUnownedActivationResourcesAsync(
                    recorder,
                    device,
                    enumerator,
                    endpoint,
                    "input");
            }
        }, cancellationToken);
    }

    public bool IsEndpointActive(AudioEndpointReference endpoint, bool output)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Id))
        {
            return output;
        }

        try
        {
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(IsEndpointActive));
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDevice(endpoint.Id);
            return device.State == NDeviceState.Active &&
                (output ? device.DataFlow == DataFlow.Render : device.DataFlow == DataFlow.Capture);
        }
        catch
        {
            return false;
        }
    }

    private WasapiPlayer BuildOutputPlayer(
        MMDevice device,
        AudioTestChimeWaveProvider chime,
        AudioEndpointReference endpoint)
    {
        WasapiPlayer? lowLatencyPlayer = null;
        try
        {
            lowLatencyPlayer = CreateOutputPlayer(device, lowLatency: true);
            lowLatencyPlayer.Init(chime);
            return lowLatencyPlayer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lowLatencyPlayer?.Dispose();
            _logger.Debug("AudioEndpointTestService", () =>
                $"output-test-normal-latency-fallback | endpoint={LogPrivacy.Device(endpoint.Name)} error={ex.GetType().Name}");
            WasapiPlayer fallback = CreateOutputPlayer(device, lowLatency: false);
            fallback.Init(chime);
            return fallback;
        }
    }

    private static WasapiPlayer CreateOutputPlayer(MMDevice device, bool lowLatency) =>
        new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(50)
            .WithLowLatency(lowLatency)
            .WithCategory(AudioStreamCategory.Media)
            .Build();

    private WasapiRecorder BuildInputRecorder(MMDevice device, AudioEndpointReference endpoint)
    {
        try
        {
            return CreateInputRecorder(device, lowLatency: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug("AudioEndpointTestService", () =>
                $"input-test-normal-latency-fallback | endpoint={LogPrivacy.Device(endpoint.Name)} error={ex.GetType().Name}");
            return CreateInputRecorder(device, lowLatency: false);
        }
    }

    private static WasapiRecorder CreateInputRecorder(MMDevice device, bool lowLatency) =>
        new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(50)
            .WithLowLatency(lowLatency)
            .Build();

    private static MMDevice ResolveActiveDevice(
        MMDeviceEnumerator enumerator,
        AudioEndpointReference endpoint,
        DataFlow expectedFlow)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Id))
        {
            throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Unavailable,
                "The selected audio device is unavailable.");
        }

        MMDevice device;
        try
        {
            device = enumerator.GetDevice(endpoint.Id);
        }
        catch (Exception ex)
        {
            throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Unavailable,
                $"{endpoint.Name} is disconnected or unavailable.",
                ex);
        }

        if (device.State != NDeviceState.Active || device.DataFlow != expectedFlow)
        {
            device.Dispose();
            throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Unavailable,
                $"{endpoint.Name} is not currently active.");
        }

        return device;
    }

    private static void EnsureOutputAudible(MMDevice device)
    {
        using AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
        if (endpointVolume.Mute)
        {
            throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Muted,
                $"{device.FriendlyName} is muted. Unmute it and try again.");
        }

        if (endpointVolume.MasterVolumeLevelScalar <= 0.0001f)
        {
            throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.ZeroVolume,
                $"{device.FriendlyName} is set to 0% volume. Raise it and try again.");
        }
    }

    private void LogFailedStartCleanup(
        AudioEndpointReference endpoint,
        string kind,
        Exception disposeException)
    {
        _logger.Warning(
            "AudioEndpointTestService",
            () => $"audio-test-start-failure-cleanup-failed | kind={kind} endpoint={LogPrivacy.Device(endpoint.Name)} error={disposeException.GetType().Name}",
            nameof(LogFailedStartCleanup),
            disposeException);
    }

    private async Task DisposeUnownedActivationResourcesAsync(
        IAsyncDisposable? stream,
        MMDevice? device,
        MMDeviceEnumerator? enumerator,
        AudioEndpointReference endpoint,
        string kind)
    {
        await AudioEndpointTestResourceDisposer.DisposeAsync(
            [
                () => stream?.DisposeAsync() ?? ValueTask.CompletedTask,
                () =>
                {
                    device?.Dispose();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    enumerator?.Dispose();
                    return ValueTask.CompletedTask;
                },
            ],
            disposeException => _logger.Warning(
                "AudioEndpointTestService",
                () => $"audio-test-activation-cleanup-failed | kind={kind} endpoint={LogPrivacy.Device(endpoint.Name)} error={disposeException.GetType().Name}",
                nameof(DisposeUnownedActivationResourcesAsync),
                disposeException));
    }
}

internal sealed class WasapiOutputTestSession : IAudioOutputTestSession
{
    private readonly IDisposable _enumerator;
    private readonly IDisposable _device;
    private readonly IAudioTestPlayerAdapter _player;
    private readonly Logger _logger;
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public WasapiOutputTestSession(
        AudioEndpointReference endpoint,
        IDisposable enumerator,
        IDisposable device,
        IAudioTestPlayerAdapter player,
        Logger logger)
    {
        Endpoint = endpoint;
        _enumerator = enumerator;
        _device = device;
        _player = player;
        _logger = logger;
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    public AudioEndpointReference Endpoint { get; }

    public Task Completion => _completion.Task;

    internal void Start()
    {
        _player.Play();
        _logger.Debug("AudioEndpointTestService", () =>
            $"output-test-stream-started | endpoint={LogPrivacy.Device(Endpoint.Name)} lowLatency={_player.LowLatencyActive} latencyMs={_player.LatencyMilliseconds}");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _completion.TrySetException(e.Exception);
        }
        else
        {
            _completion.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        await AudioEndpointTestResourceDisposer.DisposeAsync(
            [
                () =>
                {
                    _player.PlaybackStopped -= OnPlaybackStopped;
                    return ValueTask.CompletedTask;
                },
                () => _player.DisposeAsync(),
                () =>
                {
                    _device.Dispose();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    _enumerator.Dispose();
                    return ValueTask.CompletedTask;
                },
            ],
            failure => (failures ??= []).Add(failure));
        _completion.TrySetResult(null);
        AudioEndpointTestResourceDisposer.ThrowIfAny(failures);
    }
}

internal sealed class WasapiInputTestSession : IAudioInputTestSession
{
    private static readonly TimeSpan MonitorBufferDuration = TimeSpan.FromMilliseconds(250);
    private readonly IDisposable _captureEnumerator;
    private readonly IDisposable _captureDevice;
    private readonly IAudioTestRecorderAdapter _recorder;
    private readonly IAudioTestMonitorPlayerFactory _monitorPlayerFactory;
    private readonly Logger _logger;
    private readonly AudioInputLevelMeter _meter = new();
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LowLatencyMonitorBuffer? _monitorBuffer;
    private IAudioTestPlayerAdapter? _monitorPlayer;
    private IDisposable? _monitorEnumerator;
    private IDisposable? _monitorDevice;
    private AudioEndpointReference? _monitorEndpoint;
    private float _monitorVolume = 0.5f;
    private int _disposed;

    public WasapiInputTestSession(
        AudioEndpointReference endpoint,
        AudioEndpointReference? initialMonitorEndpoint,
        IDisposable captureEnumerator,
        IDisposable captureDevice,
        IAudioTestRecorderAdapter recorder,
        IAudioTestMonitorPlayerFactory monitorPlayerFactory,
        Logger logger)
    {
        Endpoint = endpoint;
        _monitorEndpoint = initialMonitorEndpoint;
        _captureEnumerator = captureEnumerator;
        _captureDevice = captureDevice;
        _recorder = recorder;
        _monitorPlayerFactory = monitorPlayerFactory;
        _logger = logger;
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public AudioEndpointReference Endpoint { get; }

    public Task Completion => _completion.Task;

    public bool MonitoringEnabled => Volatile.Read(ref _monitorPlayer) != null;

    public AudioEndpointReference? MonitorEndpoint => _monitorEndpoint;

    public float MonitorVolume => _monitorVolume;

    internal void Start()
    {
        _recorder.StartRecording();
        _logger.Debug("AudioEndpointTestService", () =>
            $"input-test-stream-started | endpoint={LogPrivacy.Device(Endpoint.Name)} lowLatency={_recorder.LowLatencyActive} latencyMs={_recorder.LatencyMilliseconds} format={_recorder.WaveFormat}");
    }

    public AudioInputLevelSnapshot ReadLevel() => _meter.Read();

    public async Task ConfigureMonitoringAsync(
        bool enabled,
        AudioEndpointReference? monitorEndpoint,
        float volume,
        CancellationToken cancellationToken)
    {
        await _monitorGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            float normalizedVolume = Math.Clamp(volume, 0, 1);
            bool sameEndpoint = Nullable.Equals(_monitorEndpoint, monitorEndpoint);
            if (enabled && MonitoringEnabled && sameEndpoint)
            {
                _monitorVolume = normalizedVolume;
                _monitorPlayer!.Volume = normalizedVolume;
                return;
            }

            if (!enabled)
            {
                _monitorEndpoint = monitorEndpoint;
                _monitorVolume = normalizedVolume;
                await DisposeMonitorCoreAsync();
                return;
            }

            var buffer = new LowLatencyMonitorBuffer(_recorder.WaveFormat, MonitorBufferDuration);
            AudioTestMonitorPlaybackResources resources = await _monitorPlayerFactory.CreateAsync(
                monitorEndpoint,
                buffer,
                normalizedVolume,
                cancellationToken);

            IAudioTestPlayerAdapter? oldPlayer = Interlocked.Exchange(ref _monitorPlayer, resources.Player);
            LowLatencyMonitorBuffer? oldBuffer = Interlocked.Exchange(ref _monitorBuffer, buffer);
            IDisposable? oldEnumerator = Interlocked.Exchange(ref _monitorEnumerator, resources.Enumerator);
            IDisposable? oldDevice = Interlocked.Exchange(ref _monitorDevice, resources.Device);
            _monitorEndpoint = monitorEndpoint;
            _monitorVolume = normalizedVolume;

            oldBuffer?.Clear();
            List<Exception>? failures = null;
            await DisposeMonitorResourcesAsync(
                oldPlayer,
                oldDevice,
                oldEnumerator,
                failure => (failures ??= []).Add(failure));

            _logger.Debug("AudioEndpointTestService", () =>
                $"input-monitor-started | target={LogPrivacy.Device(monitorEndpoint?.Name ?? "Default output")} volume={normalizedVolume:P0} lowLatency={resources.Player.LowLatencyActive} latencyMs={resources.Player.LatencyMilliseconds}");
            AudioEndpointTestResourceDisposer.ThrowIfAny(failures);
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        try
        {
            _meter.Process(buffer, _recorder.WaveFormat, flags);
            Volatile.Read(ref _monitorBuffer)?.AddSamples(buffer);
        }
        catch (AudioEndpointTestException ex)
        {
            _completion.TrySetException(ex);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Unexpected,
                "Microphone data processing failed.",
                ex));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _completion.TrySetException(e.Exception);
        }
        else
        {
            _completion.TrySetResult(null);
        }
    }

    private async Task DisposeMonitorCoreAsync()
    {
        LowLatencyMonitorBuffer? buffer = Interlocked.Exchange(ref _monitorBuffer, null);
        IAudioTestPlayerAdapter? player = Interlocked.Exchange(ref _monitorPlayer, null);
        IDisposable? device = Interlocked.Exchange(ref _monitorDevice, null);
        IDisposable? enumerator = Interlocked.Exchange(ref _monitorEnumerator, null);
        long droppedBytes = buffer?.DroppedBytes ?? 0;
        buffer?.Clear();

        List<Exception>? failures = null;
        await DisposeMonitorResourcesAsync(
            player,
            device,
            enumerator,
            failure => (failures ??= []).Add(failure));

        if (droppedBytes > 0)
        {
            _logger.Debug("AudioEndpointTestService", () => $"input-monitor-stopped | droppedBytes={droppedBytes}");
        }

        AudioEndpointTestResourceDisposer.ThrowIfAny(failures);
    }

    private static ValueTask DisposeMonitorResourcesAsync(
        IAudioTestPlayerAdapter? player,
        IDisposable? device,
        IDisposable? enumerator,
        Action<Exception> recordFailure) =>
        AudioEndpointTestResourceDisposer.DisposeAsync(
            [
                () => player?.DisposeAsync() ?? ValueTask.CompletedTask,
                () =>
                {
                    device?.Dispose();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    enumerator?.Dispose();
                    return ValueTask.CompletedTask;
                },
            ],
            recordFailure);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        await AudioEndpointTestResourceDisposer.DisposeAsync(
            [
                () =>
                {
                    _recorder.DataAvailable -= OnDataAvailable;
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    _recorder.RecordingStopped -= OnRecordingStopped;
                    return ValueTask.CompletedTask;
                },
            ],
            failure => (failures ??= []).Add(failure));
        await _monitorGate.WaitAsync();
        try
        {
            try
            {
                await DisposeMonitorCoreAsync();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }
        finally
        {
            _monitorGate.Release();
        }

        await AudioEndpointTestResourceDisposer.DisposeAsync(
            [
                () => _recorder.DisposeAsync(),
                () =>
                {
                    _captureDevice.Dispose();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    _captureEnumerator.Dispose();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    _monitorGate.Dispose();
                    return ValueTask.CompletedTask;
                },
            ],
            failure => (failures ??= []).Add(failure));
        _completion.TrySetResult(null);
        AudioEndpointTestResourceDisposer.ThrowIfAny(failures);
    }
}
