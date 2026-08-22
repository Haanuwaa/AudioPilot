using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Routines;

internal interface IRoutineMuteEndpoint : IDisposable
{
    string Id { get; }
    bool Mute { get; set; }
    Guid NotificationGuid { get; set; }
    event Action<Guid>? Changed;
}

/// <summary>Applies endpoint mute actions and retains restoration ownership until another writer intervenes.</summary>
internal static class RoutineEndpointMuteService
{
    private static readonly Lock OwnershipLock = new();
    private static readonly Dictionary<string, EndpointChange> Owners = new(StringComparer.OrdinalIgnoreCase);

    private static void ReplaceOwner(string id, EndpointChange? owner)
    {
        lock (OwnershipLock)
        {
            if (Owners.Remove(id, out EndpointChange? previous)) previous.Invalidate();
            if (owner != null) Owners[id] = owner;
        }
    }

    internal static RoutineMuteApplicationResult Apply(AudioDeviceService audio, Logger logger, bool playback,
        string? deviceId, bool muted, bool captureRestore, string operationId, Func<bool, bool, string?>? guard)
    {
        return Apply(() =>
        {
            MMDevice? device = playback ? audio.TryGetPlaybackDeviceForRoutine(deviceId) : audio.TryGetRecordingDeviceForRoutine(deviceId);
            if (device == null) return null;
            try { return new NativeEndpoint(device); }
            catch { device.Dispose(); throw; }
        }, logger, playback, muted, captureRestore, operationId, guard);
    }

    internal static RoutineMuteApplicationResult Apply(Func<IRoutineMuteEndpoint?> getEndpoint, Logger logger,
        bool playback, bool muted, bool captureRestore, string operationId, Func<bool, bool, string?>? guard = null)
    {
        return ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            string? blocked = guard?.Invoke(playback, muted);
            if (blocked != null) return new RoutineMuteApplicationResult(false, FailureDetail: blocked);
            IRoutineMuteEndpoint? device = getEndpoint();
            if (device == null) return new RoutineMuteApplicationResult(false, FailureDetail: "The audio endpoint is unavailable.");
            EndpointChange? change = null;
            try
            {
                IRoutineMuteEndpoint endpoint = device;
                bool original = endpoint.Mute;
                if (original == muted)
                {
                    ReplaceOwner(endpoint.Id, null);
                    return new RoutineMuteApplicationResult(true);
                }
                change = new EndpointChange(endpoint, original, muted, () => guard?.Invoke(playback, original), logger, operationId);
                device = null;
                change.Apply();
                ReplaceOwner(endpoint.Id, captureRestore ? change : null);
                logger.Debug("RoutineMute", () => $"routine-mute-applied | flow={(playback ? "output" : "input")} muted={muted} opId={operationId}");
                if (!captureRestore) return new RoutineMuteApplicationResult(true);
                EndpointChange owned = change;
                change = null;
                return new RoutineMuteApplicationResult(true, owned);
            }
            finally { change?.Dispose(); device?.Dispose(); }
        });
    }

    private sealed class NativeEndpoint : IRoutineMuteEndpoint
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
        public bool Mute { get => _volume.Mute; set => _volume.Mute = value; }
        public Guid NotificationGuid { get => _volume.NotificationGuid; set => _volume.NotificationGuid = value; }
        public event Action<Guid>? Changed;
        private void OnChanged(AudioVolumeNotificationData data) => Changed?.Invoke(data.EventContext);
        public void Dispose()
        {
            try { _volume.OnVolumeNotification -= OnChanged; }
            finally { _device.Dispose(); }
        }
    }

    private sealed class EndpointChange : IRoutineAudioChange
    {
        private readonly IRoutineMuteEndpoint _endpoint;
        private readonly string _endpointId;
        private readonly bool _original;
        private readonly bool _applied;
        private readonly Func<string?> _guard;
        private readonly Logger _logger;
        private readonly string _operationId;
        private readonly Guid _context = Guid.NewGuid();
        private readonly Guid _previousContext;
        private int _invalidated;
        private int _disposed;

        internal EndpointChange(IRoutineMuteEndpoint endpoint, bool original, bool applied,
            Func<string?> guard, Logger logger, string operationId)
        {
            _endpoint = endpoint;
            _endpointId = endpoint.Id;
            _original = original;
            _applied = applied;
            _guard = guard;
            _logger = logger;
            _operationId = operationId;
            _previousContext = endpoint.NotificationGuid;
            endpoint.NotificationGuid = _context;
            endpoint.Changed += OnChanged;
        }

        private void OnChanged(Guid context)
        {
            if (context != _context) Interlocked.Exchange(ref _invalidated, 1);
        }

        internal void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        internal void Apply() => _endpoint.Mute = _applied;

        public void Restore() => ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            string? blocked = _guard();
            if (Volatile.Read(ref _invalidated) != 0 || blocked != null || _endpoint.Mute != _applied)
            {
                _logger.Debug("RoutineMute", () => $"routine-mute-restore-skipped | opId={_operationId} reason={(blocked != null ? "microphone-or-deafen-ownership" : "newer-endpoint-change")}");
                return;
            }
            _endpoint.Mute = _original;
            _logger.Debug("RoutineMute", () => $"routine-mute-restored | opId={_operationId} muted={_original}");
        });

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (OwnershipLock)
                if (Owners.TryGetValue(_endpointId, out EndpointChange? owner) && ReferenceEquals(owner, this)) Owners.Remove(_endpointId);
            ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                try { _endpoint.Changed -= OnChanged; _endpoint.NotificationGuid = _previousContext; }
                finally { _endpoint.Dispose(); }
            });
        }
    }
}
