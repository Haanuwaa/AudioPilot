using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Routines;

internal interface IRoutineVolumeEndpoint : IDisposable
{
    string Id { get; }
    float Volume { get; set; }
    Guid NotificationGuid { get; set; }
    event Action<Guid, float>? Changed;
}

internal readonly record struct RoutineVolumeApplicationResult(bool Success, IRoutineAudioChange? Change = null);

/// <summary>Restores an explicit volume action on its original endpoint unless a newer volume choice supersedes it.</summary>
internal static class RoutineEndpointVolumeService
{
    private static readonly Lock OwnershipLock = new();
    private static readonly Dictionary<string, VolumeChange> Owners = new(StringComparer.OrdinalIgnoreCase);

    internal static RoutineVolumeApplicationResult Apply(AudioDeviceService audio, Logger logger, bool playback,
        string? deviceId, int percent, bool captureRestore, string operationId) => Apply(() =>
        {
            MMDevice? device = playback ? audio.TryGetPlaybackDeviceForRoutine(deviceId) : audio.TryGetRecordingDeviceForRoutine(deviceId);
            if (device == null) return null;
            try { return new NativeEndpoint(device); }
            catch { device.Dispose(); throw; }
        }, logger, percent, captureRestore, operationId);

    internal static RoutineVolumeApplicationResult Apply(Func<IRoutineVolumeEndpoint?> getEndpoint, Logger logger,
        int percent, bool captureRestore, string operationId) => ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            IRoutineVolumeEndpoint? endpoint = getEndpoint();
            if (endpoint == null) return new RoutineVolumeApplicationResult(false);
            VolumeChange? change = null;
            try
            {
                change = new VolumeChange(endpoint, logger, operationId);
                endpoint = null;
                change.Apply(Math.Clamp(percent, 0, 100) / 100f);
                lock (OwnershipLock)
                {
                    if (Owners.Remove(change.EndpointId, out VolumeChange? previous)) previous.Invalidate();
                    if (captureRestore) Owners[change.EndpointId] = change;
                }
                logger.Debug("RoutineVolume", () => $"routine-volume-applied | percent={percent} opId={operationId}");
                if (!captureRestore) return new RoutineVolumeApplicationResult(true);
                VolumeChange owned = change;
                change = null;
                return new RoutineVolumeApplicationResult(true, owned);
            }
            finally { change?.Dispose(); endpoint?.Dispose(); }
        });

    private sealed class NativeEndpoint : IRoutineVolumeEndpoint
    {
        private readonly MMDevice _device;
        private readonly AudioEndpointVolume _volume;
        internal NativeEndpoint(MMDevice device)
        {
            _device = device;
            _volume = device.AudioEndpointVolume;
            _volume.OnVolumeNotification += OnChanged;
        }
        public string Id => _device.ID;
        public float Volume { get => _volume.MasterVolumeLevelScalar; set => _volume.MasterVolumeLevelScalar = value; }
        public Guid NotificationGuid { get => _volume.NotificationGuid; set => _volume.NotificationGuid = value; }
        public event Action<Guid, float>? Changed;
        private void OnChanged(AudioVolumeNotificationData data) => Changed?.Invoke(data.EventContext, data.MasterVolume);
        public void Dispose()
        {
            try { _volume.OnVolumeNotification -= OnChanged; }
            finally { _device.Dispose(); }
        }
    }

    private sealed class VolumeChange : IRoutineAudioChange
    {
        private readonly IRoutineVolumeEndpoint _endpoint;
        private readonly Logger _logger;
        private readonly string _operationId;
        private readonly float _original;
        private readonly Guid _context = Guid.NewGuid();
        private readonly Guid _previousContext;
        private volatile float _applied;
        private int _invalidated;
        private int _disposed;
        internal string EndpointId { get; }

        internal VolumeChange(IRoutineVolumeEndpoint endpoint, Logger logger, string operationId)
        {
            _endpoint = endpoint;
            EndpointId = endpoint.Id;
            _original = endpoint.Volume;
            _previousContext = endpoint.NotificationGuid;
            _logger = logger;
            _operationId = operationId;
            endpoint.NotificationGuid = _context;
            endpoint.Changed += OnChanged;
        }

        internal void Apply(float value)
        {
            _applied = value;
            _endpoint.Volume = value;
            _applied = _endpoint.Volume;
        }

        internal void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        private void OnChanged(Guid context, float volume)
        {
            if (context != _context && Math.Abs(volume - _applied) > 0.0001f) Invalidate();
        }

        public void Restore() => ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            if (Volatile.Read(ref _invalidated) != 0 || Math.Abs(_endpoint.Volume - _applied) > 0.0001f)
            {
                _logger.Debug("RoutineVolume", () => $"routine-volume-restore-skipped | opId={_operationId} reason=newer-volume-change");
                return;
            }
            _endpoint.Volume = _original;
            _logger.Debug("RoutineVolume", () => $"routine-volume-restored | opId={_operationId}");
        });

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (OwnershipLock)
                if (Owners.TryGetValue(EndpointId, out VolumeChange? owner) && ReferenceEquals(owner, this)) Owners.Remove(EndpointId);
            ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                try { _endpoint.Changed -= OnChanged; _endpoint.NotificationGuid = _previousContext; }
                finally { _endpoint.Dispose(); }
            });
        }
    }
}
