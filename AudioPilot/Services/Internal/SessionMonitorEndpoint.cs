using AudioPilot.Helpers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Internal
{
    internal delegate void SessionMonitorSessionCreatedDelegate(object? sender, ISessionMonitorSession session);

    internal interface ISessionMonitorSessionLease : IDisposable
    {
        AudioSessionState State { get; }
        uint ProcessId { get; }
        string DisplayName { get; }
        void UseNativeControl(Action<AudioSessionControl> action);
    }

    internal interface ISessionMonitorSession : IDisposable
    {
        string? SessionInstanceId { get; }
        string? SessionId { get; }
        uint ProcessId { get; }
        string DisplayName { get; }
        void RegisterEventClient(IAudioSessionEventsHandler eventClient);
        void UnregisterEventClient(IAudioSessionEventsHandler eventClient);
        ISessionMonitorSessionLease? TryAcquireLease();
    }

    internal interface ISessionMonitorEndpoint : IDisposable
    {
        string EndpointId { get; }
        string DisplayName { get; }
        IReadOnlyList<ISessionMonitorSession> GetExistingSessions();
        void SubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler);
        void UnsubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler);
        void SubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler);
        void UnsubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler);
    }

    /// <summary>
    /// Reference-counted owner for an NAudio session wrapper. The monitor owns the initial reference and delayed
    /// restore work takes a lease, so endpoint teardown cannot release the COM wrapper while that work is running.
    /// </summary>
    internal sealed class CoreAudioSessionMonitorSession(AudioSessionControl sessionControl) : ISessionMonitorSession
    {
        private sealed class Lease(CoreAudioSessionMonitorSession owner) : ISessionMonitorSessionLease
        {
            private CoreAudioSessionMonitorSession? _owner = owner;

            public AudioSessionState State => GetOwner().State;
            public uint ProcessId => GetOwner().ProcessId;
            public string DisplayName => GetOwner().DisplayName;

            public void UseNativeControl(Action<AudioSessionControl> action)
            {
                GetOwner().UseNativeControl(action);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.ReleaseReference();
            }

            private CoreAudioSessionMonitorSession GetOwner()
            {
                return Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(Lease));
            }
        }

        private AudioSessionControl? _sessionControl = sessionControl;
        private int _referenceCount = 1;
        private int _ownerReleased;

        public string? SessionInstanceId => GetControl().GetSessionInstanceIdentifier;
        public string? SessionId => GetControl().GetSessionIdentifier;
        public uint ProcessId => GetControl().GetProcessID;
        public string DisplayName => GetControl().DisplayName ?? string.Empty;
        private AudioSessionState State => GetControl().State;

        public void RegisterEventClient(IAudioSessionEventsHandler eventClient)
        {
            GetControl().RegisterEventClient(eventClient);
        }

        public void UnregisterEventClient(IAudioSessionEventsHandler eventClient)
        {
            GetControl().UnRegisterEventClient(eventClient);
        }

        public ISessionMonitorSessionLease? TryAcquireLease()
        {
            while (true)
            {
                int current = Volatile.Read(ref _referenceCount);
                if (current == 0)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                {
                    return new Lease(this);
                }
            }
        }

        public void UseNativeControl(Action<AudioSessionControl> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action(GetControl());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            {
                ReleaseReference();
            }
        }

        private AudioSessionControl GetControl()
        {
            return Volatile.Read(ref _sessionControl) ?? throw new ObjectDisposedException(nameof(CoreAudioSessionMonitorSession));
        }

        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                Interlocked.Exchange(ref _sessionControl, null)?.Dispose();
            }
        }
    }

    internal sealed class CoreAudioSessionMonitorEndpoint : ISessionMonitorEndpoint
    {
        private readonly Lock _lock = new();
        private readonly MMDevice _device;
        private readonly AudioSessionManager? _sessionManager;
        private readonly AudioEndpointVolume? _endpointVolume;
        private readonly Dictionary<SessionMonitorSessionCreatedDelegate, AudioSessionManager.SessionCreatedDelegate> _sessionCreatedHandlers = [];
        private bool _disposed;

        public CoreAudioSessionMonitorEndpoint(MMDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            // Ownership transfers immediately. If initialization fails, this constructor releases the device and
            // every wrapper acquired before rethrowing the original failure.
            _device = device;
            try
            {
                EndpointId = device.ID;
                if (string.IsNullOrWhiteSpace(EndpointId))
                {
                    throw new InvalidOperationException("A session-monitor endpoint must have a stable device ID.");
                }

                DisplayName = device.FriendlyName;
                _sessionManager = device.AudioSessionManager;
                _endpointVolume = TryResolveEndpointVolume(device);
            }
            catch
            {
                DisposeIgnoringErrors(_endpointVolume);
                DisposeIgnoringErrors(_sessionManager);
                DisposeIgnoringErrors(device);
                throw;
            }
        }

        public string EndpointId { get; }

        public string DisplayName { get; }

        public IReadOnlyList<ISessionMonitorSession> GetExistingSessions()
        {
            lock (_lock)
            {
                if (_disposed || _sessionManager == null)
                {
                    return [];
                }

                using SessionCollection sessions = _sessionManager.Sessions;
                var materialized = new List<ISessionMonitorSession>(sessions.Count);
                for (int index = 0; index < sessions.Count; index++)
                {
                    AudioSessionControl? session = null;
                    try
                    {
                        session = sessions[index];
                        if (session == null)
                        {
                            continue;
                        }

                        materialized.Add(new CoreAudioSessionMonitorSession(session));
                        session = null;
                    }
                    catch
                    {
                        DisposeIgnoringErrors(session);
                    }
                }

                return materialized;
            }
        }

        public void SubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            void bridge(object sender, AudioSessionControl sessionControl)
            {
                var session = new CoreAudioSessionMonitorSession(sessionControl);
                try
                {
                    handler(sender, session);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            lock (_lock)
            {
                if (_disposed || _sessionManager == null || _sessionCreatedHandlers.ContainsKey(handler))
                {
                    return;
                }

                _sessionCreatedHandlers.Add(handler, bridge);
                _sessionManager.OnSessionCreated += bridge;
            }
        }

        public void UnsubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (_sessionManager != null &&
                    _sessionCreatedHandlers.Remove(handler, out AudioSessionManager.SessionCreatedDelegate? bridge))
                {
                    _sessionManager.OnSessionCreated -= bridge;
                }
            }
        }

        public void SubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_disposed && _endpointVolume != null)
                {
                    _endpointVolume.OnVolumeNotification += handler;
                }
            }
        }

        public void UnsubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_disposed && _endpointVolume != null)
                {
                    _endpointVolume.OnVolumeNotification -= handler;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_sessionManager != null)
                {
                    foreach (AudioSessionManager.SessionCreatedDelegate bridge in _sessionCreatedHandlers.Values)
                    {
                        _sessionManager.OnSessionCreated -= bridge;
                    }
                }

                _sessionCreatedHandlers.Clear();
            }

            DisposeIgnoringErrors(_endpointVolume);
            DisposeIgnoringErrors(_sessionManager);
            DisposeIgnoringErrors(_device);
        }

        private static AudioEndpointVolume? TryResolveEndpointVolume(MMDevice device)
        {
            try
            {
                return device.AudioEndpointVolume;
            }
            catch
            {
                return null;
            }
        }

        private static void DisposeIgnoringErrors(IDisposable? disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
            }
        }
    }

    internal static class SessionMonitorEndpointFactory
    {
        public static IReadOnlyList<ISessionMonitorEndpoint> Materialize(MMDeviceCollection devices)
        {
            try
            {
                List<MMDevice> materializedDevices = AudioDeviceCollectionHelper.MaterializeDevices(devices);
                var endpoints = new List<ISessionMonitorEndpoint>(materializedDevices.Count);

                for (int index = 0; index < materializedDevices.Count; index++)
                {
                    MMDevice? device = materializedDevices[index];
                    if (device == null)
                    {
                        continue;
                    }

                    try
                    {
                        endpoints.Add(new CoreAudioSessionMonitorEndpoint(device));
                    }
                    catch
                    {
                        // CoreAudioSessionMonitorEndpoint takes ownership as soon as construction begins and performs
                        // partial-initialization cleanup before rethrowing.
                    }
                }

                return endpoints;
            }
            finally
            {
                devices.Dispose();
            }
        }
    }
}
