using System.Windows;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel : IResumeRecoveryHandler
    {
        internal static Action? ExitApplicationOverrideForTests { get; set; }

        /// <summary>
        /// Brings the main window to the foreground and refreshes device/mixer state for interactive use.
        /// </summary>
        public Task<bool> ShowWindowAsync(CancellationToken cancellationToken = default) =>
            ShowWindowAsync(MainWindowOpenTarget.Default, cancellationToken: cancellationToken);

        internal async Task<bool> ShowWindowAsync(
            MainWindowOpenTarget target,
            MainWindowHotkeyPresentation? hotkey = null,
            CancellationToken cancellationToken = default)
        {
            if (IsRuntimeShutdownRequested || _isCleaningUp)
            {
                _logger.Debug("AppViewModel", "show-window-skipped-during-shutdown");
                return false;
            }

            try
            {
                Func<bool>? canPresent = hotkey?.CanPresent;
                if (canPresent != null && (!_windowState.IsStartupVisibilityResolved || !canPresent())) return false;
                return await AppWindowVisibilityCoordinator.ShowWindowAsync(
                    _windowState,
                    () => _shell.ShowWindowFrontAndCenterAsync(target, hotkey, cancellationToken),
                    _logger,
                    DateTime.Now,
                    canPresent);
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
        public Task<bool> ToggleWindowVisibilityAsync(CancellationToken cancellationToken = default) => ToggleWindowVisibilityAsync(null, cancellationToken);

        internal async Task<bool> ToggleWindowVisibilityAsync(MainWindowHotkeyPresentation? hotkey, CancellationToken cancellationToken = default)
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
                    () => ShowWindowAsync(MainWindowOpenTarget.Default, hotkey, cancellationToken),
                    MinimizeWindow,
                    _logger,
                    retryBackgroundShow: hotkey != null && _shell.CanRetryHotkeyShow);
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
            Volatile.Write(ref _skipNextVisibleWindowRestoreRefresh, 0);
            try
            {
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
        public void MinimizeWindow() => MinimizeWindow(honorShowCooldown: true);

        internal void MinimizeWindow(bool honorShowCooldown)
        {
            if (IsRuntimeShutdownRequested) return;
            try
            {
                MinimizeWindowPlan plan = AppWindowVisibilityCoordinator.BuildMinimizePlan(
                    _windowState,
                    ShowBalloonAfterSave,
                    DateTime.Now,
                    honorShowCooldown);

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

        public async Task RecoverAfterSystemResumeAsync(string? resumeOpId = null, CancellationToken cancellationToken = default)
        {
            string opId = AppResumeRecoveryCoordinator.ResolveOperationId(resumeOpId);

            if (_isCleaningUp)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | opId={opId} reason=cleanup-in-progress");
                return;
            }

            CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : _backgroundWorkCts.Token;
            ResumeRecoveryExecutionResult result = await AppResumeRecoveryCoordinator.ExecuteAsync(
                opId,
                new ResumeRecoveryExecutionDependencies(
                    () => _audio.RecoverAfterSystemResumeAsync(token),
                    id => ReRegisterHotkeysAfterResumeAsync(id, token),
                    () => RefreshDevicesAfterResumeAsync(opId, token)),
                _logger,
                nameof(RecoverAfterSystemResumeAsync),
                token);
            if (token.IsCancellationRequested || IsRuntimeShutdownRequested || _isCleaningUp) return;
            Volatile.Write(ref _lastResumeHealth, new(DateTimeOffset.UtcNow, result.Succeeded, result.HotkeyAttempts,
                result.HotkeyFailedCount, result.AudioRecovered, result.DevicesRefreshed));
            if (!result.Succeeded)
            {
                await _shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.ResumeRecovery);
            }
            else _shell.ClearBackgroundFailure(BackgroundFailureKind.ResumeRecovery);
            if (!_isCleaningUp && !token.IsCancellationRequested) ReportHotkeyAvailability();
        }

        private async Task<(ResumeHotkeyRegistrationResult Result, int Attempts)> ReRegisterHotkeysAfterResumeAsync(
            string resumeOpId, CancellationToken cancellationToken)
        {
            bool firstAttempt = true;
            try
            {
                return await AppResumeRecoveryCoordinator.RegisterHotkeysAsync(
                    () =>
                    {
                        bool reset = firstAttempt;
                        firstAttempt = false;
                        return RegisterResumeHotkeysOnDispatcherAsync(reset, cancellationToken);
                    },
                    RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                    _logger,
                    resumeOpId,
                    cancellationToken);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    await InvokeOnDispatcherAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested && !_backgroundWorkCts.IsCancellationRequested &&
                            !IsRuntimeShutdownRequested && !_isCleaningUp)
                            PushToTalkModeChanged?.Invoke(CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
                    });
            }
        }

        private Task<ResumeHotkeyRegistrationResult> RegisterResumeHotkeysOnDispatcherAsync(bool reset, CancellationToken cancellationToken)
        {
            Settings? hotkeySettings = null;
            return AppSwitchInteractionCoordinator.RegisterResumeHotkeysOnDispatcherAsync(
                callback => InvokeOnDispatcherAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _backgroundWorkCts.Token.ThrowIfCancellationRequested();
                    return callback();
                }, fallback: default),
                () =>
                {
                    lock (_settingsLock) hotkeySettings = (_cachedSettings ?? new Settings()).Clone();
                    hotkeySettings.DeviceSwitching.Output.HotkeysEnabled = OutputHotkeysEnabled;
                    hotkeySettings.DeviceSwitching.Input.HotkeysEnabled = InputHotkeysEnabled;
                    return _hotkeyRegistrationCoordinator.RegisterAll(hotkeySettings, unregisterAllFirst: reset);
                },
                () => RegisterRoutineHotkeysFromSettings(hotkeySettings!, context: "resume"));
        }

        private async Task RefreshDevicesAfterResumeAsync(string opId, CancellationToken cancellationToken)
        {
            bool cacheRefreshed = await _deviceCache.RefreshAfterResumeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            long muteRevision = Volatile.Read(ref _muteStateRevision);
            var (isPlaybackMuted, isMicrophoneMuted) = await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => ReadAuthoritativeMuteStates(opId), cancellationToken);
            await InvokeOnDispatcherAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRuntimeShutdownRequested) return;
                if (muteRevision == _muteStateRevision)
                    ApplyAuthoritativeMuteFlags(isPlaybackMuted, isMicrophoneMuted, opId);

                GenerateDeviceReferenceFile();
                if (!_shell.IsWindowVisible || IsResettingSettings) return;
                await RefreshAvailableDeviceCollectionsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_shell.IsWindowVisible || IsRuntimeShutdownRequested || IsResettingSettings) return;
                _audio.InvalidateRecentMixerSnapshotState(refreshProcessMetadata: true);
                await RefreshMixerAsync(interactive: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (_shell.IsWindowVisible && !IsRuntimeShutdownRequested && TryGetActiveMixer() is { } mixer)
                    await mixer.RefreshApplicationRoutingAsync();
            });

            if (!cacheRefreshed) throw new InvalidOperationException("The device cache did not refresh after resume.");
        }

        /// <summary>
        /// Switches to the next configured output device and updates overlay/UI state.
        /// </summary>
        /// <remarks>
        /// The switch path is debounced and single-flight. Mixer refresh is intentionally skipped when hidden so tray
        /// mode avoids background UI churn.
        /// </remarks>
        public ValueTask<bool> SwitchDevicesAsync(bool muteMic, bool muteSound, bool deafen, bool reverse = false, string source = "hotkey") =>
            SwitchOutputCycleAsync(muteMic, muteSound, deafen, reverse, source);

        internal Task SwitchCycleFromHotkeyAsync(bool output, bool reverse, Func<bool> canAdmit) =>
            output ? SwitchOutputCycleAsync(false, false, Deafen, reverse, "hotkey", canAdmit).AsTask()
                : SwitchInputCycleAsync(reverse, "hotkey", canAdmit).AsTask();

        private async ValueTask<bool> SwitchOutputCycleAsync(bool muteMic, bool muteSound, bool deafen, bool reverse, string source, Func<bool>? canAdmit = null)
        {
            var configuredCycle = CaptureDeviceCycleSnapshot(output: true, canAdmit);
            if (configuredCycle == null || canAdmit?.Invoke() == false) return false;
            if (configuredCycle.Count == 0)
            {
                RecordSwitchHistory(true, reverse, source, false, skipped: true, "No output devices configured.");
                if (!string.Equals(source, "cli", StringComparison.OrdinalIgnoreCase))
                {
                    await _dialogs.ShowWarningAsync("Please configure output cycle devices before switching.", DialogText.Captions.OutputDevicesMissing, cancellationToken: GetDialogShutdownToken());
                }

                return false;
            }

            if (AudioTesting.IsAudioTestActive)
            {
                await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced).ConfigureAwait(false);
                if (canAdmit?.Invoke() == false) return false;
            }

            bool switched = await TrackDeviceSwitchAsync(() => _switchCoordinator.SwitchOutputAsync(
                configuredCycle,
                muteMic,
                muteSound,
                deafen || Deafen,
                _preserveAudioLevelsBackingField,
                reverse,
                GetBluetoothReconnectOptions(),
                ScheduleOutputPostSwitchRefresh,
                static () => { },
                static () => { },
                canAdmit), output: true, reverse, source).ConfigureAwait(false);

            if (switched)
            {
                ClearDeviceSwitchFeedback(output: true);
                MarkSwitchOverlayShown(output: true);
            }

            return switched;
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
                if (IsRuntimeShutdownRequested) return;
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
                    if (_isCleaningUp || IsRuntimeShutdownRequested || !_hasPendingMuteRefresh)
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
                    ReadAuthoritativeMuteStates(context), ShutdownToken);
            }
            catch (OperationCanceledException) when (IsRuntimeShutdownRequested || ShutdownToken.IsCancellationRequested)
            {
                return;
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
                if (!IsRuntimeShutdownRequested)
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

        /// <summary>Closes shell command admission before teardown awaits, preserving pending settings flushes.</summary>
        internal void BeginRuntimeShutdown()
        {
            if (Interlocked.Exchange(ref _runtimeShutdownStarted, 1) != 0) return;
            _backgroundWorkCancellation = ExecuteCleanupTaskAsync(_backgroundWorkCts.CancelAsync, "cancel-background-work", "shutdown-admission");
            _backgroundWorkHelper.ClearDeferredOperations();
            ExecuteCleanupAction(_routineRuntime.BeginShutdown, "stop-routine-runtime", "shutdown-admission");
            AppDispatcherHelper.Dispatch(_dispatcher, _logger, () =>
            {
                OnPropertyChanged(nameof(IsRuntimeShutdownRequested));
            }, "Failed to update command availability during shutdown", nameof(BeginRuntimeShutdown));
        }

        public Task CleanupAsync()
        {
            BeginRuntimeShutdown();
            TaskCompletionSource completion;
            lock (_settingsLock)
            {
                if (_cleanupTask != null) return _cleanupTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleanupTask = completion.Task;
            }
            // Publish completion before settings/property callbacks can reenter cleanup.
            _ = CompleteAsync();
            return completion.Task;

            async Task CompleteAsync()
            {
                try { await CleanupCoreAsync(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }
        }

        private async Task CleanupCoreAsync()
        {
            string cleanupOpId = $"cleanup:{Guid.NewGuid():N}";

            CancellationTokenSource? autoSaveDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts);
            await ExecuteCleanupTaskAsync(
                () => FlushPendingAutoSaveForShutdownAsync(cleanupOpId),
                "flush-pending-auto-save",
                cleanupOpId);

            _isCleaningUp = true;
            _settingsFeedbackWork?.Abort();
            _settingsFeedbackWork = null;
            _notSavedFeedbackTabs = SettingsFeedbackTabs.None;
            _saveProblems?.Clear();
            if (_settingsFeedbackTimer is { } feedbackTimer)
            {
                feedbackTimer.Stop();
                feedbackTimer.Tick -= SaveConfirmationTimer_Tick;
                _settingsFeedbackTimer = null;
                _settingsFeedbackTimerSavedAt = null;
                _savedFeedbackTabs = SettingsFeedbackTabs.None;
                _saveConfirmations.Clear();
            }
            _shell.Dispose();
            if (_quickDevicePicker != null && !_dispatcher.HasShutdownStarted)
                await ExecuteCleanupTaskAsync(() => _dispatcher.InvokeAsync(() => _quickDevicePicker?.Dismiss(restoreFocus: false)).Task,
                    "close-device-picker", cleanupOpId);
            if (_quickMixerWindow != null && !_dispatcher.HasShutdownStarted)
                await ExecuteCleanupTaskAsync(() => _dispatcher.InvokeAsync(() => _quickMixerWindow?.Close()).Task,
                    "close-quick-mixer", cleanupOpId);
            _mixerPresentationDemand?.Dispose();
            _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupStart} | opId={cleanupOpId} pendingTasks={_backgroundTasks.Count}");
            ExecuteCleanupAction(
                () => SetMixerSessionMonitoringTarget(desiredMode: null, context: "cleanup"),
                "stop-mixer-session-monitoring",
                cleanupOpId);
            ExecuteCleanupAction(
                () =>
                {
                    DetachOwnedEventHandlers();
                    _audio.AudioSessionCreated -= OnAudioSessionCreated;
                    _audio.AudioSessionLifecycleChanged -= OnAudioSessionLifecycleChanged;
                    _audio.DefaultAudioDeviceChanged -= OnDefaultAudioDeviceChanged;
                    _audio.DeviceStateChanged -= OnMeterDevicesChanged;
                    _audio.DefaultRoleChanged -= OnPrimaryEndpointIdentityChanged;
                },
                "detach-owned-event-handlers",
                cleanupOpId);

            using (var meterDeadline = new CancellationTokenSource(AppConstants.Timing.CleanupWaitMs))
            {
                Task layoutFlush = QuickPanelLayouts != null
                    ? ExecuteCleanupTaskAsync(QuickPanelLayouts.FlushAsync, "flush-quick-panel-layouts", cleanupOpId)
                    : Task.CompletedTask;
                Task stopMeters = ExecuteCleanupTaskAsync(() => StopQuickMixerMetersAsync(meterDeadline.Token), "stop-quick-mixer-meters", cleanupOpId);
                Task stopUpdates = ExecuteCleanupTaskAsync(() => Updates.DisposeAsync().AsTask(), "stop-update-checks", cleanupOpId);
                Task shutdownCliOverlay = ExecuteCleanupTaskAsync(_audioCommandCoordinator.ShutdownAsync, "shutdown-cli-overlay", cleanupOpId);
                Task disposeTesting = ExecuteCleanupTaskAsync(() => AudioTesting.DisposeAsync().AsTask(), "dispose-audio-endpoint-testing", cleanupOpId);
                await Task.WhenAll(layoutFlush, stopMeters, stopUpdates, shutdownCliOverlay, disposeTesting);
            }
            await ExecuteCleanupTaskAsync(DeactivateAllRoutineStatefulSessionsForCleanupAsync, "deactivate-stateful-routines", cleanupOpId);

            CancellationTokenSource? startupDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts);
            CancellationTokenSource? sessionDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _sessionRefreshDebounceCts);
            CancellationTokenSource? visibleMixerActivationDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _visibleMixerActivationRefreshDebounceCts);

            bool backgroundTasksCompleted = false;
            try
            {
                backgroundTasksCompleted = await ExecuteBlockingCleanupStepsAsync(
                    cleanupOpId,
                    autoSaveDebounceToDispose,
                    startupDebounceToDispose,
                    sessionDebounceToDispose,
                    visibleMixerActivationDebounceToDispose);
            }
            catch (Exception ex)
            {
                LogCleanupStepFailure("drain-background-work", cleanupOpId, ex);
            }

            ExecuteCleanupAction(DisposeOwnedCommands, "dispose-commands", cleanupOpId);
            ExecuteCleanupAction(() => TryGetMixer(AudioMixerMode.Output)?.Cleanup(), "dispose-output-mixer", cleanupOpId);
            ExecuteCleanupAction(() => TryGetMixer(AudioMixerMode.Input)?.Cleanup(), "dispose-input-mixer", cleanupOpId);
            await ExecuteCleanupTaskAsync(() => _routineRuntime.DisposeAsync().AsTask(), "dispose-routine-runtime", cleanupOpId);
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
            AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
            CancellationTokenSource? pendingStartupDebounce = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts);
            bool hadPendingStartupToggle = _startupTogglePending || pendingStartupDebounce != null;
            pendingStartupDebounce?.Dispose();
            if (_settingsResetCompletion is { } reset) await reset.Task;

            // Join any active settings save until its busy flags clear before
            // checking for final edits, since RunAutoSaveAsync skips while busy.
            await WaitForActiveSettingsSavesAsync();
            AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
            CancellationTokenSource? trailingStartupDebounce = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts);
            hadPendingStartupToggle |= _startupTogglePending || trailingStartupDebounce != null;
            trailingStartupDebounce?.Dispose();
            if (_isCleaningUp) return;

            if (hadPendingStartupToggle && _runAtStartupUserModified)
            {
                bool targetStartup = _runAtStartup;
                Settings? cached = GetCachedSettingsSnapshot();
                if (cached == null || cached.Startup.Enabled != targetStartup)
                {
                    try
                    {
                        await ApplyStartupToggleAsync(targetStartup, $"cleanup-startup:{cleanupOpId}", static () => false, allowDuringShutdown: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("AppViewModel", () => $"startup-toggle-flush-before-cleanup-failed | opId={cleanupOpId} error={ex.GetType().Name}");
                    }
                }
                else
                {
                    _startupTogglePending = false;
                }
            }

            if (!IsPersistedAutoSaveEnabled()) return;

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

        internal Task FlushPendingAutoSaveForShutdownAsync(string reason)
        {
            TaskCompletionSource completion;
            lock (_settingsLock)
            {
                if (_shutdownAutoSaveFlushTask != null) return _shutdownAutoSaveFlushTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownAutoSaveFlushTask = completion.Task;
            }

            _ = CompleteAsync();
            return completion.Task;

            async Task CompleteAsync()
            {
                try { await FlushPendingAutoSaveBeforeCleanupAsync(reason); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }
        }

        internal async Task PrepareSecondaryStateForSessionEndingAsync(string opId, bool finalShutdown = false)
        {
            await ExecuteCleanupTaskAsync(
                () => InvokeOnDispatcherAsync(() =>
                {
                    _quickDevicePicker?.FlushPendingLayoutSave();
                    _quickMixerWindow?.FlushPendingLayoutSave();
                }),
                "session-ending-capture-quick-panel-layouts",
                opId);

            Task flushLayouts = QuickPanelLayouts != null
                ? ExecuteCleanupTaskAsync(QuickPanelLayouts.FlushAsync, "session-ending-flush-quick-panel-layouts", opId)
                : Task.CompletedTask;
            AudioEndpointTestStopReason stopReason = finalShutdown
                ? AudioEndpointTestStopReason.Shutdown
                : AudioEndpointTestStopReason.Suspend;
            Task stopAudioTest = ExecuteCleanupTaskAsync(
                () => StopAudioEndpointTestAsync(stopReason),
                "session-ending-stop-audio-test",
                opId);
            Task deactivateRoutines = finalShutdown
                ? ExecuteCleanupTaskAsync(
                    DeactivateAllRoutineStatefulSessionsForCleanupAsync,
                    "session-ending-deactivate-stateful-routines",
                    opId)
                : Task.CompletedTask;
            await Task.WhenAll(flushLayouts, stopAudioTest, deactivateRoutines);
        }

        public void Cleanup() => Task.Run(async () => await CleanupAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

        private async Task<bool> ExecuteBlockingCleanupStepsAsync(
            string cleanupOpId,
            CancellationTokenSource? autoSaveDebounceToDispose,
            CancellationTokenSource? startupDebounceToDispose,
            CancellationTokenSource? sessionDebounceToDispose,
            CancellationTokenSource? visibleMixerActivationDebounceToDispose)
        {
            bool backgroundTasksCompleted = true;
            try
            {
                await _backgroundWorkCancellation;
                await WaitForMixerRestoreReadinessAsync(CancellationToken.None);
                backgroundTasksCompleted = await WaitForBackgroundTasksToCompleteAsync(cleanupOpId);
                if (!backgroundTasksCompleted)
                {
                    _logger.Warning("AppViewModel", () => $"cleanup-deferred | opId={cleanupOpId} reason=unfinished-background-work");
                    try { await Task.WhenAll(BackgroundTaskHelper.SnapshotPendingTasks(_backgroundTasks)); }
                    catch (Exception ex) { LogCleanupStepFailure("finish-background-drain", cleanupOpId, ex); }
                    backgroundTasksCompleted = true;
                }
            }
            catch
            {
                // A cancellation callback can throw before the usual drain runs.
                // Completion still must not release resources under unfinished work.
                try { await Task.WhenAll(BackgroundTaskHelper.SnapshotPendingTasks(_backgroundTasks)); }
                catch (Exception ex) { LogCleanupStepFailure("finish-background-drain", cleanupOpId, ex); }
                backgroundTasksCompleted = true;
            }
            finally
            {
                autoSaveDebounceToDispose?.Dispose();
                startupDebounceToDispose?.Dispose();
                sessionDebounceToDispose?.Dispose();
                visibleMixerActivationDebounceToDispose?.Dispose();

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
            _deviceReferenceGeneration.Request();
        }

        public ValueTask<bool> SwitchInputDevicesAsync(bool reverse = false, string source = "hotkey") =>
            SwitchInputCycleAsync(reverse, source);

        private async ValueTask<bool> SwitchInputCycleAsync(bool reverse, string source, Func<bool>? canAdmit = null)
        {
            var configuredCycle = CaptureDeviceCycleSnapshot(output: false, canAdmit);
            if (configuredCycle == null || canAdmit?.Invoke() == false) return false;
            if (configuredCycle.Count == 0)
            {
                RecordSwitchHistory(false, reverse, source, false, skipped: true, "No input devices configured.");
                if (!string.Equals(source, "cli", StringComparison.OrdinalIgnoreCase))
                {
                    await _dialogs.ShowWarningAsync("Please configure input cycle devices before switching.", DialogText.Captions.InputDevicesMissing, cancellationToken: GetDialogShutdownToken());
                }

                return false;
            }

            if (AudioTesting.IsAudioTestActive)
            {
                await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced).ConfigureAwait(false);
                if (canAdmit?.Invoke() == false) return false;
            }

            bool switched = await TrackDeviceSwitchAsync(() => _switchCoordinator.SwitchInputAsync(configuredCycle, reverse,
                _preserveAudioLevelsBackingField, GetBluetoothReconnectOptions(), canAdmit), output: false, reverse, source).ConfigureAwait(false);

            if (switched)
            {
                ClearDeviceSwitchFeedback(output: false);
                MarkSwitchOverlayShown(output: false);
            }

            return switched;
        }

        private BluetoothReconnectOptions GetBluetoothReconnectOptions()
        {
            Settings effectiveSettings = _cachedSettings ?? new Settings();
            return BluetoothReconnectOptions.FromSettings(effectiveSettings);
        }

        private List<CycleDevice>? CaptureDeviceCycleSnapshot(bool output, Func<bool>? canAdmit)
        {
            if (canAdmit?.Invoke() == false) return null;
            CycleDevice[] snapshot = Volatile.Read(ref output ? ref _outputCycleSnapshot : ref _inputCycleSnapshot);
            return AppViewModelDeviceCycleHelper.CloneCycleDevices(snapshot);
        }
    }
}
