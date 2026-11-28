using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private long _endpointStateProjectionRevision;
        private long _lastAppliedOutputEndpointStateRevision;
        private long _lastAppliedInputEndpointStateRevision;
        private long _outputEndpointGeneration;
        private long _inputEndpointGeneration;
        private string? _currentOutputEndpointId;
        private string? _currentInputEndpointId;
        private int _currentOutputEndpointRole = (int)Role.Multimedia;
        private int _currentInputEndpointRole = (int)Role.Console;
        private AudioMixerMode _activeMixerMode = AudioMixerMode.Output;

        public int SelectedSettingsTabIndex
        {
            get => _selectedSettingsTabIndex;
            set
            {
                if (_selectedSettingsTabIndex == value) return;
                int previousTabIndex = _selectedSettingsTabIndex;
                TryGetActiveMixer()?.ResetMixerInteractions();
                AudioMixerMode previousMixerMode = _activeMixerMode;
                if (value is 0 or 1)
                    _activeMixerMode = value == 1 ? AudioMixerMode.Input : AudioMixerMode.Output;
                _selectedSettingsTabIndex = value;
                OnPropertyChanged(nameof(SelectedSettingsTabIndex));
                OnPropertyChanged(nameof(IsRoutinesTabActive));
                OnPropertyChanged(nameof(IsSettingsTabActive));
                OnPropertyChanged(nameof(IsDeviceTabsActive));
                OnPropertyChanged(nameof(IsEditorTabsActive));
                OnPropertyChanged(nameof(ActiveDeviceSwitchFeedback));
                if (_activeMixerMode != previousMixerMode)
                {
                    OnPropertyChanged(nameof(ActiveMixer));
                    OnPropertyChanged(nameof(ActiveMixerSessions));
                    OnPropertyChanged(nameof(ActiveMixerHeader));
                    OnPropertyChanged(nameof(ActiveMixerTitle));
                    OnPropertyChanged(nameof(ActiveMixerHelp));
                }
                UpdateRoutineLastRunRefreshTimerState(
                    $"tab-selection:{value}",
                    forceRefresh: IsRoutinesTabActive && _isWindowVisible);
                UpdateMixerSessionMonitoringState($"tab-selection:{value}");
                QueueVisibleMixerActivationRefresh($"tab-selection:{value}");
                if ((previousTabIndex == 0 && value != 0 && (AudioTesting.IsOutputTestStatusVisible || AudioTesting.ActiveOrPendingKind == AudioEndpointTestKind.Output))
                    || (previousTabIndex == 1 && value != 1 && (AudioTesting.IsInputTestPanelVisible || AudioTesting.ActiveOrPendingKind == AudioEndpointTestKind.Input)))
                {
                    RequestStopAudioEndpointTest(AudioEndpointTestStopReason.TabChanged);
                }
            }
        }

        private MixerViewModel? TryGetActiveMixer()
        {
            return _activeMixerMode == AudioMixerMode.Input
                ? _inputMixer
                : _mixer;
        }

        internal bool ClearMixerSelectionIfAny()
        {
            if (TryGetActiveMixer() is not { } mixer || (!mixer.HasSelectedApplications && !mixer.IsSelectingApplications)) return false;
            mixer.ClearApplicationSelection();
            return true;
        }

        internal void RefreshOpenMixerRoutingOnActivation()
        {
            if (!_isCleaningUp && IsDeviceTabsActive) TryGetActiveMixer()?.RefreshApplicationRoutingOnActivation();
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

        private void ApplyEndpointStateToInitializedMixers(AudioMixerMode mode, float volumePercent, bool isMuted, string endpointId,
            MixerViewModel.MixerRowEditState? outputState, MixerViewModel.MixerRowEditState? inputState, bool applyMute)
        {
            MixerViewModel? outputMixer;
            MixerViewModel? inputMixer;

            lock (_mixerInitializationLock)
            {
                outputMixer = _mixer;
                inputMixer = _inputMixer;
            }

            if (outputState.HasValue) outputMixer?.ApplyEndpointStateFromSystem(mode, volumePercent, isMuted, endpointId, outputState, applyMute);
            if (inputState.HasValue && !ReferenceEquals(inputMixer, outputMixer))
            {
                inputMixer?.ApplyEndpointStateFromSystem(mode, volumePercent, isMuted, endpointId, inputState, applyMute);
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
            long endpointGeneration = Interlocked.Read(ref (mode == AudioMixerMode.Input ? ref _inputEndpointGeneration : ref _outputEndpointGeneration));
            long muteRevision = _audio.GetEndpointMuteIntentRevision(mode == AudioMixerMode.Output);
            long muteIntentRevision = Volatile.Read(ref _muteIntentRevision);
            MixerViewModel.MixerRowEditState? outputState = _mixer?.CaptureEndpointEditState(mode);
            MixerViewModel.MixerRowEditState? inputState = _inputMixer?.CaptureEndpointEditState(mode);
            void Apply()
            {
                if (_isCleaningUp || endpointGeneration != Interlocked.Read(ref (mode == AudioMixerMode.Input ? ref _inputEndpointGeneration : ref _outputEndpointGeneration))
                    || !IsCurrentEndpointProjection(mode, endpointId))
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
                AudioSessionItem? outputRow = _mixer?.GetEndpointRow(mode);
                AudioSessionItem? inputRow = _inputMixer?.GetEndpointRow(mode);
                bool muteFeedbackIsStale = (outputRow?.HasPendingMuteEdit == true && outputRow.IsMuted != isMuted
                        && string.Equals(outputRow.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                    || (inputRow?.HasPendingMuteEdit == true && inputRow.IsMuted != isMuted
                        && string.Equals(inputRow.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
                muteFeedbackIsStale |= muteRevision != _audio.GetEndpointMuteIntentRevision(mode == AudioMixerMode.Output)
                    || muteIntentRevision != Volatile.Read(ref _muteIntentRevision)
                    || (outputState.HasValue && (outputState.Value.Item.MuteEditRevision != outputState.Value.MuteRevision || !ReferenceEquals(outputRow, outputState.Value.Item)))
                    || (inputState.HasValue && (inputState.Value.Item.MuteEditRevision != inputState.Value.MuteRevision || !ReferenceEquals(inputRow, inputState.Value.Item)));
                if (!muteFeedbackIsStale && mode == AudioMixerMode.Input)
                {
                    ApplyAuthoritativeMuteFlags(_deafenBackingField || _muteSoundBackingField, isMuted, $"endpoint-state:{source}:input");
                }
                else if (!muteFeedbackIsStale)
                {
                    ApplyAuthoritativeMuteFlags(isMuted, _deafenBackingField || _muteMicBackingField, $"endpoint-state:{source}:output");
                }

                ApplyEndpointStateToInitializedMixers(mode, volumePercent, isMuted, endpointId, outputState, inputState, !muteFeedbackIsStale);
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

        private bool IsCurrentEndpointProjection(AudioMixerMode mode, string endpointId)
        {
            bool input = mode == AudioMixerMode.Input;
            Role role = input ? _audio.RecordingDetectionRole : _audio.PlaybackDetectionRole;
            string? currentId;
            if ((int)role == Volatile.Read(ref (input ? ref _currentInputEndpointRole : ref _currentOutputEndpointRole))
                && Interlocked.Read(ref (input ? ref _inputEndpointGeneration : ref _outputEndpointGeneration)) > 0)
            {
                currentId = Volatile.Read(ref (input ? ref _currentInputEndpointId : ref _currentOutputEndpointId));
                if (currentId == null) return false;
            }
            else currentId = input ? _deviceCache.GetRecordingDeviceIdWithoutRefresh(role) : _deviceCache.GetPlaybackDeviceIdWithoutRefresh(role);
            if (currentId != null) return string.Equals(currentId, endpointId, StringComparison.OrdinalIgnoreCase);
            AudioSessionItem? item = _mixer?.GetEndpointRow(mode) ?? _inputMixer?.GetEndpointRow(mode);
            if (!string.IsNullOrWhiteSpace(item?.EndpointId))
                return string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private void OnPrimaryEndpointIdentityChanged(DataFlow flow, Role role, string? endpointId)
        {
            if (flow == DataFlow.Render && role == _audio.PlaybackDetectionRole)
            {
                Volatile.Write(ref _currentOutputEndpointRole, (int)role);
                Volatile.Write(ref _currentOutputEndpointId, endpointId);
                Interlocked.Increment(ref _outputEndpointGeneration);
            }
            else if (flow == DataFlow.Capture && role == _audio.RecordingDetectionRole)
            {
                Volatile.Write(ref _currentInputEndpointRole, (int)role);
                Volatile.Write(ref _currentInputEndpointId, endpointId);
                Interlocked.Increment(ref _inputEndpointGeneration);
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
                        _mixer.ManualRoutingScope = RunManualApplicationRoutingAsync;
                        createdOrConnected = true;
                    }
                }
                else if (_inputMixer == null)
                {
                    _inputMixer = _inputMixerFactory();
                    _inputMixer.ManualRoutingScope = RunManualApplicationRoutingAsync;
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

        private async Task<ApplicationRoutingChange> RunManualApplicationRoutingAsync(uint processId, bool output, Func<Task<ApplicationRoutingChange>> apply, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken, cancellationToken);
            ApplicationRoutingChange result = default;
            bool applied = await _routineRuntime.Lifetime.ApplyRoutingAsync(() => !_isCleaningUp, async () =>
            {
                var processes = _routineRuntime.Lifetime.HasRoutingOwnership
                    ? await Task.Run(() => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.IncludeParentProcessId), linked.Token)
                    : [];
                linked.Token.ThrowIfCancellationRequested();
                result = await apply();
                if (result.OverridesRoutine) _routineRuntime.Lifetime.MarkManualRouting(processId, output, processes);
                return true;
            }, linked.Token);
            if (!applied) throw new OperationCanceledException(linked.Token);
            return result;
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

        internal static int ResolvePreviousSettingsTabIndex(int currentTabIndex)
        {
            return (currentTabIndex + 3) % 4;
        }

        private void SwitchToNextSettingsTab()
        {
            SelectedSettingsTabIndex = ResolveNextSettingsTabIndex(SelectedSettingsTabIndex);
        }

        private void SwitchToPreviousSettingsTab()
        {
            SelectedSettingsTabIndex = ResolvePreviousSettingsTabIndex(SelectedSettingsTabIndex);
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
                if (!_refreshingOutputDropdown && value >= 0) _disconnectedOutputSelection = null;
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
                if (!_refreshingInputDropdown && value >= 0) _disconnectedInputSelection = null;
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
            _quickMixerMeters?.RequestRefresh(mixerMode);
            if (_isCleaningUp)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            QueueRoutineAppOutputLeaseRefresh();

            int queuedSignals = Interlocked.Increment(ref GetPendingSessionCreatedSignalsRef(mixerMode));

            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget activeTarget))
            {
                return;
            }

            MixerRefreshTarget refreshTarget = GetMixerRefreshTarget(mixerMode);
            if (activeTarget != MixerRefreshTarget.Both && activeTarget != refreshTarget)
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

            if (signal.Kind == AudioSessionLifecycleSignalKind.Disconnected
                || signal is { Kind: AudioSessionLifecycleSignalKind.StateChanged, State: AudioSessionState.AudioSessionStateExpired })
                _quickMixerMeters?.RequestRefresh(signal.MixerMode);

            RequestSteamBigPictureFallbackRevalidation();

            if (signal.Kind == AudioSessionLifecycleSignalKind.VolumeChanged
                || signal.Kind == AudioSessionLifecycleSignalKind.StateChanged
                || signal.Kind == AudioSessionLifecycleSignalKind.Disconnected)
            {
                _audio?.InvalidateRecentMixerSnapshotState();
            }

            if (signal.Kind == AudioSessionLifecycleSignalKind.EndpointVolumeChanged
                && !string.IsNullOrWhiteSpace(signal.EndpointId)
                && signal.VolumePercent is float endpointVolumePercent
                && signal.IsMuted is bool endpointMuted)
            {
                QueueEndpointStateProjection(
                    signal.MixerMode,
                    signal.EndpointId,
                    endpointVolumePercent,
                    endpointMuted,
                    "notification");
                return;
            }
            else if (signal.Kind == AudioSessionLifecycleSignalKind.VolumeChanged)
            {
                QueueSessionStateProjection(signal);
            }

            int queuedSignals = Interlocked.Increment(ref GetPendingSessionLifecycleSignalsRef(signal.MixerMode));

            if (!TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget activeTarget))
            {
                return;
            }

            if (signal.Kind == AudioSessionLifecycleSignalKind.EndpointVolumeChanged)
            {
                RunBackgroundWork(_ => UpdateMuteFlagsFromSystem($"session-lifecycle:{signal.MixerMode}:endpoint"), nameof(OnAudioSessionLifecycleChanged));
            }

            MixerRefreshTarget refreshTarget = GetMixerRefreshTarget(signal.MixerMode);
            bool shouldRefreshVisibleTarget = activeTarget == refreshTarget
                || activeTarget == MixerRefreshTarget.Both
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
            _quickMixerMeters?.RequestRefresh(flow switch
            {
                DataFlow.Render => AudioMixerMode.Output,
                DataFlow.Capture => AudioMixerMode.Input,
                _ => null,
            });
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
            Action settle = TrackQueuedMixerRefresh(refreshTarget);
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                ref _sessionRefreshDebounceCts);

            QueueTrackedMixerRefresh(settle, shutdownToken => AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                nextDebounceCts,
                ownedDebounce => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _sessionRefreshDebounceCts, ownedDebounce),
                linkedToken => AppMixerRefreshCoordinator.ExecuteSessionCreatedRefreshAsync(
                    new SessionCreatedMixerRefreshInput(queuedSignals, debounceMs, signalLabel, refreshTarget),
                    new SessionCreatedMixerRefreshDependencies(
                        DrainPendingMixerRefreshSignals,
                        () => TryGetVisibleMixerRefreshTarget(out _),
                        () => _isCleaningUp,
                        IsMixerRefreshInProgress,
                        WaitForMixerRefreshSettlementAsync,
                        AppConstants.Timing.ShutdownStepTimeoutMs,
                        RefreshVisibleMixerAsync),
                    _logger,
                    linkedToken),
                shutdownToken), operationName, coalescingKey: nameof(QueueCoalescedMixerRefresh),
                onDiscarded: () => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _sessionRefreshDebounceCts, nextDebounceCts));
        }

        internal Task WaitForMixerRefreshSettlementAsync(CancellationToken cancellationToken)
        {
            return WaitForMixerRefreshSettlementAsync(MixerRefreshTarget.Both, cancellationToken);
        }

        private Action TrackQueuedMixerRefresh(MixerRefreshTarget target)
        {
            lock (_mixerRestoreQueueLock)
            {
                if (target != MixerRefreshTarget.Input) _outputMixerRefreshQueue.Begin();
                if (target != MixerRefreshTarget.Output) _inputMixerRefreshQueue.Begin();
            }

            return () =>
            {
                lock (_mixerRestoreQueueLock)
                {
                    if (target != MixerRefreshTarget.Input) _outputMixerRefreshQueue.Complete();
                    if (target != MixerRefreshTarget.Output) _inputMixerRefreshQueue.Complete();
                }
            };
        }

        private void QueueTrackedMixerRefresh(Action settle, Func<CancellationToken, Task> refresh, string operationName,
            string? coalescingKey = null, Action? onDiscarded = null)
        {
            TryRunBackgroundWork(async token =>
            {
                try { await refresh(token); }
                finally { settle(); }
            }, operationName, coalescingKey, onDiscarded: () =>
            {
                try { onDiscarded?.Invoke(); }
                finally { settle(); }
            });
        }

        private Task GetMixerRefreshQueueIdleTask(MixerRefreshTarget target)
        {
            lock (_mixerRestoreQueueLock)
            {
                Task output = _outputMixerRefreshQueue.Idle;
                Task input = _inputMixerRefreshQueue.Idle;
                if (target == MixerRefreshTarget.Output) return output;
                if (target == MixerRefreshTarget.Input || output.IsCompleted) return input;
                if (input.IsCompleted) return output;
                return Task.WhenAll(output, input);
            }
        }

        internal Task WaitForActiveMixerRestoreRefreshAsync(CancellationToken cancellationToken) =>
            WaitForTrackedMixerRefreshAsync(GetMixerRefreshTarget(_activeMixerMode), cancellationToken);

        private async Task WaitForTrackedMixerRefreshAsync(MixerRefreshTarget target, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task queuedWork = GetMixerRefreshQueueIdleTask(target);
                if (!queuedWork.IsCompleted)
                {
                    await queuedWork.WaitAsync(cancellationToken);
                    continue;
                }
                if (!IsMixerRefreshInProgress(target)) return;
                await WaitForMixerRefreshSettlementAsync(target, cancellationToken);
            }
        }

        private async Task WaitForMixerRestoreReadinessAsync(CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(AppConstants.Timing.ShutdownStepTimeoutMs);
            while (!cancellationToken.IsCancellationRequested)
            {
                // Shutdown still joins a refresh that has begun using mixer resources.
                if (IsMixerRefreshInProgress(MixerRefreshTarget.Both))
                {
                    await WaitForMixerRefreshSettlementAsync(cancellationToken);
                    continue;
                }

                Task queuedWork = GetMixerRefreshQueueIdleTask(MixerRefreshTarget.Both);
                TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
                if (queuedWork.IsCompleted || remaining <= TimeSpan.Zero) return;
                try { await queuedWork.WaitAsync(remaining, cancellationToken); }
                catch (TimeoutException) { return; }
            }
        }

        // Access is serialized by the view model's mixer restore queue lock.
        private sealed class MixerRefreshQueue
        {
            private int _pending;
            private TaskCompletionSource? _settlement;

            public Task Idle => _settlement?.Task ?? Task.CompletedTask;

            public void Begin()
            {
                if (_pending++ == 0) _settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void Complete()
            {
                if (--_pending != 0) return;
                _settlement!.SetResult();
                _settlement = null;
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

        private CancellationTokenSource? _windowRestoreRefreshCts;

        internal void HandleWindowVisibilityChanged(bool isVisible)
        {
            if (_isCleaningUp || (isVisible && IsRuntimeShutdownRequested))
            {
                return;
            }

            if (_hasHandledWindowVisibilityChange && _isWindowVisible == isVisible)
            {
                return;
            }

            _isWindowVisible = isVisible;
            _hasHandledWindowVisibilityChange = true;
            if (!isVisible)
            {
                AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _windowRestoreRefreshCts);
                _mixer?.ResetMixerInteractions();
                _inputMixer?.ResetMixerInteractions();
                ClearDeviceSwitchFeedback();
            }
            if (isVisible)
            {
                _mixer?.CancelIdleTrim();
                _inputMixer?.CancelIdleTrim();
            }

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
                Volatile.Write(ref _skipNextVisibleWindowRestoreRefresh, 0);
                RequestStopAudioEndpointTest(AudioEndpointTestStopReason.WindowHidden);
                _deviceCache.TrimDiagnosticsForHiddenMode();
                AudioDeviceHelper.TrimCachesForHiddenMode();
                TrimMixersForIdleState("window-hidden");
                if (!IsRuntimeShutdownRequested && !TryGetVisibleMixerRefreshTarget(out _) && _audio is { } audio)
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
            if (Interlocked.Exchange(ref _skipNextVisibleWindowRestoreRefresh, 0) == 0)
            {
                QueueWindowRestoreRefresh();
            }

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

            QueueTrackedMixerRefresh(TrackQueuedMixerRefresh(target), async shutdownToken =>
            {
                try
                {
                    if (IsMixerRefreshInProgress(target))
                    {
                        await WaitForMixerRefreshSettlementAsync(target, shutdownToken);
                    }

                    if (_isCleaningUp || !TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget currentTarget) || currentTarget != target)
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

        private void QueueWindowRestoreRefresh()
        {
            CancellationTokenSource source = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _windowRestoreRefreshCts);
            RunBackgroundWork(shutdownToken => AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                source,
                owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _windowRestoreRefreshCts, owned),
                token => InvokeOnDispatcherAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    await Task.WhenAll(_deviceCache.RefreshAsync(),
                        RefreshAvailableDeviceCollectionsAsync(token), UpdateMuteFlagsFromSystem("show-window"));
                }),
                shutdownToken),
                nameof(QueueWindowRestoreRefresh), coalescingKey: nameof(QueueWindowRestoreRefresh),
                onDiscarded: () => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _windowRestoreRefreshCts, source));
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
                    await _mixer!.RefreshAsync(interactive);
                    break;
                case MixerRefreshTarget.Input:
                    await EnsureMixerInitializedAsync(AudioMixerMode.Input, context);
                    await _inputMixer!.RefreshAsync(interactive);
                    break;
                default:
                    await EnsureMixerInitializedAsync(AudioMixerMode.Output, context);
                    await EnsureMixerInitializedAsync(AudioMixerMode.Input, context);
                    await Task.WhenAll(
                        _mixer!.RefreshAsync(interactive),
                        _inputMixer!.RefreshAsync(interactive));
                    break;
            }
        }

        private void UpdateMixerSessionMonitoringState(string context)
        {
            SetMixerSessionMonitoringTarget(TryGetVisibleMixerRefreshTarget(out var target) ? target : null, context);
        }

        private void TrimMixersForIdleState(string context)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AppViewModel", () => $"mixer-idle-trim | context={context}");
            }

            AudioMeterModes demanded = GetVisibleMixerModes();
            if ((demanded & AudioMeterModes.Output) == 0) _mixer?.TrimIdleState();
            if ((demanded & AudioMeterModes.Input) == 0) _inputMixer?.TrimIdleState();

            OnPropertyChanged(nameof(ActiveMixerSessions));
        }

        private void SetMixerSessionMonitoringTarget(MixerRefreshTarget? desiredMode, string context)
        {
            MixerRefreshTarget? previousMode = _mixerSessionMonitoringTarget;
            if (previousMode == desiredMode)
            {
                return;
            }

            _mixerSessionMonitoringTarget = desiredMode;

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
                if (previousMode != MixerRefreshTarget.Input) _mixer?.MarkActivationRefreshStale(context);
                if (previousMode != MixerRefreshTarget.Output) _inputMixer?.MarkActivationRefreshStale(context);
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
            AudioMeterModes modes = GetVisibleMixerModes();
            target = modes switch
            {
                AudioMeterModes.Output => MixerRefreshTarget.Output,
                AudioMeterModes.Input => MixerRefreshTarget.Input,
                _ => MixerRefreshTarget.Both
            };
            return modes != AudioMeterModes.None;
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

            Action settle = TrackQueuedMixerRefresh(target);
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                ref _visibleMixerActivationRefreshDebounceCts);

            QueueTrackedMixerRefresh(settle, async shutdownToken =>
            {
                try
                {
                    await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                        nextDebounceCts,
                        ownedDebounce => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _visibleMixerActivationRefreshDebounceCts, ownedDebounce),
                        RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs,
                        async linkedToken =>
                        {
                            if (IsMixerRefreshInProgress(target))
                            {
                                await WaitForMixerRefreshSettlementAsync(target, linkedToken);
                            }

                            if (_isCleaningUp || !TryGetVisibleMixerRefreshTarget(out MixerRefreshTarget currentTarget) || currentTarget != target)
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
            }, nameof(QueueVisibleMixerActivationRefresh), coalescingKey: nameof(QueueVisibleMixerActivationRefresh),
                onDiscarded: () => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _visibleMixerActivationRefreshDebounceCts, nextDebounceCts));
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

        private bool _hasDeviceInventorySnapshot;
        private bool _outputInventoryComplete;
        private bool _inputInventoryComplete;

        public bool IsOutputDeviceInventoryKnownEmpty => _hasDeviceInventorySnapshot && _outputInventoryComplete && _outputDevices.Count == 0;
        public bool IsInputDeviceInventoryKnownEmpty => _hasDeviceInventorySnapshot && _inputInventoryComplete && _inputDevices.Count == 0;

        /// <summary>Refreshes available endpoints while preserving cycle edits and selections made during enumeration.</summary>
        internal async Task RefreshAvailableDeviceCollectionsAsync(CancellationToken cancellationToken, bool allowUnavailableInventory = false)
        {
            if (_isCleaningUp || IsRuntimeShutdownRequested)
            {
                return;
            }

            AudioDeviceInventory outputDevices;
            AudioDeviceInventory inputDevices;
            try
            {
                (outputDevices, inputDevices) = await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                    () => (ReadInventory(output: true), ReadInventory(output: false)), cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && !_isCleaningUp && !IsRuntimeShutdownRequested)
            {
                if (_hasDeviceInventorySnapshot)
                {
                    _hasDeviceInventorySnapshot = false;
                    OnPropertyChanged(nameof(IsOutputDeviceInventoryKnownEmpty));
                    OnPropertyChanged(nameof(IsInputDeviceInventoryKnownEmpty));
                    (SwitchToSelectedDeviceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_isCleaningUp || IsRuntimeShutdownRequested)
            {
                return;
            }

            ApplyAvailableDeviceInventory(outputDevices, inputDevices);

            AudioDeviceInventory ReadInventory(bool output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return _audio.ReadActiveDeviceInventory(output); }
                catch (COMException) when (allowUnavailableInventory)
                {
                    // Enumeration already logged the failure. Keep the other flow usable at startup.
                    return new AudioDeviceInventory([], isComplete: false);
                }
            }
        }

        private void ApplyAvailableDeviceInventory(AudioDeviceInventory outputDevices, AudioDeviceInventory inputDevices)
        {
            bool outputWasKnownEmpty = IsOutputDeviceInventoryKnownEmpty;
            bool inputWasKnownEmpty = IsInputDeviceInventoryKnownEmpty;

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

            _hasDeviceInventorySnapshot = true;
            _outputInventoryComplete = outputDevices.IsComplete;
            _inputInventoryComplete = inputDevices.IsComplete;
            if (outputWasKnownEmpty != IsOutputDeviceInventoryKnownEmpty) OnPropertyChanged(nameof(IsOutputDeviceInventoryKnownEmpty));
            if (inputWasKnownEmpty != IsInputDeviceInventoryKnownEmpty) OnPropertyChanged(nameof(IsInputDeviceInventoryKnownEmpty));
            (SwitchToSelectedDeviceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
