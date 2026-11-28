using System.Diagnostics.CodeAnalysis;
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
                EnsureOutputAudible(_logger, device, endpoint);

                using AudioClient audioClient = device.CreateAudioClient();
                bool stereo = audioClient.MixFormat.Channels >= 2;
                int sampleRate = audioClient.MixFormat.SampleRate > 0
                    ? audioClient.MixFormat.SampleRate
                    : AudioTestChimeWaveProvider.SampleRate;
                var chime = new AudioTestChimeWaveProvider(stereo, sampleRate, gain: 0.25f);
                player = new WasapiPlayerBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(50)
                    .WithLowLatency()
                    .WithCategory(AudioStreamCategory.Media)
                    .Build();
                player.Init(chime);
                EnsureAppSessionAudible(_logger, player, endpoint);
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
                    disposeException => LogFailedStartCleanup(endpoint, "output", disposeException)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioEndpointTestException)
            {
                throw AudioEndpointTestFailureClassifier.Classify(endpoint, ex);
            }
            finally
            {
                await DisposeUnownedActivationResourcesAsync(
                    player,
                    device,
                    enumerator,
                    endpoint,
                    "output").ConfigureAwait(false);
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
                recorder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithBufferLength(50)
                    .WithLowLatency()
                    .Build();
                cancellationToken.ThrowIfCancellationRequested();

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
                    disposeException => LogFailedStartCleanup(endpoint, "input", disposeException)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioEndpointTestException)
            {
                throw AudioEndpointTestFailureClassifier.Classify(endpoint, ex);
            }
            finally
            {
                await DisposeUnownedActivationResourcesAsync(
                    recorder,
                    device,
                    enumerator,
                    endpoint,
                    "input").ConfigureAwait(false);
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

        try
        {
            if (device.State != NDeviceState.Active || device.DataFlow != expectedFlow)
            {
                throw new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.Unavailable,
                    $"{endpoint.Name} is not currently active.");
            }

            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    internal static void EnsureOutputAudible(Logger logger, MMDevice device, AudioEndpointReference endpoint)
    {
        if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out AudioEndpointVolume? endpointVolume, "output-test"))
        {
            return;
        }

        using (endpointVolume)
        {
            if (endpointVolume.Mute)
            {
                throw new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.Muted,
                    $"{endpoint.Name} is muted. Unmute it and try again.");
            }

            if (endpointVolume.MasterVolumeLevelScalar <= 0.0001f)
            {
                throw new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.ZeroVolume,
                    $"{endpoint.Name} is set to 0% volume. Raise it and try again.");
            }
        }
    }

    internal static void EnsureAppSessionAudible(Logger logger, WasapiPlayer player, AudioEndpointReference endpoint)
    {
        try
        {
            if (player.IsMuted)
            {
                throw new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.Muted,
                    "AudioPilot is muted in the Windows Volume Mixer. Unmute it and try again.");
            }

            if (player.Volume <= 0.0001f)
            {
                throw new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.ZeroVolume,
                    "AudioPilot is set to 0% volume in the Windows Volume Mixer. Raise it and try again.");
            }
        }
        catch (AudioEndpointTestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Trace(
                "AudioEndpointTestService",
                () => $"output-test-session-volume-probe-skipped | endpoint={LogPrivacy.Device(endpoint.Name)} error={ex.GetType().Name}");
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
                disposeException)).ConfigureAwait(false);
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
            $"output-test-stream-started | endpoint={LogPrivacy.Device(Endpoint.Name)} lowLatency={_player.LowLatencyActive} latencyMs={_player.LatencyMilliseconds} lowLatencyUnavailableReason={_player.LowLatencyUnavailableReason ?? "none"}");
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

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
            return;
        }

        await DisposeCoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
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
            failure => (failures ??= []).Add(failure)).ConfigureAwait(false);
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
    private EventHandler<StoppedEventArgs>? _monitorStoppedHandler;
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
            $"input-test-stream-started | endpoint={LogPrivacy.Device(Endpoint.Name)} lowLatency={_recorder.LowLatencyActive} latencyMs={_recorder.LatencyMilliseconds} lowLatencyUnavailableReason={_recorder.LowLatencyUnavailableReason ?? "none"} format={_recorder.WaveFormat}");
    }

    public AudioInputLevelSnapshot ReadLevel() => _meter.Read();

    public async Task ConfigureMonitoringAsync(
        bool enabled,
        AudioEndpointReference? monitorEndpoint,
        float volume,
        CancellationToken cancellationToken)
    {
        await _monitorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            float normalizedVolume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0f;
            bool sameEndpoint = Nullable.Equals(_monitorEndpoint, monitorEndpoint);
            if (enabled && MonitoringEnabled && sameEndpoint)
            {
                _monitorVolume = normalizedVolume;
                if (Volatile.Read(ref _monitorBuffer) is { } activeBuffer)
                {
                    activeBuffer.Gain = normalizedVolume;
                }

                return;
            }

            if (!enabled)
            {
                _monitorEndpoint = monitorEndpoint;
                _monitorVolume = normalizedVolume;
                await DisposeMonitorCoreAsync().ConfigureAwait(false);
                return;
            }

            var buffer = new LowLatencyMonitorBuffer(_recorder.WaveFormat, MonitorBufferDuration, normalizedVolume);
            AudioTestMonitorPlaybackResources resources = await _monitorPlayerFactory.CreateAsync(
                monitorEndpoint,
                buffer,
                normalizedVolume,
                cancellationToken).ConfigureAwait(false);

            bool adopted = false;
            Exception? cleanupFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await DisposeMonitorCoreAsync().ConfigureAwait(false); }
                catch (Exception ex) { cleanupFailure = ex; }

                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _monitorEndpoint = monitorEndpoint;
                _monitorVolume = normalizedVolume;
                _monitorDevice = resources.Device;
                _monitorEnumerator = resources.Enumerator;
                _monitorStoppedHandler = (_, args) =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref _monitorPlayer), resources.Player)) return;
                    _completion.TrySetException(new AudioEndpointTestException(
                        AudioEndpointTestFailureKind.Unexpected,
                        "Microphone monitoring stopped unexpectedly. Start the microphone test again.",
                        args.Exception));
                };
                Volatile.Write(ref _monitorPlayer, resources.Player);
                Volatile.Write(ref _monitorBuffer, buffer);
                adopted = true;
                try
                {
                    resources.Player.PlaybackStopped += _monitorStoppedHandler;
                    if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    {
                        await Task.Run(resources.Player.Play, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        resources.Player.Play();
                    }
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(new AudioEndpointTestException(
                        AudioEndpointTestFailureKind.Unexpected,
                        "Microphone monitoring could not start. Start the microphone test again.",
                        ex));
                    throw;
                }
            }
            finally
            {
                if (!adopted)
                {
                    if (cleanupFailure != null) LogMonitorCleanupFailure(cleanupFailure);
                    await DisposeMonitorResourcesAsync(resources.Player, resources.Device, resources.Enumerator, LogMonitorCleanupFailure).ConfigureAwait(false);
                }
            }

            _logger.Debug("AudioEndpointTestService", () =>
                $"input-monitor-started | target={LogPrivacy.Device(monitorEndpoint?.Name ?? "Default output")} volume={normalizedVolume:P0} lowLatency={resources.Player.LowLatencyActive} latencyMs={resources.Player.LatencyMilliseconds} lowLatencyUnavailableReason={resources.Player.LowLatencyUnavailableReason ?? "none"}");
            if (cleanupFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cleanupFailure);
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private void LogMonitorCleanupFailure(Exception exception) =>
        _logger.Warning("AudioEndpointTestService", "input-monitor-cleanup-failed", nameof(ConfigureMonitoringAsync), exception);

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        try
        {
            _meter.Process(buffer, _recorder.WaveFormat, flags);
            Volatile.Read(ref _monitorBuffer)?.AddSamples(buffer, flags.HasFlag(AudioClientBufferFlags.Silent));
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
        EventHandler<StoppedEventArgs>? stoppedHandler = _monitorStoppedHandler;
        _monitorStoppedHandler = null;
        if (player != null && stoppedHandler != null)
        {
            player.PlaybackStopped -= stoppedHandler;
        }
        IDisposable? device = Interlocked.Exchange(ref _monitorDevice, null);
        IDisposable? enumerator = Interlocked.Exchange(ref _monitorEnumerator, null);
        long droppedBytes = buffer?.DroppedBytes ?? 0;
        buffer?.Clear();

        List<Exception>? failures = null;
        await DisposeMonitorResourcesAsync(
            player,
            device,
            enumerator,
            failure => (failures ??= []).Add(failure)).ConfigureAwait(false);

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

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            await Task.Run(DisposeCoreAsync).ConfigureAwait(false);
            return;
        }

        await DisposeCoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
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
            failure => (failures ??= []).Add(failure)).ConfigureAwait(false);
        await _monitorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await DisposeMonitorCoreAsync().ConfigureAwait(false);
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
            failure => (failures ??= []).Add(failure)).ConfigureAwait(false);
        _completion.TrySetResult(null);
        AudioEndpointTestResourceDisposer.ThrowIfAny(failures);
    }
}
