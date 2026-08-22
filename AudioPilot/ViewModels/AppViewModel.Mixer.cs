using System.Collections.Specialized;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private long _endpointStateProjectionRevision;
        private long _lastAppliedOutputEndpointStateRevision;
        private long _lastAppliedInputEndpointStateRevision;

        public int SelectedSettingsTabIndex
        {
            get => _selectedSettingsTabIndex;
            set
            {
                if (_selectedSettingsTabIndex == value) return;
                int previousTabIndex = _selectedSettingsTabIndex;
                _selectedSettingsTabIndex = value;
                OnPropertyChanged(nameof(SelectedSettingsTabIndex));
                OnPropertyChanged(nameof(IsRoutinesTabActive));
                OnPropertyChanged(nameof(IsSettingsTabActive));
                OnPropertyChanged(nameof(IsDeviceTabsActive));
                OnPropertyChanged(nameof(IsEditorTabsActive));
                OnPropertyChanged(nameof(ActiveMixer));
                OnPropertyChanged(nameof(ActiveMixerSessions));
                OnPropertyChanged(nameof(ActiveMixerHeader));
                UpdateRoutineLastRunRefreshTimerState(
                    $"tab-selection:{value}",
                    forceRefresh: IsRoutinesTabActive && _isWindowVisible);
                UpdateMixerSessionMonitoringState($"tab-selection:{value}");
                QueueVisibleMixerActivationRefresh($"tab-selection:{value}");
                if ((_audioTestState.Kind == AudioEndpointTestKind.Output && previousTabIndex == 0 && value != 0)
                    || (_audioTestState.Kind == AudioEndpointTestKind.Input && previousTabIndex == 1 && value != 1))
                {
                    RequestStopAudioEndpointTest(AudioEndpointTestStopReason.TabChanged);
                }
            }
        }

        private MixerViewModel? TryGetActiveMixer()
        {
            return IsInputSettingsTab(SelectedSettingsTabIndex)
                ? _inputMixer
                : _mixer;
        }

        private MixerViewModel? TryGetMixer(AudioMixerMode mode)
        {
            return mode == AudioMixerMode.Input
                ? _inputMixer
                : _mixer;
        }

        private void ApplyEndpointMuteStateToInitializedMixers(bool playbackMuted, bool microphoneMuted)
        {
            MixerViewModel? outputMixer;
            MixerViewModel? inputMixer;

            lock (_mixerInitializationLock)
            {
                outputMixer = _mixer;
                inputMixer = _inputMixer;
            }

            outputMixer?.ApplyEndpointMuteStateFromSystem(playbackMuted, microphoneMuted);
            if (!ReferenceEquals(inputMixer, outputMixer))
            {
                inputMixer?.ApplyEndpointMuteStateFromSystem(playbackMuted, microphoneMuted);
            }
        }

        private void ApplyEndpointStateToInitializedMixers(AudioMixerMode mode, float volumePercent, bool isMuted)
        {
            MixerViewModel? outputMixer;
            MixerViewModel? inputMixer;

            lock (_mixerInitializationLock)
            {
                outputMixer = _mixer;
                inputMixer = _inputMixer;
            }

            outputMixer?.ApplyEndpointStateFromSystem(mode, volumePercent, isMuted);
            if (!ReferenceEquals(inputMixer, outputMixer))
            {
                inputMixer?.ApplyEndpointStateFromSystem(mode, volumePercent, isMuted);
            }
        }

        internal void ProjectEndpointVolumeStateFromCommand(
            AudioMixerMode mode,
            string endpointId,
            float volumePercent,
            bool isMuted)
        {
            QueueEndpointStateProjection(mode, endpointId, volumePercent, isMuted, "command");
        }

        private void QueueEndpointStateProjection(
            AudioMixerMode mode,
            string endpointId,
            float volumePercent,
            bool isMuted,
            string source)
        {
            if (_isCleaningUp || string.IsNullOrWhiteSpace(endpointId))
            {
                return;
            }

            long revision = Interlocked.Increment(ref _endpointStateProjectionRevision);
            void Apply()
            {
                if (_isCleaningUp)
                {
                    return;
                }

                ref long lastAppliedRevision = ref (mode == AudioMixerMode.Input
                    ? ref _lastAppliedInputEndpointStateRevision
                    : ref _lastAppliedOutputEndpointStateRevision);
                if (revision <= lastAppliedRevision)
                {
                    return;
                }

                lastAppliedRevision = revision;
                if (mode == AudioMixerMode.Input)
                {
                    ApplyAuthoritativeMuteFlags(_muteSoundBackingField, isMuted, $"endpoint-state:{source}:input");
                }
                else
                {
                    ApplyAuthoritativeMuteFlags(isMuted, _muteMicBackingField, $"endpoint-state:{source}:output");
                }

                ApplyEndpointStateToInitializedMixers(mode, volumePercent, isMuted);
            }

            if (_dispatcher.CheckAccess())
            {
                Apply();
                return;
            }

            try
            {
                _ = _dispatcher.InvokeAsync(Apply, DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping endpoint state projection because shutdown is in progress", nameof(QueueEndpointStateProjection), ex);
            }
        }

        private void QueueSessionStateProjection(AudioSessionLifecycleSignal signal)
        {
            if (signal.VolumePercent is not float || signal.IsMuted is not bool || _isCleaningUp)
            {
                return;
            }

            string projectionKey = $"{signal.MixerMode}:{signal.EndpointId}:{signal.SessionInstanceId}";
            lock (_sessionStateProjectionLock)
            {
                _pendingSessionStateProjections[projectionKey] = signal;
                if (_sessionStateProjectionScheduled)
                {
                    return;
                }

                _sessionStateProjectionScheduled = true;
            }

            void Drain()
            {
                while (true)
                {
                    AudioSessionLifecycleSignal[] batch;
                    lock (_sessionStateProjectionLock)
                    {
                        if (_isCleaningUp || _pendingSessionStateProjections.Count == 0)
                        {
                            _pendingSessionStateProjections.Clear();
                            _sessionStateProjectionScheduled = false;
                            return;
                        }

                        batch = [.. _pendingSessionStateProjections.Values];
                        _pendingSessionStateProjections.Clear();
                    }

                    foreach (AudioSessionLifecycleSignal pendingSignal in batch)
                    {
                        if (pendingSignal.VolumePercent is float volumePercent && pendingSignal.IsMuted is bool isMuted)
                        {
                            TryGetMixer(pendingSignal.MixerMode)?.ApplySessionStateFromSystem(
                                pendingSignal.SessionInstanceId,
                                volumePercent,
                                isMuted);
                        }
                    }
                }
            }

            try
            {
                if (_dispatcher.CheckAccess()) Drain();
                else _ = _dispatcher.InvokeAsync(Drain, DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                lock (_sessionStateProjectionLock)
                {
                    _pendingSessionStateProjections.Clear();
                    _sessionStateProjectionScheduled = false;
                }
                _logger.Warning("AppViewModel", "Skipping session state projection because shutdown is in progress", nameof(QueueSessionStateProjection), ex);
            }
        }

        private bool EnsureMixerInitialized(AudioMixerMode mode)
        {
            bool createdOrConnected = false;

            lock (_mixerInitializationLock)
            {
                if (mode == AudioMixerMode.Output)
                {
                    if (_mixer == null)
                    {
                        _mixer = _mixerFactory();
                        createdOrConnected = true;
                    }
                }
                else if (_inputMixer == null)
                {
                    _inputMixer = _inputMixerFactory();
                    createdOrConnected = true;
                }

                if (!_mixersConnected && _mixer != null && _inputMixer != null)
                {
                    MixerViewModel.ConnectSharedSessionPair(_mixer, _inputMixer);
                    _mixersConnected = true;
                    createdOrConnected = true;
                }
            }

            return createdOrConnected;
        }

        private async Task EnsureMixerInitializedAsync(AudioMixerMode mode, string context)
        {
            if (!EnsureMixerInitialized(mode))
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug) && ShouldLogLazyMixerInitialization(context))
            {
                _logger.Debug("AppViewModel", () => $"lazy-mixer-initialized | mode={mode} context={context}");
            }

            await InvokeOnDispatcherAsync(() =>
            {
                if (mode == AudioMixerMode.Input)
                {
                    OnPropertyChanged(nameof(InputMixer));
                }
                else
                {
                    OnPropertyChanged(nameof(Mixer));
                }

                OnPropertyChanged(nameof(ActiveMixer));
                OnPropertyChanged(nameof(ActiveMixerSessions));
            });
        }

        internal static bool ShouldApplySettingsTabSave(int selectedTabIndex)
        {
            return selectedTabIndex == 3;
        }

        internal static bool ShouldSaveRoutinesTab(int selectedTabIndex)
        {
            return selectedTabIndex == 2;
        }

        internal void MarkSwitchOverlayShown(bool output)
        {
            SuppressConnectedHotplugOverlay(output);
        }

        internal void SuppressConnectedHotplugOverlay(bool output)
        {
            SuppressConnectedHotplugOverlay(output, RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs);
        }

        internal void SuppressConnectedHotplugOverlay(bool output, int suppressMs)
        {
            long suppressUntilTicks = DateTime.UtcNow
                .AddMilliseconds(Math.Max(0, suppressMs))
                .Ticks;

            if (output)
            {
                Interlocked.Exchange(ref _suppressHotplugOutputConnectedUntilUtcTicks, suppressUntilTicks);
                return;
            }

            Interlocked.Exchange(ref _suppressHotplugInputConnectedUntilUtcTicks, suppressUntilTicks);
        }

        internal bool ShouldSuppressConnectedHotplugOverlay(bool output)
        {
            long suppressUntilTicks = output
                ? Interlocked.Read(ref _suppressHotplugOutputConnectedUntilUtcTicks)
                : Interlocked.Read(ref _suppressHotplugInputConnectedUntilUtcTicks);

            return suppressUntilTicks > DateTime.UtcNow.Ticks;
        }

        internal static int ResolveNextSettingsTabIndex(int currentTabIndex)
        {
            return (currentTabIndex + 1) % 4;
        }

        private void SwitchToNextSettingsTab()
        {
            SelectedSettingsTabIndex = ResolveNextSettingsTabIndex(SelectedSettingsTabIndex);
        }

        private void SelectSettingsTab(object? parameter)
        {
            int? tabIndex = ResolveSettingsTabIndex(parameter);
            if (tabIndex.HasValue)
            {
                SelectedSettingsTabIndex = tabIndex.Value;
            }
        }

        internal static int? ResolveSettingsTabIndex(object? parameter)
        {
            int candidate;
            if (parameter is int integer)
            {
                candidate = integer;
            }
            else if (parameter is string text && int.TryParse(text, out int parsed))
            {
                candidate = parsed;
            }
            else
            {
                return null;
            }

            return candidate is >= 0 and < 4 ? candidate : null;
        }

        private void OnOutputCycleDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SelectedAvailableOutputIndex));
            QueueAutoSave(nameof(OutputCycleDevices));
        }

        private void OnInputCycleDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SelectedAvailableInputIndex));
            QueueAutoSave(nameof(InputCycleDevices));
        }

        public int SelectedAvailableOutputIndex
        {
            get => _selectedAvailableOutputIndex;
            set
            {
                if (_selectedAvailableOutputIndex == value) return;
                _selectedAvailableOutputIndex = value;
                OnPropertyChanged(nameof(SelectedAvailableOutputIndex));
            }
        }

        public int SelectedOutputCycleIndex
        {
            get => _selectedOutputCycleIndex;
            set
            {
                if (_selectedOutputCycleIndex == value) return;
                _selectedOutputCycleIndex = value;
                OnPropertyChanged(nameof(SelectedOutputCycleIndex));
            }
        }

        public int SelectedAvailableInputIndex
        {
            get => _selectedAvailableInputIndex;
            set
            {
                if (_selectedAvailableInputIndex == value) return;
                _selectedAvailableInputIndex = value;
                OnPropertyChanged(nameof(SelectedAvailableInputIndex));
            }
        }

        public int SelectedInputCycleIndex
        {
            get => _selectedInputCycleIndex;
            set
            {
                if (_selectedInputCycleIndex == value) return;
                _selectedInputCycleIndex = value;
                OnPropertyChanged(nameof(SelectedInputCycleIndex));
            }
        }

        private void OnAudioSessionCreated(AudioMixerMode mixerMode)
        {
            if (_isCleaningUp)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            QueueRoutineAppOutputLeaseRefresh();

            int queuedSignals = Interlocked.Increment(ref GetPendingSessionCreatedSignalsRef(mixerMode));

            if (!_isWindowVisible)
            {
                return;
            }

            MixerRefreshTarget refreshTarget = GetMixerRefreshTarget(mixerMode);
            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget activeTarget) || activeTarget != refreshTarget)
            {
                return;
            }

            QueueCoalescedMixerRefresh(
                queuedSignals,
                "Session-created",
                nameof(OnAudioSessionCreated),
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                refreshTarget);
        }

        private void OnAudioSessionLifecycleChanged(AudioSessionLifecycleSignal signal)
        {
            if (_isCleaningUp)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            int queuedSignals = Interlocked.Increment(ref GetPendingSessionLifecycleSignalsRef(signal.MixerMode));

            if (signal.Kind == AudioSessionLifecycleSignalKind.VolumeChanged
                || signal.Kind == AudioSessionLifecycleSignalKind.StateChanged
                || signal.Kind == AudioSessionLifecycleSignalKind.Disconnected)
            {
                _audio?.InvalidateRecentMixerSnapshotState();
            }

            if (signal.Kind == AudioSessionLifecycleSignalKind.EndpointVolumeChanged
                && signal.VolumePercent is float endpointVolumePercent
                && signal.IsMuted is bool endpointMuted)
            {
                QueueEndpointStateProjection(
                    signal.MixerMode,
                    signal.EndpointId,
                    endpointVolumePercent,
                    endpointMuted,
                    "notification");
            }
            else if (signal.Kind == AudioSessionLifecycleSignalKind.VolumeChanged)
            {
                QueueSessionStateProjection(signal);
            }

            if (!_isWindowVisible)
            {
                return;
            }

            if (signal.Kind == AudioSessionLifecycleSignalKind.EndpointVolumeChanged)
            {
                RunBackgroundWork(_ => UpdateMuteFlagsFromSystem($"session-lifecycle:{signal.MixerMode}:endpoint"), nameof(OnAudioSessionLifecycleChanged));
            }

            MixerRefreshTarget refreshTarget = GetMixerRefreshTarget(signal.MixerMode);
            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget activeTarget))
            {
                return;
            }

            bool shouldRefreshVisibleTarget = activeTarget == refreshTarget
                || signal.Kind == AudioSessionLifecycleSignalKind.EndpointVolumeChanged
                || signal.AffectsSharedRows;
            if (!shouldRefreshVisibleTarget)
            {
                return;
            }

            QueueCoalescedMixerRefresh(
                queuedSignals,
                "Session-lifecycle",
                nameof(OnAudioSessionLifecycleChanged),
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                activeTarget);
        }

        private void OnDefaultAudioDeviceChanged(DataFlow flow, Role role)
        {
            if (_isCleaningUp)
            {
                return;
            }

            AudioMixerMode mode = flow == DataFlow.Capture ? AudioMixerMode.Input : AudioMixerMode.Output;
            string context = $"default-device:{flow}:{role}";
            _mixer?.MarkActivationRefreshStale(context);
            _inputMixer?.MarkActivationRefreshStale(context);

            RunBackgroundWork(
                _ => UpdateMuteFlagsFromSystem(context),
                nameof(OnDefaultAudioDeviceChanged));

            int queuedSignals = Interlocked.Increment(ref GetPendingSessionLifecycleSignalsRef(mode));
            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget activeTarget))
            {
                return;
            }

            QueueCoalescedMixerRefresh(
                queuedSignals,
                "Default-device-changed",
                nameof(OnDefaultAudioDeviceChanged),
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                activeTarget);
        }

        private void QueueShowWindowMixerRefresh(MixerRefreshTarget refreshTarget)
        {
            if (_isCleaningUp)
            {
                return;
            }

            int queuedSignals = Interlocked.Increment(ref _pendingShowWindowMixerRefreshSignals);

            QueueCoalescedMixerRefresh(
                queuedSignals,
                "Show-window",
                nameof(ShowWindow),
                RuntimeTuningConfig.ShowWindowMixerRefreshDebounceMs,
                refreshTarget);
        }

        private void QueueCoalescedMixerRefresh(
            int queuedSignals,
            string signalLabel,
            string operationName,
            int debounceMs,
            MixerRefreshTarget refreshTarget)
        {
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                nextDebounce => SwapSessionRefreshDebounce(ref _sessionRefreshDebounceCts, nextDebounce));
            MarkPendingMixerRestoreQueueWork();

            bool queued = TryRunBackgroundWork(async shutdownToken =>
            {
                try
                {
                    await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                        nextDebounceCts,
                        ownedDebounce => ReleaseOwnedDebounce(ref _sessionRefreshDebounceCts, ownedDebounce),
                        linkedToken => AppMixerRefreshCoordinator.ExecuteSessionCreatedRefreshAsync(
                            new SessionCreatedMixerRefreshInput(queuedSignals, debounceMs, signalLabel, refreshTarget),
                            new SessionCreatedMixerRefreshDependencies(
                                DrainPendingMixerRefreshSignals,
                                () => _shell.IsWindowVisible,
                                () => _isCleaningUp,
                                IsMixerRefreshInProgress,
                                WaitForMixerRefreshSettlementAsync,
                                AppConstants.Timing.ShutdownStepTimeoutMs,
                                RefreshMixerAsync),
                            _logger,
                            linkedToken),
                        shutdownToken);
                }
                finally
                {
                    CompletePendingMixerRestoreQueueWork();
                }
            }, operationName);

            if (queued)
            {
                return;
            }

            CompletePendingMixerRestoreQueueWork();
            CancellationTokenSource? detachedDebounce = CancelAndDetachDebounce(ref _sessionRefreshDebounceCts);
            detachedDebounce?.Dispose();
        }

        internal Task WaitForMixerRefreshSettlementAsync(CancellationToken cancellationToken)
        {
            return WaitForMixerRefreshSettlementAsync(MixerRefreshTarget.Both, cancellationToken);
        }

        internal bool HasPendingMixerRestoreWork()
        {
            int pendingSessionCreatedSignals =
                Interlocked.CompareExchange(ref _pendingOutputSessionCreatedSignals, 0, 0) +
                Interlocked.CompareExchange(ref _pendingInputSessionCreatedSignals, 0, 0);
            int pendingSessionLifecycleSignals =
                Interlocked.CompareExchange(ref _pendingOutputSessionLifecycleSignals, 0, 0) +
                Interlocked.CompareExchange(ref _pendingInputSessionLifecycleSignals, 0, 0);
            int pendingShowWindowMixerRefreshSignals =
                Interlocked.CompareExchange(ref _pendingShowWindowMixerRefreshSignals, 0, 0);

            return HasPendingMixerRefreshSignals(
                    pendingSessionCreatedSignals,
                    pendingSessionLifecycleSignals,
                    pendingShowWindowMixerRefreshSignals)
                || Interlocked.CompareExchange(ref _pendingMixerRestoreQueueCount, 0, 0) > 0
                || Volatile.Read(ref _sessionRefreshDebounceCts) != null
                || IsMixerRefreshInProgress(MixerRefreshTarget.Both);
        }

        private void MarkPendingMixerRestoreQueueWork()
        {
            if (Interlocked.Increment(ref _pendingMixerRestoreQueueCount) != 1)
            {
                return;
            }

            lock (_mixerRestoreQueueLock)
            {
                if (_pendingMixerRestoreQueueCount > 0 && _mixerRestoreQueueIdleTcs.Task.IsCompleted)
                {
                    _mixerRestoreQueueIdleTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        private void CompletePendingMixerRestoreQueueWork()
        {
            if (Interlocked.Decrement(ref _pendingMixerRestoreQueueCount) != 0)
            {
                return;
            }

            lock (_mixerRestoreQueueLock)
            {
                if (_pendingMixerRestoreQueueCount == 0)
                {
                    _mixerRestoreQueueIdleTcs.TrySetResult(null);
                }
            }
        }

        private Task GetPendingMixerRestoreQueueIdleTask()
        {
            if (Interlocked.CompareExchange(ref _pendingMixerRestoreQueueCount, 0, 0) == 0)
            {
                return Task.CompletedTask;
            }

            lock (_mixerRestoreQueueLock)
            {
                return _pendingMixerRestoreQueueCount == 0
                    ? Task.CompletedTask
                    : _mixerRestoreQueueIdleTcs.Task;
            }
        }

        internal async Task WaitForMixerRestoreReadinessAsync(CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(AppConstants.Timing.ShutdownStepTimeoutMs);

            while (!cancellationToken.IsCancellationRequested)
            {
                bool isRefreshInProgress = IsMixerRefreshInProgress(MixerRefreshTarget.Both);
                if (!HasPendingMixerRestoreWork())
                {
                    return;
                }

                if (isRefreshInProgress)
                {
                    await WaitForMixerRefreshSettlementAsync(cancellationToken);
                    continue;
                }

                Task pendingQueueIdleTask = GetPendingMixerRestoreQueueIdleTask();
                if (pendingQueueIdleTask.IsCompleted)
                {
                    return;
                }

                TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                try
                {
                    await pendingQueueIdleTask.WaitAsync(remaining, cancellationToken);
                }
                catch (TimeoutException)
                {
                    return;
                }
            }
        }

        private async Task RefreshMixerAsync(bool interactive)
        {
            if (TryGetSelectedMixerRefreshTarget(out MixerRefreshTarget target))
            {
                await RefreshMixerAsync(target, interactive);
                return;
            }

            await RefreshMixerAsync(MixerRefreshTarget.Both, interactive);
        }

        internal void HandleWindowVisibilityChanged(bool isVisible)
        {
            if (_isCleaningUp)
            {
                return;
            }

            if (_hasHandledWindowVisibilityChange && _isWindowVisible == isVisible)

            {

                return;

            }

            _isWindowVisible = isVisible;
            _hasHandledWindowVisibilityChange = true;

            if (isVisible && !_windowState.IsStartupVisibilityResolved && !_windowState.HasInteractiveShowRequest)
            {
                return;
            }

            UpdateRoutineLastRunRefreshTimerState(
                isVisible ? "window-visible" : "window-hidden",
                forceRefresh: isVisible && IsRoutinesTabActive);

            UpdateMixerSessionMonitoringState(isVisible ? "window-visible" : "window-hidden");

            if (!isVisible)
            {
                RequestStopAudioEndpointTest(AudioEndpointTestStopReason.WindowHidden);
                _deviceCache.TrimDiagnosticsForHiddenMode();
                AudioDeviceHelper.TrimCachesForHiddenMode();
                TrimMixersForIdleState("window-hidden");
                Services.UI.AppIconImageProvider.ClearCache();
                if (_audio is { } audio)
                {
                    audio.InvalidateRecentMixerSnapshotState();
                    Task pauseTask = audio.PauseIdleSessionCacheCleanupAsync();
                    RunBackgroundWork(
                        _ => pauseTask,
                        "pause-hidden-session-cache-cleanup");
                }
                return;
            }

            if (_audio is { } visibleAudio)
            {
                _ = visibleAudio.ResumeIdleSessionCacheCleanupAsync();
            }

            RequestSteamBigPictureFallbackRevalidation();

            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget target))
            {
                return;
            }

            if (HasPendingMixerRefreshSignals(target))
            {
                QueueShowWindowMixerRefresh(target);
                return;
            }

            if (!ShouldRefreshMixerOnActivation(target))
            {
                return;
            }

            RunBackgroundWork(async shutdownToken =>
            {
                try
                {
                    if (IsMixerRefreshInProgress(target))
                    {
                        await WaitForMixerRefreshSettlementAsync(target, shutdownToken);
                    }

                    if (_isCleaningUp || !_shell.IsWindowVisible || !TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget currentTarget) || currentTarget != target)
                    {
                        return;
                    }

                    await RefreshMixerAsync(target, interactive: true);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", "visible-window-refresh-failed", nameof(HandleWindowVisibilityChanged), ex);
                }
            }, nameof(HandleWindowVisibilityChanged));
        }

        internal static bool HasPendingMixerRefreshSignals(
            int pendingSessionCreatedSignals,
            int pendingSessionLifecycleSignals,
            int pendingShowWindowMixerRefreshSignals)
        {
            return pendingSessionCreatedSignals > 0
                || pendingSessionLifecycleSignals > 0
                || pendingShowWindowMixerRefreshSignals > 0;
        }

        internal static bool IsInputSettingsTab(int selectedTabIndex)
        {
            return selectedTabIndex == 1;
        }

        private bool IsMixerRefreshInProgress(MixerRefreshTarget target)
        {
            return target switch
            {
                MixerRefreshTarget.Output => TryGetMixer(AudioMixerMode.Output)?.IsRefreshInProgress == true,
                MixerRefreshTarget.Input => TryGetMixer(AudioMixerMode.Input)?.IsRefreshInProgress == true,
                _ => TryGetMixer(AudioMixerMode.Output)?.IsRefreshInProgress == true
                    || TryGetMixer(AudioMixerMode.Input)?.IsRefreshInProgress == true,
            };
        }

        private Task WaitForMixerRefreshSettlementAsync(MixerRefreshTarget target, CancellationToken cancellationToken)
        {
            return target switch
            {
                MixerRefreshTarget.Output => TryGetMixer(AudioMixerMode.Output)?.WaitForRefreshSettlementAsync(cancellationToken) ?? Task.CompletedTask,
                MixerRefreshTarget.Input => TryGetMixer(AudioMixerMode.Input)?.WaitForRefreshSettlementAsync(cancellationToken) ?? Task.CompletedTask,
                _ => WaitForAllMixerRefreshSettlementAsync(cancellationToken),
            };
        }

        private async Task WaitForAllMixerRefreshSettlementAsync(CancellationToken cancellationToken)
        {
            Task? outputSettlementTask = TryGetMixer(AudioMixerMode.Output)?.WaitForRefreshSettlementAsync(cancellationToken);
            Task? inputSettlementTask = TryGetMixer(AudioMixerMode.Input)?.WaitForRefreshSettlementAsync(cancellationToken);

            if (outputSettlementTask == null && inputSettlementTask == null)
            {
                return;
            }

            if (outputSettlementTask == null)
            {
                await inputSettlementTask!;
                return;
            }

            if (inputSettlementTask == null)
            {
                await outputSettlementTask;
                return;
            }

            await Task.WhenAll(outputSettlementTask, inputSettlementTask);
        }

        private async Task RefreshMixerAsync(MixerRefreshTarget target, bool interactive)
        {
            string context = $"refresh:{target}:{(interactive ? "interactive" : "background")}";

            switch (target)
            {
                case MixerRefreshTarget.Output:
                    await EnsureMixerInitializedAsync(AudioMixerMode.Output, context);
                    await RefreshMixerAsync(_mixer!, interactive);
                    break;
                case MixerRefreshTarget.Input:
                    await EnsureMixerInitializedAsync(AudioMixerMode.Input, context);
                    await RefreshMixerAsync(_inputMixer!, interactive);
                    break;
                default:
                    await EnsureMixerInitializedAsync(AudioMixerMode.Output, context);
                    await EnsureMixerInitializedAsync(AudioMixerMode.Input, context);
                    await Task.WhenAll(
                        RefreshMixerAsync(_mixer!, interactive),
                        RefreshMixerAsync(_inputMixer!, interactive));
                    break;
            }
        }

        private static Task RefreshMixerAsync(MixerViewModel mixer, bool interactive)
        {
            return mixer.RefreshAsync(interactive);
        }

        private void UpdateMixerSessionMonitoringState(string context)
        {
            SetMixerSessionMonitoringMode(GetDesiredMixerSessionMonitoringMode(), context);
        }

        private void TrimMixersForIdleState(string context)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AppViewModel", () => $"mixer-idle-trim | context={context}");
            }

            _mixer?.TrimIdleState();
            _inputMixer?.TrimIdleState();

            OnPropertyChanged(nameof(ActiveMixerSessions));
        }

        private AudioMixerMode? GetDesiredMixerSessionMonitoringMode()
        {
            if (!_isWindowVisible)
            {
                return null;
            }

            return TryGetSelectedMixerMode(SelectedSettingsTabIndex, out AudioMixerMode mode)
                ? mode
                : null;
        }

        private void SetMixerSessionMonitoringMode(AudioMixerMode? desiredMode, string context)
        {
            AudioMixerMode? previousMode = _mixerSessionMonitoringMode;
            if (previousMode == desiredMode)
            {
                return;
            }

            _mixerSessionMonitoringMode = desiredMode;

            if (_audio == null)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug) && ShouldLogMixerSessionMonitoringState(context))
            {
                _logger.Debug(
                    "AppViewModel",
                    () => $"mixer-session-monitoring-state | previous={previousMode?.ToString() ?? "None"} current={desiredMode?.ToString() ?? "None"} context={context}");
            }

            if (previousMode.HasValue && previousMode != desiredMode)
            {
                TryGetMixer(previousMode.Value)?.MarkActivationRefreshStale(context);
            }

            bool wasActive = previousMode.HasValue;
            bool isActive = desiredMode.HasValue;

            if (wasActive && !isActive)
            {
                _audio.ReleaseSessionMonitoring(AudioMixerMode.Output);
                _audio.ReleaseSessionMonitoring(AudioMixerMode.Input);
                return;
            }

            if (!wasActive && isActive)
            {
                _audio.AcquireSessionMonitoring(AudioMixerMode.Output);
                _audio.AcquireSessionMonitoring(AudioMixerMode.Input);
            }
        }

        private static MixerRefreshTarget GetMixerRefreshTarget(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input ? MixerRefreshTarget.Input : MixerRefreshTarget.Output;
        }

        private static bool ShouldLogLazyMixerInitialization(string context)
        {
            return !context.StartsWith("refresh:", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(context, "show-window", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(context, "start-hidden-to-tray", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldLogMixerSessionMonitoringState(string context)
        {
            return !string.Equals(context, "show-window", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(context, "start-hidden-to-tray", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetSelectedMixerMode(int selectedTabIndex, out AudioMixerMode mode)
        {
            switch (selectedTabIndex)
            {
                case 0:
                    mode = AudioMixerMode.Output;
                    return true;
                case 1:
                    mode = AudioMixerMode.Input;
                    return true;
                default:
                    mode = AudioMixerMode.Output;
                    return false;
            }
        }

        private bool TryGetSelectedMixerRefreshTarget(out MixerRefreshTarget target)
        {
            if (TryGetSelectedMixerMode(SelectedSettingsTabIndex, out AudioMixerMode mode))
            {
                target = GetMixerRefreshTarget(mode);
                return true;
            }

            target = MixerRefreshTarget.Both;
            return false;
        }

        private bool TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget target)
        {
            if (!_isWindowVisible)
            {
                target = MixerRefreshTarget.Both;
                return false;
            }

            return TryGetSelectedMixerRefreshTarget(out target);
        }

        private bool HasPendingMixerRefreshSignals(MixerRefreshTarget target)
        {
            int showWindowSignals = Interlocked.CompareExchange(ref _pendingShowWindowMixerRefreshSignals, 0, 0);

            return target switch
            {
                MixerRefreshTarget.Output => HasPendingMixerRefreshSignals(
                    Interlocked.CompareExchange(ref _pendingOutputSessionCreatedSignals, 0, 0),
                    Interlocked.CompareExchange(ref _pendingOutputSessionLifecycleSignals, 0, 0),
                    showWindowSignals),
                MixerRefreshTarget.Input => HasPendingMixerRefreshSignals(
                    Interlocked.CompareExchange(ref _pendingInputSessionCreatedSignals, 0, 0),
                    Interlocked.CompareExchange(ref _pendingInputSessionLifecycleSignals, 0, 0),
                    showWindowSignals),
                _ => HasPendingMixerRefreshSignals(
                    Interlocked.CompareExchange(ref _pendingOutputSessionCreatedSignals, 0, 0)
                        + Interlocked.CompareExchange(ref _pendingInputSessionCreatedSignals, 0, 0),
                    Interlocked.CompareExchange(ref _pendingOutputSessionLifecycleSignals, 0, 0)
                        + Interlocked.CompareExchange(ref _pendingInputSessionLifecycleSignals, 0, 0),
                    showWindowSignals),
            };
        }

        private bool ShouldRefreshMixerOnActivation(MixerRefreshTarget target)
        {
            return target switch
            {
                MixerRefreshTarget.Output => _mixer == null || (_mixer.Sessions?.Count ?? 0) == 0 || _mixer.RequiresActivationRefresh,
                MixerRefreshTarget.Input => _inputMixer == null || (_inputMixer.Sessions?.Count ?? 0) == 0 || _inputMixer.RequiresActivationRefresh,
                _ => _mixer == null
                    || _inputMixer == null
                    || (_mixer.Sessions?.Count ?? 0) == 0
                    || (_inputMixer.Sessions?.Count ?? 0) == 0
                    || _mixer.RequiresActivationRefresh
                    || _inputMixer.RequiresActivationRefresh,
            };
        }

        private void QueueVisibleMixerActivationRefresh(string context)
        {
            if (_isCleaningUp || !TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget target))
            {
                return;
            }

            if (HasPendingMixerRefreshSignals(target))
            {
                QueueShowWindowMixerRefresh(target);
                return;
            }

            if (!ShouldRefreshMixerOnActivation(target))
            {
                return;
            }

            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                nextDebounce => SwapSessionRefreshDebounce(ref _visibleMixerActivationRefreshDebounceCts, nextDebounce));

            bool queued = TryRunBackgroundWork(async shutdownToken =>
            {
                try
                {
                    await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                        nextDebounceCts,
                        ownedDebounce => ReleaseOwnedDebounce(ref _visibleMixerActivationRefreshDebounceCts, ownedDebounce),
                        RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs,
                        async linkedToken =>
                        {
                            if (IsMixerRefreshInProgress(target))
                            {
                                await WaitForMixerRefreshSettlementAsync(target, linkedToken);
                            }

                            if (_isCleaningUp || !_shell.IsWindowVisible || !TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget currentTarget) || currentTarget != target)
                            {
                                return;
                            }

                            await RefreshMixerAsync(target, interactive: true);
                        },
                        shutdownToken);
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", $"visible-mixer-activation-refresh-failed | context={context}", nameof(QueueVisibleMixerActivationRefresh), ex);
                }
            }, nameof(QueueVisibleMixerActivationRefresh));

            if (queued)
            {
                return;
            }

            CancellationTokenSource? detachedDebounce = CancelAndDetachDebounce(ref _visibleMixerActivationRefreshDebounceCts);
            detachedDebounce?.Dispose();
        }

        private ref int GetPendingSessionCreatedSignalsRef(AudioMixerMode mixerMode)
        {
            return ref mixerMode == AudioMixerMode.Input
                ? ref _pendingInputSessionCreatedSignals
                : ref _pendingOutputSessionCreatedSignals;
        }

        private ref int GetPendingSessionLifecycleSignalsRef(AudioMixerMode mixerMode)
        {
            return ref mixerMode == AudioMixerMode.Input
                ? ref _pendingInputSessionLifecycleSignals
                : ref _pendingOutputSessionLifecycleSignals;
        }

        private async Task RefreshAvailableDeviceCollectionsAsync()
        {
            if (_isCleaningUp)
            {
                return;
            }

            var (outputDevices, inputDevices) = await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => GetActiveDeviceInfoSnapshot());

            if (_isCleaningUp)
            {
                return;
            }

            // Refresh available endpoints only; cycle edits and selections can change during enumeration.
            SelectedAvailableOutputIndex = LoadAvailableOutputDevices(
                _outputDevices,
                AvailableOutputDeviceNames,
                outputDevices,
                _selectedAvailableOutputIndex);

            SelectedAvailableInputIndex = LoadAvailableInputDevices(
                _inputDevices,
                AvailableInputDeviceNames,
                inputDevices,
                _selectedAvailableInputIndex);
        }
    }
}
