using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Internal
{
    internal sealed class SessionMonitorCoordinator(
        Logger logger,
        AudioMixerMode mixerMode,
        Func<IReadOnlyList<ISessionMonitorEndpoint>> getActiveEndpoints,
        Action<AudioMixerMode, object?, ISessionMonitorSession> onSessionCreated,
        Action<AudioMixerMode, string, float, bool> onEndpointVolumeChanged,
        Action<AudioSessionLifecycleSignal> onSessionLifecycleChanged,
        Action<Func<CancellationToken, Task>, string> runBackgroundWork,
        Func<bool> isDisposed)
    {
        private sealed class MonitoredSessionRegistration(ISessionMonitorSession sessionControl, SessionEventClient eventClient) : IDisposable
        {
            private readonly ISessionMonitorSession _sessionControl = sessionControl;
            private readonly SessionEventClient _eventClient = eventClient;

            public bool AffectsSharedRows { get; } = ResolveAffectsSharedRows(sessionControl);

            public void Dispose()
            {
                try
                {
                    _sessionControl.UnregisterEventClient(_eventClient);
                }
                catch
                {
                }

                _sessionControl.Dispose();
            }

            private static bool ResolveAffectsSharedRows(ISessionMonitorSession sessionControl)
            {
                try
                {
                    if (sessionControl.ProcessId == 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }

                try
                {
                    string? displayName = sessionControl.DisplayName;
                    return string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase)
                        || (displayName?.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase) == true);
                }
                catch
                {
                    return false;
                }
            }
        }

        private sealed class SessionEventClient(
            long monitorInstanceId,
            string endpointId,
            string sessionInstanceId,
            Action<long, string, string, float, bool> onVolumeChanged,
            Action<long, string, string, AudioSessionState> onStateChanged,
            Action<long, string, string, AudioSessionDisconnectReason> onSessionDisconnected) : IAudioSessionEventsHandler
        {
            private readonly long _monitorInstanceId = monitorInstanceId;
            private readonly string _endpointId = endpointId;
            private readonly string _sessionInstanceId = sessionInstanceId;
            private readonly Action<long, string, string, float, bool> _onVolumeChanged = onVolumeChanged;
            private readonly Action<long, string, string, AudioSessionState> _onStateChanged = onStateChanged;
            private readonly Action<long, string, string, AudioSessionDisconnectReason> _onSessionDisconnected = onSessionDisconnected;

            public void OnVolumeChanged(float volume, bool isMuted)
            {
                _onVolumeChanged(_monitorInstanceId, _endpointId, _sessionInstanceId, volume, isMuted);
            }

            public void OnDisplayNameChanged(string displayName)
            {
            }

            public void OnIconPathChanged(string iconPath)
            {
            }

            public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
            {
            }

            public void OnGroupingParamChanged(ref Guid groupingId)
            {
            }

            public void OnStateChanged(AudioSessionState state)
            {
                _onStateChanged(_monitorInstanceId, _endpointId, _sessionInstanceId, state);
            }

            public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            {
                _onSessionDisconnected(_monitorInstanceId, _endpointId, _sessionInstanceId, disconnectReason);
            }
        }

        private sealed class EndpointMonitorState(
            long instanceId,
            string endpointId,
            string displayName,
            ISessionMonitorEndpoint endpoint,
            SessionMonitorSessionCreatedDelegate sessionCreatedHandler,
            AudioEndpointVolumeNotificationDelegate endpointVolumeHandler)
        {
            public long InstanceId { get; } = instanceId;
            public string EndpointId { get; } = endpointId;
            public string DisplayName { get; } = displayName;
            public ISessionMonitorEndpoint Endpoint { get; } = endpoint;
            public SessionMonitorSessionCreatedDelegate SessionCreatedHandler { get; } = sessionCreatedHandler;
            public AudioEndpointVolumeNotificationDelegate EndpointVolumeHandler { get; } = endpointVolumeHandler;
            public Dictionary<string, MonitoredSessionRegistration> MonitoredSessions { get; } = new(StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                try
                {
                    Endpoint.UnsubscribeSessionCreated(SessionCreatedHandler);
                }
                catch
                {
                }

                try
                {
                    Endpoint.UnsubscribeEndpointVolume(EndpointVolumeHandler);
                }
                catch
                {
                }

                foreach (MonitoredSessionRegistration registration in MonitoredSessions.Values)
                {
                    try
                    {
                        registration.Dispose();
                    }
                    catch
                    {
                    }
                }

                MonitoredSessions.Clear();

                try
                {
                    Endpoint.Dispose();
                }
                catch
                {
                }
            }
        }

        private readonly Lock _lock = new();
        private readonly Logger _logger = logger;
        private readonly AudioMixerMode _mixerMode = mixerMode;
        private readonly Func<IReadOnlyList<ISessionMonitorEndpoint>> _getActiveEndpoints = getActiveEndpoints;
        private readonly Action<AudioMixerMode, object?, ISessionMonitorSession> _onSessionCreated = onSessionCreated;
        private readonly Action<AudioMixerMode, string, float, bool> _onEndpointVolumeChanged = onEndpointVolumeChanged;
        private readonly Action<AudioSessionLifecycleSignal> _onSessionLifecycleChanged = onSessionLifecycleChanged;
        private readonly Action<Func<CancellationToken, Task>, string> _runBackgroundWork = runBackgroundWork;
        private readonly Func<bool> _isDisposed = isDisposed;

        private readonly record struct SessionMonitorDetachSnapshot(
            CancellationTokenSource? Debounce,
            EndpointMonitorState[] EndpointStates);

        private CancellationTokenSource? _sessionMonitorDebounce;
        private readonly Dictionary<string, EndpointMonitorState> _endpointMonitors = new(StringComparer.OrdinalIgnoreCase);
        private long _nextEndpointMonitorInstanceId;

        public void Stop()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioDeviceService", () => $"session-monitor-stop | mode={_mixerMode}", nameof(Stop));
            }
            Task cleanupTask = StopAndDrainAsync();
            if (!cleanupTask.IsCompletedSuccessfully)
            {
                _ = ObserveStopCleanupAsync(cleanupTask);
            }
        }

        private async Task ObserveStopCleanupAsync(Task cleanupTask)
        {
            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", "Error stopping session monitoring", nameof(Stop), ex);
            }
        }

        internal Task StopAndDrainAsync()
        {
            SessionMonitorDetachSnapshot snapshot;

            lock (_lock)
            {
                snapshot = DetachStateUnsafe();
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-stop-drain-start | mode={_mixerMode} endpoints={snapshot.EndpointStates.Length}",
                    nameof(StopAndDrainAsync));
            }

            try
            {
                return ComThreadingHelper.RunOnCoreAudioThreadAsync(() => DisposeDetachedState(snapshot));
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", "Error stopping session monitoring", nameof(StopAndDrainAsync), ex);
                return Task.CompletedTask;
            }
        }

        internal int GetEndpointMonitorCountForTests()
        {
            lock (_lock)
            {
                return _endpointMonitors.Count;
            }
        }

        public void Update()
        {
            if (_isDisposed())
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"session-monitor-update-skip | mode={_mixerMode} reason=disposed", nameof(Update));
                }
                return;
            }

            var nextDebounce = new CancellationTokenSource();
            CancellationTokenSource? previousDebounce;
            lock (_lock)
            {
                if (_isDisposed())
                {
                    nextDebounce.Dispose();
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioDeviceService", () => $"session-monitor-update-skip | mode={_mixerMode} reason=disposed-after-lock", nameof(Update));
                    }
                    return;
                }

                previousDebounce = _sessionMonitorDebounce;
                _sessionMonitorDebounce = nextDebounce;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-update-scheduled | mode={_mixerMode} replacedDebounce={(previousDebounce != null)} delayMs={AppConstants.Timing.SessionMonitorDebounceMs}",
                    nameof(Update));
            }

            previousDebounce?.Cancel();
            previousDebounce?.Dispose();
            CancellationToken debounceToken = nextDebounce.Token;

            _runBackgroundWork(async shutdownToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(debounceToken, shutdownToken);
                CancellationToken linkedToken = linkedCts.Token;

                try
                {
                    await Task.Delay(AppConstants.Timing.SessionMonitorDebounceMs, linkedToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (linkedToken.IsCancellationRequested || _isDisposed())
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioDeviceService", () => $"session-monitor-update-skip | mode={_mixerMode} reason=cancelled-or-disposed", nameof(Update));
                    }
                    return;
                }

                try
                {
                    await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                        () => ReconcileEndpointMonitors(nextDebounce),
                        linkedToken);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.Error("AudioDeviceService", "Failed to reconcile session monitoring endpoints", nameof(Update), ex);
                }
            }, nameof(Update));
        }

        private void ReconcileEndpointMonitors(CancellationTokenSource expectedDebounce)
        {
            if (_isDisposed())
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"session-monitor-reconcile-skip | mode={_mixerMode} reason=disposed", nameof(ReconcileEndpointMonitors));
                }
                return;
            }

            IReadOnlyList<ISessionMonitorEndpoint> activeEndpoints;
            try
            {
                activeEndpoints = _getActiveEndpoints();
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AudioDeviceService",
                        () => $"session-monitor-reconcile-start | mode={_mixerMode} endpointCandidates={activeEndpoints.Count}",
                        nameof(ReconcileEndpointMonitors));
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", $"Failed to enumerate active {_mixerMode} endpoints for session monitoring", nameof(ReconcileEndpointMonitors), ex);
                return;
            }

            List<EndpointMonitorState> detachStates = [];
            List<ISessionMonitorEndpoint> attachEndpoints = [];
            List<ISessionMonitorEndpoint> disposeEndpoints = [];
            int retainedCount = 0;
            int duplicateActiveEndpoints = 0;
            int detachedCount = 0;

            try
            {
                lock (_lock)
                {
                    if (_isDisposed() || !ReferenceEquals(_sessionMonitorDebounce, expectedDebounce))
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("AudioDeviceService", () => $"session-monitor-reconcile-skip | mode={_mixerMode} reason=stale-or-disposed", nameof(ReconcileEndpointMonitors));
                        }
                        DisposeEndpoints(activeEndpoints);
                        return;
                    }

                    var activeEndpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ISessionMonitorEndpoint endpoint in activeEndpoints)
                    {
                        string endpointId = endpoint.EndpointId;
                        if (string.IsNullOrWhiteSpace(endpointId))
                        {
                            disposeEndpoints.Add(endpoint);
                            continue;
                        }

                        if (!activeEndpointIds.Add(endpointId))
                        {
                            duplicateActiveEndpoints++;
                            disposeEndpoints.Add(endpoint);
                            continue;
                        }

                        if (_endpointMonitors.ContainsKey(endpointId))
                        {
                            retainedCount++;
                            disposeEndpoints.Add(endpoint);
                            continue;
                        }

                        attachEndpoints.Add(endpoint);
                    }

                    foreach ((string endpointId, EndpointMonitorState state) in _endpointMonitors.ToArray())
                    {
                        if (activeEndpointIds.Contains(endpointId))
                        {
                            continue;
                        }

                        _endpointMonitors.Remove(endpointId);
                        detachStates.Add(state);
                    }
                }

                detachedCount = detachStates.Count;
                DisposeDetachedStates(detachStates, logIndividualEndpoints: true, logSummary: false);
                detachStates.Clear();

                int attachedCount = 0;
                int failedAttachments = 0;
                foreach (ISessionMonitorEndpoint endpoint in attachEndpoints)
                {
                    EndpointMonitorState? state = TryCreateEndpointMonitorState(endpoint);
                    if (state == null)
                    {
                        failedAttachments++;
                        continue;
                    }

                    bool installed = false;
                    lock (_lock)
                    {
                        if (!_isDisposed() && ReferenceEquals(_sessionMonitorDebounce, expectedDebounce) && !_endpointMonitors.ContainsKey(state.EndpointId))
                        {
                            _endpointMonitors[state.EndpointId] = state;
                            installed = true;
                        }
                    }

                    if (installed)
                    {
                        attachedCount++;
                        continue;
                    }

                    _logger.Trace("AudioDeviceService", () => $"session-monitor-endpoint-discarded | mode={_mixerMode} endpoint={LogPrivacy.Id(state.EndpointId)} name={LogPrivacy.Device(state.DisplayName)} reason=stale-reconcile");
                    state.Dispose();
                }

                int activeCount;
                lock (_lock)
                {
                    activeCount = _endpointMonitors.Count;
                }

                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-reconcile | mode={_mixerMode} active={activeCount} attached={attachedCount} retained={retainedCount} detached={detachedCount} duplicates={duplicateActiveEndpoints} failed={failedAttachments}");
            }
            finally
            {
                DisposeDetachedStates(detachStates, logIndividualEndpoints: true, logSummary: false);
                DisposeEndpoints(disposeEndpoints);
            }
        }

        private EndpointMonitorState? TryCreateEndpointMonitorState(ISessionMonitorEndpoint endpoint)
        {
            try
            {
                long monitorInstanceId = Interlocked.Increment(ref _nextEndpointMonitorInstanceId);
                string endpointId = endpoint.EndpointId;
                string displayName = endpoint.DisplayName;
                var state = new EndpointMonitorState(
                    monitorInstanceId,
                    endpointId,
                    displayName,
                    endpoint,
                    (sender, newSession) => HandleSessionCreated(endpointId, monitorInstanceId, sender, newSession),
                    notificationData => HandleEndpointVolumeNotification(endpointId, monitorInstanceId, notificationData));

                endpoint.SubscribeSessionCreated(state.SessionCreatedHandler);
                endpoint.SubscribeEndpointVolume(state.EndpointVolumeHandler);

                IReadOnlyList<ISessionMonitorSession> existingSessions = endpoint.GetExistingSessions();
                for (int index = 0; index < existingSessions.Count; index++)
                {
                    ISessionMonitorSession? sessionControl = existingSessions[index];
                    if (!TryRegisterSessionInState(state, sessionControl))
                    {
                        sessionControl?.Dispose();
                    }
                }

                return state;
            }
            catch (Exception ex)
            {
                try
                {
                    endpoint.Dispose();
                }
                catch
                {
                }

                _logger.Warning(
                    "AudioDeviceService",
                    () => $"session-monitor-endpoint-attach-failed | mode={_mixerMode} endpoint={LogPrivacy.Id(endpoint.EndpointId)} name={LogPrivacy.Device(endpoint.DisplayName)}",
                    nameof(TryCreateEndpointMonitorState),
                    ex);
                return null;
            }
        }

        private void HandleSessionCreated(string endpointId, long monitorInstanceId, object? sender, ISessionMonitorSession newSession)
        {
            if (!IsEndpointMonitoringActive(endpointId, monitorInstanceId))
            {
                try
                {
                    newSession.Dispose();
                }
                catch
                {
                }

                return;
            }

            bool registered = TryRegisterRuntimeSession(endpointId, monitorInstanceId, newSession);
            if (!registered || !IsEndpointMonitoringActive(endpointId, monitorInstanceId))
            {
                return;
            }

            _logger.Debug("AudioDeviceService", () => $"session-monitor-session-created | mode={_mixerMode} endpoint={LogPrivacy.Id(endpointId)}");
            _onSessionCreated(_mixerMode, sender, newSession);
        }

        private bool TryRegisterRuntimeSession(string endpointId, long monitorInstanceId, ISessionMonitorSession sessionControl)
        {
            try
            {
                string? sessionInstanceId = TryResolveSessionInstanceId(sessionControl);
                if (string.IsNullOrWhiteSpace(sessionInstanceId))
                {
                    sessionControl.Dispose();
                    return false;
                }

                lock (_lock)
                {
                    if (_isDisposed()
                        || !_endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                        || state.InstanceId != monitorInstanceId
                        || state.MonitoredSessions.ContainsKey(sessionInstanceId))
                    {
                        sessionControl.Dispose();
                        return false;
                    }

                    var eventClient = new SessionEventClient(monitorInstanceId, endpointId, sessionInstanceId, HandleSessionVolumeChanged, HandleSessionStateChanged, HandleSessionDisconnected);
                    sessionControl.RegisterEventClient(eventClient);
                    state.MonitoredSessions[sessionInstanceId] = new MonitoredSessionRegistration(sessionControl, eventClient);
                    return true;
                }
            }
            catch (Exception ex)
            {
                sessionControl.Dispose();
                _logger.Warning("AudioDeviceService", "Failed to attach session lifecycle monitor", nameof(TryRegisterRuntimeSession), ex);
                return false;
            }
        }

        private bool TryRegisterSessionInState(EndpointMonitorState state, ISessionMonitorSession sessionControl)
        {
            try
            {
                string? sessionInstanceId = TryResolveSessionInstanceId(sessionControl);
                if (string.IsNullOrWhiteSpace(sessionInstanceId) || state.MonitoredSessions.ContainsKey(sessionInstanceId))
                {
                    return false;
                }

                var eventClient = new SessionEventClient(state.InstanceId, state.EndpointId, sessionInstanceId, HandleSessionVolumeChanged, HandleSessionStateChanged, HandleSessionDisconnected);
                sessionControl.RegisterEventClient(eventClient);
                state.MonitoredSessions[sessionInstanceId] = new MonitoredSessionRegistration(sessionControl, eventClient);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", "Failed to attach session lifecycle monitor", nameof(TryRegisterSessionInState), ex);
                return false;
            }
        }

        private static string? TryResolveSessionInstanceId(ISessionMonitorSession sessionControl)
        {
            try
            {
                return sessionControl.SessionInstanceId;
            }
            catch
            {
                try
                {
                    return sessionControl.SessionId;
                }
                catch
                {
                    return null;
                }
            }
        }

        private bool TryGetSharedRowLifecycleState(string endpointId, long monitorInstanceId, string sessionInstanceId, out bool affectsSharedRows)
        {
            lock (_lock)
            {
                affectsSharedRows = false;

                if (_isDisposed()
                    || !_endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                    || state.InstanceId != monitorInstanceId
                    || !state.MonitoredSessions.TryGetValue(sessionInstanceId, out MonitoredSessionRegistration? registration))
                {
                    return false;
                }

                affectsSharedRows = registration.AffectsSharedRows;
                return true;
            }
        }

        private void HandleSessionStateChanged(long monitorInstanceId, string endpointId, string sessionInstanceId, AudioSessionState state)
        {
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows))
            {
                return;
            }

            _onSessionLifecycleChanged(new AudioSessionLifecycleSignal(
                _mixerMode,
                AudioSessionLifecycleSignalKind.StateChanged,
                sessionInstanceId,
                EndpointId: endpointId,
                AffectsSharedRows: affectsSharedRows,
                State: state));

            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                ReleaseMonitoredSession(endpointId, monitorInstanceId, sessionInstanceId);
            }
        }

        private void HandleSessionVolumeChanged(long monitorInstanceId, string endpointId, string sessionInstanceId, float volume, bool isMuted)
        {
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows))
            {
                return;
            }

            _onSessionLifecycleChanged(new AudioSessionLifecycleSignal(
                _mixerMode,
                AudioSessionLifecycleSignalKind.VolumeChanged,
                sessionInstanceId,
                EndpointId: endpointId,
                AffectsSharedRows: affectsSharedRows,
                VolumePercent: Math.Clamp(volume * 100f, 0f, 100f),
                IsMuted: isMuted));
        }

        private void HandleEndpointVolumeNotification(string endpointId, long monitorInstanceId, AudioVolumeNotificationData notificationData)
        {
            if (!IsEndpointMonitoringActive(endpointId, monitorInstanceId))
            {
                return;
            }

            _onEndpointVolumeChanged(
                _mixerMode,
                endpointId,
                Math.Clamp(notificationData.MasterVolume * 100f, 0f, 100f),
                notificationData.Muted);
        }

        private void HandleSessionDisconnected(long monitorInstanceId, string endpointId, string sessionInstanceId, AudioSessionDisconnectReason disconnectReason)
        {
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows))
            {
                return;
            }

            _onSessionLifecycleChanged(new AudioSessionLifecycleSignal(
                _mixerMode,
                AudioSessionLifecycleSignalKind.Disconnected,
                sessionInstanceId,
                EndpointId: endpointId,
                AffectsSharedRows: affectsSharedRows,
                DisconnectReason: disconnectReason));

            ReleaseMonitoredSession(endpointId, monitorInstanceId, sessionInstanceId);
        }

        private bool IsEndpointMonitoringActive(string endpointId, long monitorInstanceId)
        {
            lock (_lock)
            {
                return !_isDisposed()
                    && _endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                    && state.InstanceId == monitorInstanceId;
            }
        }

        private void ReleaseMonitoredSession(string endpointId, long monitorInstanceId, string sessionInstanceId)
        {
            MonitoredSessionRegistration? registration = null;

            lock (_lock)
            {
                if (_endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                    && state.InstanceId == monitorInstanceId)
                {
                    state.MonitoredSessions.Remove(sessionInstanceId, out registration);
                }
            }

            registration?.Dispose();
        }

        private SessionMonitorDetachSnapshot DetachStateUnsafe()
        {
            CancellationTokenSource? debounce = _sessionMonitorDebounce;
            EndpointMonitorState[] endpointStates = [.. _endpointMonitors.Values];

            _sessionMonitorDebounce = null;
            _endpointMonitors.Clear();

            return new SessionMonitorDetachSnapshot(debounce, endpointStates);
        }

        private void DisposeDetachedState(SessionMonitorDetachSnapshot snapshot)
        {
            try
            {
                snapshot.Debounce?.Cancel();
            }
            catch
            {
            }

            try
            {
                snapshot.Debounce?.Dispose();
            }
            catch
            {
            }

            DisposeDetachedStates(snapshot.EndpointStates, logIndividualEndpoints: false, logSummary: true);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-stop-drain-complete | mode={_mixerMode} endpoints={snapshot.EndpointStates.Length}",
                    nameof(DisposeDetachedState));
            }
        }

        private void DisposeDetachedStates(
            IEnumerable<EndpointMonitorState> states,
            bool logIndividualEndpoints,
            bool logSummary)
        {
            int detachedCount = 0;
            int totalSessions = 0;

            foreach (EndpointMonitorState state in states)
            {
                try
                {
                    detachedCount++;
                    totalSessions += state.MonitoredSessions.Count;

                    if (logIndividualEndpoints)
                    {
                        _logger.Trace("AudioDeviceService", () => $"session-monitor-endpoint-detached | mode={_mixerMode} endpoint={LogPrivacy.Id(state.EndpointId)} name={LogPrivacy.Device(state.DisplayName)} sessions={state.MonitoredSessions.Count}");
                    }

                    state.Dispose();
                }
                catch
                {
                }
            }

            if (logSummary && detachedCount > 0)
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-endpoints-detached | mode={_mixerMode} count={detachedCount} sessions={totalSessions} reason=shutdown");
            }
            else if (logSummary && _logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AudioDeviceService", () => $"session-monitor-endpoints-detached | mode={_mixerMode} count=0 sessions=0 reason=shutdown");
            }
        }

        private static void DisposeEndpoints(IEnumerable<ISessionMonitorEndpoint> endpoints)
        {
            foreach (ISessionMonitorEndpoint endpoint in endpoints)
            {
                try
                {
                    endpoint.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
