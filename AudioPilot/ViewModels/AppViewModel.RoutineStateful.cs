using System.IO;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        /// <summary>
        /// Captures the previous default device state for a stateful routine activation.
        /// </summary>
        /// <remarks>
        /// Stateful routines keep enough activation context to restore prior defaults when the last active session
        /// for a trigger path ends, without treating device-change routines as sticky sessions.
        /// </remarks>
        internal readonly record struct RoutineAudioRestoreSnapshot(
            string PreviousOutputDeviceId,
            string PreviousOutputDeviceName,
            string PreviousInputDeviceId,
            string PreviousInputDeviceName,
            float? PreviousOutputVolumePercent = null,
            bool? PreviousOutputMuted = null,
            float? PreviousInputVolumePercent = null,
            bool? PreviousInputMuted = null);

        internal sealed class RoutineStatefulSession(
            string sessionKey,
            string routineId,
            string routineName,
            RoutineTriggerKind triggerKind,
            long activationSequence,
            bool restorePreviousAudioOnDeactivate,
            RoutineAudioRestoreSnapshot? restoreSnapshot,
            int? rootProcessId = null,
            string routineConfigurationFingerprint = "")
        {
            public string SessionKey { get; } = sessionKey;
            public string RoutineId { get; } = routineId;
            public string RoutineName { get; } = routineName;
            public RoutineTriggerKind TriggerKind { get; } = triggerKind;
            public long ActivationSequence { get; } = activationSequence;
            public bool RestorePreviousAudioOnDeactivate { get; } = restorePreviousAudioOnDeactivate;
            public RoutineAudioRestoreSnapshot? RestoreSnapshot { get; } = restoreSnapshot;
            public int? RootProcessId { get; } = rootProcessId;
            public string RoutineConfigurationFingerprint { get; } = routineConfigurationFingerprint;
        }

        private static readonly HashSet<string> SteamProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam",
            "steamwebhelper",
            "steamui",
        };
        private const string SteamBigPictureTitleTokenBig = "big";
        private const string SteamBigPictureTitleTokenPicture = "picture";
        private const string SteamBigPictureWindowClassSdl = "SDL_app";
        private const string SteamBigPictureWindowClassCui = "CUIEngineWin32";
        private const string SteamProcessNameRoot = "steam";

        private readonly Dictionary<string, RoutineStatefulSession> _activeRoutineStatefulSessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lazy<ISteamBigPictureSignalMonitor> _steamBigPictureSignalMonitor;
        private ISteamBigPictureSignalMonitor? _unmaterializedSteamBigPictureSignalMonitor;
        private readonly HashSet<nint> _activeSteamBigPictureWindows = [];
        private List<AudioRoutine> _steamBigPictureTriggeredRoutines = [];
        private bool _steamBigPictureMonitorRunning;
        private bool _steamBigPictureMonitorStartPending;
        private bool _steamBigPictureUsingFallbackRevalidation;
        private bool _steamBigPictureDetected;
        private long _routineStatefulActivationSequence;

        private RoutineAudioRestoreSnapshot? CaptureRoutineRestoreSnapshotIfNeeded(AudioRoutine routine)
        {
            return AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
                routine,
                GetDefaultPlaybackDeviceInfo,
                GetDefaultRecordingDeviceInfo,
                _logger);
        }

        private void RegisterRoutineStatefulSession(AudioRoutine routine, int? rootProcessId, RoutineAudioRestoreSnapshot? restoreSnapshot)
        {
            if (routine == null || !routine.IsStatefulTrigger)
            {
                return;
            }

            long activationSequence = Interlocked.Increment(ref _routineStatefulActivationSequence);
            RoutineStatefulSession session;

            lock (_routineAppStartMonitorLock)
            {
                session = AppRoutineStatefulCoordinator.CreateSession(routine, rootProcessId, activationSequence, restoreSnapshot);
                _activeRoutineStatefulSessions[session.SessionKey] = session;
            }

            _logger.Info(
                "AppViewModel",
                () => $"routine-stateful-session-registered | {BuildRoutineStatefulSessionLogContext(session, shouldRestore: null)}");

            UpdateRoutineAppStartMonitorState();
            UpdateSteamBigPictureMonitorState();
        }

        private async Task DeactivateEndedAppStartSessionsAsync(
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
            CancellationToken cancellationToken)
        {
            List<string> endedSessionKeys;
            long latestActivationSequence;
            lock (_routineAppStartMonitorLock)
            {
                endedSessionKeys = AppRoutineStatefulCoordinator.GetEndedAppStartSessionKeys(_activeRoutineStatefulSessions, processSnapshots);
                latestActivationSequence = AppRoutineStatefulCoordinator.GetLatestActivationSequence(_activeRoutineStatefulSessions.Values);
            }

            foreach (string sessionKey in endedSessionKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeactivateRoutineStatefulSessionAsync(sessionKey, latestActivationSequence);
            }
        }

        private async Task DeactivateSteamBigPictureSessionsAsync()
        {
            List<string> steamSessionKeys;
            long latestActivationSequence;
            lock (_routineAppStartMonitorLock)
            {
                latestActivationSequence = AppRoutineStatefulCoordinator.GetLatestActivationSequence(_activeRoutineStatefulSessions.Values);
                steamSessionKeys = [.. _activeRoutineStatefulSessions.Values
                    .Where(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture)
                    .OrderByDescending(static session => session.ActivationSequence)
                    .Select(static session => session.SessionKey)];
            }

            foreach (string sessionKey in steamSessionKeys)
            {
                await DeactivateRoutineStatefulSessionAsync(sessionKey, latestActivationSequence);
            }
        }

        private async Task DeactivateAllRoutineStatefulSessionsForCleanupAsync()
        {
            List<string> sessionKeys;
            long latestActivationSequence;
            lock (_routineAppStartMonitorLock)
            {
                if (_activeRoutineStatefulSessions.Count == 0)
                {
                    return;
                }

                latestActivationSequence = AppRoutineStatefulCoordinator.GetLatestActivationSequence(_activeRoutineStatefulSessions.Values);
                sessionKeys = [..
                    _activeRoutineStatefulSessions.Values
                        .OrderByDescending(static session => session.ActivationSequence)
                        .Select(static session => session.SessionKey)];
            }

            foreach (string sessionKey in sessionKeys)
            {
                await DeactivateRoutineStatefulSessionAsync(sessionKey, latestActivationSequence);
            }
        }

        private async Task DeactivateRoutineStatefulSessionAsync(string sessionKey, long? restoreActivationSequence = null)
        {
            RoutineStatefulSession? session;
            bool shouldRestore;

            lock (_routineAppStartMonitorLock)
            {
                RoutineStatefulSessionDeactivationResult result = AppRoutineStatefulCoordinator.DeactivateSession(
                    _activeRoutineStatefulSessions,
                    sessionKey,
                    restoreActivationSequence);
                session = result.Session;
                shouldRestore = result.ShouldRestore;
            }

            try
            {
                await AppViewModelRoutineStatefulDeactivationHelper.ApplyAsync(
                    session,
                    shouldRestore,
                    _logger,
                    RestoreRoutineAudioSnapshotAsync,
                    UpdateRoutineAppStartMonitorState,
                    UpdateSteamBigPictureMonitorState);
            }
            finally
            {
                CompletePendingAppAudioWaitIfNeeded(session);
            }
        }

        private Task DeactivateRoutineFromApplicationTrigger(AudioRoutine routine, int processId)
        {
            return DeactivateRoutineFromApplicationTrigger(
                routine,
                processId,
                sessionKey => DeactivateRoutineStatefulSessionAsync(sessionKey));
        }

        private Task DeactivateRoutineFromApplicationTrigger(
            AudioRoutine routine,
            int processId,
            Func<string, Task> deactivateSessionAsync)
        {
            return _dispatcher.InvokeAsync((Func<Task>)(async () =>
            {
                if (_isCleaningUp || processId <= 0)
                {
                    return;
                }

                RoutineAppOutputLease? removedLease = null;
                string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id;
                string leaseKey = CreateRoutineAppOutputLeaseKey(routineId, processId);
                lock (_routineAppStartMonitorLock)
                {
                    if (_activeRoutineAppOutputLeases.Remove(leaseKey, out RoutineAppOutputLease? lease))
                    {
                        removedLease = lease.Clone();
                        RecalculateRoutineAppOutputLeasePendingCountsLocked();
                    }

                    _pendingRoutineAppStartRoots.Remove(leaseKey);
                }

                if (routine.SwitchOutputPerApp && routine.RestorePreviousAudioOnDeactivate)
                {
                    _audio.TryResetApplicationDeviceRouting(
                        (uint)processId,
                        resetOutput: !string.IsNullOrWhiteSpace(routine.OutputDeviceId),
                        resetInput: !string.IsNullOrWhiteSpace(routine.InputDeviceId),
                        opId: $"routine-focus-deactivate:{RoutineTriggerPathHelper.GetTriggerDisplayName(routine.TriggerAppPath)}:{processId}");
                }

                if (removedLease != null)
                {
                    CompletePendingAppAudioWaitForRemovedLease(removedLease);
                    UpdateRoutineAppOutputLeaseSessionMonitoringState();
                    UpdateRoutineAppStartMonitorState();
                }

                string sessionKey = CreateRoutineStatefulSessionKey(routine, processId);
                await deactivateSessionAsync(sessionKey);
            })).Task.Unwrap();
        }

        private void CompletePendingAppAudioWaitIfNeeded(RoutineStatefulSession? session)
        {
            if (session == null || session.TriggerKind != RoutineTriggerKind.Application)
            {
                return;
            }

            string normalizedRoutineId = NormalizeRoutineId(session.RoutineId);
            lock (_routineAppStartMonitorLock)
            {
                bool hasRemainingSession = _activeRoutineStatefulSessions.Values.Any(activeSession =>
                    activeSession.TriggerKind == RoutineTriggerKind.Application &&
                    string.Equals(NormalizeRoutineId(activeSession.RoutineId), normalizedRoutineId, StringComparison.OrdinalIgnoreCase));
                if (hasRemainingSession)
                {
                    return;
                }
            }

            RoutineRuntimeState? runtimeState = GetRoutineRuntimeStateSnapshot(normalizedRoutineId);
            if (runtimeState?.LastRunState != RoutineLastRunState.WaitingForApp)
            {
                return;
            }

            SetRoutineLastRunState(
                session.RoutineId,
                RoutineLastRunState.Skipped,
                "App closed before audio appeared");
        }

        private async Task RestoreRoutineAudioSnapshotAsync(RoutineStatefulSession session)
        {
            RoutineRestoreResult result;
            if (!session.RestoreSnapshot.HasValue)
            {
                result = await AppViewModelRoutineRestoreHelper.RestoreAsync(
                    session,
                    default,
                    _logger);
                ShowRoutineRestoreOverlay(result);
                return;
            }

            result = await AppViewModelRoutineRestoreHelper.RestoreAsync(
                session,
                new AppViewModelRoutineRestoreDependencies(
                    _audio.TryGetActivePlaybackCycleEntry,
                    GetDefaultPlaybackDeviceId,
                    async (currentDeviceId, targetDeviceId, opId) =>
                    {
                        await _audio.SwitchAudioDeviceAsync(
                            currentDeviceId,
                            targetDeviceId,
                            _muteMicBackingField,
                            _muteSoundBackingField,
                            _deafenBackingField,
                            _preserveAudioLevelsBackingField,
                            opId: opId);
                    },
                    _audio.TryGetActiveRecordingCycleEntry,
                    async (targetDeviceId, targetDeviceName, opId) =>
                    {
                        await _audio.SwitchInputDeviceToAsync(
                            targetDeviceId,
                            targetDeviceName,
                            preserveAudioLevels: ShouldPreserveAudioLevelsForRoutineDeviceSwitch(),
                            showOverlay: null,
                            opId: opId);
                    },
                    RestoreDefaultPlaybackVolumeAsync,
                    RestoreDefaultRecordingVolumeAsync),
                _logger);
            ShowRoutineRestoreOverlay(result);
        }

        private void ShowRoutineRestoreOverlay(RoutineRestoreResult result)
        {
            if (!result.HasRestoredDevice ||
                !AppViewModelRoutineOverlayHelper.TryBuildRoutineRestoreOverlayPlan(
                    result.RestoredOutputDeviceName,
                    result.RestoredInputDeviceName,
                    out AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan plan))
            {
                return;
            }

            ShowRoutineSuccessOverlay(plan);
        }

        private string? GetDefaultPlaybackDeviceId()
        {
            return AppViewModelRoutineRestoreDeviceInfoHelper.GetDeviceId(
                _audio.GetDefaultPlaybackDevice,
                static device => device.ID);
        }

        private RoutineRestoreDeviceInfo GetDefaultPlaybackDeviceInfo()
        {
            return GetDefaultEndpointRestoreDeviceInfo(_audio.GetDefaultPlaybackDevice, "routine-restore-snapshot:playback");
        }

        private RoutineRestoreDeviceInfo GetDefaultRecordingDeviceInfo()
        {
            return GetDefaultEndpointRestoreDeviceInfo(_audio.GetDefaultRecordingDevice, "routine-restore-snapshot:recording");
        }

        private RoutineRestoreDeviceInfo GetDefaultEndpointRestoreDeviceInfo(Func<MMDevice?> getDefaultDevice, string reason)
        {
            using MMDevice? device = getDefaultDevice();
            if (device == null)
            {
                return new RoutineRestoreDeviceInfo(string.Empty, string.Empty);
            }

            float? volumePercent = null;
            bool? isMuted = null;
            if (AppCliOverlayCoordinator.TryGetEndpointVolumeState(_logger, device, reason, out float currentPercent, out bool muted))
            {
                volumePercent = currentPercent;
                isMuted = muted;
            }
            else
            {
                _logger.Warning("AppViewModel", () => $"routine-restore-snapshot-read-skipped | reason={NormalizeRoutineLogValue(reason)} deviceId={FormatRoutineLogIdentifier(device.ID)} device={FormatRoutineLogLabel(device.FriendlyName)} endpointState=unavailable");
            }

            return new RoutineRestoreDeviceInfo(
                device.ID ?? string.Empty,
                device.FriendlyName ?? string.Empty,
                volumePercent,
                isMuted);
        }

        private Task RestoreDefaultPlaybackVolumeAsync(float targetPercent, bool muted, string opId)
        {
            ApplyDefaultEndpointVolume(_audio.GetDefaultPlaybackDevice, targetPercent, muted, opId, "playback");
            return Task.CompletedTask;
        }

        private Task RestoreDefaultRecordingVolumeAsync(float targetPercent, bool muted, string opId)
        {
            ApplyDefaultEndpointVolume(_audio.GetDefaultRecordingDevice, targetPercent, muted, opId, "recording");
            return Task.CompletedTask;
        }

        private void ApplyDefaultEndpointVolume(Func<MMDevice?> getDefaultDevice, float targetPercent, bool muted, string opId, string flow)
        {
            try
            {
                using MMDevice? device = getDefaultDevice();
                if (device == null)
                {
                    _logger.Warning("AppViewModel", () => $"routine-stateful-restore-volume-skipped | flow={flow} opId={NormalizeRoutineLogValue(opId)} reason=no-default-device");
                    return;
                }

                if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(_logger, device, targetPercent, opId, muteAtZero: false, unmuteAboveZero: true, out _))
                {
                    _logger.Warning("AppViewModel", () => $"routine-stateful-restore-volume-skipped | flow={flow} opId={NormalizeRoutineLogValue(opId)} reason=apply-failed");
                    return;
                }

                if (AudioDeviceHelper.TryGetEndpointVolume(_logger, device, out var endpointVolume, opId))
                {
                    endpointVolume.Mute = muted;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", $"routine-stateful-restore-volume-failed | flow={flow}", nameof(ApplyDefaultEndpointVolume), ex);
            }
        }

        internal async Task ExecuteDeviceChangeTriggeredRoutinesAsync(CancellationToken cancellationToken)
        {
            if (_isCleaningUp)
            {
                return;
            }

            List<AudioRoutine> routines = GetDeviceChangeTriggeredRoutinesForExecution(GetPersistedRoutineSnapshot());

            foreach (AudioRoutine routine in routines)
            {
                RoutineExecutionResult result = await ExecuteRoutineOnDispatcherAsync(
                    routine,
                    showOverlay: true,
                    applicationProcessId: null,
                    executionSource: "device-change",
                    cancellationToken);
                if (!result.Success)
                {
                    continue;
                }
            }
        }

        internal static List<AudioRoutine> GetDeviceChangeTriggeredRoutinesForExecution(IEnumerable<AudioRoutine> routines)
        {
            ArgumentNullException.ThrowIfNull(routines);

            return
            [
                .. routines
                    .Where(static routine =>
                        routine.Enabled &&
                        routine.TriggerKind == RoutineTriggerKind.DeviceChange &&
                        routine.HasExecutionTarget)
                    .OrderBy(static routine => routine.DisplayOrder)
                    .ThenBy(static routine => routine.Name, StringComparer.Ordinal)
                    .Select(static routine => routine.Clone())
            ];
        }

        private Task<RoutineExecutionResult> ExecuteRoutineOnDispatcherAsync(AudioRoutine routine, bool showOverlay, int? applicationProcessId, string executionSource, CancellationToken cancellationToken = default)
        {
            if (_dispatcher.CheckAccess())
            {
                return ExecuteRoutineAsync(routine, showOverlay, applicationProcessId, executionSource, cancellationToken: cancellationToken);
            }

            return InvokeOnDispatcherAsync(
                () => ExecuteRoutineAsync(routine, showOverlay, applicationProcessId, executionSource, cancellationToken: cancellationToken),
                BuildSkippedRoutineExecutionResult());
        }

        private void UpdateSteamBigPictureMonitorState()
        {
            bool startMonitor = false;
            bool stopMonitor = false;
            SteamBigPictureMonitorDecision decision;

            lock (_routineAppStartMonitorLock)
            {
                decision = AppRoutineStatefulCoordinator.ResolveSteamBigPictureMonitorDecision(
                    _routineAppStartMonitoringEnabled,
                    _isCleaningUp,
                    _steamBigPictureTriggeredRoutines.Count,
                    _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture),
                    _steamBigPictureMonitorRunning ||
                    _steamBigPictureMonitorStartPending ||
                    _steamBigPictureUsingFallbackRevalidation);

                if (!decision.ShouldMonitor)
                {
                    _steamBigPictureDetected = false;
                    _activeSteamBigPictureWindows.Clear();
                    stopMonitor = _steamBigPictureMonitorRunning;
                    _steamBigPictureMonitorRunning = false;
                    _steamBigPictureMonitorStartPending = false;
                    _steamBigPictureUsingFallbackRevalidation = false;
                }
                else if (decision.StartMonitor)
                {
                    _steamBigPictureMonitorStartPending = true;
                    startMonitor = true;
                }
            }

            if (stopMonitor)
            {
                _steamBigPictureSignalMonitor.Value.Stop();
                CancellationTokenSource? debounceToDispose = CancelAndDetachDebounce(ref _steamBigPictureDebounceCts);
                debounceToDispose?.Dispose();
                CancellationTokenSource? confirmationToDispose = CancelAndDetachDebounce(ref _steamBigPictureConfirmationDebounceCts);
                confirmationToDispose?.Dispose();
                _ = CancelAndDetachDebounce(ref _steamBigPictureFallbackMaintenanceCts);
            }

            if (!startMonitor)
            {
                return;
            }

            SteamBigPictureSignalMonitorStartResult startResult = _steamBigPictureSignalMonitor.Value.Start();
            bool keepMonitor;
            lock (_routineAppStartMonitorLock)
            {
                _steamBigPictureMonitorStartPending = false;
                keepMonitor = _routineAppStartMonitoringEnabled &&
                    !_isCleaningUp &&
                    (_steamBigPictureTriggeredRoutines.Count > 0 ||
                        _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));

                _steamBigPictureMonitorRunning = startResult.Success && keepMonitor;
                _steamBigPictureUsingFallbackRevalidation = !startResult.Success && keepMonitor;
            }

            if (!keepMonitor)
            {
                if (startResult.Success)
                {
                    _steamBigPictureSignalMonitor.Value.Stop();
                }

                return;
            }

            if (startResult.Success)
            {
                _logger.Info("AppViewModel", () => $"steam-big-picture-monitor-active | watcher={_steamBigPictureSignalMonitor.Value.GetType().Name}");
                QueueSteamBigPictureSignalEvaluation();
                return;
            }

            _logger.Warning("AppViewModel", () => $"steam-big-picture-monitor-fallback-active | reason={startResult.FailureReason ?? startResult.Status} mode=event-revalidation watcher={_steamBigPictureSignalMonitor.Value.GetType().Name}");
            QueueSteamBigPictureSignalEvaluation();
            StartSteamBigPictureFallbackMaintenance();
        }

        private void StartSteamBigPictureFallbackMaintenance()
        {
            CancellationTokenSource maintenanceCts;
            lock (_routineAppStartMonitorLock)
            {
                if (_isCleaningUp ||
                    !_steamBigPictureUsingFallbackRevalidation ||
                    _steamBigPictureFallbackMaintenanceCts != null)
                {
                    return;
                }

                maintenanceCts = new CancellationTokenSource();
                _steamBigPictureFallbackMaintenanceCts = maintenanceCts;
            }

            bool queued = TryRunBackgroundWork(async shutdownToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    shutdownToken,
                    maintenanceCts.Token);
                CancellationToken cancellationToken = linkedCts.Token;
                int elapsedSinceHookRetryMs = 0;

                try
                {
                    while (true)
                    {
                        await Task.Delay(
                            AppConstants.Routines.SteamBigPictureFallbackRevalidationIntervalMs,
                            cancellationToken);
                        QueueSteamBigPictureSignalEvaluation();

                        elapsedSinceHookRetryMs += AppConstants.Routines.SteamBigPictureFallbackRevalidationIntervalMs;
                        if (elapsedSinceHookRetryMs < AppConstants.Routines.SteamBigPictureHookRetryIntervalMs)
                        {
                            continue;
                        }

                        elapsedSinceHookRetryMs = 0;
                        if (TryRecoverSteamBigPictureMonitorFromFallback())
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (_routineAppStartMonitorLock)
                    {
                        if (ReferenceEquals(_steamBigPictureFallbackMaintenanceCts, maintenanceCts))
                        {
                            _steamBigPictureFallbackMaintenanceCts = null;
                        }
                    }

                    maintenanceCts.Dispose();
                }
            }, nameof(StartSteamBigPictureFallbackMaintenance));

            if (!queued)
            {
                lock (_routineAppStartMonitorLock)
                {
                    if (ReferenceEquals(_steamBigPictureFallbackMaintenanceCts, maintenanceCts))
                    {
                        _steamBigPictureFallbackMaintenanceCts = null;
                    }
                }

                maintenanceCts.Dispose();
            }
        }

        private bool TryRecoverSteamBigPictureMonitorFromFallback()
        {
            lock (_routineAppStartMonitorLock)
            {
                if (_isCleaningUp ||
                    !_steamBigPictureUsingFallbackRevalidation ||
                    _steamBigPictureMonitorRunning ||
                    _steamBigPictureMonitorStartPending)
                {
                    return false;
                }

                _steamBigPictureMonitorStartPending = true;
            }

            SteamBigPictureSignalMonitorStartResult startResult = _steamBigPictureSignalMonitor.Value.Start();
            bool recovered;
            lock (_routineAppStartMonitorLock)
            {
                _steamBigPictureMonitorStartPending = false;
                recovered = startResult.Success &&
                    _routineAppStartMonitoringEnabled &&
                    !_isCleaningUp &&
                    (_steamBigPictureTriggeredRoutines.Count > 0 ||
                        _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
                if (recovered)
                {
                    _steamBigPictureMonitorRunning = true;
                    _steamBigPictureUsingFallbackRevalidation = false;
                }
                else if (startResult.Success)
                {
                    _steamBigPictureMonitorRunning = false;
                    _steamBigPictureUsingFallbackRevalidation = false;
                }
            }

            if (!recovered)
            {
                if (startResult.Success)
                {
                    _steamBigPictureSignalMonitor.Value.Stop();
                }

                _logger.Trace("AppViewModel", () => $"steam-big-picture-monitor-retry-failed | reason={startResult.FailureReason ?? startResult.Status}");
                return false;
            }

            _logger.Info("AppViewModel", () => $"steam-big-picture-monitor-recovered | watcher={_steamBigPictureSignalMonitor.Value.GetType().Name}");
            QueueSteamBigPictureSignalEvaluation();
            return true;
        }

        private void OnSteamBigPictureMonitorSignaled(SteamBigPictureSignal signal)
        {
            if (_isCleaningUp)
            {
                return;
            }

            if (TryHandleSteamBigPictureSignalDirectly(signal))
            {
                return;
            }

            QueueSteamBigPictureSignalEvaluation();
        }

        private void RequestSteamBigPictureFallbackRevalidation()
        {
            if (_isCleaningUp)
            {
                return;
            }

            lock (_routineAppStartMonitorLock)
            {
                if (!_steamBigPictureUsingFallbackRevalidation)
                {
                    return;
                }
            }

            QueueSteamBigPictureSignalEvaluation();
        }

        private bool TryHandleSteamBigPictureSignalDirectly(SteamBigPictureSignal signal)
        {
            if (signal.Hwnd == nint.Zero)
            {
                return false;
            }

            SteamBigPictureStateChangeResult transition = default;
            bool handled;
            bool requiresSnapshotEvaluation = false;

            lock (_routineAppStartMonitorLock)
            {
                if (_steamBigPictureUsingFallbackRevalidation || !_steamBigPictureMonitorRunning)
                {
                    return false;
                }

                bool knownHandle = _activeSteamBigPictureWindows.Contains(signal.Hwnd);
                bool directMatch = TryMatchSteamBigPictureSignal(signal, out _);

                handled = true;
                switch (signal.Kind)
                {
                    case SteamBigPictureSignalKind.Foreground:
                    case SteamBigPictureSignalKind.Create:
                    case SteamBigPictureSignalKind.Show:
                    case SteamBigPictureSignalKind.Unknown:
                        if (directMatch)
                        {
                            _activeSteamBigPictureWindows.Add(signal.Hwnd);
                            transition = AppSteamBigPictureCoordinator.ResolveStateChange(
                                _steamBigPictureDetected,
                                isSteamBigPictureActive: true,
                                _steamBigPictureTriggeredRoutines);
                            _steamBigPictureDetected = transition.NextDetectedState;
                        }

                        break;

                    case SteamBigPictureSignalKind.Destroy:
                        if (knownHandle)
                        {
                            _activeSteamBigPictureWindows.Remove(signal.Hwnd);
                        }

                        if (knownHandle || directMatch)
                        {
                            requiresSnapshotEvaluation = true;
                            handled = false;
                        }

                        break;

                    case SteamBigPictureSignalKind.Hide:
                    case SteamBigPictureSignalKind.NameChange:
                        if (knownHandle || directMatch)
                        {
                            if (knownHandle)
                            {
                                _activeSteamBigPictureWindows.Remove(signal.Hwnd);
                            }

                            requiresSnapshotEvaluation = true;
                            handled = false;
                        }

                        break;
                }
            }

            if (requiresSnapshotEvaluation)
            {
                return false;
            }

            if (!transition.StateChanged)
            {
                return handled;
            }

            RunBackgroundWork(
                async _ => await ApplySteamBigPictureStateChangeAsync(transition),
                nameof(OnSteamBigPictureMonitorSignaled));
            return true;
        }

        private static bool TryMatchSteamBigPictureSignal(SteamBigPictureSignal signal, out string matchReason)
        {
            string processName = string.IsNullOrWhiteSpace(signal.ProcessExecutablePath)
                ? string.Empty
                : NormalizeSteamWindowValue(Path.GetFileNameWithoutExtension(signal.ProcessExecutablePath));

            if (!SteamProcessNames.Contains(processName))
            {
                matchReason = string.Empty;
                return false;
            }

            return TryMatchSteamBigPictureWindow(
                new AudioDeviceHelper.VisibleWindowMetadata(signal.Title, signal.ClassName),
                processName,
                out matchReason);
        }

        private void QueueSteamBigPictureSignalEvaluation()
        {
            Interlocked.Increment(ref _pendingSteamBigPictureSignals);
            CancellationTokenSource? confirmationToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _steamBigPictureConfirmationDebounceCts);
            confirmationToDispose?.Dispose();
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _steamBigPictureDebounceCts);
            RunBackgroundWork(async shutdownToken =>
            {
                await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                    nextDebounceCts,
                    ownedDebounce => ReleaseOwnedDebounce(ref _steamBigPictureDebounceCts, ownedDebounce),
                    RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs,
                    async linkedToken =>
                    {
                        Interlocked.Exchange(ref _pendingSteamBigPictureSignals, 0);

                        SteamBigPictureLoopContext loopContext;
                        lock (_routineAppStartMonitorLock)
                        {
                            loopContext = AppSteamBigPictureCoordinator.BuildLoopContext(
                                _routineAppStartMonitoringEnabled,
                                _isCleaningUp,
                                _steamBigPictureTriggeredRoutines,
                                _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
                        }

                        if (!loopContext.ShouldMonitor)
                        {
                            return;
                        }

                        await EvaluateSteamBigPictureMonitorSignalAsync(loopContext.WatchedRoutines, allowConfirmationCheck: true, linkedToken);
                    },
                    shutdownToken);
            }, nameof(OnSteamBigPictureMonitorSignaled));
        }

        private void QueueSteamBigPictureConfirmationEvaluation()
        {
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _steamBigPictureConfirmationDebounceCts);
            RunBackgroundWork(async shutdownToken =>
            {
                await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                    nextDebounceCts,
                    ownedDebounce => ReleaseOwnedDebounce(ref _steamBigPictureConfirmationDebounceCts, ownedDebounce),
                    RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs,
                    async linkedToken =>
                    {
                        SteamBigPictureLoopContext loopContext;
                        lock (_routineAppStartMonitorLock)
                        {
                            loopContext = AppSteamBigPictureCoordinator.BuildLoopContext(
                                _routineAppStartMonitoringEnabled,
                                _isCleaningUp,
                                _steamBigPictureTriggeredRoutines,
                                _activeRoutineStatefulSessions.Values.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
                        }

                        if (!loopContext.ShouldMonitor)
                        {
                            return;
                        }

                        await EvaluateSteamBigPictureMonitorSignalAsync(loopContext.WatchedRoutines, allowConfirmationCheck: false, linkedToken);
                    },
                    shutdownToken);
            }, nameof(QueueSteamBigPictureConfirmationEvaluation));
        }

        private async Task EvaluateSteamBigPictureMonitorSignalAsync(
            IReadOnlyList<AudioRoutine> watchedRoutines,
            bool allowConfirmationCheck,
            CancellationToken cancellationToken)
        {
            List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                () => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.None),
                cancellationToken);
            IReadOnlyList<AudioDeviceHelper.VisibleWindowHandleMetadata> visibleWindows = AudioDeviceHelper.GetVisibleWindowHandleMetadataByProcessIds(
                processSnapshots.Select(static snapshot => snapshot.ProcessId));
            HashSet<nint> activeWindowHandles = [..
                GetSteamBigPictureWindowHandles(
                    processSnapshots,
                    visibleWindows,
                    message => _logger.Trace("AppViewModel", message))];
            bool isSteamBigPictureActive = activeWindowHandles.Count > 0;
            await EvaluateSteamBigPictureStateChangeAsync(
                isSteamBigPictureActive,
                watchedRoutines,
                allowConfirmationCheck,
                activeWindowHandles);
        }

        private async Task EvaluateSteamBigPictureStateChangeAsync(
            bool isSteamBigPictureActive,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            bool allowConfirmationCheck,
            IReadOnlySet<nint>? activeWindowHandles = null)
        {
            SteamBigPictureSignalEvaluationDecision decision;
            lock (_routineAppStartMonitorLock)
            {
                _activeSteamBigPictureWindows.Clear();
                if (activeWindowHandles != null)
                {
                    foreach (nint hwnd in activeWindowHandles)
                    {
                        _activeSteamBigPictureWindows.Add(hwnd);
                    }
                }

                decision = AppSteamBigPictureCoordinator.ResolveSignalEvaluation(
                    _steamBigPictureDetected,
                    isSteamBigPictureActive,
                    watchedRoutines,
                    allowConfirmationCheck);
                _steamBigPictureDetected = decision.StateChange.NextDetectedState;
            }

            if (!decision.StateChange.StateChanged)
            {
                if (decision.ShouldQueueConfirmationCheck)
                {
                    QueueSteamBigPictureConfirmationEvaluation();
                }

                return;
            }

            await ApplySteamBigPictureStateChangeAsync(decision.StateChange);
        }

        private async Task ApplySteamBigPictureStateChangeAsync(SteamBigPictureStateChangeResult transition)
        {
            if (transition.Action == SteamBigPictureStateChangeAction.Activate)
            {
                foreach (AudioRoutine routine in transition.ActivationRoutines)
                {
                    await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
                        routine,
                        rootProcessId: null,
                        showOverlay: true,
                        executionSource: "steam-big-picture",
                        _logger,
                        CaptureRoutineRestoreSnapshotIfNeeded,
                        async (targetRoutine, shouldShowOverlay, _, source) =>
                        {
                            return await InvokeOnDispatcherAsync(
                                () => ExecuteRoutineAsync(targetRoutine, shouldShowOverlay, executionSource: source),
                                BuildSkippedRoutineExecutionResult());
                        },
                        RegisterRoutineStatefulSession,
                        BuildRoutineExecutionLogContext,
                        BuildRoutineExecutionResultLogContext);
                }

                return;
            }

            if (transition.Action == SteamBigPictureStateChangeAction.Deactivate)
            {
                await DeactivateSteamBigPictureSessionsAsync();
            }
        }

        internal static IReadOnlyList<nint> GetSteamBigPictureWindowHandles(
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
            IReadOnlyList<AudioDeviceHelper.VisibleWindowHandleMetadata> visibleWindows,
            Action<string>? trace)
        {
            ArgumentNullException.ThrowIfNull(processSnapshots);
            ArgumentNullException.ThrowIfNull(visibleWindows);

            Dictionary<int, string> steamProcessNamesById = processSnapshots
                .Where(static snapshot => snapshot.ProcessId > 0)
                .Select(snapshot => new
                {
                    snapshot.ProcessId,
                    ProcessName = Path.GetFileNameWithoutExtension(snapshot.ExecutablePath) ?? string.Empty,
                })
                .Where(static snapshot => SteamProcessNames.Contains(snapshot.ProcessName))
                .ToDictionary(
                    static snapshot => snapshot.ProcessId,
                    static snapshot => snapshot.ProcessName,
                    EqualityComparer<int>.Default);

            if (steamProcessNamesById.Count == 0)
            {
                trace?.Invoke("steam-big-picture-detect | result=inactive reason=no-steam-processes");
                return [];
            }

            int inspectedWindowCount = 0;
            var matchingHandles = new HashSet<nint>();
            foreach (AudioDeviceHelper.VisibleWindowHandleMetadata window in visibleWindows)
            {
                if (!steamProcessNamesById.TryGetValue(window.ProcessId, out string? processName))
                {
                    continue;
                }

                inspectedWindowCount++;
                AudioDeviceHelper.VisibleWindowMetadata metadata = new(window.Title, window.ClassName);
                if (TryMatchSteamBigPictureWindow(metadata, processName, out string matchReason))
                {
                    matchingHandles.Add(window.WindowHandle);
                    trace?.Invoke($"steam-big-picture-detect | result=active reason={matchReason} processId={FormatRoutineLogProcessId(window.ProcessId)} class={FormatSteamWindowClass(window.ClassName)} title={FormatSteamWindowTitle(window.Title)}");
                    continue;
                }

                trace?.Invoke($"steam-big-picture-window-rejected | processId={FormatRoutineLogProcessId(window.ProcessId)} class={FormatSteamWindowClass(window.ClassName)} title={FormatSteamWindowTitle(window.Title)}");
            }

            if (matchingHandles.Count == 0)
            {
                trace?.Invoke($"steam-big-picture-detect | result=inactive reason=no-matching-window inspectedWindowCount={inspectedWindowCount}");
                return [];
            }

            return [.. matchingHandles];
        }

        internal static bool IsSteamBigPictureActive(
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
            IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId,
            Action<string>? trace)
        {
            ArgumentNullException.ThrowIfNull(processSnapshots);
            ArgumentNullException.ThrowIfNull(visibleWindowsByProcessId);

            Dictionary<int, string> steamProcessNamesById = processSnapshots
                .Where(static snapshot => snapshot.ProcessId > 0)
                .Select(snapshot => new
                {
                    snapshot.ProcessId,
                    ProcessName = Path.GetFileNameWithoutExtension(snapshot.ExecutablePath) ?? string.Empty,
                })
                .Where(static snapshot => SteamProcessNames.Contains(snapshot.ProcessName))
                .ToDictionary(
                    static snapshot => snapshot.ProcessId,
                    static snapshot => snapshot.ProcessName,
                    EqualityComparer<int>.Default);

            if (steamProcessNamesById.Count == 0)
            {
                trace?.Invoke("steam-big-picture-detect | result=inactive reason=no-steam-processes");
                return false;
            }

            int inspectedWindowCount = 0;
            foreach ((int processId, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata> windows) in visibleWindowsByProcessId)
            {
                if (!steamProcessNamesById.TryGetValue(processId, out string? processName))
                {
                    continue;
                }

                foreach (AudioDeviceHelper.VisibleWindowMetadata window in windows)
                {
                    inspectedWindowCount++;

                    if (TryMatchSteamBigPictureWindow(window, processName, out string matchReason))
                    {
                        trace?.Invoke($"steam-big-picture-detect | result=active reason={matchReason} processId={FormatRoutineLogProcessId(processId)} class={FormatSteamWindowClass(window.ClassName)} title={FormatSteamWindowTitle(window.Title)}");
                        return true;
                    }

                    trace?.Invoke($"steam-big-picture-window-rejected | processId={FormatRoutineLogProcessId(processId)} class={FormatSteamWindowClass(window.ClassName)} title={FormatSteamWindowTitle(window.Title)}");
                }
            }

            trace?.Invoke($"steam-big-picture-detect | result=inactive reason=no-matching-window inspectedWindowCount={inspectedWindowCount}");
            return false;
        }

        internal static bool TryMatchSteamBigPictureWindow(
            AudioDeviceHelper.VisibleWindowMetadata window,
            string? processName,
            out string matchReason)
        {
            string normalizedClassName = NormalizeSteamWindowValue(window.ClassName);
            string normalizedTitle = NormalizeSteamWindowValue(window.Title);
            string normalizedProcessName = NormalizeSteamWindowValue(processName);

            if (normalizedClassName == NormalizeSteamWindowValue(SteamBigPictureWindowClassSdl) &&
                normalizedTitle.Contains(SteamBigPictureTitleTokenBig, StringComparison.Ordinal) &&
                normalizedTitle.Contains(SteamBigPictureTitleTokenPicture, StringComparison.Ordinal))
            {
                matchReason = "sdl-big-picture-title";
                return true;
            }

            if (normalizedTitle == "steam" &&
                normalizedClassName == NormalizeSteamWindowValue(SteamBigPictureWindowClassCui) &&
                normalizedProcessName != SteamProcessNameRoot)
            {
                matchReason = "cui-steam-fallback";
                return true;
            }

            if (normalizedTitle == "sp" && normalizedClassName == NormalizeSteamWindowValue(SteamBigPictureWindowClassSdl))
            {
                matchReason = "sdl-sp-fallback";
                return true;
            }

            matchReason = string.Empty;
            return false;
        }

        private static string NormalizeSteamWindowValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
        }

        private static string FormatSteamWindowTitle(string? title)
        {
            return LogPrivacy.Label(NormalizeSteamWindowValue(title));
        }

        private static string FormatSteamWindowClass(string? className)
        {
            return string.IsNullOrWhiteSpace(className) ? "<empty>" : className.Trim();
        }

        private static string CreateRoutineStatefulSessionKey(AudioRoutine routine, int? rootProcessId)
        {
            return AppRoutineStatefulCoordinator.CreateRoutineStatefulSessionKey(routine, rootProcessId);
        }

        internal static string BuildRoutineStatefulSessionLogContext(RoutineStatefulSession session, bool? shouldRestore)
        {
            ArgumentNullException.ThrowIfNull(session);

            string rootProcessValue = FormatRoutineLogProcessId(session.RootProcessId);
            return $"sessionKey={FormatRoutineLogSession(session.SessionKey)} routineId={FormatRoutineLogIdentifier(session.RoutineId)} routineName={FormatRoutineLogLabel(session.RoutineName)} triggerKind={session.TriggerKind} activationSequence={session.ActivationSequence} rootProcessId={rootProcessValue} restorePreviousAudioOnDeactivate={session.RestorePreviousAudioOnDeactivate} shouldRestore={FormatRoutineLogBool(shouldRestore)} hasRestoreSnapshot={session.RestoreSnapshot.HasValue}";
        }

        internal static string BuildRoutineRestoreSnapshotLogContext(RoutineAudioRestoreSnapshot snapshot)
        {
            return $"hasOutputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousOutputDeviceId)} outputDeviceId={FormatRoutineLogIdentifier(snapshot.PreviousOutputDeviceId)} outputDevice={FormatRoutineLogDevice(snapshot.PreviousOutputDeviceName)} outputVolumePercent={snapshot.PreviousOutputVolumePercent?.ToString() ?? "none"} outputMuted={FormatRoutineLogBool(snapshot.PreviousOutputMuted)} hasInputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousInputDeviceId)} inputDeviceId={FormatRoutineLogIdentifier(snapshot.PreviousInputDeviceId)} inputDevice={FormatRoutineLogDevice(snapshot.PreviousInputDeviceName)} inputVolumePercent={snapshot.PreviousInputVolumePercent?.ToString() ?? "none"} inputMuted={FormatRoutineLogBool(snapshot.PreviousInputMuted)}";
        }
    }
}
