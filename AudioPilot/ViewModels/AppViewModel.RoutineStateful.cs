using System.IO;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using NAudio.CoreAudioApi;
using static AudioPilot.Services.Routines.RoutineApplicationRouting;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
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

        private readonly HashSet<nint> _activeSteamBigPictureWindows = [];
        private bool _steamBigPictureMonitorRunning;
        private bool _steamBigPictureMonitorStartPending;
        private bool _steamBigPictureUsingFallbackRevalidation;
        private bool _steamBigPictureDetected;

        private RoutineAudioRestoreSnapshot? CaptureRoutineRestoreSnapshotIfNeeded(AudioRoutine routine)
        {
            return AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
                routine,
                GetDefaultPlaybackDeviceInfo,
                GetDefaultRecordingDeviceInfo,
                _logger);
        }

        private void RegisterRoutineStatefulSession(AudioRoutine routine, int? rootProcessId, RoutineAudioRestoreSnapshot? restoreSnapshot, RoutineAppStartProcessSnapshot? processIdentity = null, RoutineAppOutputLease? routingLease = null, CancellationToken cancellationToken = default)
        {
            if (routine == null || !routine.IsStatefulTrigger)
            {
                return;
            }

            RoutineStatefulSession session = _routineRuntime.Lifetime.Register(routine, rootProcessId, restoreSnapshot, processIdentity, routingLease, cancellationToken);

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
            foreach (RoutineLifetimeService.Deactivation deactivation in _routineRuntime.Lifetime.CaptureEnded(processSnapshots))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeactivateRoutineStatefulSessionAsync(deactivation);
            }
        }

        private async Task DeactivateSteamBigPictureSessionsAsync()
        {
            _routineRuntime.Lifetime.CancelTrigger(RoutineTriggerKind.SteamBigPicture);
            foreach (RoutineLifetimeService.Deactivation deactivation in _routineRuntime.Lifetime.CaptureDeactivations(
                static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture))
                await DeactivateRoutineStatefulSessionAsync(deactivation);
        }

        private async Task DeactivateAllRoutineStatefulSessionsForCleanupAsync()
        {
            _routineRuntime.BeginShutdown();
            await _routineRuntime.DrainWorkAsync();
            await _routineRuntime.Lifetime.DrainAsync();
            foreach (RoutineLifetimeService.Deactivation deactivation in _routineRuntime.Lifetime.CaptureDeactivations(static _ => true))
                await DeactivateRoutineStatefulSessionAsync(deactivation);
        }

        private async Task DeactivateRoutineStatefulSessionAsync(string sessionKey, long? restoreActivationSequence = null)
        {
            foreach (RoutineLifetimeService.Deactivation deactivation in _routineRuntime.Lifetime.CaptureDeactivations(
                session => string.Equals(session.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase)))
                await DeactivateRoutineStatefulSessionAsync(restoreActivationSequence.HasValue
                    ? deactivation with { LatestSequence = restoreActivationSequence.Value } : deactivation);
        }

        private Task DeactivateRoutineStatefulSessionAsync(RoutineLifetimeService.Deactivation deactivation)
        {
            return _routineRuntime.Lifetime.DeactivateAsync(deactivation, async (session, shouldRestore) =>
            {
                try
                {
                    try
                    {
                        await ReleaseRoutineApplicationRoutingAsync(session, shouldRestore);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AppViewModel", "routine-routing-restore-failed", nameof(DeactivateRoutineStatefulSessionAsync), ex);
                    }
                    await AppViewModelRoutineStatefulDeactivationHelper.ApplyAsync(
                        session, shouldRestore, _logger, RestoreRoutineAudioSnapshotAsync,
                        UpdateRoutineAppStartMonitorState, UpdateSteamBigPictureMonitorState);
                }
                finally { CompletePendingAppAudioWaitIfNeeded(session); }
            });
        }

        private async Task ReleaseRoutineApplicationRoutingAsync(RoutineStatefulSession session, bool shouldRestore)
        {
            foreach (var (Lease, ResetOutput, ResetInput) in _routineRuntime.Lifetime.ReleaseSessionRouting(session, shouldRestore))
            {
                CompletePendingAppAudioWaitForRemovedLease(Lease);
                _routineRuntime.UpdateRoutingSessionMonitoring();
                if (!ResetOutput && !ResetInput) continue;
                try
                {
                    bool? reset = await Task.Run<bool?>(() =>
                    {
                        RoutineAppStartProcessSnapshot? current = _routineRuntime.ProcessSnapshots.TryCapture(Lease.RootProcessId);
                        if (current == null || (Lease.ProcessIdentity is { } expected && !RoutineLifetimeService.IsSameProcess(expected, current.Value))) return null;
                        return ResetRoutineRoutingForTests?.Invoke((uint)Lease.RootProcessId, ResetOutput, ResetInput)
                            ?? _audio.TryResetApplicationDeviceRouting((uint)Lease.RootProcessId, ResetOutput, ResetInput,
                                opId: $"routine-routing-deactivate:{session.RoutineId}");
                    });
                    if (reset == false) _logger.Warning("AppViewModel", () => $"routine-routing-restore-failed | routineId={LogPrivacy.Id(session.RoutineId)} processId={FormatRoutineLogProcessId(Lease.RootProcessId)}");
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", $"routine-routing-restore-failed | routineId={LogPrivacy.Id(session.RoutineId)} processId={FormatRoutineLogProcessId(Lease.RootProcessId)}", nameof(ReleaseRoutineApplicationRoutingAsync), ex);
                }
            }
        }

        private Task DeactivateRoutineFromApplicationTrigger(
            AudioRoutine routine,
            int processId,
            Func<string, Task>? deactivateSessionAsync = null)
        {
            RoutineLifetimeService.ActivationEnd ending = _routineRuntime.Lifetime.CancelActivation(routine, processId);
            string leaseKey = CreateRoutineAppOutputLeaseKey(routine.Id, processId);
            RoutineAppOutputLease? expectedLease = ending.Lease;
            return _dispatcher.InvokeAsync((Func<Task>)(async () =>
            {
                if (_isCleaningUp || processId <= 0)
                {
                    return;
                }

                bool hasOwnedRouting = ending.Sessions.Any(static item => item.Session.RoutingLease != null);
                RoutineAppOutputLease? removedLease = hasOwnedRouting || expectedLease == null ? null :
                    _routineRuntime.Lifetime.RemoveLease(leaseKey, expectedLease.Generation);

                if (!hasOwnedRouting && routine.RouteToApplication && routine.RestorePreviousAudioOnDeactivate &&
                    string.Equals(routine.TargetAppPath, routine.TriggerAppPath, StringComparison.OrdinalIgnoreCase))
                {
                    await _routineRuntime.Lifetime.ApplyRoutingAsync(() =>
                    {
                        lock (_routineRuntime.Sync)
                            return _routineRuntime.Lifetime.FindLease(leaseKey) == null;
                    }, () => Task.Run(() =>
                    {
                        return _audio.TryResetApplicationDeviceRouting(
                            (uint)processId,
                            resetOutput: !string.IsNullOrWhiteSpace(routine.OutputDeviceId),
                            resetInput: !string.IsNullOrWhiteSpace(routine.InputDeviceId),
                            opId: $"routine-focus-deactivate:{RoutineTriggerPathHelper.GetTriggerDisplayName(routine.TriggerAppPath)}:{processId}");
                    }));
                }

                if (removedLease != null)
                {
                    CompletePendingAppAudioWaitForRemovedLease(removedLease);
                    _routineRuntime.UpdateRoutingSessionMonitoring();
                    UpdateRoutineAppStartMonitorState();
                }

                string sessionKey = CreateRoutineStatefulSessionKey(routine, processId);
                if (deactivateSessionAsync != null)
                    await deactivateSessionAsync(sessionKey);
                else
                    foreach (RoutineLifetimeService.Deactivation deactivation in ending.Sessions)
                        await DeactivateRoutineStatefulSessionAsync(deactivation);
            })).Task.Unwrap();
        }

        private void CompletePendingAppAudioWaitIfNeeded(RoutineStatefulSession? session)
        {
            if (session == null || session.TriggerKind != RoutineTriggerKind.Application)
            {
                return;
            }

            string normalizedRoutineId = NormalizeRoutineId(session.RoutineId);
            lock (_routineRuntime.Sync)
            {
                bool hasRemainingSession = _routineRuntime.Lifetime.Sessions.Any(activeSession =>
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
                            targetDeviceId,
                            _muteMicBackingField,
                            _muteSoundBackingField,
                            _deafenBackingField,
                            _preserveAudioLevelsBackingField,
                            opId: opId, preserveEndpointMute: true);
                    },
                    _audio.TryGetActiveRecordingCycleEntry,
                    async (targetDeviceId, targetDeviceName, opId) =>
                    {
                        await _audio.SwitchInputDeviceToAsync(
                            targetDeviceId,
                            targetDeviceName,
                            preserveAudioLevels: _preserveAudioLevelsBackingField,
                            showOverlay: null,
                            opId: opId, preserveEndpointMute: true);
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
            if (EndpointVolumeOperations.TryGetEndpointVolumeState(_logger, device, reason, out float currentPercent, out bool muted))
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

        private Task RestoreDefaultPlaybackVolumeAsync(float targetPercent, bool? muted, string opId)
        {
            ApplyDefaultEndpointVolume(_audio.GetDefaultPlaybackDevice, targetPercent, muted, opId, "playback");
            return Task.CompletedTask;
        }

        private Task RestoreDefaultRecordingVolumeAsync(float targetPercent, bool? muted, string opId)
        {
            ApplyDefaultEndpointVolume(_audio.GetDefaultRecordingDevice, targetPercent, muted, opId, "recording");
            return Task.CompletedTask;
        }

        private void ApplyDefaultEndpointVolume(Func<MMDevice?> getDefaultDevice, float targetPercent, bool? muted, string opId, string flow)
        {
            try
            {
                using MMDevice? device = getDefaultDevice();
                if (device == null)
                {
                    _logger.Warning("AppViewModel", () => $"routine-stateful-restore-volume-skipped | flow={flow} opId={NormalizeRoutineLogValue(opId)} reason=no-default-device");
                    return;
                }

                if (!EndpointVolumeOperations.TryApplyEndpointVolume(_logger, device, targetPercent, opId, muteAtZero: false, unmuteAboveZero: false, out _))
                {
                    _logger.Warning("AppViewModel", () => $"routine-stateful-restore-volume-skipped | flow={flow} opId={NormalizeRoutineLogValue(opId)} reason=apply-failed");
                    return;
                }

                if (muted.HasValue && GetRoutineMuteBlockReason(flow == "playback", muted.Value) == null && AudioDeviceHelper.TryGetEndpointVolume(_logger, device, out var endpointVolume, opId))
                {
                    endpointVolume.Mute = muted.Value;
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

            IReadOnlyList<AudioRoutine> configured = GetPersistedRoutineSnapshot();
            List<AudioRoutine> routines = GetDeviceChangeTriggeredRoutinesForExecution(configured);
            routines.AddRange(_routineRuntime.Availability.Observe(configured, GetRoutineAvailabilityDevices, evaluateTransitions: true));

            foreach (var group in routines.GroupBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AudioRoutine routine = group.First();
                await ExecuteRoutineOnDispatcherAsync(
                    routine,
                    showOverlay: true,
                    applicationProcessId: null,
                    executionSource: routine.TriggerKind == RoutineTriggerKind.DeviceAvailability ? "device-availability" : "device-change",
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (AudioRoutine completed in group) _routineRuntime.Availability.Complete(completed);
            }
        }

        private IReadOnlyList<CycleDevice> GetRoutineAvailabilityDevices(bool playback) => RoutineExecutionOperationsForTests?.GetActiveDevices(playback)
            ?? (playback ? _audio.GetActivePlaybackCycleEntries() : _audio.GetActiveCaptureCycleEntries());

        internal static List<AudioRoutine> GetDeviceChangeTriggeredRoutinesForExecution(IEnumerable<AudioRoutine> routines)
        {
            ArgumentNullException.ThrowIfNull(routines);

            return
            [
                .. routines.Where(static routine => routine.Enabled && routine.HasExecutionTarget)
                    .SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.DeviceChange))
                    .DistinctBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static routine => routine.DisplayOrder)
                    .ThenBy(static routine => routine.Name, StringComparer.Ordinal)
            ];
        }

        private Task<RoutineExecutionResult> ExecuteRoutineOnDispatcherAsync(AudioRoutine routine, bool showOverlay, int? applicationProcessId, string executionSource, CancellationToken cancellationToken = default)
        {
            if (_dispatcher.CheckAccess())
            {
                return ExecuteRoutineForResolvedProcessAsync(routine, applicationProcessId ?? 0, showOverlay, executionSource, trackLifetime: false, cancellationToken: cancellationToken);
            }

            return InvokeOnDispatcherAsync(
                () => ExecuteRoutineForResolvedProcessAsync(routine, applicationProcessId ?? 0, showOverlay, executionSource, trackLifetime: false, cancellationToken: cancellationToken),
                BuildSkippedRoutineExecutionResult());
        }

        private void UpdateSteamBigPictureMonitorState()
        {
            bool startMonitor = false;
            bool stopMonitor = false;
            SteamBigPictureMonitorDecision decision;

            lock (_routineRuntime.Sync)
            {
                decision = AppRoutineStatefulCoordinator.ResolveSteamBigPictureMonitorDecision(
                    _routineRuntime.MonitoringEnabled,
                    _isCleaningUp,
                    _routineRuntime.SteamRoutines.Count,
                    _routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture),
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
                _routineRuntime.SteamMonitor.Stop();
                _routineRuntime.CancelWork(RoutineRuntimeWork.SteamSignal);
                _routineRuntime.CancelWork(RoutineRuntimeWork.SteamConfirmation);
                _routineRuntime.CancelWork(RoutineRuntimeWork.SteamMaintenance);
            }

            if (!startMonitor)
            {
                return;
            }

            SteamBigPictureSignalMonitorStartResult startResult = _routineRuntime.SteamMonitor.Start();
            bool keepMonitor;
            lock (_routineRuntime.Sync)
            {
                _steamBigPictureMonitorStartPending = false;
                keepMonitor = _routineRuntime.MonitoringEnabled &&
                    !_isCleaningUp &&
                    (_routineRuntime.SteamRoutines.Count > 0 ||
                        _routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));

                _steamBigPictureMonitorRunning = startResult.Success && keepMonitor;
                _steamBigPictureUsingFallbackRevalidation = !startResult.Success && keepMonitor;
            }

            if (!keepMonitor)
            {
                if (startResult.Success)
                {
                    _routineRuntime.SteamMonitor.Stop();
                }

                return;
            }

            if (startResult.Success)
            {
                _logger.Info("AppViewModel", () => $"steam-big-picture-monitor-active | watcher={_routineRuntime.SteamMonitor.GetType().Name}");
                QueueSteamBigPictureSignalEvaluation();
                return;
            }

            _logger.Warning("AppViewModel", () => $"steam-big-picture-monitor-fallback-active | reason={startResult.FailureReason ?? startResult.Status} mode=event-revalidation watcher={_routineRuntime.SteamMonitor.GetType().Name}");
            QueueSteamBigPictureSignalEvaluation();
            StartSteamBigPictureFallbackMaintenance();
        }

        private void StartSteamBigPictureFallbackMaintenance()
        {
            lock (_routineRuntime.Sync)
            {
                if (_isCleaningUp || !_steamBigPictureUsingFallbackRevalidation) return;
            }
            _routineRuntime.QueueDebounced(RoutineRuntimeWork.SteamMaintenance, 0, async cancellationToken =>
            {
                int elapsedSinceHookRetryMs = 0;
                while (true)
                {
                    await Task.Delay(AppConstants.Routines.SteamBigPictureFallbackRevalidationIntervalMs, cancellationToken);
                    QueueSteamBigPictureSignalEvaluation();
                    elapsedSinceHookRetryMs += AppConstants.Routines.SteamBigPictureFallbackRevalidationIntervalMs;
                    if (elapsedSinceHookRetryMs < AppConstants.Routines.SteamBigPictureHookRetryIntervalMs) continue;
                    elapsedSinceHookRetryMs = 0;
                    if (TryRecoverSteamBigPictureMonitorFromFallback()) return;
                }
            }, nameof(StartSteamBigPictureFallbackMaintenance), replace: false);
        }

        private bool TryRecoverSteamBigPictureMonitorFromFallback()
        {
            lock (_routineRuntime.Sync)
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

            SteamBigPictureSignalMonitorStartResult startResult = _routineRuntime.SteamMonitor.Start();
            bool recovered;
            lock (_routineRuntime.Sync)
            {
                _steamBigPictureMonitorStartPending = false;
                recovered = startResult.Success &&
                    _routineRuntime.MonitoringEnabled &&
                    !_isCleaningUp &&
                    (_routineRuntime.SteamRoutines.Count > 0 ||
                        _routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
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
                    _routineRuntime.SteamMonitor.Stop();
                }

                _logger.Trace("AppViewModel", () => $"steam-big-picture-monitor-retry-failed | reason={startResult.FailureReason ?? startResult.Status}");
                return false;
            }

            _logger.Info("AppViewModel", () => $"steam-big-picture-monitor-recovered | watcher={_routineRuntime.SteamMonitor.GetType().Name}");
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

            lock (_routineRuntime.Sync)
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

            lock (_routineRuntime.Sync)
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
                                _routineRuntime.SteamRoutines);
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

            _routineRuntime.QueueWork(
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
            _routineRuntime.CancelWork(RoutineRuntimeWork.SteamConfirmation);
            _routineRuntime.QueueDebounced(RoutineRuntimeWork.SteamSignal, RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs,
                async token =>
                {
                    Interlocked.Exchange(ref _pendingSteamBigPictureSignals, 0);
                    SteamBigPictureLoopContext context;
                    lock (_routineRuntime.Sync)
                        context = AppSteamBigPictureCoordinator.BuildLoopContext(_routineRuntime.MonitoringEnabled,
                            _isCleaningUp, _routineRuntime.SteamRoutines,
                            _routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
                    if (context.ShouldMonitor)
                        await EvaluateSteamBigPictureMonitorSignalAsync(context.WatchedRoutines, allowConfirmationCheck: true, token);
                }, nameof(OnSteamBigPictureMonitorSignaled));
        }

        private void QueueSteamBigPictureConfirmationEvaluation()
        {
            _routineRuntime.QueueDebounced(RoutineRuntimeWork.SteamConfirmation, RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs,
                async token =>
                {
                    SteamBigPictureLoopContext context;
                    lock (_routineRuntime.Sync)
                        context = AppSteamBigPictureCoordinator.BuildLoopContext(_routineRuntime.MonitoringEnabled,
                            _isCleaningUp, _routineRuntime.SteamRoutines,
                            _routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture));
                    if (context.ShouldMonitor)
                        await EvaluateSteamBigPictureMonitorSignalAsync(context.WatchedRoutines, allowConfirmationCheck: false, token);
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
            lock (_routineRuntime.Sync)
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
                    await ExecuteRoutineForResolvedProcessAsync(routine, 0, showOverlay: true, executionSource: "steam-big-picture");
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

            Dictionary<int, string> steamProcessNamesById = [];
            foreach (RoutineAppStartProcessSnapshot snapshot in processSnapshots)
            {
                if (snapshot.ProcessId <= 0)
                {
                    continue;
                }

                string processName = Path.GetFileNameWithoutExtension(snapshot.ExecutablePath) ?? string.Empty;
                if (SteamProcessNames.Contains(processName))
                {
                    steamProcessNamesById.TryAdd(snapshot.ProcessId, processName);
                }
            }

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
            return $"hasOutputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousOutputDeviceId)} outputDeviceId={FormatRoutineLogIdentifier(snapshot.PreviousOutputDeviceId)} outputDevice={FormatRoutineLogDevice(snapshot.PreviousOutputDeviceName)} outputVolumePercent={snapshot.PreviousOutputVolumePercent?.ToString() ?? "none"} outputMuted={FormatRoutineLogBool(snapshot.PreviousOutputMuted)} hasInputSnapshot={!string.IsNullOrWhiteSpace(snapshot.PreviousInputDeviceId)} inputDeviceId={FormatRoutineLogIdentifier(snapshot.PreviousInputDeviceId)} inputDevice={FormatRoutineLogDevice(snapshot.PreviousInputDeviceName)} inputVolumePercent={snapshot.PreviousInputVolumePercent?.ToString() ?? "none"} inputMuted={FormatRoutineLogBool(snapshot.PreviousInputMuted)} hasAudioRestoration={snapshot.AudioRestoration != null}";
        }
    }
}
