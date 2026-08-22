using System.Text;
using System.Windows;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel : IResumeRecoveryHandler
    {
        internal static Action? ExitApplicationOverrideForTests { get; set; }

        internal readonly record struct ResumeHotkeyRegistrationResult(
            bool ToggleAppVisibilityRegistered,
            bool MediaHotkeysRegistered,
            bool MuteHotkeysRegistered,
            bool ListenToInputRegistered,
            bool VolumeStepHotkeysRegistered,
            bool OutputSwitchRegistered,
            bool InputSwitchRegistered,
            bool OutputReverseSwitchRegistered,
            bool InputReverseSwitchRegistered,
            bool RoutineHotkeysRegistered = true)
        {
            public bool AllSucceeded =>
                ToggleAppVisibilityRegistered &&
                MediaHotkeysRegistered &&
                MuteHotkeysRegistered &&
                ListenToInputRegistered &&
                VolumeStepHotkeysRegistered &&
                OutputSwitchRegistered &&
                InputSwitchRegistered &&
                OutputReverseSwitchRegistered &&
                InputReverseSwitchRegistered &&
                RoutineHotkeysRegistered;

            public int FailedCount =>
                (ToggleAppVisibilityRegistered ? 0 : 1) +
                (MediaHotkeysRegistered ? 0 : 1) +
                (MuteHotkeysRegistered ? 0 : 1) +
                (ListenToInputRegistered ? 0 : 1) +
                (VolumeStepHotkeysRegistered ? 0 : 1) +
                (OutputSwitchRegistered ? 0 : 1) +
                (InputSwitchRegistered ? 0 : 1) +
                (OutputReverseSwitchRegistered ? 0 : 1) +
                (InputReverseSwitchRegistered ? 0 : 1) +
                (RoutineHotkeysRegistered ? 0 : 1);
        }

        /// <summary>
        /// Brings the main window to the foreground and refreshes device/mixer state for interactive use.
        /// </summary>
        public Task<bool> ShowWindowAsync(CancellationToken cancellationToken = default) =>
            ShowWindowAsync(MainWindowOpenTarget.Default, cancellationToken);

        internal async Task<bool> ShowWindowAsync(
            MainWindowOpenTarget target,
            CancellationToken cancellationToken = default)
        {
            if (_isCleaningUp)
            {
                _logger.Debug("AppViewModel", "show-window-skipped-during-cleanup");
                return false;
            }

            try
            {
                return await AppWindowVisibilityCoordinator.ShowWindowAsync(
                    _windowState,
                    () => _shell.ShowWindowFrontAndCenterAsync(target, cancellationToken),
                    RefreshAvailableDeviceCollectionsAsync,
                    _deviceCache.Refresh,
                    () =>
                    {
                        if (TryGetSelectedMixerRefreshTarget(out MixerRefreshTarget mixerTarget))
                        {
                            QueueShowWindowMixerRefresh(mixerTarget);
                        }

                        return Task.CompletedTask;
                    },
                    () => UpdateMuteFlagsFromSystem("show-window"),
                    _logger,
                    DateTime.Now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("AppViewModel", "show-window-cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Error in ShowWindow", nameof(ShowWindowAsync), ex);
                return false;
            }
        }

        public void ShowWindow() => ObserveWindowAction(ShowWindowAsync(), "show-window");

        /// <summary>
        /// Shows the main window when it is hidden and minimizes it to the tray when it is visible.
        /// </summary>
        public async Task<bool> ToggleWindowVisibilityAsync(CancellationToken cancellationToken = default)
        {
            if (_isCleaningUp)
            {
                _logger.Debug("AppViewModel", "window-visibility-toggle-skipped-during-cleanup");
                return false;
            }

            try
            {
                return await AppWindowVisibilityCoordinator.ToggleWindowVisibilityAsync(
                    _shell.IsWindowVisible,
                    () => ShowWindowAsync(cancellationToken: cancellationToken),
                    MinimizeWindow,
                    _logger);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("AppViewModel", "window-visibility-toggle-cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Error toggling window visibility", nameof(ToggleWindowVisibilityAsync), ex);
                return false;
            }
        }

        internal bool HasInteractiveShowRequest => _windowState.HasInteractiveShowRequest;

        internal void MarkStartupVisibilityResolved()
        {
            _windowState.MarkStartupVisibilityResolved();
        }

        public async Task<bool> StartHiddenToTrayAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // AppRuntimeHost publishes the tray only after startup initialization has completed.
                // This path establishes the hidden window state without exposing an interactive icon early.
                bool succeeded = AppWindowVisibilityCoordinator.StartHiddenToTray(_shell.PrepareHiddenStartup, _logger);
                if (succeeded)
                {
                    HandleWindowVisibilityChanged(isVisible: false);
                    return true;
                }

                _logger.Warning("AppViewModel", "start-hidden-to-tray-failed | action=show-window");
                return await ShowWindowAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Error in StartHiddenToTray", nameof(StartHiddenToTrayAsync), ex);
                return false;
            }
        }

        private void ObserveWindowAction(Task<bool> action, string operation)
        {
            _ = ObserveWindowActionAsync(action, operation);
        }

        private async Task ObserveWindowActionAsync(Task<bool> action, string operation)
        {
            try
            {
                _ = await action;
            }
            catch (Exception ex)
            {
                _logger.Error(
                    "AppViewModel",
                    () => $"window-action-observer-failed | operation={operation} error={ex.GetType().Name}",
                    nameof(ObserveWindowActionAsync),
                    ex);
            }
        }

        /// <summary>
        /// Minimizes the app to tray while guarding against show/minimize races.
        /// </summary>
        /// <remarks>
        /// A short post-show cooldown prevents accidental immediate re-minimize loops triggered by rapid state
        /// changes.
        /// </remarks>
        public void MinimizeWindow()
        {
            try
            {
                MinimizeWindowPlan plan = AppWindowVisibilityCoordinator.BuildMinimizePlan(
                    _windowState,
                    ShowBalloonAfterSave,
                    DateTime.Now);

                AppWindowVisibilityCoordinator.ApplyMinimizePlan(
                    _windowState,
                    plan,
                    (showBalloon, appName) => _shell.MinimizeToTray(showBalloon, appName),
                    () => ShowBalloonAfterSave = false,
                    _logger);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Error in MinimizeWindow", nameof(MinimizeWindow), ex);
                _windowState.AbortMinimize();
            }
        }

        public void ExitApplication()
        {
            _logger.Info("AppViewModel", "Exit requested");
            if (ExitApplicationOverrideForTests != null)
            {
                ExitApplicationOverrideForTests();
                return;
            }

            if (Application.Current is App app)
            {
                _ = app.RequestRuntimeShutdownAsync("view-model-exit");
                return;
            }

            Application.Current?.Shutdown();
        }

        public async Task RecoverAfterSystemResumeAsync(string? resumeOpId = null)
        {
            string opId = AppResumeRecoveryCoordinator.ResolveOperationId(resumeOpId);

            if (_isCleaningUp)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | opId={opId} reason=cleanup-in-progress");
                return;
            }

            await AppResumeRecoveryCoordinator.ExecuteAsync(
                opId,
                new ResumeRecoveryExecutionDependencies(
                    _audio.RecoverAfterSystemResumeAsync,
                    ReRegisterHotkeysAfterResumeAsync,
                    RefreshDevicesForHotplugAsync),
                _logger,
                nameof(RecoverAfterSystemResumeAsync),
                _backgroundWorkCts.Token);
        }

        private async Task<(ResumeHotkeyRegistrationResult Result, int Attempts)> ReRegisterHotkeysAfterResumeAsync(string resumeOpId)
        {
            Settings hotkeySettings;
            lock (_settingsLock)
            {
                hotkeySettings = (_cachedSettings ?? new Settings()).Clone();
            }

            return await AppResumeRecoveryCoordinator.RegisterHotkeysAsync(
                () => RegisterResumeHotkeysOnDispatcherAsync(hotkeySettings),
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                _logger,
                resumeOpId,
                _backgroundWorkCts.Token);
        }

        private Task<ResumeHotkeyRegistrationResult> RegisterResumeHotkeysOnDispatcherAsync(Settings hotkeySettings)
        {
            return AppSwitchInteractionCoordinator.RegisterResumeHotkeysOnDispatcherAsync(
                callback => InvokeOnDispatcherAsync(callback, fallback: default),
                () => _hotkeyRegistrationCoordinator.RegisterAll(hotkeySettings, unregisterAllFirst: true),
                () => RegisterRoutineHotkeysFromSettings(hotkeySettings, context: "resume"));
        }

        /// <summary>
        /// Switches to the next configured output device and updates overlay/UI state.
        /// </summary>
        /// <remarks>
        /// The switch path is debounced and single-flight. Mixer refresh is intentionally skipped when hidden so tray
        /// mode avoids background UI churn.
        /// </remarks>
        public async ValueTask<bool> SwitchDevicesAsync(bool muteMic, bool muteSound, bool deafen, bool reverse = false)
        {
            var configuredCycle = await CaptureOutputCycleSnapshotAsync();
            if (configuredCycle.Count == 0)
            {
                await _dialogs.ShowWarningAsync("Please configure output cycle devices before switching.", DialogText.Captions.OutputDevicesMissing);
                return false;
            }

            bool switched = await _switchCoordinator.SwitchOutputAsync(
                configuredCycle,
                muteMic,
                muteSound,
                deafen,
                _preserveAudioLevelsBackingField,
                reverse,
                GetBluetoothReconnectOptions(),
                ScheduleOutputPostSwitchRefresh,
                static () => { },
                static () => { });

            return AppSwitchInteractionCoordinator.FinalizeSwitch(switched, output: true, MarkSwitchOverlayShown);
        }

        private void ScheduleOutputPostSwitchRefresh(string opId)
        {
            RunBackgroundWork(async shutdownToken =>
            {
                try
                {
                    await AppSwitchPostRefreshCoordinator.ExecuteOutputPostSwitchRefreshAsync(
                        new SwitchPostRefreshInput(opId, _shell.IsWindowVisible, _isCleaningUp),
                        _deviceCache.Refresh,
                        () => UpdateMuteFlagsFromSystem($"post-output-switch:{opId}"),
                        () => RefreshMixerAsync(interactive: true),
                        _logger,
                        shutdownToken);
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.PostFailed} | opId={opId}", nameof(SwitchDevicesAsync), ex);
                }
            }, nameof(SwitchDevicesAsync));
        }

        private Task UpdateMuteFlagsFromSystem()
        {
            return UpdateMuteFlagsFromSystem("unspecified");
        }

        private async Task UpdateMuteFlagsFromSystem(string context)
        {
            Task processorTask;

            lock (_muteRefreshLock)
            {
                _hasPendingMuteRefresh = true;
                _pendingMuteRefreshContext = context;
                _pendingMuteRefreshCount++;

                if (_muteRefreshProcessorTask == null || _muteRefreshProcessorTask.IsCompleted)
                {
                    _muteRefreshProcessorTask = ProcessPendingMuteRefreshesAsync();
                }

                processorTask = _muteRefreshProcessorTask;
            }

            await processorTask;
        }

        private async Task ProcessPendingMuteRefreshesAsync()
        {
            bool loggedDeferredWhileRefreshing = false;

            while (true)
            {
                string context;
                int coalescedRequests;

                lock (_muteRefreshLock)
                {
                    if (_isCleaningUp || !_hasPendingMuteRefresh)
                    {
                        _muteRefreshProcessorTask = null;
                        return;
                    }

                    context = _pendingMuteRefreshContext;
                    coalescedRequests = _pendingMuteRefreshCount;
                }

                if (IsMixerRefreshInProgress(MixerRefreshTarget.Both))
                {
                    if (!loggedDeferredWhileRefreshing && _logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AppViewModel", () => $"mute-refresh-deferred | context={context} queued={coalescedRequests} reason=mixer-refresh-in-progress");
                        loggedDeferredWhileRefreshing = true;
                    }

                    try
                    {
                        await WaitForMixerRefreshSettlementAsync(_backgroundWorkCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_muteRefreshLock)
                        {
                            _muteRefreshProcessorTask = null;
                        }

                        return;
                    }

                    continue;
                }

                lock (_muteRefreshLock)
                {
                    context = _pendingMuteRefreshContext;
                    coalescedRequests = _pendingMuteRefreshCount;
                    _hasPendingMuteRefresh = false;
                    _pendingMuteRefreshCount = 0;
                    _pendingMuteRefreshContext = "unspecified";
                }

                loggedDeferredWhileRefreshing = false;
                await UpdateMuteFlagsCoreAsync(context, coalescedRequests);
            }
        }

        private async Task UpdateMuteFlagsCoreAsync(string context, int coalescedRequests)
        {
            (bool isPlaybackMuted, bool isMicMuted) muteStates;

            try
            {
                muteStates = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                    ReadAuthoritativeMuteStates(context));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Error fetching mute states in background", nameof(UpdateMuteFlagsFromSystem), ex);
                QueueMuteRefreshRetry(context);
                return;
            }

            if (coalescedRequests > 1 && _logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AppViewModel", () => $"mute-refresh-coalesced | context={context} count={coalescedRequests}");
            }

            await InvokeOnDispatcherAsync(() =>
            {
                ApplyAuthoritativeMuteFlags(muteStates.isPlaybackMuted, muteStates.isMicMuted, context);
            });
        }

        private void QueueMuteRefreshRetry(string context)
        {
            if (_isCleaningUp || context.EndsWith(":retry", StringComparison.Ordinal))
            {
                return;
            }

            RunBackgroundWork(async shutdownToken =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), shutdownToken);
                await UpdateMuteFlagsFromSystem($"{context}:retry");
            }, nameof(UpdateMuteFlagsFromSystem));
        }

        private void ApplyAuthoritativeMuteFlags(bool isPlaybackMuted, bool isMicMuted, string context)
        {
            MuteFlagUpdateResult update = AppSwitchPostRefreshCoordinator.ResolveMuteFlagUpdate(
                isPlaybackMuted,
                isMicMuted,
                _deafenBackingField,
                _muteSoundBackingField,
                _muteMicBackingField);

            _deafenBackingField = update.NewDeafen;
            _muteSoundBackingField = update.NewMuteSound;
            _muteMicBackingField = update.NewMuteMic;
            ApplyEndpointMuteStateToInitializedMixers(isPlaybackMuted, isMicMuted);

            if (update.AnyChanged)
            {
                _muteStateRevision++;
                OnPropertyChanged(nameof(Deafen));
                OnPropertyChanged(nameof(MuteSound));
                OnPropertyChanged(nameof(MuteMic));

                _logger.Trace("AppViewModel", () => $"mute-flags-updated | playback={_muteSoundBackingField} mic={_muteMicBackingField} deafen={_deafenBackingField} context={context}");
            }
        }

        public async Task CleanupAsync()
        {
            if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
            {
                return;
            }

            string cleanupOpId = $"cleanup:{Guid.NewGuid():N}";

            CancellationTokenSource? autoSaveDebounceToDispose = CancelAndDetachDebounce(ref _autoSaveDebounceCts);
            await ExecuteCleanupTaskAsync(
                () => FlushPendingAutoSaveBeforeCleanupAsync(cleanupOpId),
                "flush-pending-auto-save",
                cleanupOpId);

            _isCleaningUp = true;
            _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupStart} | opId={cleanupOpId} pendingTasks={_backgroundTasks.Count}");
            ExecuteCleanupAction(
                () => SetMixerSessionMonitoringMode(desiredMode: null, context: "cleanup"),
                "stop-mixer-session-monitoring",
                cleanupOpId);
            ExecuteCleanupAction(
                () =>
                {
                    DetachOwnedEventHandlers();
                    _audio.AudioSessionCreated -= OnAudioSessionCreated;
                    _audio.AudioSessionLifecycleChanged -= OnAudioSessionLifecycleChanged;
                    _audio.DefaultAudioDeviceChanged -= OnDefaultAudioDeviceChanged;
                    _routineAppProcessMonitor.ProcessStarted -= OnRoutineAppProcessStarted;
                    _routineAppProcessMonitor.ProcessStopped -= OnRoutineAppProcessStopped;
                    if (_steamBigPictureSignalMonitor.IsValueCreated)
                    {
                        _steamBigPictureSignalMonitor.Value.Signaled -= OnSteamBigPictureMonitorSignaled;
                    }
                },
                "detach-owned-event-handlers",
                cleanupOpId);

            await ExecuteCleanupTaskAsync(_cliOverlayCoordinator.ShutdownAsync, "shutdown-cli-overlay", cleanupOpId);
            await ExecuteCleanupTaskAsync(DisposeAudioEndpointTestingAsync, "dispose-audio-endpoint-testing", cleanupOpId);
            await ExecuteCleanupTaskAsync(DeactivateAllRoutineStatefulSessionsForCleanupAsync, "deactivate-stateful-routines", cleanupOpId);

            CancellationTokenSource? startupDebounceToDispose = CancelAndDetachDebounce(ref _startupDebounceCts);
            CancellationTokenSource? sessionDebounceToDispose = CancelAndDetachDebounce(ref _sessionRefreshDebounceCts);
            CancellationTokenSource? visibleMixerActivationDebounceToDispose = CancelAndDetachDebounce(ref _visibleMixerActivationRefreshDebounceCts);
            CancellationTokenSource? steamBigPictureDebounceToDispose = CancelAndDetachDebounce(ref _steamBigPictureDebounceCts);
            CancellationTokenSource? steamBigPictureConfirmationDebounceToDispose = CancelAndDetachDebounce(ref _steamBigPictureConfirmationDebounceCts);
            CancellationTokenSource? steamBigPictureFallbackMaintenanceToDispose = CancelAndDetachDebounce(ref _steamBigPictureFallbackMaintenanceCts);
            CancellationTokenSource? routineLeaseDebounceToDispose = CancelAndDetachDebounce(ref _routineAppOutputLeaseRefreshDebounceCts);

            bool backgroundTasksCompleted = false;
            try
            {
                backgroundTasksCompleted = await ExecuteBlockingCleanupStepsAsync(
                    cleanupOpId,
                    autoSaveDebounceToDispose,
                    startupDebounceToDispose,
                    sessionDebounceToDispose,
                    visibleMixerActivationDebounceToDispose,
                    steamBigPictureDebounceToDispose,
                    steamBigPictureConfirmationDebounceToDispose,
                    steamBigPictureFallbackMaintenanceToDispose,
                    routineLeaseDebounceToDispose);
            }
            catch (Exception ex)
            {
                LogCleanupStepFailure("drain-background-work", cleanupOpId, ex);
            }

            ExecuteCleanupAction(DisposeOwnedCommands, "dispose-commands", cleanupOpId);
            ExecuteCleanupAction(() => TryGetMixer(AudioMixerMode.Output)?.Cleanup(), "dispose-output-mixer", cleanupOpId);
            ExecuteCleanupAction(() => TryGetMixer(AudioMixerMode.Input)?.Cleanup(), "dispose-input-mixer", cleanupOpId);
            await ExecuteCleanupTaskAsync(DisposeCleanupMonitorsAsync, "dispose-process-monitors", cleanupOpId);
            ExecuteCleanupAction(_routineLastRunRefreshTimer.Stop, "stop-routine-refresh-timer", cleanupOpId);

            _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupComplete} | opId={cleanupOpId} backgroundTasksCompleted={backgroundTasksCompleted}");
        }

        private void ExecuteCleanupAction(Action action, string stepName, string cleanupOpId)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogCleanupStepFailure(stepName, cleanupOpId, ex);
            }
        }

        private async Task ExecuteCleanupTaskAsync(Func<Task> action, string stepName, string cleanupOpId)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogCleanupStepFailure(stepName, cleanupOpId, ex);
            }
        }

        private void LogCleanupStepFailure(string stepName, string cleanupOpId, Exception ex)
        {
            _logger.Warning(
                "AppViewModel",
                () => $"cleanup-step-failed | step={stepName} opId={cleanupOpId}",
                nameof(CleanupAsync),
                ex);
        }

        private async Task FlushPendingAutoSaveBeforeCleanupAsync(string cleanupOpId)
        {
            if (!IsPersistedAutoSaveEnabled())
            {
                return;
            }

            if (!HasUiSettingsDivergedFromCachedSettings() &&
                !HasSettingsDraftDivergedFromCachedSettings() &&
                !HasRoutineEdits())
            {
                return;
            }

            try
            {
                _logger.Info("AppViewModel", () => $"auto-save-flush-before-cleanup | opId={cleanupOpId}");
                await RunAutoSaveAsync($"cleanup-flush:{cleanupOpId}");
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", () => $"auto-save-flush-before-cleanup-failed | opId={cleanupOpId} error={ex.GetType().Name}");
            }
        }

        public void Cleanup() => Task.Run(async () => await CleanupAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

        private async Task<bool> ExecuteBlockingCleanupStepsAsync(
            string cleanupOpId,
            CancellationTokenSource? autoSaveDebounceToDispose,
            CancellationTokenSource? startupDebounceToDispose,
            CancellationTokenSource? sessionDebounceToDispose,
            CancellationTokenSource? visibleMixerActivationDebounceToDispose,
            CancellationTokenSource? steamBigPictureDebounceToDispose,
            CancellationTokenSource? steamBigPictureConfirmationDebounceToDispose,
            CancellationTokenSource? steamBigPictureFallbackMaintenanceToDispose,
            CancellationTokenSource? routineLeaseDebounceToDispose)
        {
            bool backgroundTasksCompleted = true;
            try
            {
                _backgroundWorkHelper.ClearDeferredOperations();
                _backgroundWorkCts.Cancel();
                await WaitForMixerRestoreReadinessAsync(CancellationToken.None);
                backgroundTasksCompleted = await WaitForBackgroundTasksToCompleteAsync(cleanupOpId);
            }
            catch
            {
                backgroundTasksCompleted = false;
            }
            finally
            {
                autoSaveDebounceToDispose?.Dispose();
                startupDebounceToDispose?.Dispose();
                sessionDebounceToDispose?.Dispose();
                visibleMixerActivationDebounceToDispose?.Dispose();
                steamBigPictureDebounceToDispose?.Dispose();
                steamBigPictureConfirmationDebounceToDispose?.Dispose();
                steamBigPictureFallbackMaintenanceToDispose?.Dispose();
                routineLeaseDebounceToDispose?.Dispose();

                try
                {
                    BackgroundTaskHelper.DisposeResources(_backgroundWorkCts, _backgroundTasks);
                }
                catch
                {
                }
            }

            return backgroundTasksCompleted;
        }

        private Task DisposeCleanupMonitorsAsync()
        {
            return Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        _routineAppProcessMonitor.Dispose();
                    }
                    finally
                    {
                        if (_steamBigPictureSignalMonitor.IsValueCreated)
                        {
                            _steamBigPictureSignalMonitor.Value.Dispose();
                        }
                        else
                        {
                            Interlocked.Exchange(ref _unmaterializedSteamBigPictureSignalMonitor, null)?.Dispose();
                        }
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private async Task<bool> WaitForBackgroundTasksToCompleteAsync(string cleanupOpId)
        {
            Task[] pendingTasks = BackgroundTaskHelper.SnapshotPendingTasks(_backgroundTasks);
            return await BackgroundTaskHelper.DrainWithGraceAndLoggingAsync(
                pendingTasks,
                AppConstants.Timing.CleanupWaitMs,
                AppConstants.Timing.CleanupGraceExtensionMs,
                AppViewModelCleanupDrainLogHelper.CreateCallbacks(_logger, cleanupOpId));
        }

        private void GenerateDeviceReferenceFile()
        {
            List<CycleDevice>? outputDevices = null;
            List<CycleDevice>? inputDevices = null;

            try
            {
                Settings? cachedSettings;
                lock (_settingsLock)
                {
                    cachedSettings = _cachedSettings;
                }

                DeviceReferenceFileMode mode = cachedSettings?.Miscellaneous.DeviceReferenceFileMode ?? DeviceReferenceFileMode.Off;
                if (mode == DeviceReferenceFileMode.Off)
                {
                    return;
                }

                if (_outputDevices.Count > 0 || _inputDevices.Count > 0)
                {
                    outputDevices = new List<CycleDevice>(_outputDevices.Count);
                    for (int index = 0; index < _outputDevices.Count; index++)
                    {
                        var device = _outputDevices[index];
                        outputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name });
                    }

                    inputDevices = new List<CycleDevice>(_inputDevices.Count);
                    for (int index = 0; index < _inputDevices.Count; index++)
                    {
                        var device = _inputDevices[index];
                        inputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name });
                    }
                }
                else
                {
                    outputDevices = GetActiveOutputDeviceInfos();
                    inputDevices = GetActiveInputDeviceInfos();
                }

                string topologyFingerprint = $"{mode}:{BuildDeviceTopologyFingerprint(outputDevices, inputDevices)}";
                lock (_deviceReferenceFingerprintLock)
                {
                    if (string.Equals(_lastDeviceReferenceFingerprint, topologyFingerprint, StringComparison.Ordinal))
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.DeviceReferenceSkip} | reason=topology-unchanged");
                        }

                        return;
                    }
                }

                _settings.GenerateDeviceReferenceFile(
                    outputDevices,
                    inputDevices,
                    anonymizeIds: mode == DeviceReferenceFileMode.Hashed);

                lock (_deviceReferenceFingerprintLock)
                {
                    _lastDeviceReferenceFingerprint = topologyFingerprint;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", () => $"device-reference-file-generate-failed | error={ex.GetType().Name}");
            }
        }

        internal static string BuildDeviceTopologyFingerprint(
            IEnumerable<CycleDevice> outputDevices,
            IEnumerable<CycleDevice> inputDevices)
        {
            var builder = new StringBuilder();

            builder.Append("OUT|");
            AppendNormalizedTopologyDevices(builder, outputDevices);

            builder.Append("IN|");
            AppendNormalizedTopologyDevices(builder, inputDevices);

            return builder.ToString();
        }

        private static void AppendNormalizedTopologyDevices(StringBuilder builder, IEnumerable<CycleDevice> devices)
        {
            var normalized = new List<CycleDevice>();
            foreach (var device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id))
                {
                    continue;
                }

                normalized.Add(device);
            }

            normalized.Sort(static (left, right) =>
            {
                int byId = StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
                if (byId != 0)
                {
                    return byId;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty);
            });

            for (int index = 0; index < normalized.Count; index++)
            {
                CycleDevice device = normalized[index];
                builder.Append(device.Id.Trim());
                builder.Append('=');
                builder.Append((device.Name ?? string.Empty).Trim());
                builder.Append('|');
            }
        }

        public async ValueTask<bool> SwitchInputDevicesAsync(bool reverse = false)
        {
            var configuredCycle = await CaptureInputCycleSnapshotAsync();
            if (configuredCycle.Count == 0)
            {
                await _dialogs.ShowWarningAsync("Please configure input cycle devices before switching.", DialogText.Captions.InputDevicesMissing);
                return false;
            }

            bool switched = await _switchCoordinator.SwitchInputAsync(configuredCycle, reverse, _preserveAudioLevelsBackingField, GetBluetoothReconnectOptions());

            return AppSwitchInteractionCoordinator.FinalizeSwitch(switched, output: false, MarkSwitchOverlayShown);
        }

        private BluetoothReconnectOptions GetBluetoothReconnectOptions()
        {
            Settings effectiveSettings = _cachedSettings ?? new Settings();
            return BluetoothReconnectOptions.FromSettings(effectiveSettings);
        }

        private Task<List<CycleDevice>> CaptureOutputCycleSnapshotAsync()
        {
            if (_dispatcher.CheckAccess())
            {
                return Task.FromResult(AppViewModelDeviceCycleHelper.CloneCycleDevices(OutputCycleDevices));
            }

            return InvokeOnDispatcherAsync(() => AppViewModelDeviceCycleHelper.CloneCycleDevices(OutputCycleDevices), fallback: []);
        }

        private Task<List<CycleDevice>> CaptureInputCycleSnapshotAsync()
        {
            if (_dispatcher.CheckAccess())
            {
                return Task.FromResult(AppViewModelDeviceCycleHelper.CloneCycleDevices(InputCycleDevices));
            }

            return InvokeOnDispatcherAsync(() => AppViewModelDeviceCycleHelper.CloneCycleDevices(InputCycleDevices), fallback: []);
        }
    }
}
