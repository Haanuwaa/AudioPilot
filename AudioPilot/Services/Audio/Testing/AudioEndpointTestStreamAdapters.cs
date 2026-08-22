using System.Diagnostics.CodeAnalysis;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace AudioPilot.Services.Audio.Testing;

internal interface IAudioTestPlayerAdapter : IAsyncDisposable
{
    event EventHandler<StoppedEventArgs>? PlaybackStopped;

    bool LowLatencyActive { get; }

    int LatencyMilliseconds { get; }

    float Volume { get; set; }

    void Init(IWaveProvider source);

    void Play();
}

internal interface IAudioTestRecorderAdapter : IAsyncDisposable
{
    event CaptureDataAvailableHandler? DataAvailable;

    event EventHandler<StoppedEventArgs>? RecordingStopped;

    WaveFormat WaveFormat { get; }

    bool LowLatencyActive { get; }

    int LatencyMilliseconds { get; }

    void StartRecording();
}

internal interface IAudioTestMonitorPlayerFactory
{
    Task<AudioTestMonitorPlaybackResources> CreateAsync(
        AudioEndpointReference? monitorEndpoint,
        IWaveProvider source,
        float volume,
        CancellationToken cancellationToken);
}

internal sealed record AudioTestMonitorPlaybackResources(
    IAudioTestPlayerAdapter Player,
    IDisposable? Enumerator,
    IDisposable? Device);

[ExcludeFromCodeCoverage(Justification = "Thin forwarding adapter over NAudio's concrete WASAPI player; behavior is covered through fake adapters and opt-in hardware tests.")]
internal sealed class WasapiTestPlayerAdapter(WasapiPlayer player) : IAudioTestPlayerAdapter
{
    public event EventHandler<StoppedEventArgs>? PlaybackStopped
    {
        add => player.PlaybackStopped += value;
        remove => player.PlaybackStopped -= value;
    }

    public bool LowLatencyActive => player.LowLatencyActive;

    public int LatencyMilliseconds => player.LatencyMilliseconds;

    public float Volume
    {
        get => player.Volume;
        set => player.Volume = value;
    }

    public void Init(IWaveProvider source) => player.Init(source);

    public void Play() => player.Play();

    public ValueTask DisposeAsync() => player.DisposeAsync();
}

[ExcludeFromCodeCoverage(Justification = "Thin forwarding adapter over NAudio's concrete WASAPI recorder; behavior is covered through fake adapters and opt-in hardware tests.")]
internal sealed class WasapiTestRecorderAdapter(WasapiRecorder recorder) : IAudioTestRecorderAdapter
{
    public event CaptureDataAvailableHandler? DataAvailable
    {
        add => recorder.DataAvailable += value;
        remove => recorder.DataAvailable -= value;
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => recorder.RecordingStopped += value;
        remove => recorder.RecordingStopped -= value;
    }

    public WaveFormat WaveFormat => recorder.WaveFormat;

    public bool LowLatencyActive => recorder.LowLatencyActive;

    public int LatencyMilliseconds => recorder.LatencyMilliseconds;

    public void StartRecording() => recorder.StartRecording();

    public ValueTask DisposeAsync() => recorder.DisposeAsync();
}

[ExcludeFromCodeCoverage(Justification = "Concrete WASAPI monitor activation requires a real Windows render endpoint and is covered by opt-in hardware tests.")]
internal sealed class WasapiAudioTestMonitorPlayerFactory(Logger? logger = null) : IAudioTestMonitorPlayerFactory
{
    private readonly Logger _logger = logger ?? Logger.Instance;

    public async Task<AudioTestMonitorPlaybackResources> CreateAsync(
        AudioEndpointReference? monitorEndpoint,
        IWaveProvider source,
        float volume,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(CreateAsync));

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiPlayer? player = null;
            try
            {
                var builder = new WasapiPlayerBuilder()
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(50)
                    .WithCategory(AudioStreamCategory.Media);

                if (monitorEndpoint is { Id.Length: > 0 } explicitEndpoint)
                {
                    enumerator = new MMDeviceEnumerator();
                    device = enumerator.GetDevice(explicitEndpoint.Id);
                    if (device.State != NDeviceState.Active || device.DataFlow != DataFlow.Render)
                    {
                        throw new AudioEndpointTestException(
                            AudioEndpointTestFailureKind.Unavailable,
                            $"{explicitEndpoint.Name} is unavailable for microphone monitoring.");
                    }

                    player = builder.WithDevice(device).WithLowLatency(false).Build();
                }
                else
                {
                    player = await builder.WithDefaultDeviceStreamRouting().BuildAsync();
                }

                player.Init(source);
                player.Volume = volume;
                player.Play();
                cancellationToken.ThrowIfCancellationRequested();

                var resources = new AudioTestMonitorPlaybackResources(
                    new WasapiTestPlayerAdapter(player),
                    enumerator,
                    device);
                player = null;
                enumerator = null;
                device = null;
                return resources;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not AudioEndpointTestException)
            {
                throw AudioEndpointTestFailureClassifier.ClassifyActivation(
                    monitorEndpoint ?? new AudioEndpointReference(string.Empty, "Default output"),
                    ex);
            }
            finally
            {
                await AudioEndpointTestResourceDisposer.DisposeAsync(
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
                    disposeException => _logger.Warning(
                        "AudioEndpointTestService",
                        () => $"input-monitor-activation-cleanup-failed | target={LogPrivacy.Device(monitorEndpoint?.Name ?? "Default output")} error={disposeException.GetType().Name}",
                        nameof(CreateAsync),
                        disposeException));
            }
        }, cancellationToken);
    }
}
