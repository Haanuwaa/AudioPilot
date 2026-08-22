using System.IO;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private bool HasUiSettingsDivergedFromCachedSettings()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();

            if (cachedCopy == null)
            {
                return false;
            }

            string currentOutputHotkey = Hotkey.ToHotkeyString();
            string currentInputHotkey = InputHotkey.ToHotkeyString();

            SaveEditState editState = AppSettingsWorkflowCoordinator.BuildSaveEditState(
                OutputCycleDevices,
                InputCycleDevices,
                currentOutputHotkey,
                currentInputHotkey,
                OutputHotkeysEnabled,
                InputHotkeysEnabled,
                cachedCopy);

            if (editState.OutputEdited || editState.InputEdited)
            {
                return true;
            }

            return RunAtStartup != cachedCopy.RunAtStartup ||
                PreserveAudioLevels != cachedCopy.DeviceSwitching.PreserveAudioLevels ||
                OverlayEnabled != cachedCopy.Overlay.Enabled ||
                OverlayPosition != cachedCopy.Overlay.Position ||
                !double.TryParse(OverlayDurationSecondsText, out double currentOverlaySeconds) ||
                Math.Abs(currentOverlaySeconds - cachedCopy.Overlay.DurationSeconds) > 0.001 ||
                OutputHotkeysEnabled != cachedCopy.DeviceSwitching.Output.HotkeysEnabled ||
                InputHotkeysEnabled != cachedCopy.DeviceSwitching.Input.HotkeysEnabled ||
                Theme != cachedCopy.Theme;
        }

        private bool HasPendingLocalEditsForRefresh()
        {
            return HasUiSettingsDivergedFromCachedSettings() || HasSettingsDraftDivergedFromCachedSettings() || HasRoutineEdits();
        }

        private bool CanAutoApplySettingsDrafts()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            return cachedCopy != null && SettingsAutoSaveEnabledDraft == cachedCopy.Miscellaneous.AutoSaveEnabled;
        }

        private bool HasSettingsDraftDivergedFromCachedSettings()
        {
            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            if (cachedCopy == null)
            {
                return true;
            }

            if (!string.Equals(SettingsLogLevelDraft.ToString(), cachedCopy.Miscellaneous.LogLevel ?? "Info", StringComparison.OrdinalIgnoreCase) ||
                SettingsRedactLogContentDraft != cachedCopy.Miscellaneous.RedactLogContent ||
                SettingsPlayAppSoundsDraft != cachedCopy.Miscellaneous.PlayAppSounds ||
                SettingsUseScheduledStartupDraft != cachedCopy.Miscellaneous.UseScheduledStartup ||
                SettingsCheckForUpdatesDraft != cachedCopy.Miscellaneous.CheckForUpdates ||
                SettingsOverlayPositionDraft != cachedCopy.Overlay.Position ||
                !string.Equals(SettingsOverlayDurationSecondsDraft, cachedCopy.Overlay.DurationSeconds.ToString("0.0"), StringComparison.Ordinal) ||
                (!MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out int seekStep) || seekStep != cachedCopy.Hotkeys.Media.SeekStepSeconds) ||
                !string.Equals(SettingsMasterVolumeStepPercentDraft, cachedCopy.Hotkeys.Volume.MasterVolumeStepPercent.ToString(), StringComparison.Ordinal) ||
                !string.Equals(SettingsMicVolumeStepPercentDraft, cachedCopy.Hotkeys.Volume.MicVolumeStepPercent.ToString(), StringComparison.Ordinal) ||
                !string.Equals(SettingsForegroundVolumeStepPercentDraft, cachedCopy.Hotkeys.Volume.ForegroundVolumeStepPercent.ToString(), StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(
                SettingsListenMonitorOutputDeviceIdDraft,
                cachedCopy.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(
                _settingsListenMonitorOutputDeviceNameDraft,
                cachedCopy.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty,
                StringComparison.Ordinal))
            {
                return true;
            }

            bool outputRoleMultimedia = cachedCopy.DeviceSwitching.Output.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
            bool outputRoleCommunications = cachedCopy.DeviceSwitching.Output.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
            bool outputRoleConsole = cachedCopy.DeviceSwitching.Output.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);

            if (SettingsOutputRoleMultimediaDraft != outputRoleMultimedia ||
                SettingsOutputRoleCommunicationsDraft != outputRoleCommunications ||
                SettingsOutputRoleConsoleDraft != outputRoleConsole)
            {
                return true;
            }

            bool inputRoleMultimedia = cachedCopy.DeviceSwitching.Input.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
            bool inputRoleCommunications = cachedCopy.DeviceSwitching.Input.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
            bool inputRoleConsole = cachedCopy.DeviceSwitching.Input.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);

            if (SettingsInputRoleMultimediaDraft != inputRoleMultimedia ||
                SettingsInputRoleCommunicationsDraft != inputRoleCommunications ||
                SettingsInputRoleConsoleDraft != inputRoleConsole)
            {
                return true;
            }

            return SettingsPushToTalkEnabledDraft != cachedCopy.Hotkeys.Mute.PushToTalkEnabled ||
                !AreHotkeyStringsEquivalent(SettingsShowAudioStatusHotkeyDraft, cachedCopy.Hotkeys.App.ShowAudioStatus) ||
                !AreHotkeyStringsEquivalent(SettingsToggleAppVisibilityHotkeyDraft, cachedCopy.Hotkeys.App.ToggleAppVisibility) ||
                !AreHotkeyStringsEquivalent(SettingsShowCurrentTrackHotkeyDraft, cachedCopy.Hotkeys.Media.ShowCurrentTrack) ||
                !AreHotkeyStringsEquivalent(SettingsPlayPauseHotkeyDraft, cachedCopy.Hotkeys.Media.PlayPause) ||
                !AreHotkeyStringsEquivalent(SettingsNextTrackHotkeyDraft, cachedCopy.Hotkeys.Media.NextTrack) ||
                !AreHotkeyStringsEquivalent(SettingsPreviousTrackHotkeyDraft, cachedCopy.Hotkeys.Media.PreviousTrack) ||
                !AreHotkeyStringsEquivalent(SettingsSeekForwardHotkeyDraft, cachedCopy.Hotkeys.Media.SeekForward) ||
                !AreHotkeyStringsEquivalent(SettingsSeekBackwardHotkeyDraft, cachedCopy.Hotkeys.Media.SeekBackward) ||
                !AreHotkeyStringsEquivalent(SettingsMuteMicHotkeyDraft, cachedCopy.Hotkeys.Mute.Mic) ||
                !AreHotkeyStringsEquivalent(SettingsMuteSoundHotkeyDraft, cachedCopy.Hotkeys.Mute.Sound) ||
                !AreHotkeyStringsEquivalent(SettingsDeafenHotkeyDraft, cachedCopy.Hotkeys.Mute.Deafen) ||
                !AreHotkeyStringsEquivalent(SettingsListenToInputHotkeyDraft, cachedCopy.Hotkeys.Listen.ListenToInput) ||
                !AreHotkeyStringsEquivalent(SettingsMasterVolumeUpHotkeyDraft, cachedCopy.Hotkeys.Volume.MasterUp) ||
                !AreHotkeyStringsEquivalent(SettingsMasterVolumeDownHotkeyDraft, cachedCopy.Hotkeys.Volume.MasterDown) ||
                !AreHotkeyStringsEquivalent(SettingsMicVolumeUpHotkeyDraft, cachedCopy.Hotkeys.Volume.MicUp) ||
                !AreHotkeyStringsEquivalent(SettingsMicVolumeDownHotkeyDraft, cachedCopy.Hotkeys.Volume.MicDown) ||
                !AreHotkeyStringsEquivalent(SettingsForegroundVolumeUpHotkeyDraft, cachedCopy.Hotkeys.Volume.ForegroundUp) ||
                !AreHotkeyStringsEquivalent(SettingsForegroundVolumeDownHotkeyDraft, cachedCopy.Hotkeys.Volume.ForegroundDown) ||
                !AreHotkeyStringsEquivalent(SettingsForegroundMuteHotkeyDraft, cachedCopy.Hotkeys.Volume.ForegroundMute) ||
                !AreHotkeyStringsEquivalent(SettingsPushToTalkHotkeyDraft, cachedCopy.Hotkeys.Mute.PushToTalk) ||
                !AreHotkeyStringsEquivalent(SettingsHoldToMuteHotkeyDraft, cachedCopy.Hotkeys.Mute.HoldToMute);
        }

        private void ApplyExternallyReloadedSettings(Settings newSettings, string context = "external-reload")
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => ApplyExternallyReloadedSettings(newSettings, context));
                return;
            }

            using (SuppressAutoSave())
            {
                AppExternalReloadCoordinator.Apply(
                    newSettings,
                    new ExternalReloadDependencies(
                        CacheSettings: () =>
                        {
                            lock (_settingsLock)
                            {
                                _cachedSettings = newSettings;
                            }

                            NotifyAutoSaveStateChanged();
                        },
                        ApplyLogLevel: () => _logger.ApplyLogLevel(newSettings),
                        ApplyAdvancedTuning: () => ApplyPersistedAdvancedTuning(newSettings),
                        LoadOutputDevices: LoadOutputDevices,
                        ApplyOutputCycle: ApplyOutputCycleFromSettings,
                        LoadInputDevices: LoadInputDevices,
                        ApplyInputCycle: ApplyInputCycleFromSettings,
                        ApplyRoutines: ApplyRoutinesFromSettings,
                        LoadOutputHotkey: () => Hotkey.LoadFromString(newSettings.DeviceSwitching.Output.SwitchHotkey),
                        LoadOutputReverseHotkey: () => OutputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Output.ReverseSwitchHotkey),
                        LoadInputHotkey: () => InputHotkey.LoadFromString(newSettings.DeviceSwitching.Input.SwitchHotkey),
                        LoadInputReverseHotkey: () => InputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Input.ReverseSwitchHotkey),
                        ApplyOutputHotkeysEnabled: () => OutputHotkeysEnabled = newSettings.DeviceSwitching.Output.HotkeysEnabled,
                        ApplyInputHotkeysEnabled: () => InputHotkeysEnabled = newSettings.DeviceSwitching.Input.HotkeysEnabled,
                        ApplyTheme: () => Theme = newSettings.Theme,
                        ApplyOverlayPosition: () => OverlayPosition = newSettings.Overlay.Position,
                        ApplyOverlayDurationText: () => OverlayDurationSecondsText = newSettings.Overlay.DurationSeconds.ToString("0.0"),
                        RegisterHotkeys: () => _hotkeyRegistrationCoordinator.RegisterAll(newSettings),
                        LogHotkeyResults: _hotkeyRegistrationCoordinator.LogApplySettingsResults,
                        RegisterRoutineHotkeys: () => RegisterRoutineHotkeysFromSettings(newSettings, context),
                        ApplyRunAtStartup: () => SetRunAtStartupInternal(newSettings.RunAtStartup),
                        ApplyPreserveAudioLevels: () =>
                        {
                            _preserveAudioLevelsBackingField = newSettings.DeviceSwitching.PreserveAudioLevels;
                            OnPropertyChanged(nameof(PreserveAudioLevels));
                        },
                        ApplyOverlayEnabled: () =>
                        {
                            _overlayEnabledBackingField = newSettings.Overlay.Enabled;
                            OnPropertyChanged(nameof(OverlayEnabled));
                        },
                        LogSettingsApply: () => _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.SettingsApply} | preserveAudioLevels={_preserveAudioLevelsBackingField}"),
                        ApplyOverlayDisplaySettings: ApplyOverlayDisplaySettings,
                        UpdateAudioConfiguration: () => _audio.UpdateRoleConfiguration(newSettings.DeviceSwitching.Output.SwitchRoles, newSettings.DeviceSwitching.Input.SwitchRoles),
                        SyncSettingsDrafts: () => SyncSettingsDraftFromCurrentState(resetHotkeyExpansion: true)));
            }
        }

        private async Task SaveCurrentContextAsync()
        {
            if (ShouldSaveRoutinesTab(SelectedSettingsTabIndex))
            {
                if (!SaveRoutinesCommand.CanExecute(null))
                {
                    return;
                }

                await SaveRoutinesAsync();
                return;
            }

            if (ShouldApplySettingsTabSave(SelectedSettingsTabIndex))
            {
                await ApplySettingsAsync();
                return;
            }

            await SaveSettingsAsync();
        }

        private async Task ApplySettingsAsync(bool autoSave = false)
        {
            if (IsResettingSettings) return;

            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            if (!TryGetSettingsOverlayDurationSeconds(out double overlayDurationSeconds))
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(DialogText.Messages.InvalidOverlayDuration, DialogText.Captions.InvalidOverlayDuration);
                }
                return;
            }

            if (!MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out int seekStepSeconds))
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(MediaSeekStep.InputHelp, DialogText.Captions.InvalidSettings);
                }
                return;
            }

            if (!TryGetVolumeStepPercent(SettingsForegroundVolumeStepPercentDraft, out int foregroundVolumeStepPercent))
            {
                if (!autoSave) await _dialogs.ShowWarningAsync("Foreground app volume step must be between 1 and 100%.", DialogText.Captions.InvalidSettings);
                return;
            }

            bool masterStepRequired =
                HasConfiguredHotkeyDraft(SettingsMasterVolumeUpHotkeyDraft) ||
                HasConfiguredHotkeyDraft(SettingsMasterVolumeDownHotkeyDraft);
            bool micStepRequired =
                HasConfiguredHotkeyDraft(SettingsMicVolumeUpHotkeyDraft) ||
                HasConfiguredHotkeyDraft(SettingsMicVolumeDownHotkeyDraft);

            if (!TryResolveVolumeStepPercent(
                    SettingsMasterVolumeStepPercentDraft,
                    cachedCopy?.Hotkeys.Volume.MasterVolumeStepPercent ?? 5,
                    masterStepRequired,
                    out int masterVolumeStepPercent) ||
                !TryResolveVolumeStepPercent(
                    SettingsMicVolumeStepPercentDraft,
                    cachedCopy?.Hotkeys.Volume.MicVolumeStepPercent ?? 5,
                    micStepRequired,
                    out int micVolumeStepPercent))
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync("Volume step values must be whole numbers between 1 and 100.", DialogText.Captions.InvalidSettings);
                }
                return;
            }

            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            IsApplyingSettings = true;

            ApplySettingsSideEffectResult? applyDialogEffects = null;
            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    List<string> outputRoles = BuildRoleSelections(SettingsOutputRoleMultimediaDraft, SettingsOutputRoleCommunicationsDraft, SettingsOutputRoleConsoleDraft);
                    List<string> inputRoles = BuildRoleSelections(SettingsInputRoleMultimediaDraft, SettingsInputRoleCommunicationsDraft, SettingsInputRoleConsoleDraft);
                    Settings newSettings = AppSettingsWorkflowCoordinator.BuildAppliedSettings(
                        cachedCopy,
                        new ApplySettingsBuildInput(
                            OutputReverseHotkey.ToHotkeyString(),
                            _outputHotkeysEnabledBackingField,
                            InputReverseHotkey.ToHotkeyString(),
                            _inputHotkeysEnabledBackingField,
                            outputRoles,
                            inputRoles,
                            SettingsAutoSaveEnabledDraft,
                            SettingsPlayAppSoundsDraft,
                            SettingsRunAtStartupDraft,
                            SettingsToggleAppVisibilityHotkeyDraft,
                            SettingsShowCurrentTrackHotkeyDraft,
                            SettingsPlayPauseHotkeyDraft,
                            SettingsNextTrackHotkeyDraft,
                            SettingsPreviousTrackHotkeyDraft,
                            SettingsMuteMicHotkeyDraft,
                            SettingsMuteSoundHotkeyDraft,
                            SettingsDeafenHotkeyDraft,
                            SettingsListenToInputHotkeyDraft,
                            SettingsMasterVolumeUpHotkeyDraft,
                            SettingsMasterVolumeDownHotkeyDraft,
                            SettingsMicVolumeUpHotkeyDraft,
                            SettingsMicVolumeDownHotkeyDraft,
                            masterVolumeStepPercent,
                            micVolumeStepPercent,
                            SettingsListenMonitorOutputDeviceIdDraft,
                            _settingsListenMonitorOutputDeviceNameDraft,
                            _outputDevices,
                            SettingsPreserveAudioLevelsDraft,
                            SettingsBluetoothReconnectEnabledDraft,
                            SettingsDeviceReferenceFileModeDraft,
                            SettingsOverlayEnabledDraft,
                            SettingsThemeDraft,
                            SettingsLogLevelDraft.ToString(),
                            SettingsRedactLogContentDraft,
                            SettingsAutoScrollToMixerOnRestoreDraft,
                            SettingsOverlayPositionDraft,
                            overlayDurationSeconds,
                            SettingsUseScheduledStartupDraft,
                            SettingsCheckForUpdatesDraft,
                            SettingsSeekForwardHotkeyDraft,
                            SettingsSeekBackwardHotkeyDraft,
                            seekStepSeconds,
                            SettingsShowAudioStatusHotkeyDraft,
                            SettingsForegroundVolumeUpHotkeyDraft,
                            SettingsForegroundVolumeDownHotkeyDraft,
                            SettingsForegroundMuteHotkeyDraft,
                            SettingsPushToTalkHotkeyDraft,
                            SettingsHoldToMuteHotkeyDraft,
                            foregroundVolumeStepPercent,
                            SettingsPushToTalkEnabledDraft));

                    SettingsCommitValidationResult commitValidation = ValidateSettingsForCommit(newSettings);
                    if (commitValidation.HasBlockingIssues)
                    {
                        if (!autoSave)
                        {
                            await _dialogs.ShowWarningAsync(
                                DialogText.Messages.BuildInvalidSettingsBeforeApplying(commitValidation.BlockingMessages),
                                DialogText.Captions.InvalidSettings);
                        }

                        return;
                    }

                    await Task.Run(() => SaveSettingsWithStartupRegistration(newSettings));
                    await InvokeOnDispatcherAsync(() =>
                    {
                        bool preserveDrafts = Volatile.Read(ref _autoSaveDirtyRevision) != autoSaveRevisionAtStart;
                        AppTheme pendingThemeDraft = SettingsThemeDraft;
                        ApplySettingsSideEffectResult applyEffects = AppSettingsEffectsCoordinator.RunApplySideEffects(
                            cachedCopy,
                            newSettings,
                            outputRoles.Count == 0,
                            inputRoles.Count == 0,
                            persistUiState: () =>
                            {
                                lock (_settingsLock)
                                {
                                    _cachedSettings = newSettings;
                                }

                                UpdateLastSettingsWriteTime();
                                SetRunAtStartupInternal(newSettings.RunAtStartup);
                                OnPropertyChanged(nameof(RunAtStartup));
                                NotifyAutoSaveStateChanged();
                                _logger.ApplyLogLevel(newSettings);
                                ApplyPersistedAdvancedTuning(newSettings);

                                _preserveAudioLevelsBackingField = newSettings.DeviceSwitching.PreserveAudioLevels;
                                OnPropertyChanged(nameof(PreserveAudioLevels));

                                _outputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Output.HotkeysEnabled;
                                _inputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Input.HotkeysEnabled;
                                OnPropertyChanged(nameof(OutputHotkeysEnabled));
                                OnPropertyChanged(nameof(InputHotkeysEnabled));

                                Theme = newSettings.Theme;

                                _overlayPositionBackingField = newSettings.Overlay.Position;
                                _overlayDurationSecondsTextBackingField = newSettings.Overlay.DurationSeconds.ToString("0.0");
                                _overlayEnabledBackingField = newSettings.Overlay.Enabled;
                                OnPropertyChanged(nameof(OverlayEnabled));
                                OnPropertyChanged(nameof(OverlayPosition));
                                OnPropertyChanged(nameof(OverlayDurationSecondsText));
                            },
                            registerHotkeys: () =>
                            {
                                HotkeyRegistrationResult result = _hotkeyRegistrationCoordinator.RegisterChangedGlobalHotkeys(cachedCopy, newSettings);
                                RefreshRegistrationWarningsForAllHotkeys(newSettings);
                                return result;
                            },
                            logHotkeyResults: _hotkeyRegistrationCoordinator.LogApplySettingsResults,
                            registerRoutineHotkeys: settings => RegisterRoutineHotkeysFromSettings(settings, context: "apply-settings"),
                            updateAudioConfiguration: settings => _audio.UpdateRoleConfiguration(settings.DeviceSwitching.Output.SwitchRoles, settings.DeviceSwitching.Input.SwitchRoles),
                            updateOverlayState: _ => ApplyOverlayDisplaySettings(),
                            generateDeviceReferenceFile: GenerateDeviceReferenceFile,
                            syncSettingsDrafts: () =>
                            {
                                if (preserveDrafts)
                                {
                                    using (SuppressAutoSave()) SettingsThemeDraft = pendingThemeDraft;
                                    Updates.SetEnabled(newSettings.Miscellaneous.CheckForUpdates);
                                    _logger.Debug("AppViewModel", "settings-save-preserved-newer-drafts");
                                }
                                else
                                {
                                    SyncSettingsDraftFromCurrentState(resetHotkeyExpansion: false);
                                }
                            },
                            getHotkeyRegistrationWarnings: GetHotkeyRegistrationWarnings);

                        AcknowledgePersistedHotkeys(newSettings, deferWhileEditing: autoSave);
                        PushToTalkModeChanged?.Invoke(newSettings.Hotkeys.Mute.PushToTalkEnabled);
                        applyDialogEffects = applyEffects;
                    });
                });

                if (!autoSave && applyDialogEffects.HasValue)
                {
                    await AppSettingsEntryCoordinator.ShowApplyResultAsync(
                        applyDialogEffects.Value,
                        _dialogs,
                        DialogText.Captions.SettingsWarnings,
                        DialogText.Captions.Success);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "apply-settings-failed", nameof(ApplySettingsAsync), ex);
                if (autoSave && !_isCleaningUp && !_backgroundWorkCts.IsCancellationRequested)
                {
                    _shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
                }
                if (!autoSave)
                {
                    await _dialogs.ShowErrorAsync(ex is InvalidDataException ? ex.Message : "Failed to apply settings.");
                }
            }
            finally
            {
                IsApplyingSettings = false;

                if (!autoSave)
                {
                    QueueAutoSave(nameof(ApplySettingsAsync));
                }

                TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(ApplySettingsAsync));
            }
        }

        private void SaveSettingsWithStartupRegistration(Settings settings, bool forceStartup = false)
        {
            Settings previous = GetCachedSettingsSnapshot() ?? _settings.LoadSettings();
            SettingsPersistenceTransaction.Save(previous, settings, candidate => _settings.SaveSettings(candidate),
                (candidate, persist) =>
                {
                    string opId = AppStartupToggleCoordinator.CreateOperationId();
                    if (TryApplyStartupChangeOverrideForTests is { } apply)
                    {
                        if (!apply(candidate.RunAtStartup, opId)) throw new InvalidOperationException("Failed to update startup registration.");
                        persist();
                        return;
                    }
                    _startup.ApplyRegistration(candidate.RunAtStartup, candidate.Miscellaneous.UseScheduledStartup, persist, opId);
                }, forceStartup);
        }

        private bool TryPrepareSaveContext(
            out SaveEditState editState,
            out string currentOutputHotkey,
            out string currentInputHotkey,
            out SaveValidationResult validationResult)
        {
            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            currentOutputHotkey = Hotkey.ToHotkeyString();
            currentInputHotkey = InputHotkey.ToHotkeyString();
            string currentOutputReverseHotkey = OutputReverseHotkey.ToHotkeyString();
            string currentInputReverseHotkey = InputReverseHotkey.ToHotkeyString();
            int outputCycleCount = AppSettingsWorkflowCoordinator.CountValidCycleDevices(OutputCycleDevices);
            int inputCycleCount = AppSettingsWorkflowCoordinator.CountValidCycleDevices(InputCycleDevices);

            editState = AppSettingsWorkflowCoordinator.BuildSaveEditState(
                OutputCycleDevices,
                InputCycleDevices,
                currentOutputHotkey,
                currentInputHotkey,
                OutputHotkeysEnabled,
                InputHotkeysEnabled,
                cachedCopy);

            validationResult = AppSaveValidationCoordinator.Validate(
                new SaveValidationInput(
                    editState,
                    outputCycleCount,
                    inputCycleCount,
                    OutputHotkeysEnabled,
                    InputHotkeysEnabled,
                    Hotkey.HasMainInput || !string.IsNullOrWhiteSpace(currentOutputReverseHotkey),
                    InputHotkey.HasMainInput || !string.IsNullOrWhiteSpace(currentInputReverseHotkey),
                    TryGetOverlayDurationSeconds(out _)));

            if (validationResult.IsValid)
            {
                return true;
            }

            AppSaveValidationCoordinator.LogFailure(validationResult, _logger);
            return false;
        }

        private async Task SaveSettingsAsync(bool autoSave = false)
        {
            if (IsResettingSettings) return;

            if (!TryPrepareSaveContext(out SaveEditState editState, out string currentOutputHotkey, out string currentInputHotkey, out SaveValidationResult validationResult))
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(validationResult.WarningMessage ?? string.Empty, validationResult.WarningCaption);
                }

                return;
            }

            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            CancellationTokenSource? startupDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts);
            startupDebounceToDispose?.Dispose();

            int outputCycleCount = AppSettingsWorkflowCoordinator.CountValidCycleDevices(OutputCycleDevices);
            int inputCycleCount = AppSettingsWorkflowCoordinator.CountValidCycleDevices(InputCycleDevices);
            bool hasOutputSwitchHotkey = Hotkey.HasMainInput || !string.IsNullOrWhiteSpace(OutputReverseHotkey.ToHotkeyString());
            bool hasInputSwitchHotkey = InputHotkey.HasMainInput || !string.IsNullOrWhiteSpace(InputReverseHotkey.ToHotkeyString());
            bool canWriteOutput = outputCycleCount > 0 && (hasOutputSwitchHotkey || !OutputHotkeysEnabled);
            bool canWriteInput = inputCycleCount > 0 && (hasInputSwitchHotkey || !InputHotkeysEnabled);

            string currentOutputReverseHotkey = OutputReverseHotkey.ToHotkeyString();
            string currentInputReverseHotkey = InputReverseHotkey.ToHotkeyString();
            _ = TryGetOverlayDurationSeconds(out double overlayDurationSeconds);

            Settings newSettings = AppSettingsWorkflowCoordinator.BuildSavedSettings(
                cachedCopy,
                new SaveSettingsBuildInput(
                    OutputCycleDevices,
                    InputCycleDevices,
                    _outputDevices,
                    _inputDevices,
                    editState,
                    canWriteOutput,
                    canWriteInput,
                    currentOutputReverseHotkey,
                    currentInputReverseHotkey,
                    OutputHotkeysEnabled,
                    InputHotkeysEnabled,
                    RunAtStartup,
                    _preserveAudioLevelsBackingField,
                    _overlayEnabledBackingField,
                    _overlayPositionBackingField,
                    overlayDurationSeconds,
                    _themeBackingField,
                    _cachedSettings?.Miscellaneous.RedactLogContent ?? true));

            SettingsCommitValidationResult commitValidation = ValidateSettingsForCommit(newSettings);
            if (commitValidation.HasBlockingIssues)
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(
                        DialogText.Messages.BuildInvalidSettingsBeforeSaving(commitValidation.BlockingMessages),
                        DialogText.Captions.InvalidSettings);
                }

                return;
            }

            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            IsSaving = true;

            SaveSettingsSideEffectResult? saveDialogEffects = null;
            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            SaveSettingsWithStartupRegistration(newSettings);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("AppViewModel", () => $"settings-save-background-failed | error={ex.GetType().Name}", nameof(SaveSettingsAsync), ex);
                            throw;
                        }
                    });

                    IReadOnlyList<string> disconnectedOutput = await Task.Run(() => GetDisconnectedConfiguredDeviceNames(newSettings.DeviceSwitching.Output.CycleDevices, output: true));
                    IReadOnlyList<string> disconnectedInput = await Task.Run(() => GetDisconnectedConfiguredDeviceNames(newSettings.DeviceSwitching.Input.CycleDevices, output: false));

                    await InvokeOnDispatcherAsync(() =>
                    {
                        _logger.Info("AppViewModel", () => $"settings-save-applied | outputCycleCount={newSettings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={newSettings.DeviceSwitching.Input.CycleDevices.Count} startup={RunAtStartup} preserveAudioLevels={_preserveAudioLevelsBackingField} theme={newSettings.Theme}");

                        SaveSettingsSideEffectResult saveEffects = AppSettingsEffectsCoordinator.RunSaveSideEffects(
                            cachedCopy,
                            newSettings,
                            disconnectedOutput,
                            disconnectedInput,
                            persistUiState: () =>
                            {
                                lock (_settingsLock)
                                {
                                    _cachedSettings = newSettings;
                                }

                                UpdateLastSettingsWriteTime();
                                NotifyAutoSaveStateChanged();
                                ApplyPersistedAdvancedTuning(newSettings);
                            },
                            registerSwitchHotkeys: () =>
                            {
                                SwitchHotkeyRegistrationResult result = _hotkeyRegistrationCoordinator.RegisterChangedSwitchHotkeys(cachedCopy, newSettings);
                                RefreshRegistrationWarningsForSwitchHotkeys(newSettings);
                                return result;
                            },
                            logSwitchHotkeyResults: result => _hotkeyRegistrationCoordinator.LogSwitchOnlyFailure(result, context: "save"),
                            updateAudioConfiguration: settings => _audio.UpdateRoleConfiguration(settings.DeviceSwitching.Output.SwitchRoles, settings.DeviceSwitching.Input.SwitchRoles),
                            updateOverlayState: settings =>
                            {
                                _overlay.UpdateEnabled(settings.Overlay.Enabled);
                                _overlay.UpdateDisplayOptions(settings.Overlay.Position, settings.Overlay.DurationSeconds);
                            },
                            getSwitchHotkeyRegistrationWarnings: GetSwitchHotkeyRegistrationWarnings);

                        AcknowledgePersistedHotkeys(newSettings, deferWhileEditing: autoSave, switchOnly: true);
                        saveDialogEffects = saveEffects;
                    });
                });

                if (!autoSave && saveDialogEffects.HasValue)
                {
                    await AppSettingsEntryCoordinator.ShowSaveResultAsync(
                        saveDialogEffects.Value,
                        _dialogs,
                        DialogText.Captions.SettingsWarnings,
                        DialogText.Captions.Success);
                }

                RunBackgroundWork(async shutdownToken =>
                {
                    try
                    {
                        await ApplyPostSaveHotkeyAndMuteStateAsync(shutdownToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AppViewModel", "save-post-failed", nameof(SaveSettingsAsync), ex);
                    }
                }, nameof(SaveSettingsAsync));

                if (!autoSave)
                {
                    ShowBalloonAfterSave = true;
                    _windowState.ShowBalloonOnFirstMinimize = false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "save-failed", nameof(SaveSettingsAsync), ex);
                if (autoSave && !_isCleaningUp && !_backgroundWorkCts.IsCancellationRequested)
                {
                    _shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
                }
                if (!autoSave)
                {
                    await _dialogs.ShowErrorAsync(ex is InvalidDataException ? ex.Message : "Failed to save settings.");
                }
            }
            finally
            {
                IsSaving = false;
                TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(SaveSettingsAsync));
            }
        }

        private async Task ApplyPostSaveHotkeyAndMuteStateAsync(CancellationToken shutdownToken)
        {
            PostSaveMuteApplication? muteApplication = await InvokeOnDispatcherAsync(
                () => (PostSaveMuteApplication?)AppPostSaveCoordinator.BuildMuteApplication(
                    _deafenBackingField,
                    _muteMicBackingField,
                    _muteSoundBackingField),
                fallback: null);

            if (muteApplication is not PostSaveMuteApplication application)
            {
                return;
            }

            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                _audio.SetMicrophoneMute(application.MuteMicrophone);
                _audio.SetPlaybackMute(application.MutePlayback);
            }, shutdownToken);
        }

        private async Task ResetPerAppAudioRoutingAsync()
        {
            AppDialogResult result = await _dialogs.ShowAsync(AppDialogRequest.Confirm(
                "This will clear the per-application audio device assignments saved by Windows and return those applications to the default system devices.\n\nAre you sure you want to continue?",
                DialogText.Captions.ResetPerAppAudio,
                AppDialogKind.Warning,
                "_Clear assignments",
                "_Cancel",
                AppDialogActionStyle.Destructive));

            if (!AppResetCoordinator.ShouldProceed(result))
            {
                _logger.Info("AppViewModel", "reset-per-app-audio-cancelled");
                return;
            }

            try
            {
                PerAppAudioRoutingResetResult resetResult = await Task.Run(_audio.ResetAllPerAppAudioRouting);
                ResetPerAppRoutingDialogPlan dialogPlan = AppResetCoordinator.BuildPerAppRoutingDialogPlan(resetResult);
                await AppResetCoordinator.ShowResetPerAppRoutingDialogAsync(
                    dialogPlan,
                    (message, caption) => _dialogs.ShowInformationAsync(message, caption),
                    (message, caption) => _dialogs.ShowSuccessAsync(message, caption),
                    (message, caption) => _dialogs.ShowErrorAsync(message, caption));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "reset-per-app-audio-failed", nameof(ResetPerAppAudioRoutingAsync), ex);
                await _dialogs.ShowErrorAsync("Failed to reset per-application audio assignments.", DialogText.Captions.ResetPerAppAudio);
            }
        }

        private AutoSaveSuppressionScope SuppressAutoSave()
        {
            Interlocked.Increment(ref _autoSaveSuppressionCount);
            return new AutoSaveSuppressionScope(this);
        }

        private bool IsPersistedAutoSaveEnabled()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            return cachedCopy?.Miscellaneous.AutoSaveEnabled ?? false;
        }

        private void NotifyAutoSaveStateChanged()
        {
            OnPropertyChanged(nameof(IsAutoSaveActive));
            OnPropertyChanged(nameof(IsAutoSavePendingActivation));
        }

        private void QueueAutoSave(string trigger)
        {
            if (_isInitializing || _isCleaningUp || IsResettingSettings)
            {
                return;
            }

            if (Volatile.Read(ref _autoSaveSuppressionCount) > 0)
            {
                return;
            }

            Interlocked.Increment(ref _autoSaveDirtyRevision);
            if (!IsPersistedAutoSaveEnabled()) return;
            if (_isApplyingSettings || _isSaving || IsSavingRoutines)
            {
                return;
            }

            ScheduleAutoSave(trigger);
        }

        private void ScheduleAutoSave(string trigger)
        {
            if (_isInitializing || _isCleaningUp || IsResettingSettings || Volatile.Read(ref _autoSaveSuppressionCount) > 0 || !IsPersistedAutoSaveEnabled())
            {
                return;
            }

            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                ref _autoSaveDebounceCts);

            RunBackgroundWork(async shutdownToken =>
            {
                await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                    nextDebounceCts,
                    ownedDebounce => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _autoSaveDebounceCts, ownedDebounce),
                    async linkedToken =>
                    {
                        await Task.Delay(RuntimeTuningConfig.AutoSaveDebounceMs, linkedToken);
                        await InvokeOnDispatcherAsync(() => RunAutoSaveAsync(trigger));
                    },
                    shutdownToken);
            }, $"auto-save:{trigger}");
        }

        private void TryQueueTrailingAutoSave(long revisionAtStart, string trigger)
        {
            if (Volatile.Read(ref _autoSaveDirtyRevision) == revisionAtStart ||
                _isInitializing ||
                _isCleaningUp ||
                IsResettingSettings ||
                _isApplyingSettings ||
                _isSaving ||
                IsSavingRoutines ||
                Volatile.Read(ref _autoSaveSuppressionCount) > 0 ||
                !IsPersistedAutoSaveEnabled())
            {
                return;
            }

            bool hasPendingChanges =
                (CanAutoApplySettingsDrafts() && HasSettingsDraftDivergedFromCachedSettings()) ||
                HasUiSettingsDivergedFromCachedSettings() ||
                HasRoutineEdits();
            if (hasPendingChanges)
            {
                ScheduleAutoSave($"trailing:{trigger}");
            }
        }

        private async Task RunAutoSaveAsync(string trigger)
        {
            if (_isInitializing || _isCleaningUp || IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines)
            {
                return;
            }

            if (Volatile.Read(ref _autoSaveSuppressionCount) > 0 || !IsPersistedAutoSaveEnabled())
            {
                return;
            }

            _logger.Debug("AppViewModel", () => $"auto-save-triggered | trigger={trigger}");

            if (CanAutoApplySettingsDrafts() && HasSettingsDraftDivergedFromCachedSettings())
            {
                await ApplySettingsAsync(autoSave: true);
            }

            if (_isApplyingSettings || _isSaving || IsSavingRoutines || !IsPersistedAutoSaveEnabled())
            {
                return;
            }

            if (HasUiSettingsDivergedFromCachedSettings())
            {
                await SaveSettingsAsync(autoSave: true);
            }

            if (_isApplyingSettings || _isSaving || IsSavingRoutines || !IsPersistedAutoSaveEnabled())
            {
                return;
            }

            if (HasRoutineEdits())
            {
                await SaveRoutinesAsync(resetRemovedPerAppRouting: false, autoSave: true);
            }
        }

        private sealed class AutoSaveSuppressionScope(AppViewModel owner) : IDisposable
        {
            private readonly AppViewModel _owner = owner;
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Interlocked.Decrement(ref _owner._autoSaveSuppressionCount);
            }
        }
    }
}
