using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private bool _runAtStartup;
        private bool _isInitializing;
        private bool _deafenBackingField;
        private bool _muteMicBackingField;
        private bool _muteSoundBackingField;
        private long _muteStateRevision;
        private readonly Lock _muteApplyLock = new();
        private PendingMuteStateApply? _pendingMuteStateApply;
        private bool _muteApplyProcessorRunning;
        private bool _preserveAudioLevelsBackingField;
        private bool _overlayEnabledBackingField = true;
        private OverlayPosition _overlayPositionBackingField = OverlayPosition.TopRight;
        private string _overlayDurationSecondsTextBackingField = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0");
        private bool _settingsAutoSaveEnabledDraft;
        private bool _settingsPlayDialogSoundsDraft = true;
        private bool _settingsRunAtStartupDraft;
        private AppTheme _settingsThemeDraft = AppTheme.System;
        private bool _settingsPreserveAudioLevelsDraft = true;
        private bool _settingsAutoScrollToMixerOnRestoreDraft = true;
        private bool _settingsOverlayEnabledDraft = true;
        private bool _settingsBluetoothReconnectEnabledDraft = true;
        private DeviceReferenceFileMode _settingsDeviceReferenceFileModeDraft = DeviceReferenceFileMode.Off;
        private LogLevel _settingsLogLevelDraft = LogLevel.Info;
        private bool _settingsRedactLogContentDraft = true;
        private bool _settingsOutputRoleMultimediaDraft = true;
        private bool _settingsOutputRoleCommunicationsDraft = true;
        private bool _settingsOutputRoleConsoleDraft = true;
        private bool _settingsInputRoleMultimediaDraft = true;
        private bool _settingsInputRoleCommunicationsDraft = true;
        private bool _settingsInputRoleConsoleDraft = true;
        private OverlayPosition _settingsOverlayPositionDraft = OverlayPosition.TopRight;
        private string _settingsOverlayDurationSecondsDraft = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0");
        private string _settingsMasterVolumeStepPercentDraft = "5";
        private string _settingsMicVolumeStepPercentDraft = "5";
        private string _settingsListenMonitorOutputDeviceIdDraft = string.Empty;
        private string _settingsListenMonitorOutputDeviceNameDraft = string.Empty;
        private bool _settingsMasterVolumeControlsExpanded = true;
        private bool _settingsMicVolumeControlsExpanded;
        private bool _isApplyingSettings;
        private bool _outputHotkeysEnabledBackingField = true;
        private bool _inputHotkeysEnabledBackingField = true;
        private readonly HashSet<string> _hotkeyConflictKeys = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _autoSaveDebounceCts;
        private CancellationTokenSource? _startupDebounceCts;
        private CancellationTokenSource? _sessionRefreshDebounceCts;
        private CancellationTokenSource? _visibleMixerActivationRefreshDebounceCts;
        private CancellationTokenSource? _steamBigPictureDebounceCts;
        private CancellationTokenSource? _steamBigPictureConfirmationDebounceCts;
        private CancellationTokenSource? _steamBigPictureFallbackMaintenanceCts;
        private int _autoSaveSuppressionCount;
        private long _autoSaveDirtyRevision;
        private int _pendingOutputSessionCreatedSignals;
        private int _pendingInputSessionCreatedSignals;
        private int _pendingOutputSessionLifecycleSignals;
        private int _pendingInputSessionLifecycleSignals;
        private int _pendingShowWindowMixerRefreshSignals;
        private int _pendingSteamBigPictureSignals;
        private readonly Lock _deviceReferenceFingerprintLock = new();
        private string _lastDeviceReferenceFingerprint = string.Empty;
        private DateTime _lastSettingsWriteTime;
        private AudioMixerMode? _mixerSessionMonitoringMode;
        private bool _hasHandledWindowVisibilityChange;
        private static readonly string[] MuteStatePropertyNames = [nameof(Deafen), nameof(MuteMic), nameof(MuteSound)];

        private readonly record struct PendingMuteStateApply(
            long Revision,
            bool MuteMicrophone,
            bool MutePlayback,
            bool PreviousDeafen,
            bool PreviousMuteMicrophone,
            bool PreviousMutePlayback,
            string Context);

        private string GetSettingsPath() => _settings.GetSettingsPath();

        public bool Deafen
        {
            get => _deafenBackingField;
            set
            {
                if (_deafenBackingField == value) return;

                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveDeafenChange(value);
                bool previousDeafen = _deafenBackingField;
                bool previousMuteMic = _muteMicBackingField;
                bool previousMuteSound = _muteSoundBackingField;
                long operationRevision = ++_muteStateRevision;

                _deafenBackingField = plan.NewDeafen;
                _muteMicBackingField = plan.NewMuteMic;
                _muteSoundBackingField = plan.NewMuteSound;
                ApplyEndpointMuteStateToInitializedMixers(plan.DeviceMutePlayback, plan.DeviceMuteMicrophone);

                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }

                _logger.Info("AppViewModel", () => plan.LogMessage);

                QueueMuteStateApply(new PendingMuteStateApply(
                    operationRevision,
                    plan.DeviceMuteMicrophone,
                    plan.DeviceMutePlayback,
                    previousDeafen,
                    previousMuteMic,
                    previousMuteSound,
                    nameof(Deafen)));
            }
        }

        public bool MuteMic
        {
            get => _muteMicBackingField;
            set
            {
                if (_muteMicBackingField == value) return;
                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteMicChange(value, _deafenBackingField, _muteSoundBackingField);
                bool previousDeafen = _deafenBackingField;
                bool previousMuteMic = _muteMicBackingField;
                bool previousMuteSound = _muteSoundBackingField;
                long operationRevision = ++_muteStateRevision;

                _deafenBackingField = plan.NewDeafen;
                _muteMicBackingField = plan.NewMuteMic;
                _muteSoundBackingField = plan.NewMuteSound;
                ApplyEndpointMuteStateToInitializedMixers(plan.DeviceMutePlayback, plan.DeviceMuteMicrophone);

                _logger.Trace("AppViewModel", () => plan.LogMessage);
                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }

                QueueMuteStateApply(new PendingMuteStateApply(
                    operationRevision,
                    plan.DeviceMuteMicrophone,
                    plan.DeviceMutePlayback,
                    previousDeafen,
                    previousMuteMic,
                    previousMuteSound,
                    nameof(MuteMic)));
            }
        }

        public bool MuteSound
        {
            get => _muteSoundBackingField;
            set
            {
                if (_muteSoundBackingField == value) return;
                MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteSoundChange(value, _deafenBackingField, _muteMicBackingField);
                bool previousDeafen = _deafenBackingField;
                bool previousMuteMic = _muteMicBackingField;
                bool previousMuteSound = _muteSoundBackingField;
                long operationRevision = ++_muteStateRevision;

                _deafenBackingField = plan.NewDeafen;
                _muteMicBackingField = plan.NewMuteMic;
                _muteSoundBackingField = plan.NewMuteSound;
                ApplyEndpointMuteStateToInitializedMixers(plan.DeviceMutePlayback, plan.DeviceMuteMicrophone);

                _logger.Trace("AppViewModel", () => plan.LogMessage);
                foreach (string propertyName in plan.PropertyNamesToNotify)
                {
                    OnPropertyChanged(propertyName);
                }

                QueueMuteStateApply(new PendingMuteStateApply(
                    operationRevision,
                    plan.DeviceMuteMicrophone,
                    plan.DeviceMutePlayback,
                    previousDeafen,
                    previousMuteMic,
                    previousMuteSound,
                    nameof(MuteSound)));
            }
        }

        private void QueueMuteStateApply(PendingMuteStateApply request)
        {
            lock (_muteApplyLock)
            {
                _pendingMuteStateApply = request;
                if (_muteApplyProcessorRunning)
                {
                    return;
                }

                _muteApplyProcessorRunning = true;
                if (TryRunBackgroundWork(ProcessPendingMuteStateAppliesAsync, "MuteStateApply"))
                {
                    return;
                }

                _muteApplyProcessorRunning = false;
                _pendingMuteStateApply = null;
            }

            RollbackMuteStateIfCurrent(
                request.Revision,
                request.PreviousDeafen,
                request.PreviousMuteMicrophone,
                request.PreviousMutePlayback,
                MuteStatePropertyNames);
        }

        private async Task ProcessPendingMuteStateAppliesAsync(CancellationToken shutdownToken)
        {
            bool processorOwnershipReleased = false;
            try
            {
                while (!shutdownToken.IsCancellationRequested)
                {
                    PendingMuteStateApply request;
                    lock (_muteApplyLock)
                    {
                        if (_pendingMuteStateApply is not PendingMuteStateApply pendingRequest)
                        {
                            _muteApplyProcessorRunning = false;
                            processorOwnershipReleased = true;
                            return;
                        }

                        request = pendingRequest;
                        _pendingMuteStateApply = null;
                    }

                    MuteOperationResult result = default;
                    bool applyThrew = false;
                    try
                    {
                        result = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                        {
                            MuteOperationResult microphoneResult = _audio.SetMicrophoneMute(request.MuteMicrophone);
                            MuteOperationResult playbackResult = _audio.SetPlaybackMute(request.MutePlayback);
                            return MuteOperationResult.Combine(microphoneResult, playbackResult);
                        }, shutdownToken);

                        if (result.HasFailures)
                        {
                            _logger.Warning(
                                "AppViewModel",
                                () => $"mute-state-apply-partial | target={request.Context} attempted={result.AttemptedEndpointCount} succeeded={result.SucceededEndpointCount} failed={result.FailedEndpointCount}");
                        }
                    }
                    catch (Exception ex)
                    {
                        applyThrew = true;
                        _logger.Error(
                            "AppViewModel",
                            () => $"mute-state-apply-failed | target={request.Context} error={ex.GetType().Name}",
                            request.Context,
                            ex);
                    }

                    bool reconciled = await TryReconcileMuteStateAfterApplyAsync(request, shutdownToken);
                    if (!reconciled && (applyThrew || result.SucceededEndpointCount == 0))
                    {
                        await InvokeOnDispatcherAsync(() => RollbackMuteStateIfCurrent(
                            request.Revision,
                            request.PreviousDeafen,
                            request.PreviousMuteMicrophone,
                            request.PreviousMutePlayback,
                            MuteStatePropertyNames));
                    }
                }
            }
            finally
            {
                if (!processorOwnershipReleased)
                {
                    lock (_muteApplyLock)
                    {
                        _muteApplyProcessorRunning = false;
                        if (shutdownToken.IsCancellationRequested)
                        {
                            _pendingMuteStateApply = null;
                        }
                        else if (_pendingMuteStateApply != null && !_isCleaningUp)
                        {
                            _muteApplyProcessorRunning = true;
                            if (!TryRunBackgroundWork(ProcessPendingMuteStateAppliesAsync, "MuteStateApplyRecovery"))
                            {
                                _muteApplyProcessorRunning = false;
                                _pendingMuteStateApply = null;
                            }
                        }
                    }
                }
            }
        }

        private async Task<bool> TryReconcileMuteStateAfterApplyAsync(
            PendingMuteStateApply request,
            CancellationToken shutdownToken)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (Interlocked.Read(ref _muteStateRevision) != request.Revision)
                {
                    return true;
                }

                try
                {
                    (bool isPlaybackMuted, bool isMicMuted) =
                        await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                            () => ReadAuthoritativeMuteStates($"mute-apply:{request.Context}"),
                            shutdownToken);

                    await InvokeOnDispatcherAsync(() =>
                    {
                        if (_muteStateRevision == request.Revision)
                        {
                            ApplyAuthoritativeMuteFlags(
                                isPlaybackMuted,
                                isMicMuted,
                                $"mute-apply:{request.Context}");
                        }
                    });

                    return true;
                }
                catch (Exception ex) when (attempt == 1 && !shutdownToken.IsCancellationRequested)
                {
                    _logger.Warning(
                        "AppViewModel",
                        () => $"mute-state-reconcile-retry | target={request.Context} error={ex.GetType().Name}");
                    await Task.Delay(TimeSpan.FromMilliseconds(150), shutdownToken);
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        "AppViewModel",
                        () => $"mute-state-reconcile-failed | target={request.Context} error={ex.GetType().Name}",
                        request.Context,
                        ex);
                    return false;
                }
            }

            return false;
        }

        private (bool IsPlaybackMuted, bool IsMicrophoneMuted) ReadAuthoritativeMuteStates(string context)
        {
            if (AudioDeviceService.TryGetMuteStateOverrideForTests(out bool testPlaybackMuted, out bool testMicrophoneMuted))
            {
                return (testPlaybackMuted, testMicrophoneMuted);
            }

            bool playbackRead = _deviceCache.TryGetPlaybackMuteState(
                out bool playbackMuted,
                $"{context}:playback");
            bool microphoneRead = _deviceCache.TryGetRecordingMuteState(
                out bool microphoneMuted,
                $"{context}:recording");

            if (!playbackRead || !microphoneRead)
            {
                throw new InvalidOperationException(
                    $"Mute state unavailable (playbackRead={playbackRead}, microphoneRead={microphoneRead}).");
            }

            return (playbackMuted, microphoneMuted);
        }

        private void RollbackMuteStateIfCurrent(
            long operationRevision,
            bool previousDeafen,
            bool previousMuteMic,
            bool previousMuteSound,
            IReadOnlyList<string> propertyNamesToNotify)
        {
            if (_muteStateRevision != operationRevision)
            {
                return;
            }

            _muteStateRevision++;
            _deafenBackingField = previousDeafen;
            _muteMicBackingField = previousMuteMic;
            _muteSoundBackingField = previousMuteSound;
            ApplyEndpointMuteStateToInitializedMixers(
                previousDeafen || previousMuteSound,
                previousDeafen || previousMuteMic);

            foreach (string propertyName in propertyNamesToNotify)
            {
                OnPropertyChanged(propertyName);
            }
        }

        public bool PreserveAudioLevels
        {
            get => _preserveAudioLevelsBackingField;
            set
            {
                if (_preserveAudioLevelsBackingField == value)
                    return;

                _preserveAudioLevelsBackingField = value;
                OnPropertyChanged(nameof(PreserveAudioLevels));
            }
        }

        public OverlayPosition OverlayPosition
        {
            get => _overlayPositionBackingField;
            set
            {
                if (_overlayPositionBackingField == value)
                    return;

                _overlayPositionBackingField = value;
                OnPropertyChanged(nameof(OverlayPosition));
                ApplyOverlayDisplaySettings();
            }
        }

        public bool OverlayEnabled
        {
            get => _overlayEnabledBackingField;
            set
            {
                if (_overlayEnabledBackingField == value)
                    return;

                _overlayEnabledBackingField = value;
                OnPropertyChanged(nameof(OverlayEnabled));
                ApplyOverlayDisplaySettings();
            }
        }

        public string OverlayDurationSecondsText
        {
            get => _overlayDurationSecondsTextBackingField;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_overlayDurationSecondsTextBackingField, normalized, StringComparison.Ordinal))
                    return;

                _overlayDurationSecondsTextBackingField = normalized;
                OnPropertyChanged(nameof(OverlayDurationSecondsText));
                ApplyOverlayDisplaySettings();
            }
        }

        public IEnumerable<OverlayPosition> AvailableOverlayPositions => _availableOverlayPositions;

        public bool SettingsRunAtStartupDraft
        {
            get => _settingsRunAtStartupDraft;
            set
            {
                if (_settingsRunAtStartupDraft == value)
                    return;

                _settingsRunAtStartupDraft = value;
                OnPropertyChanged(nameof(SettingsRunAtStartupDraft));
            }
        }

        public bool SettingsAutoSaveEnabledDraft
        {
            get => _settingsAutoSaveEnabledDraft;
            set
            {
                if (_settingsAutoSaveEnabledDraft == value)
                    return;

                _settingsAutoSaveEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsAutoSaveEnabledDraft));
                OnPropertyChanged(nameof(IsAutoSaveActive));
                OnPropertyChanged(nameof(IsAutoSavePendingActivation));
            }
        }

        public bool SettingsPlayDialogSoundsDraft
        {
            get => _settingsPlayDialogSoundsDraft;
            set
            {
                if (_settingsPlayDialogSoundsDraft == value)
                {
                    return;
                }

                _settingsPlayDialogSoundsDraft = value;
                _dialogs.SetSoundsEnabled(value);
                OnPropertyChanged(nameof(SettingsPlayDialogSoundsDraft));
            }
        }

        public bool IsAutoSaveActive => IsPersistedAutoSaveEnabled();

        public bool IsAutoSavePendingActivation =>
            SettingsAutoSaveEnabledDraft && !IsPersistedAutoSaveEnabled();

        public AppTheme SettingsThemeDraft
        {
            get => _settingsThemeDraft;
            set
            {
                if (_settingsThemeDraft == value)
                    return;

                _settingsThemeDraft = value;
                OnPropertyChanged(nameof(SettingsThemeDraft));
            }
        }

        public bool SettingsPreserveAudioLevelsDraft
        {
            get => _settingsPreserveAudioLevelsDraft;
            set
            {
                if (_settingsPreserveAudioLevelsDraft == value)
                    return;

                _settingsPreserveAudioLevelsDraft = value;
                OnPropertyChanged(nameof(SettingsPreserveAudioLevelsDraft));
            }
        }

        public bool SettingsAutoScrollToMixerOnRestoreDraft
        {
            get => _settingsAutoScrollToMixerOnRestoreDraft;
            set
            {
                if (_settingsAutoScrollToMixerOnRestoreDraft == value)
                    return;

                _settingsAutoScrollToMixerOnRestoreDraft = value;
                OnPropertyChanged(nameof(SettingsAutoScrollToMixerOnRestoreDraft));
            }
        }

        public bool SettingsOverlayEnabledDraft
        {
            get => _settingsOverlayEnabledDraft;
            set
            {
                if (_settingsOverlayEnabledDraft == value)
                    return;

                _settingsOverlayEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsOverlayEnabledDraft));
            }
        }

        public LogLevel SettingsLogLevelDraft
        {
            get => _settingsLogLevelDraft;
            set
            {
                if (_settingsLogLevelDraft == value)
                    return;

                _settingsLogLevelDraft = value;
                OnPropertyChanged(nameof(SettingsLogLevelDraft));
            }
        }

        public bool SettingsRedactLogContentDraft
        {
            get => _settingsRedactLogContentDraft;
            set
            {
                if (_settingsRedactLogContentDraft == value)
                    return;

                _settingsRedactLogContentDraft = value;
                OnPropertyChanged(nameof(SettingsRedactLogContentDraft));
            }
        }

        public bool SettingsBluetoothReconnectEnabledDraft
        {
            get => _settingsBluetoothReconnectEnabledDraft;
            set
            {
                if (_settingsBluetoothReconnectEnabledDraft == value)
                    return;

                _settingsBluetoothReconnectEnabledDraft = value;
                OnPropertyChanged(nameof(SettingsBluetoothReconnectEnabledDraft));
            }
        }

        public DeviceReferenceFileMode SettingsDeviceReferenceFileModeDraft
        {
            get => _settingsDeviceReferenceFileModeDraft;
            set
            {
                if (_settingsDeviceReferenceFileModeDraft == value)
                    return;

                _settingsDeviceReferenceFileModeDraft = value;
                OnPropertyChanged(nameof(SettingsDeviceReferenceFileModeDraft));
            }
        }

        public string SettingsPlayPauseHotkeyDraft
        {
            get => SettingsPlayPauseHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsPlayPauseHotkeyDraftCapture, value, nameof(SettingsPlayPauseHotkeyDraft));
        }

        public string SettingsShowCurrentTrackHotkeyDraft
        {
            get => SettingsShowCurrentTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsShowCurrentTrackHotkeyDraftCapture, value, nameof(SettingsShowCurrentTrackHotkeyDraft));
        }

        public string SettingsToggleAppVisibilityHotkeyDraft
        {
            get => SettingsToggleAppVisibilityHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsToggleAppVisibilityHotkeyDraftCapture, value, nameof(SettingsToggleAppVisibilityHotkeyDraft));
        }

        public string SettingsNextTrackHotkeyDraft
        {
            get => SettingsNextTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsNextTrackHotkeyDraftCapture, value, nameof(SettingsNextTrackHotkeyDraft));
        }

        public string SettingsPreviousTrackHotkeyDraft
        {
            get => SettingsPreviousTrackHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsPreviousTrackHotkeyDraftCapture, value, nameof(SettingsPreviousTrackHotkeyDraft));
        }

        public string SettingsMuteMicHotkeyDraft
        {
            get => SettingsMuteMicHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMuteMicHotkeyDraftCapture, value, nameof(SettingsMuteMicHotkeyDraft));
        }

        public string SettingsMuteSoundHotkeyDraft
        {
            get => SettingsMuteSoundHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMuteSoundHotkeyDraftCapture, value, nameof(SettingsMuteSoundHotkeyDraft));
        }

        public string SettingsDeafenHotkeyDraft
        {
            get => SettingsDeafenHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsDeafenHotkeyDraftCapture, value, nameof(SettingsDeafenHotkeyDraft));
        }

        public string SettingsListenToInputHotkeyDraft
        {
            get => SettingsListenToInputHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsListenToInputHotkeyDraftCapture, value, nameof(SettingsListenToInputHotkeyDraft));
        }

        public string SettingsMasterVolumeUpHotkeyDraft
        {
            get => SettingsMasterVolumeUpHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMasterVolumeUpHotkeyDraftCapture, value, nameof(SettingsMasterVolumeUpHotkeyDraft));
        }

        public string SettingsMasterVolumeDownHotkeyDraft
        {
            get => SettingsMasterVolumeDownHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMasterVolumeDownHotkeyDraftCapture, value, nameof(SettingsMasterVolumeDownHotkeyDraft));
        }

        public string SettingsMicVolumeUpHotkeyDraft
        {
            get => SettingsMicVolumeUpHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMicVolumeUpHotkeyDraftCapture, value, nameof(SettingsMicVolumeUpHotkeyDraft));
        }

        public string SettingsMicVolumeDownHotkeyDraft
        {
            get => SettingsMicVolumeDownHotkeyDraftCapture.ToHotkeyString();
            set => SetSettingsHotkeyDraft(SettingsMicVolumeDownHotkeyDraftCapture, value, nameof(SettingsMicVolumeDownHotkeyDraft));
        }

        public bool SettingsMasterVolumeControlsExpanded
        {
            get => _settingsMasterVolumeControlsExpanded;
            set
            {
                if (_settingsMasterVolumeControlsExpanded == value)
                {
                    return;
                }

                _settingsMasterVolumeControlsExpanded = value;
                OnPropertyChanged(nameof(SettingsMasterVolumeControlsExpanded));
            }
        }

        public bool SettingsMicVolumeControlsExpanded
        {
            get => _settingsMicVolumeControlsExpanded;
            set
            {
                if (_settingsMicVolumeControlsExpanded == value)
                {
                    return;
                }

                _settingsMicVolumeControlsExpanded = value;
                OnPropertyChanged(nameof(SettingsMicVolumeControlsExpanded));
            }
        }

        public string SettingsMasterVolumeStepPercentDraft
        {
            get => _settingsMasterVolumeStepPercentDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsMasterVolumeStepPercentDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsMasterVolumeStepPercentDraft = normalized;
                OnPropertyChanged(nameof(SettingsMasterVolumeStepPercentDraft));
            }
        }

        public string SettingsMicVolumeStepPercentDraft
        {
            get => _settingsMicVolumeStepPercentDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsMicVolumeStepPercentDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsMicVolumeStepPercentDraft = normalized;
                OnPropertyChanged(nameof(SettingsMicVolumeStepPercentDraft));
            }
        }

        public string SettingsListenMonitorOutputDeviceIdDraft
        {
            get => _settingsListenMonitorOutputDeviceIdDraft;
            set => SetSettingsListenMonitorOutputDraft(value, null);
        }











        private void DetachOwnedEventHandlers()
        {
            foreach ((HotkeyViewModel Draft, PropertyChangedEventHandler Handler) in _hotkeyDraftHandlers)
            {
                Draft.PropertyChanged -= Handler;
            }

            _hotkeyDraftHandlers.Clear();

            OutputCycleDevices.CollectionChanged -= OnOutputCycleDevicesCollectionChanged;
            InputCycleDevices.CollectionChanged -= OnInputCycleDevicesCollectionChanged;
            Routines.CollectionChanged -= OnRoutinesCollectionChanged;
            DetachRoutinePropertyHandlers();

            if (_scheduleTriggerCoordinator.IsValueCreated)
            {
                _scheduleTriggerCoordinator.Value.Dispose();
            }
            if (_networkTriggerCoordinator.IsValueCreated)
            {
                _networkTriggerCoordinator.Value.Dispose();
            }
            if (_applicationTriggerCoordinator.IsValueCreated)
            {
                _applicationTriggerCoordinator.Value.Dispose();
            }
            _routineLastRunRefreshTimer.Stop();
            _routineLastRunRefreshTimer.Tick -= OnRoutineLastRunRefreshTimerTick;
        }

        public bool OutputHotkeyHasConflict => Hotkey.HasWarning;

        public bool OutputReverseHotkeyHasConflict => OutputReverseHotkey.HasWarning;

        public bool InputHotkeyHasConflict => InputHotkey.HasWarning;

        public bool InputReverseHotkeyHasConflict => InputReverseHotkey.HasWarning;

        public bool SettingsToggleAppVisibilityHotkeyHasConflict => SettingsToggleAppVisibilityHotkeyDraftCapture.HasWarning;

        public bool SettingsShowCurrentTrackHotkeyHasConflict => SettingsShowCurrentTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsPlayPauseHotkeyHasConflict => SettingsPlayPauseHotkeyDraftCapture.HasWarning;

        public bool SettingsNextTrackHotkeyHasConflict => SettingsNextTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsPreviousTrackHotkeyHasConflict => SettingsPreviousTrackHotkeyDraftCapture.HasWarning;

        public bool SettingsMuteMicHotkeyHasConflict => SettingsMuteMicHotkeyDraftCapture.HasWarning;

        public bool SettingsMuteSoundHotkeyHasConflict => SettingsMuteSoundHotkeyDraftCapture.HasWarning;

        public bool SettingsDeafenHotkeyHasConflict => SettingsDeafenHotkeyDraftCapture.HasWarning;

        public bool SettingsListenToInputHotkeyHasConflict => SettingsListenToInputHotkeyDraftCapture.HasWarning;

        public bool SettingsMasterVolumeUpHotkeyHasConflict => SettingsMasterVolumeUpHotkeyDraftCapture.HasWarning;

        public bool SettingsMasterVolumeDownHotkeyHasConflict => SettingsMasterVolumeDownHotkeyDraftCapture.HasWarning;

        public bool SettingsMicVolumeUpHotkeyHasConflict => SettingsMicVolumeUpHotkeyDraftCapture.HasWarning;

        public bool SettingsMicVolumeDownHotkeyHasConflict => SettingsMicVolumeDownHotkeyDraftCapture.HasWarning;








        public bool SettingsOutputRoleMultimediaDraft
        {
            get => _settingsOutputRoleMultimediaDraft;
            set
            {
                if (_settingsOutputRoleMultimediaDraft == value)
                    return;

                _settingsOutputRoleMultimediaDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleMultimediaDraft));
            }
        }

        public bool SettingsOutputRoleCommunicationsDraft
        {
            get => _settingsOutputRoleCommunicationsDraft;
            set
            {
                if (_settingsOutputRoleCommunicationsDraft == value)
                    return;

                _settingsOutputRoleCommunicationsDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleCommunicationsDraft));
            }
        }

        public bool SettingsOutputRoleConsoleDraft
        {
            get => _settingsOutputRoleConsoleDraft;
            set
            {
                if (_settingsOutputRoleConsoleDraft == value)
                    return;

                _settingsOutputRoleConsoleDraft = value;
                OnPropertyChanged(nameof(SettingsOutputRoleConsoleDraft));
            }
        }

        public bool SettingsInputRoleMultimediaDraft
        {
            get => _settingsInputRoleMultimediaDraft;
            set
            {
                if (_settingsInputRoleMultimediaDraft == value)
                    return;

                _settingsInputRoleMultimediaDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleMultimediaDraft));
            }
        }

        public bool SettingsInputRoleCommunicationsDraft
        {
            get => _settingsInputRoleCommunicationsDraft;
            set
            {
                if (_settingsInputRoleCommunicationsDraft == value)
                    return;

                _settingsInputRoleCommunicationsDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleCommunicationsDraft));
            }
        }

        public bool SettingsInputRoleConsoleDraft
        {
            get => _settingsInputRoleConsoleDraft;
            set
            {
                if (_settingsInputRoleConsoleDraft == value)
                    return;

                _settingsInputRoleConsoleDraft = value;
                OnPropertyChanged(nameof(SettingsInputRoleConsoleDraft));
            }
        }

        public OverlayPosition SettingsOverlayPositionDraft
        {
            get => _settingsOverlayPositionDraft;
            set
            {
                if (_settingsOverlayPositionDraft == value)
                    return;

                _settingsOverlayPositionDraft = value;
                OnPropertyChanged(nameof(SettingsOverlayPositionDraft));
            }
        }

        public string SettingsOverlayDurationSecondsDraft
        {
            get => _settingsOverlayDurationSecondsDraft;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_settingsOverlayDurationSecondsDraft, normalized, StringComparison.Ordinal))
                    return;

                _settingsOverlayDurationSecondsDraft = normalized;
                OnPropertyChanged(nameof(SettingsOverlayDurationSecondsDraft));
            }
        }

        public bool IsRoutinesTabActive => SelectedSettingsTabIndex == 2;
        public bool IsSettingsTabActive => SelectedSettingsTabIndex == 3;
        public bool IsDeviceTabsActive => SelectedSettingsTabIndex is 0 or 1;
        public bool IsEditorTabsActive => IsDeviceTabsActive;

        public bool IsApplyingSettings
        {
            get => _isApplyingSettings;
            private set
            {
                if (_isApplyingSettings == value)
                    return;

                _isApplyingSettings = value;
                OnPropertyChanged(nameof(IsApplyingSettings));
            }
        }



        public bool OutputHotkeysEnabled
        {
            get => _outputHotkeysEnabledBackingField;
            set
            {
                if (_outputHotkeysEnabledBackingField == value)
                    return;

                _outputHotkeysEnabledBackingField = value;
                OnPropertyChanged(nameof(OutputHotkeysEnabled));
                ApplySwitchHotkeyRegistrationFromCurrentUiState();
            }
        }

        public bool InputHotkeysEnabled
        {
            get => _inputHotkeysEnabledBackingField;
            set
            {
                if (_inputHotkeysEnabledBackingField == value)
                    return;

                _inputHotkeysEnabledBackingField = value;
                OnPropertyChanged(nameof(InputHotkeysEnabled));
                ApplySwitchHotkeyRegistrationFromCurrentUiState();
            }
        }



        private AppTheme _themeBackingField;
        public AppTheme Theme
        {
            get => _themeBackingField;
            set
            {
                if (_themeBackingField == value) return;
                _themeBackingField = value;
                OnPropertyChanged(nameof(Theme));
                SyncMirroredSettingsDraftsFromLiveState(theme: value);

                Application? application = Application.Current;
                if (application == null)
                {
                    return;
                }

                WindowThemeResolver.ApplyApplicationMainWindowTheme(value);
            }
        }

        public IEnumerable<AppTheme> AvailableThemes => _availableThemes;
        public IEnumerable<LogLevel> AvailableLogLevels => _availableLogLevels;
        public IEnumerable<DeviceReferenceFileMode> AvailableDeviceReferenceFileModes => _availableDeviceReferenceFileModes;

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (_isSaving == value) return;
                _isSaving = value;
                OnPropertyChanged(nameof(IsSaving));
            }
        }

        private volatile bool _isSaving;

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand SaveCurrentContextCommand { get; }
        public ICommand ApplySettingsCommand { get; }
        public ICommand ImportSettingsCommand { get; }
        public ICommand ExportSettingsCommand { get; }
        public ICommand ResetPerAppAudioRoutingCommand { get; }
        public ICommand ShowCommand { get; }
        public ICommand MinimizeCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand AddOutputCycleDeviceCommand { get; }
        public ICommand RemoveOutputCycleDeviceCommand { get; }
        public ICommand MoveOutputCycleDeviceUpCommand { get; }
        public ICommand MoveOutputCycleDeviceDownCommand { get; }
        public ICommand AddInputCycleDeviceCommand { get; }
        public ICommand RemoveInputCycleDeviceCommand { get; }
        public ICommand MoveInputCycleDeviceUpCommand { get; }
        public ICommand MoveInputCycleDeviceDownCommand { get; }
        public ICommand AddRoutineCommand { get; }
        public ICommand EditRoutineCommand { get; }
        public ICommand DuplicateRoutineCommand { get; }
        public ICommand CopyRoutineCommand { get; }
        public ICommand RemoveRoutineCommand { get; }
        public ICommand MoveRoutineUpCommand { get; }
        public ICommand MoveRoutineDownCommand { get; }
        public ICommand EnableSelectedRoutinesCommand { get; }
        public ICommand DisableSelectedRoutinesCommand { get; }
        public ICommand SaveRoutinesCommand { get; }
        public ICommand NextSettingsTabCommand { get; }
        public ICommand SelectSettingsTabCommand { get; }

        private readonly List<string> _additionalStandaloneHotkeyKeys = [];
        private Settings? _cachedSettings;
        private readonly List<CycleDevice> _outputDevices = [];
        private int _selectedAvailableOutputIndex = -1;
        private int _selectedOutputCycleIndex = -1;
        private readonly List<CycleDevice> _inputDevices = [];
        private int _selectedAvailableInputIndex = -1;
        private int _selectedInputCycleIndex = -1;
        private long _suppressHotplugOutputConnectedUntilUtcTicks;
        private long _suppressHotplugInputConnectedUntilUtcTicks;
        private int _selectedSettingsTabIndex;
        private readonly AppTheme[] _availableThemes = Enum.GetValues<AppTheme>();
        private readonly LogLevel[] _availableLogLevels = Enum.GetValues<LogLevel>();
        private readonly OverlayPosition[] _availableOverlayPositions = Enum.GetValues<OverlayPosition>();
        private readonly DeviceReferenceFileMode[] _availableDeviceReferenceFileModes = Enum.GetValues<DeviceReferenceFileMode>();





































        public Task RefreshDevicesForHotplugAsync()
        {
            return RefreshDevicesAsync(
                promptOnPotentialOverwrite: false,
                refreshMixerWhenWindowHidden: false,
                checkSettingsFileChanges: false);
        }




        /// <summary>
        /// Handles session-created notifications and triggers a debounced mixer refresh when the window is visible.
        /// </summary>
        /// <remarks>
        /// The mixer is intentionally not refreshed while hidden/tray-minimized to avoid unnecessary background churn.
        /// </remarks>




        internal SessionCreatedMixerRefreshDrainResult DrainPendingMixerRefreshSignals(MixerRefreshTarget requestedTarget)
        {
            if (requestedTarget == MixerRefreshTarget.Output)
            {
                int outputTargetSignals = Interlocked.Exchange(ref _pendingOutputSessionCreatedSignals, 0)
                    + Interlocked.Exchange(ref _pendingOutputSessionLifecycleSignals, 0)
                    + Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
                return new SessionCreatedMixerRefreshDrainResult(outputTargetSignals, MixerRefreshTarget.Output);
            }

            if (requestedTarget == MixerRefreshTarget.Input)
            {
                int inputTargetSignals = Interlocked.Exchange(ref _pendingInputSessionCreatedSignals, 0)
                    + Interlocked.Exchange(ref _pendingInputSessionLifecycleSignals, 0)
                    + Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
                return new SessionCreatedMixerRefreshDrainResult(inputTargetSignals, MixerRefreshTarget.Input);
            }

            int outputSignals = Interlocked.Exchange(ref _pendingOutputSessionCreatedSignals, 0)
                + Interlocked.Exchange(ref _pendingOutputSessionLifecycleSignals, 0);
            int inputSignals = Interlocked.Exchange(ref _pendingInputSessionCreatedSignals, 0)
                + Interlocked.Exchange(ref _pendingInputSessionLifecycleSignals, 0);
            int showWindowSignals = Interlocked.Exchange(ref _pendingShowWindowMixerRefreshSignals, 0);
            int totalSignals = outputSignals + inputSignals + showWindowSignals;

            MixerRefreshTarget target = showWindowSignals > 0 || (outputSignals > 0 && inputSignals > 0)
                ? MixerRefreshTarget.Both
                : outputSignals > 0
                    ? MixerRefreshTarget.Output
                    : inputSignals > 0
                        ? MixerRefreshTarget.Input
                        : MixerRefreshTarget.Both;

            return new SessionCreatedMixerRefreshDrainResult(totalSignals, target);
        }

        internal static int DrainPendingMixerRefreshSignals(
            ref int pendingSessionCreatedSignals,
            ref int pendingSessionLifecycleSignals,
            ref int pendingShowWindowMixerRefreshSignals)
        {
            int totalSignals = 0;
            totalSignals += Interlocked.Exchange(ref pendingSessionCreatedSignals, 0);
            totalSignals += Interlocked.Exchange(ref pendingSessionLifecycleSignals, 0);
            totalSignals += Interlocked.Exchange(ref pendingShowWindowMixerRefreshSignals, 0);
            return totalSignals;
        }

        /// <summary>
        /// Atomically replaces the active session-refresh debounce token source.
        /// </summary>
        internal static CancellationTokenSource? SwapSessionRefreshDebounce(
            ref CancellationTokenSource? current,
            CancellationTokenSource next)
        {
            return Interlocked.Exchange(ref current, next);
        }

        internal static CancellationTokenSource? CancelAndDetachDebounce(ref CancellationTokenSource? current)
        {
            CancellationTokenSource? detached = Interlocked.Exchange(ref current, null);
            if (detached != null)
            {
                try
                {
                    detached.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The debounce owner may have completed and disposed the source after it was detached.
                }
            }
            return detached;
        }

        internal static void ReleaseOwnedDebounce(ref CancellationTokenSource? current, CancellationTokenSource ownedDebounce)
        {
            Interlocked.CompareExchange(ref current, null, ownedDebounce);
            ownedDebounce.Dispose();
        }
    }
}
