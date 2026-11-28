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
        Action<AudioMixerMode, string, ISessionMonitorSession> onSessionCreated,
        Action<AudioMixerMode, string, float, bool> onEndpointVolumeChanged,
        Action<AudioSessionLifecycleSignal> onSessionLifecycleChanged,
        Action<Func<CancellationToken, Task>, string> runBackgroundWork,
        Func<bool> isDisposed)
    {
        private sealed class MonitoredSessionRegistration(ISessionMonitorSession sessionControl, SessionEventClient eventClient) : IDisposable
        {
            private readonly ISessionMonitorSession _sessionControl = sessionControl;
            private readonly SessionEventClient _eventClient = eventClient;
            private readonly Lock _lifetime = new();
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _registering, _attempted, _disposed;
            private int _cleanupStarted;
            internal Task Completion => _completion.Task;

            public uint? ProcessId { get; } = ReadProcessId(sessionControl);

            private static uint? ReadProcessId(ISessionMonitorSession session)
            {
                try { return session.ProcessId; }
                catch { return null; }
            }

            public bool AffectsSharedRows { get; } = ResolveAffectsSharedRows(sessionControl);

            internal bool Register()
            {
                lock (_lifetime)
                {
                    if (_disposed) return false;
                    _registering = true;
                    _attempted = true;
                }
                try { _sessionControl.RegisterEventClient(_eventClient); return true; }
                finally
                {
                    bool cleanup;
                    lock (_lifetime) { _registering = false; cleanup = _disposed; }
                    if (cleanup) Cleanup();
                }
            }

            public void Dispose()
            {
                lock (_lifetime)
                {
                    _disposed = true;
                    if (_registering) return;
                }
                Cleanup();
            }

            private void Cleanup()
            {
                if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
                try
                {
                    if (_attempted) _sessionControl.UnregisterEventClient(_eventClient);
                }
                catch
                {
                }
                try { _sessionControl.Dispose(); }
                finally { _completion.TrySetResult(); }
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
            public Dictionary<string, MonitoredSessionRegistration> MonitoredSessions { get; } = [with(StringComparer.OrdinalIgnoreCase)];
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

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

                MonitoredSessionRegistration[] registrations = [.. MonitoredSessions.Values];
                foreach (MonitoredSessionRegistration registration in registrations)
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
        private readonly Action<AudioMixerMode, string, ISessionMonitorSession> _onSessionCreated = onSessionCreated;
        private readonly Action<AudioMixerMode, string, float, bool> _onEndpointVolumeChanged = onEndpointVolumeChanged;
        private readonly Action<AudioSessionLifecycleSignal> _onSessionLifecycleChanged = onSessionLifecycleChanged;
        private readonly Action<Func<CancellationToken, Task>, string> _runBackgroundWork = runBackgroundWork;
        private readonly Func<bool> _isDisposed = isDisposed;

        private readonly record struct SessionMonitorDetachSnapshot(
            CancellationTokenSource? Debounce,
            EndpointMonitorState[] EndpointStates,
            Task[] SessionCleanups);

        private CancellationTokenSource? _sessionMonitorDebounce;
        private CancellationTokenSource? _immediateUpdate;
        private bool _refreshAfterImmediate;
        private readonly Dictionary<string, EndpointMonitorState> _endpointMonitors = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly HashSet<Task> _sessionCleanupTasks = [];
        private long _nextEndpointMonitorInstanceId;
        private long _stopGeneration;

        internal Action? ReattachRequested { get; set; }

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

        internal async Task StopAndDrainAsync()
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

            Task cleanupTask;
            try
            {
                cleanupTask = ComThreadingHelper.RunOnCoreAudioThreadAsync(() => DisposeDetachedState(snapshot));
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", "Error stopping session monitoring", nameof(StopAndDrainAsync), ex);
                return;
            }
            await cleanupTask.ConfigureAwait(false);
            await Task.WhenAll(snapshot.SessionCleanups).ConfigureAwait(false);
            lock (_lock) _sessionCleanupTasks.RemoveWhere(task => task.IsCompleted);
        }

        internal int GetEndpointMonitorCountForTests()
        {
            lock (_lock)
            {
                return _endpointMonitors.Count;
            }
        }

        internal int GetMonitoredSessionCountForTests()
        {
            lock (_lock) return _endpointMonitors.Values.Sum(state => state.MonitoredSessions.Count);
        }

        private (CancellationTokenSource Source, CancellationToken Token)? ReplacePendingUpdate(bool immediate = false)
        {
            if (_isDisposed())
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"session-monitor-update-skip | mode={_mixerMode} reason=disposed", nameof(Update));
                }
                return null;
            }

            var nextDebounce = new CancellationTokenSource();
            CancellationToken debounceToken = nextDebounce.Token;
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
                    return null;
                }
                if (!immediate && _immediateUpdate != null)
                {
                    _refreshAfterImmediate = true;
                    nextDebounce.Dispose();
                    return null;
                }

                previousDebounce = _sessionMonitorDebounce;
                _sessionMonitorDebounce = nextDebounce;
                if (immediate)
                {
                    _immediateUpdate = nextDebounce;
                    _refreshAfterImmediate = false;
                }
            }

            previousDebounce?.Cancel();
            previousDebounce?.Dispose();
            return (nextDebounce, debounceToken);
        }

        /// <summary>Attaches discovery before the switch's immediate session scan closes the creation-notification gap.</summary>
        internal async Task<bool> UpdateImmediatelyAsync(CancellationToken token, bool forceRecreate = false)
        {
            token.ThrowIfCancellationRequested();
            var request = ReplacePendingUpdate(immediate: true);
            if (request == null) return false;
            var (nextDebounce, debounceToken) = request.Value;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, debounceToken);
                return await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ReconcileEndpointMonitors(nextDebounce, forceRecreate), linked.Token);
            }
            finally
            {
                bool refresh = false;
                lock (_lock)
                {
                    if (ReferenceEquals(_immediateUpdate, nextDebounce))
                    {
                        _immediateUpdate = null;
                        refresh = _refreshAfterImmediate;
                        _refreshAfterImmediate = false;
                    }
                }
                if (refresh) Update();
            }
        }

        public void Update()
        {
            var request = ReplacePendingUpdate();
            if (request == null) return;
            var (nextDebounce, debounceToken) = request.Value;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitor-update-scheduled | mode={_mixerMode} delayMs={AppConstants.Timing.SessionMonitorDebounceMs}",
                    nameof(Update));
            }

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

        private bool ReconcileEndpointMonitors(CancellationTokenSource expectedDebounce, bool forceRecreate = false)
        {
            if (_isDisposed())
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"session-monitor-reconcile-skip | mode={_mixerMode} reason=disposed", nameof(ReconcileEndpointMonitors));
                }
                return false;
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
                return false;
            }

            List<EndpointMonitorState> detachStates = [];
            List<ISessionMonitorEndpoint> attachEndpoints = [];
            List<ISessionMonitorEndpoint> disposeEndpoints = [];
            int retainedCount = 0;
            int duplicateActiveEndpoints = 0;
            int detachedCount = 0;
            bool stale;

            try
            {
                lock (_lock)
                {
                    stale = _isDisposed() || !ReferenceEquals(_sessionMonitorDebounce, expectedDebounce);
                    if (stale)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("AudioDeviceService", () => $"session-monitor-reconcile-skip | mode={_mixerMode} reason=stale-or-disposed", nameof(ReconcileEndpointMonitors));
                        }
                        disposeEndpoints.AddRange(activeEndpoints);
                    }
                    else
                    {
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

                            if (!forceRecreate && _endpointMonitors.ContainsKey(endpointId))
                            {
                                retainedCount++;
                                disposeEndpoints.Add(endpoint);
                                continue;
                            }

                            attachEndpoints.Add(endpoint);
                        }

                        foreach ((string endpointId, EndpointMonitorState state) in _endpointMonitors.ToArray())
                        {
                            if (!forceRecreate && activeEndpointIds.Contains(endpointId))
                            {
                                continue;
                            }

                            _endpointMonitors.Remove(endpointId);
                            detachStates.Add(state);
                        }
                    }
                }

                if (stale) return false;

                detachedCount = detachStates.Count;
                DisposeDetachedStates(detachStates, logIndividualEndpoints: true, logSummary: false);
                detachStates.Clear();

                int attachedCount = 0;
                int failedAttachments = 0;
                foreach (ISessionMonitorEndpoint endpoint in attachEndpoints)
                {
                    EndpointMonitorState? state = TryCreateEndpointMonitorState(endpoint, expectedDebounce);
                    if (state == null)
                    {
                        failedAttachments++;
                        continue;
                    }

                    bool installed = false;
                    lock (_lock)
                    {
                        if (_endpointMonitors.TryGetValue(state.EndpointId, out EndpointMonitorState? current) && ReferenceEquals(current, state))
                        {
                            installed = !_isDisposed() && ReferenceEquals(_sessionMonitorDebounce, expectedDebounce);
                            if (!installed)
                            {
                                _endpointMonitors.Remove(state.EndpointId);
                            }
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
                return failedAttachments == 0 && attachedCount == attachEndpoints.Count;
            }
            finally
            {
                DisposeDetachedStates(detachStates, logIndividualEndpoints: true, logSummary: false);
                DisposeEndpoints(disposeEndpoints);
            }
        }

        private EndpointMonitorState? TryCreateEndpointMonitorState(ISessionMonitorEndpoint endpoint, CancellationTokenSource expectedDebounce)
        {
            EndpointMonitorState? state = null;
            try
            {
                long monitorInstanceId = Interlocked.Increment(ref _nextEndpointMonitorInstanceId);
                string endpointId = endpoint.EndpointId;
                string displayName = endpoint.DisplayName;
                state = new EndpointMonitorState(
                    monitorInstanceId,
                    endpointId,
                    displayName,
                    endpoint,
                    (_, newSession) => HandleSessionCreated(endpointId, monitorInstanceId, newSession),
                    notificationData => HandleEndpointVolumeNotification(endpointId, monitorInstanceId, notificationData));

                bool installed;
                lock (_lock)
                {
                    installed = !_isDisposed() && ReferenceEquals(_sessionMonitorDebounce, expectedDebounce)
                        && _endpointMonitors.TryAdd(endpointId, state);
                }
                if (!installed)
                {
                    state.Dispose();
                    return null;
                }

                endpoint.SubscribeSessionCreated(state.SessionCreatedHandler);
                endpoint.SubscribeEndpointVolume(state.EndpointVolumeHandler);

                IReadOnlyList<ISessionMonitorSession> existingSessions = endpoint.GetExistingSessions();
                for (int index = 0; index < existingSessions.Count; index++)
                {
                    TryRegisterRuntimeSession(endpointId, monitorInstanceId, existingSessions[index]);
                }

                return state;
            }
            catch (Exception ex)
            {
                try
                {
                    if (state == null)
                    {
                        endpoint.Dispose();
                    }
                    else
                    {
                        lock (_lock)
                        {
                            if (_endpointMonitors.TryGetValue(state.EndpointId, out EndpointMonitorState? current) && ReferenceEquals(current, state))
                            {
                                _endpointMonitors.Remove(state.EndpointId);
                            }
                        }
                        state.Dispose();
                    }
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

        private void HandleSessionCreated(string endpointId, long monitorInstanceId, ISessionMonitorSession newSession)
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
            _onSessionCreated(_mixerMode, endpointId, newSession);
        }

        private bool TryRegisterRuntimeSession(string endpointId, long monitorInstanceId, ISessionMonitorSession sessionControl)
        {
            MonitoredSessionRegistration? registration = null;
            string? sessionInstanceId = null;
            void RemoveReservation()
            {
                lock (_lock)
                {
                    if (sessionInstanceId != null && _endpointMonitors.TryGetValue(endpointId, out var state)
                        && state.InstanceId == monitorInstanceId && state.MonitoredSessions.TryGetValue(sessionInstanceId, out var current)
                        && ReferenceEquals(current, registration)) state.MonitoredSessions.Remove(sessionInstanceId);
                }
            }
            try
            {
                sessionInstanceId = TryResolveSessionInstanceId(sessionControl);
                if (string.IsNullOrWhiteSpace(sessionInstanceId))
                {
                    sessionControl.Dispose();
                    return false;
                }

                var eventClient = new SessionEventClient(monitorInstanceId, endpointId, sessionInstanceId, HandleSessionVolumeChanged, HandleSessionStateChanged, HandleSessionDisconnected);
                // Both metadata and registration can block in a disconnected driver.
                // Reserve ownership under the state lock, but do all native work outside it.
                registration = new MonitoredSessionRegistration(sessionControl, eventClient);
                bool reserved;
                lock (_lock)
                {
                    reserved = !_isDisposed() && _endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                        && state.InstanceId == monitorInstanceId && state.MonitoredSessions.TryAdd(sessionInstanceId, registration);
                    if (reserved)
                    {
                        _sessionCleanupTasks.RemoveWhere(task => task.IsCompleted);
                        _sessionCleanupTasks.Add(registration.Completion);
                    }
                }
                if (reserved && registration.Register() && IsEndpointMonitoringActive(endpointId, monitorInstanceId)) return true;
                RemoveReservation();
                registration.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                RemoveReservation();
                if (registration != null) registration.Dispose(); else sessionControl.Dispose();
                _logger.Warning("AudioDeviceService", "Failed to attach session lifecycle monitor", nameof(TryRegisterRuntimeSession), ex);
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

        private bool TryGetSharedRowLifecycleState(string endpointId, long monitorInstanceId, string sessionInstanceId, out bool affectsSharedRows, out uint? processId)
        {
            lock (_lock)
            {
                affectsSharedRows = false;
                processId = null;

                if (_isDisposed()
                    || !_endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                    || state.InstanceId != monitorInstanceId
                    || !state.MonitoredSessions.TryGetValue(sessionInstanceId, out MonitoredSessionRegistration? registration))
                {
                    return false;
                }

                affectsSharedRows = registration.AffectsSharedRows;
                processId = registration.ProcessId;
                return true;
            }
        }

        private void HandleSessionStateChanged(long monitorInstanceId, string endpointId, string sessionInstanceId, AudioSessionState state)
        {
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows, out _))
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
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows, out uint? processId))
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
                IsMuted: isMuted,
                ProcessId: processId));
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
            if (!TryGetSharedRowLifecycleState(endpointId, monitorInstanceId, sessionInstanceId, out bool affectsSharedRows, out _))
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

            if (disconnectReason == AudioSessionDisconnectReason.DisconnectReasonServerShutdown)
            {
                DetachEndpointMonitorForReattach(endpointId, monitorInstanceId);
            }
        }

        private void DetachEndpointMonitorForReattach(string endpointId, long monitorInstanceId)
        {
            EndpointMonitorState? detached = null;
            long stopGeneration;
            lock (_lock)
            {
                if (_isDisposed()) return;
                stopGeneration = _stopGeneration;
                if (_endpointMonitors.TryGetValue(endpointId, out EndpointMonitorState? state)
                    && state.InstanceId == monitorInstanceId)
                {
                    _endpointMonitors.Remove(endpointId);
                    detached = state;
                }
            }

            if (detached == null) return;

            // Dispose detached COM state unconditionally (never drop on background queue saturation) and track for drain.
            void CleanupDetached()
            {
                try
                {
                    DisposeDetachedStates([detached], logIndividualEndpoints: true, logSummary: false);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", "session-monitor-server-shutdown-detach-failed", nameof(DetachEndpointMonitorForReattach), ex);
                }

                bool shouldReattach;
                lock (_lock)
                {
                    shouldReattach = !_isDisposed() && _stopGeneration == stopGeneration;
                }

                if (shouldReattach)
                {
                    if (ReattachRequested is { } reattach) reattach();
                    else Update();
                }
            }

            Task cleanupTask;
            try
            {
                cleanupTask = ComThreadingHelper.RunOnCoreAudioThreadAsync(CleanupDetached, CancellationToken.None);
            }
            catch
            {
                cleanupTask = Task.Run(CleanupDetached);
            }

            lock (_lock)
            {
                _sessionCleanupTasks.RemoveWhere(static task => task.IsCompleted);
                _sessionCleanupTasks.Add(cleanupTask);
            }

            _ = cleanupTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    lock (_lock)
                    {
                        _sessionCleanupTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

            if (registration == null)
            {
                return;
            }

            // Never unregister IAudioSessionEvents or release IAudioSessionControl synchronously inside a WASAPI callback
            // (AudioSes.dll can deadlock waiting for active callbacks to drain).
            Task cleanupTask;
            try
            {
                cleanupTask = ComThreadingHelper.RunOnCoreAudioThreadAsync(registration.Dispose);
            }
            catch
            {
                cleanupTask = Task.Run(registration.Dispose);
            }

            lock (_lock)
            {
                _sessionCleanupTasks.RemoveWhere(static task => task.IsCompleted);
                _sessionCleanupTasks.Add(cleanupTask);
            }

            _ = cleanupTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    lock (_lock)
                    {
                        _sessionCleanupTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private SessionMonitorDetachSnapshot DetachStateUnsafe()
        {
            _stopGeneration++;
            CancellationTokenSource? debounce = _sessionMonitorDebounce;
            EndpointMonitorState[] endpointStates = [.. _endpointMonitors.Values];

            _sessionMonitorDebounce = null;
            _immediateUpdate = null;
            _refreshAfterImmediate = false;
            _endpointMonitors.Clear();

            _sessionCleanupTasks.RemoveWhere(task => task.IsCompleted);
            return new SessionMonitorDetachSnapshot(debounce, endpointStates, [.. _sessionCleanupTasks]);
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
