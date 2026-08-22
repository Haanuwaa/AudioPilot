using AudioPilot.Models;

namespace AudioPilot.Services.Audio.Testing;

internal enum AudioEndpointTestKind
{
    None,
    Output,
    Input,
}

internal enum AudioEndpointTestPhase
{
    Idle,
    Starting,
    Running,
    Stopping,
    Completed,
    Failed,
}

internal enum AudioEndpointTestFailureKind
{
    None,
    Unavailable,
    Muted,
    ZeroVolume,
    ExclusiveUse,
    UnsupportedFormat,
    ActivationFailed,
    DeviceRemoved,
    Unexpected,
}

internal enum AudioEndpointTestStopReason
{
    User,
    Replaced,
    WindowHidden,
    TabChanged,
    Shutdown,
    Suspend,
    SessionLocked,
    DeviceRemoved,
}

internal readonly record struct AudioEndpointReference(string Id, string Name)
{
    public static AudioEndpointReference FromCycleDevice(CycleDevice device) =>
        new(device.Id ?? string.Empty, device.Name ?? string.Empty);
}

internal readonly record struct AudioInputLevelSnapshot(
    double LevelPercent,
    double PeakPercent,
    double LevelDb,
    long SampleRevision)
{
    public static AudioInputLevelSnapshot Silence { get; } = new(0, 0, -60, 0);
}

internal sealed record AudioEndpointTestState(
    long Revision,
    AudioEndpointTestKind Kind,
    AudioEndpointTestPhase Phase,
    AudioEndpointReference Endpoint,
    string Status,
    AudioEndpointTestFailureKind FailureKind = AudioEndpointTestFailureKind.None,
    bool MonitoringEnabled = false,
    AudioEndpointReference? MonitorEndpoint = null,
    float MonitorVolume = 0.5f,
    DateTimeOffset? StartedAt = null)
{
    public static AudioEndpointTestState Idle { get; } = new(
        0,
        AudioEndpointTestKind.None,
        AudioEndpointTestPhase.Idle,
        new AudioEndpointReference(string.Empty, string.Empty),
        string.Empty);
}

internal sealed class AudioEndpointTestException(
    AudioEndpointTestFailureKind failureKind,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public AudioEndpointTestFailureKind FailureKind { get; } = failureKind;
}

internal interface IAudioEndpointTestService : IAsyncDisposable
{
    event Action<AudioEndpointTestState>? StateChanged;

    AudioEndpointTestState CurrentState { get; }

    AudioInputLevelSnapshot ReadInputLevel();

    Task StartOutputTestAsync(AudioEndpointReference endpoint, CancellationToken cancellationToken = default);

    Task StartInputTestAsync(
        AudioEndpointReference endpoint,
        AudioEndpointReference? initialMonitorEndpoint,
        CancellationToken cancellationToken = default);

    Task ConfigureInputMonitoringAsync(
        bool enabled,
        AudioEndpointReference? monitorEndpoint,
        float volume,
        CancellationToken cancellationToken = default);

    Task StopAsync(AudioEndpointTestStopReason reason, CancellationToken cancellationToken = default);

    Task ReconcileActiveEndpointsAsync(CancellationToken cancellationToken = default);
}

internal interface IAudioEndpointTestSession : IAsyncDisposable
{
    AudioEndpointReference Endpoint { get; }

    Task Completion { get; }
}

internal interface IAudioOutputTestSession : IAudioEndpointTestSession
{
}

internal interface IAudioInputTestSession : IAudioEndpointTestSession
{
    bool MonitoringEnabled { get; }

    AudioEndpointReference? MonitorEndpoint { get; }

    float MonitorVolume { get; }

    AudioInputLevelSnapshot ReadLevel();

    Task ConfigureMonitoringAsync(
        bool enabled,
        AudioEndpointReference? monitorEndpoint,
        float volume,
        CancellationToken cancellationToken);
}

internal interface IAudioEndpointTestSessionFactory
{
    Task<IAudioOutputTestSession> CreateOutputAsync(
        AudioEndpointReference endpoint,
        CancellationToken cancellationToken);

    Task<IAudioInputTestSession> CreateInputAsync(
        AudioEndpointReference endpoint,
        AudioEndpointReference? initialMonitorEndpoint,
        CancellationToken cancellationToken);

    bool IsEndpointActive(AudioEndpointReference endpoint, bool output);
}
