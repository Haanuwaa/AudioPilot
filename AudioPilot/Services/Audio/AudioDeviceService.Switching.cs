using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    public partial class AudioDeviceService
    {
        public async ValueTask<(bool Success, string? DeviceName)> SwitchAudioDeviceAsync(
            string device1Id,
            string device2Id,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool restoreMasterVolume = true,
            bool restoreMicVolume = true,
            string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;
            Stopwatch switchStopwatch = Stopwatch.StartNew();

            if (_disposed)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning("AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=service-disposed");
                return (false, null);
            }

            if (_sessionService != null)
                _ = _sessionService.StartCleanupTaskAsync();

            if (string.IsNullOrEmpty(device1Id) || string.IsNullOrEmpty(device2Id))
                throw new InvalidOperationException("Both devices must be configured");

            if (!await _switchExecutionCoordinator.TryEnterOutputAsync())
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=in-progress");
                return (false, null);
            }

            bool outputSwitchSucceeded = false;
            long outputSwitchRevision = Interlocked.Increment(ref _outputSwitchRevision);
            Task<SessionVolumeSnapshot>? snapshotTask = null;
            bool snapshotTaskObservedOrHandedOff = false;

            try
            {
                var outputRoles = GetConfiguredOutputRolesSnapshot();
                var inputRoles = GetConfiguredInputRolesSnapshot();
                var outputDetectionRole = ResolveDetectionRole(outputRoles, NRole.Multimedia);
                var inputDetectionRole = ResolveDetectionRole(inputRoles, NRole.Console);
                bool snapshotDeferredToBackground = false;

                string targetDeviceId;
                string targetDeviceName;

                _enumeratorLock.EnterReadLock();
                try
                {
                    using MMDevice? targetDevice = _enumerator.GetDevice(device2Id);

                    if (targetDevice == null)
                    {
                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=target-unavailable");
                        return (false, null);
                    }

                    if (targetDevice.State != NDeviceState.Active || targetDevice.DataFlow != DataFlow.Render)
                    {
                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=target-not-active-output state={targetDevice.State}");
                        return (false, null);
                    }

                    targetDeviceId = targetDevice.ID;
                    targetDeviceName = targetDevice.FriendlyName;
                }
                finally
                {
                    _enumeratorLock.ExitReadLock();
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Start} | opId={op} muteMic={muteMic} muteSound={muteSound} deafen={deafen} preserveAudioLevels={preserveAudioLevels}");

                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=target-id-empty");
                    return (false, null);
                }

                if (preserveAudioLevels)
                {
                    string? sourcePlaybackDeviceId = GetDefaultPlaybackDeviceId(outputDetectionRole);
                    if (string.IsNullOrWhiteSpace(sourcePlaybackDeviceId))
                    {
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.SnapshotCaptured} | opId={op} result=skipped reason=source-unavailable role={outputDetectionRole}");
                    }
                    else
                    {
                        string capturedSourcePlaybackDeviceId = sourcePlaybackDeviceId;
                        snapshotTask = Task.Run(() =>
                        {
                            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SwitchAudioDeviceAsync));
                            return _volumeService.CapturePlaybackSessionVolumesForDeviceId(capturedSourcePlaybackDeviceId);
                        });
                    }
                }

                SessionVolumeSnapshot? snapshot = null;
                if (snapshotTask is { IsCompletedSuccessfully: true })
                {
                    snapshot = snapshotTask.Result;
                    snapshotTaskObservedOrHandedOff = true;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        int sessionCount = snapshot.ByPid.Count + snapshot.ByName.Count;
                        _logger.Debug("AudioDeviceService",
                            () => $"{AppConstants.Audio.LogEvents.OutputSwitch.SnapshotCaptured} | opId={op} sessionCount={sessionCount}");
                    }
                }

                double preRoleSwitchMs = switchStopwatch.Elapsed.TotalMilliseconds;
                bool snapshotReadyBeforeSwitch = snapshotTask?.IsCompletedSuccessfully ?? false;
                double roleSwitchStartMs = switchStopwatch.Elapsed.TotalMilliseconds;

                bool switched = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
                    targetDeviceId,
                    outputRoles,
                    ApplyConfiguredRole,
                    GetDefaultPlaybackDeviceId,
                    _logger,
                    op,
                    nameof(SwitchAudioDeviceAsync),
                    _backgroundWorkCts.Token);

                double roleSwitchDurationMs = switchStopwatch.Elapsed.TotalMilliseconds - roleSwitchStartMs;
                double setupDurationMs = preRoleSwitchMs;

                if (!switched)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug(
                            "AudioDeviceService",
                            () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Phases} | opId={op} snapshotReadyBeforeSwitch={snapshotReadyBeforeSwitch} setupMs={setupDurationMs:F1} roleSwitchMs={roleSwitchDurationMs:F1} finalizeMs=0.0 totalMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} result=verify-failed");
                    }

                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={op} reason=verify-failed-after-retries");
                    return (false, null);
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Confirmed} | opId={op}");

                var capturedSnapshot = snapshot;
                var capturedSnapshotTask = snapshotTask;
                var capturedTargetDeviceId = targetDeviceId;
                var capturedPreserveAudioLevels = preserveAudioLevels;
                if (ShouldRegisterPreserveSnapshot(capturedPreserveAudioLevels, capturedSnapshot))
                {
                    _volumeService.RegisterPostSwitchSnapshot(capturedSnapshot!, capturedTargetDeviceId);
                }
                var capturedMuteMic = muteMic;
                var capturedMuteSound = muteSound;
                var capturedDeafen = deafen;
                var capturedInputDetectionRole = inputDetectionRole;

                bool postSwitchQueued = TryRunBackgroundWork(async shutdownToken =>
                {
                    try
                    {
                        SessionVolumeSnapshot? snapshotForPost = capturedSnapshot;
                        bool preserveForPost = capturedPreserveAudioLevels;

                        if (preserveForPost && snapshotForPost == null && capturedSnapshotTask != null)
                        {
                            snapshotForPost = await capturedSnapshotTask;
                            if (outputSwitchRevision != Volatile.Read(ref _outputSwitchRevision))
                            {
                                return;
                            }
                            _volumeService.RegisterPostSwitchSnapshot(snapshotForPost, capturedTargetDeviceId);
                        }

                        await PostSwitchCoordinator.ExecuteAsync(
                            () => _disposed,
                            _logger,
                            _volumeService,
                            op,
                            capturedTargetDeviceId,
                            capturedInputDetectionRole,
                            capturedMuteMic,
                            capturedMuteSound,
                            capturedDeafen,
                            preserveForPost,
                            restoreMasterVolume,
                            restoreMicVolume,
                            snapshotForPost,
                            shutdownToken,
                            shouldContinue: () => outputSwitchRevision == Volatile.Read(ref _outputSwitchRevision));
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.PostFailed} | opId={op}", nameof(SwitchAudioDeviceAsync), ex);
                    }
                }, nameof(SwitchAudioDeviceAsync));

                if (capturedSnapshotTask != null && capturedSnapshot == null)
                {
                    snapshotTaskObservedOrHandedOff = postSwitchQueued;
                }

                snapshotDeferredToBackground = preserveAudioLevels && capturedSnapshot == null && capturedSnapshotTask != null;
                double finalizeDurationMs = switchStopwatch.Elapsed.TotalMilliseconds - (preRoleSwitchMs + roleSwitchDurationMs);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Phases} | opId={op} snapshotReadyBeforeSwitch={snapshotReadyBeforeSwitch} setupMs={setupDurationMs:F1} roleSwitchMs={roleSwitchDurationMs:F1} finalizeMs={finalizeDurationMs:F1} totalMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} result=success snapshotDeferred={snapshotDeferredToBackground}");
                }

                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Success} | opId={op} target={LogPrivacy.Device(targetDeviceName)} durationMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} preserveAudioLevels={preserveAudioLevels} snapshotDeferred={snapshotDeferredToBackground}");
                outputSwitchSucceeded = true;
                return (true, targetDeviceName);
            }
            catch (OperationCanceledException) when (_disposed || _backgroundWorkCts.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Skip} | opId={op} reason=shutdown-canceled");
                }

                return (false, null);
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(SwitchAudioDeviceAsync), ex);
                return (false, null);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(SwitchAudioDeviceAsync), ex);
                return (false, null);
            }
            finally
            {
                if (snapshotTask != null && !snapshotTaskObservedOrHandedOff)
                {
                    ObserveDetachedTask(snapshotTask, $"{nameof(SwitchAudioDeviceAsync)}:snapshot");
                }

                CompleteOutputSwitchAttempt(outputSwitchSucceeded);
            }
        }

        private void CompleteOutputSwitchAttempt(bool outputSwitchSucceeded)
        {
            _switchExecutionCoordinator.ReleaseOutput();
            if (outputSwitchSucceeded)
            {
                _switchExecutionCoordinator.MarkOutputSwitchSuccess(DateTime.Now);
            }

            QueueOutputSwitchCompletionSessionMonitoringUpdate();
        }

        private void QueueOutputSwitchCompletionSessionMonitoringUpdate()
        {
            RunBackgroundWork(
                _ =>
                {
                    _outputSwitchCompletionSessionMonitoringUpdate();
                    return Task.CompletedTask;
                },
                nameof(UpdateSessionMonitoring));
        }

        internal ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationOutputDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            _ = targetDeviceName;

            return SwitchApplicationDeviceDetailedAsync(
                processId,
                targetDeviceId,
                DataFlow.Render,
                TryGetPlaybackDeviceById,
                GetConfiguredOutputRolesSnapshot,
                "app-process-output",
                nameof(SwitchApplicationOutputDeviceAsync),
                opId);
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchApplicationOutputDeviceAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            ProcessAudioDeviceSwitchResult result = await SwitchApplicationOutputDeviceDetailedAsync(processId, targetDeviceId, targetDeviceName, opId);
            return (result.Success, result.DeviceName);
        }

        internal ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationInputDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            _ = targetDeviceName;

            return SwitchApplicationDeviceDetailedAsync(
                processId,
                targetDeviceId,
                DataFlow.Capture,
                TryGetCaptureDeviceById,
                GetConfiguredInputRolesSnapshot,
                "app-process-input",
                nameof(SwitchApplicationInputDeviceAsync),
                opId);
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchApplicationInputDeviceAsync(
            uint processId,
            string targetDeviceId,
            string targetDeviceName,
            string? opId = null)
        {
            ProcessAudioDeviceSwitchResult result = await SwitchApplicationInputDeviceDetailedAsync(processId, targetDeviceId, targetDeviceName, opId);
            return (result.Success, result.DeviceName);
        }

        private ValueTask<ProcessAudioDeviceSwitchResult> SwitchApplicationDeviceDetailedAsync(
            uint processId,
            string targetDeviceId,
            DataFlow flow,
            Func<string, MMDevice?> resolveTargetDevice,
            Func<NRole[]> getRoles,
            string logScope,
            string operationName,
            string? opId)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;

            if (_disposed || processId == 0 || string.IsNullOrWhiteSpace(targetDeviceId))
            {
                return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
            }

            MMDevice? targetDevice = null;
            try
            {
                targetDevice = resolveTargetDevice(targetDeviceId);
                if (targetDevice == null || targetDevice.State != NDeviceState.Active)
                {
                    _logger.Info("AudioDeviceService", () => $"{logScope}-skip | opId={op} reason=target-not-active targetId={LogPrivacy.Id(targetDeviceId)}");
                    return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
                }

                ProcessAudioDeviceSwitchResult result = _processRoutingHelper.ApplyProcessDeviceRouting(
                    processId,
                    flow,
                    targetDevice.ID,
                    targetDevice.FriendlyName,
                    getRoles,
                    logScope,
                    operationName,
                    op,
                    (scope, currentOp) => ShouldLogDeferredProcessAudio(scope, currentOp, out int occurrence) ? occurrence : null,
                    ResetDeferredProcessAudioLogCount);

                return ValueTask.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{logScope}-failed | opId={op}", operationName, ex);
                return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null));
            }
            finally
            {
                targetDevice?.Dispose();
            }
        }

        internal bool TryResetApplicationDeviceRouting(uint processId, bool resetOutput, bool resetInput, string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;
            if (_disposed || processId == 0 || (!resetOutput && !resetInput))
            {
                return false;
            }

            bool success = true;

            if (resetOutput)
            {
                success &= TryResetApplicationDeviceRoutingFlow(processId, DataFlow.Render, GetConfiguredOutputRolesSnapshot(), "app-process-output-reset", op);
            }

            if (resetInput)
            {
                success &= TryResetApplicationDeviceRoutingFlow(processId, DataFlow.Capture, GetConfiguredInputRolesSnapshot(), "app-process-input-reset", op);
            }

            return success;
        }

        internal PerAppAudioRoutingResetResult ResetAllPerAppAudioRouting()
        {
            return _perAppAudioRoutingResetter.TryResetAll();
        }

        private bool TryResetApplicationDeviceRoutingFlow(uint processId, DataFlow flow, IReadOnlyList<NRole> roles, string logScope, string op)
        {
            return _processRoutingHelper.TryResetProcessDeviceRouting(processId, flow, roles, logScope, op, nameof(TryResetApplicationDeviceRouting));
        }

        public async ValueTask<(bool Success, string? DeviceName)> SwitchInputDeviceToAsync(
            string targetDeviceId,
            string targetDeviceName,
            bool preserveAudioLevels,
            Action<OverlayDeviceKind, string, string>? showOverlay,
            string? opId = null)
        {
            string op = string.IsNullOrWhiteSpace(opId) ? "none" : opId;

            if (_disposed)
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=service-disposed");
                return (false, null);
            }

            if (!await _switchExecutionCoordinator.TryEnterInputAsync())
            {
                _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=in-progress");
                return (false, null);
            }

            Task<SessionVolumeSnapshot>? snapshotTask = null;
            bool snapshotTaskObservedOrHandedOff = false;
            long inputSwitchRevision = Interlocked.Increment(ref _inputSwitchRevision);

            try
            {
                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=target-empty");
                    return (false, null);
                }

                MMDevice? currentDefault = null;
                MMDevice? targetDevice = null;

                try
                {
                    targetDevice = TryGetCaptureDeviceById(targetDeviceId);
                    if (targetDevice == null || targetDevice.State != NDeviceState.Active)
                    {
                        _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=target-not-active targetId={LogPrivacy.Id(targetDeviceId)}");
                        showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", targetDeviceName);
                        return (false, null);
                    }

                    currentDefault = GetDefaultRecordingDevice();

                    string targetName = targetDevice.FriendlyName;
                    var inputRoles = GetConfiguredInputRolesSnapshot();
                    if (preserveAudioLevels)
                    {
                        string? sourceRecordingDeviceId = currentDefault?.ID ?? GetDefaultRecordingDeviceId(ResolveDetectionRole(inputRoles, NRole.Console));
                        if (!string.IsNullOrWhiteSpace(sourceRecordingDeviceId))
                        {
                            string capturedSourceRecordingDeviceId = sourceRecordingDeviceId;
                            snapshotTask = Task.Run(() =>
                            {
                                ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SwitchInputDeviceToAsync));
                                return _volumeService.CaptureRecordingEndpointVolumeForDeviceId(capturedSourceRecordingDeviceId);
                            });
                        }
                    }

                    bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
                        targetDeviceId,
                        targetName,
                        inputRoles,
                        ApplyConfiguredRole,
                        GetDefaultRecordingDeviceId,
                        _logger,
                        op,
                        nameof(SwitchInputDeviceToAsync),
                        emitVerifyRetryWarning: false,
                        traceComRetry: true,
                        _backgroundWorkCts.Token);

                    if (success)
                    {
                        bool postSwitchQueued = snapshotTask != null && TryRunBackgroundWork(async shutdownToken =>
                        {
                            try
                            {
                                await PostSwitchCoordinator.RestoreInputVolumeAsync(
                                    snapshotTask,
                                    targetDeviceId,
                                    ApplyRestoredInputVolume,
                                    () => !_disposed && inputSwitchRevision == Volatile.Read(ref _inputSwitchRevision),
                                    shutdownToken);
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsEnabled(LogLevel.Warning))
                                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.PostFailed} | opId={op}", nameof(SwitchInputDeviceToAsync), ex);
                            }
                        }, nameof(SwitchInputDeviceToAsync));

                        snapshotTaskObservedOrHandedOff = postSwitchQueued;

                        _logger.Info("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Success} | opId={op} target={LogPrivacy.Device(targetName)} preserveAudioLevels={preserveAudioLevels}");
                        showOverlay?.Invoke(OverlayDeviceKind.Input, "Switched input device", targetName);
                        _switchExecutionCoordinator.MarkInputSwitchSuccess(DateTime.Now);
                        return (true, targetName);
                    }

                    _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op} reason=verify-failed-after-retries attempts={RuntimeTuningConfig.SwitchMaxRetries}");
                    showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", "");
                    return (false, null);
                }
                finally
                {
                    currentDefault?.Dispose();
                    targetDevice?.Dispose();
                }
            }
            catch (OperationCanceledException) when (_disposed || _backgroundWorkCts.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Skip} | opId={op} reason=shutdown-canceled");
                }

                return (false, null);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={op}", nameof(SwitchInputDeviceToAsync), ex);
                showOverlay?.Invoke(OverlayDeviceKind.Error, "Failed to switch input device", "");
                return (false, null);
            }
            finally
            {
                if (snapshotTask != null && !snapshotTaskObservedOrHandedOff)
                {
                    ObserveDetachedTask(snapshotTask, $"{nameof(SwitchInputDeviceToAsync)}:snapshot");
                }

                _switchExecutionCoordinator.ReleaseInput();
            }
        }

        private void ApplyRestoredInputVolume(string targetDeviceId, float volumePercent)
        {
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(ApplyRestoredInputVolume));
            using MMDevice? targetDevice = TryGetCaptureDeviceById(targetDeviceId);
            if (targetDevice == null || targetDevice.State != NDeviceState.Active ||
                !AudioDeviceHelper.TryGetEndpointVolume(_logger, targetDevice, out AudioEndpointVolume? volume, nameof(ApplyRestoredInputVolume)))
            {
                throw new InvalidOperationException("The switched input endpoint is unavailable for volume restoration.");
            }

            volume.MasterVolumeLevelScalar = volumePercent / 100f;
        }
    }
}
