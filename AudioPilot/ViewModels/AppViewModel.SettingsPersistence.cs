using System.IO;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private readonly TimeProvider _autoSaveTimeProvider;
        private int _activeSettingsSaveOperations;
        private TaskCompletionSource? _settingsSaveDrainCompletion;

        private ActiveSettingsSaveScope BeginActiveSettingsSave()
        {
            lock (_settingsLock)
            {
                if (_activeSettingsSaveOperations++ == 0)
                {
                    _settingsSaveDrainCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            return new ActiveSettingsSaveScope(this);
        }

        private void EndActiveSettingsSave()
        {
            TaskCompletionSource? completion = null;
            lock (_settingsLock)
            {
                if (--_activeSettingsSaveOperations == 0)
                {
                    completion = _settingsSaveDrainCompletion;
                    _settingsSaveDrainCompletion = null;
                }
            }
            completion?.TrySetResult();
        }

        private async Task WaitForActiveSettingsSavesAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task? pendingDrain;
                lock (_settingsLock)
                {
                    pendingDrain = _settingsSaveDrainCompletion?.Task;
                }

                if (pendingDrain != null)
                {
                    await pendingDrain.WaitAsync(cancellationToken);
                    continue;
                }

                await _settingsWriteSemaphore.WaitAsync(cancellationToken);
                _settingsWriteSemaphore.Release();

                lock (_settingsLock)
                {
                    if (_settingsSaveDrainCompletion == null)
                    {
                        return;
                    }
                }
            }
        }

        private bool HasUiSettingsDivergedFromCachedSettings() => GetEditedDeviceSettingsTabs() != SettingsFeedbackTabs.None;

        private SettingsFeedbackTabs GetEditedDeviceSettingsTabs()
        {
            Settings? cached = GetCachedSettingsSnapshot();
            if (cached == null) return SettingsFeedbackTabs.None;
            SettingsFeedbackTabs tabs = SettingsFeedbackTabs.None;
            if (EnumerateDeviceSettingChanges(cached, output: true).Any()) tabs |= SettingsFeedbackTabs.Output;
            if (EnumerateDeviceSettingChanges(cached, output: false).Any()) tabs |= SettingsFeedbackTabs.Input;
            return tabs;
        }

        private bool HasPendingLocalEditsForRefresh()
        {
            return HasUiSettingsDivergedFromCachedSettings() || HasSettingsDraftDivergedFromCachedSettings() || HasRoutineEdits();
        }

        private bool CanAutoApplySettingsDrafts()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            return cachedCopy != null && SettingsAutoSaveEnabledDraft == cachedCopy.App.AutoSaveEnabled;
        }

        private bool HasSettingsDraftDivergedFromCachedSettings()
        {
            Settings? cached = GetCachedSettingsSnapshot();
            return cached == null || EnumerateSettingsDraftChanges(cached).Any();
        }

        private void ApplyExternallyReloadedSettings(Settings newSettings, string context = "external-reload")
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => ApplyExternallyReloadedSettings(newSettings, context));
                return;
            }

            Settings? priorSettings = GetCachedSettingsSnapshot();
            bool autoSaveJustEnabled = priorSettings?.App.AutoSaveEnabled == false && newSettings.App.AutoSaveEnabled;
            bool deviceReferenceModeChanged = priorSettings == null
                ? newSettings.Diagnostics.DeviceReferenceFileMode != DeviceReferenceFileMode.Off
                : priorSettings.Diagnostics.DeviceReferenceFileMode != newSettings.Diagnostics.DeviceReferenceFileMode
                    && newSettings.Diagnostics.DeviceReferenceFileMode != DeviceReferenceFileMode.Off;

            using (SuppressAutoSave())
            {
                bool cli = context == "cli";
                bool preserveSwitchEditor = cli && _isSaving;
                Settings? previousSettings = cli ? priorSettings : null;
                bool preserveRoutineEditor = cli && (IsSavingRoutines || (previousSettings != null && !AreRoutineListsEquivalent(Routines, previousSettings.Routines.Items)));
                bool preserveSettingsEditor = cli && _isApplyingSettings;
                AppTheme pendingThemeDraft = SettingsThemeDraft;
                bool preserveThemePreview = previousSettings != null && (Theme != previousSettings.App.Theme || SettingsThemeDraft != previousSettings.App.Theme);
                bool Clean(bool unchanged) => previousSettings == null || unchanged;
                AppExternalReloadCoordinator.Apply(
                    newSettings,
                    new ExternalReloadDependencies(
                        CacheSettings: () =>
                        {
                            lock (_settingsLock)
                            {
                                _cachedSettings = newSettings;
                            }

                            _quickDevicePicker?.RequestDeviceRefresh();
                            NotifyAutoSaveStateChanged();
                        },
                        ApplyLogLevel: () =>
                        {
                            Task cleanup = _logger.ApplyLogLevelAsync(newSettings);
                            if (!cleanup.IsCompletedSuccessfully) RunBackgroundWork(_ => cleanup, "LogLevelCleanup");
                        },
                        ApplyAdvancedTuning: () => _advancedTuning.Apply(newSettings.AdvancedTuning),
                        ApplyOutputCycle: devices => { if (!preserveSwitchEditor && Clean(AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(OutputCycleDevices, previousSettings?.DeviceSwitching.Output.CycleDevices))) ApplyOutputCycleFromSettings(devices); },
                        ApplyInputCycle: devices => { if (!preserveSwitchEditor && Clean(AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(InputCycleDevices, previousSettings?.DeviceSwitching.Input.CycleDevices))) ApplyInputCycleFromSettings(devices); },
                        ApplyRoutines: routines => { if (!preserveRoutineEditor) ApplyRoutinesFromSettings(routines); },
                        LoadOutputHotkey: () => { if (!preserveSwitchEditor && Clean(AreHotkeyStringsEquivalent(Hotkey.ToHotkeyString(), previousSettings?.DeviceSwitching.Output.SwitchHotkey))) Hotkey.LoadFromString(newSettings.DeviceSwitching.Output.SwitchHotkey); },
                        LoadOutputReverseHotkey: () => { if (!preserveSwitchEditor && Clean(AreHotkeyStringsEquivalent(OutputReverseHotkey.ToHotkeyString(), previousSettings?.DeviceSwitching.Output.ReverseSwitchHotkey))) OutputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Output.ReverseSwitchHotkey); },
                        LoadInputHotkey: () => { if (!preserveSwitchEditor && Clean(AreHotkeyStringsEquivalent(InputHotkey.ToHotkeyString(), previousSettings?.DeviceSwitching.Input.SwitchHotkey))) InputHotkey.LoadFromString(newSettings.DeviceSwitching.Input.SwitchHotkey); },
                        LoadInputReverseHotkey: () => { if (!preserveSwitchEditor && Clean(AreHotkeyStringsEquivalent(InputReverseHotkey.ToHotkeyString(), previousSettings?.DeviceSwitching.Input.ReverseSwitchHotkey))) InputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Input.ReverseSwitchHotkey); },
                        ApplyOutputHotkeysEnabled: () => { if (!preserveSwitchEditor && Clean(OutputHotkeysEnabled == previousSettings?.DeviceSwitching.Output.HotkeysEnabled)) OutputHotkeysEnabled = newSettings.DeviceSwitching.Output.HotkeysEnabled; },
                        ApplyInputHotkeysEnabled: () => { if (!preserveSwitchEditor && Clean(InputHotkeysEnabled == previousSettings?.DeviceSwitching.Input.HotkeysEnabled)) InputHotkeysEnabled = newSettings.DeviceSwitching.Input.HotkeysEnabled; },
                        ApplyTheme: () =>
                        {
                            if (!preserveThemePreview) Theme = newSettings.App.Theme;
                            if (!preserveSettingsEditor && Clean(SettingsLanguageDraft == LocalizationService.NormalizeLanguage(previousSettings?.App.Language))) LoadLanguage(newSettings.App.Language);
                        },
                        ApplyOverlayPosition: () => OverlayPosition = newSettings.Overlay.Position,
                        ApplyOverlayDurationText: () => OverlayDurationSecondsText = FormatOverlayDuration(newSettings.Overlay.DurationSeconds),
                        RegisterHotkeys: () =>
                        {
                            Settings registration = newSettings;
                            if (cli && (OutputHotkeysEnabled != newSettings.DeviceSwitching.Output.HotkeysEnabled || InputHotkeysEnabled != newSettings.DeviceSwitching.Input.HotkeysEnabled))
                            {
                                registration = newSettings.Clone();
                                registration.DeviceSwitching.Output.HotkeysEnabled = OutputHotkeysEnabled;
                                registration.DeviceSwitching.Input.HotkeysEnabled = InputHotkeysEnabled;
                            }
                            return _hotkeyRegistrationCoordinator.RegisterAll(registration);
                        },
                        LogHotkeyResults: _hotkeyRegistrationCoordinator.LogApplySettingsResults,
                        RegisterRoutineHotkeys: () => RegisterRoutineHotkeysFromSettings(newSettings, context),
                        ApplyRunAtStartup: () =>
                        {
                            SetRunAtStartupInternal(newSettings.Startup.Enabled);
                            OnPropertyChanged(nameof(RunAtStartup));
                        },
                        ApplyAudioPreservationSettings: () =>
                        {
                            _preserveAudioLevelsBackingField = newSettings.DeviceSwitching.PreserveAudioLevels;
                            KeepMuteStateWhenSwitching = newSettings.DeviceSwitching.KeepMuteStateWhenSwitching;
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
                        SyncSettingsDrafts: () =>
                        {
                            if (preserveSettingsEditor)
                            {
                                SettingsThemeDraft = pendingThemeDraft;
                                PushToTalkModeChanged?.Invoke(newSettings.Hotkeys.Mute.PushToTalkEnabled);
                                Updates.SetEnabled(newSettings.Updates.CheckAutomatically);
                            }
                            else SyncSettingsDraftFromCurrentState(resetHotkeyExpansion: !cli, previousSettings);
                        }));
            }

            if (deviceReferenceModeChanged)
            {
                GenerateDeviceReferenceFile();
            }

            if (autoSaveJustEnabled && HasPendingLocalEditsForRefresh())
            {
                QueueAutoSave("external-reload-autosave-enabled");
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
            RefreshSettingsFieldValidation();

            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            SettingsFeedbackTabs feedbackScope = SettingsFeedbackTabs.Settings;
            if (!TryGetSettingsOverlayDurationSeconds(out double overlayDurationSeconds))
            {
                await ReportSettingsSaveProblemAsync(feedbackScope, DialogText.Messages.InvalidOverlayDuration,
                    reasonPresentation: language => language["Dialog_PleaseEnterAValidOverlayDurationIn"]);
                if (!autoSave)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(new LocalizedText("Dialog_PleaseEnterAValidOverlayDurationIn"),
                        DialogText.Captions.InvalidOverlayDuration, AppDialogKind.Warning));
                }
                return;
            }

            if (!MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out decimal seekStepSeconds, allowDecimalComma: true))
            {
                await ReportSettingsSaveProblemAsync(feedbackScope, MediaSeekStep.InputHelp,
                    reasonPresentation: language => language["Validation_SeekStep"]);
                if (!autoSave)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(new LocalizedText("Validation_SeekStep"),
                        DialogText.Captions.InvalidSettings, AppDialogKind.Warning));
                }
                return;
            }

            if (!TryGetVolumeStepPercent(SettingsForegroundVolumeStepPercentDraft, out decimal foregroundVolumeStepPercent))
            {
                await ReportSettingsSaveProblemAsync(feedbackScope, VolumePercent.StepGuidance,
                    reasonPresentation: language => language["Validation_ForegroundVolumeStep"]);
                if (!autoSave) await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(new LocalizedText("Validation_ForegroundVolumeStep"),
                    DialogText.Captions.InvalidSettings, AppDialogKind.Warning));
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
                    cachedCopy?.Hotkeys.Volume.MasterVolumeStepPercent ?? VolumePercent.DefaultStep,
                    masterStepRequired,
                    out decimal masterVolumeStepPercent) ||
                !TryResolveVolumeStepPercent(
                    SettingsMicVolumeStepPercentDraft,
                    cachedCopy?.Hotkeys.Volume.MicVolumeStepPercent ?? VolumePercent.DefaultStep,
                    micStepRequired,
                    out decimal micVolumeStepPercent))
            {
                await ReportSettingsSaveProblemAsync(feedbackScope, VolumePercent.StepGuidance,
                    reasonPresentation: language => language["Validation_VolumeSteps"]);
                if (!autoSave)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(new LocalizedText("Validation_VolumeSteps"),
                        DialogText.Captions.InvalidSettings, AppDialogKind.Warning));
                }
                return;
            }

            AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts)?.Dispose();

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
                    SettingsPushToTalkEnabledDraft,
                    SettingsKeepMuteStateWhenSwitchingDraft,
                    SettingsQuickDevicePickerHotkeyDraft,
                    _settingsListenMonitorOutputDeviceStableIdDraft,
                    SettingsSuppressDeviceStartupWarningsDraft,
                    VolumeMixerHotkey: SettingsVolumeMixerHotkeyDraft,
                    StartInTray: SettingsStartInTrayDraft,
                    CloseButtonBehavior: SettingsCloseButtonBehaviorDraft,
                    TrayLeftClickAction: SettingsTrayLeftClickActionDraft,
                    MinimizeButtonBehavior: SettingsMinimizeButtonBehaviorDraft,
                    OverlayMonitorMode: SettingsOverlayMonitorModeDraft,
                    ShowDeviceHotplugOverlays: SettingsOverlayHotplugEnabledDraft));
            newSettings.App.Language = SettingsLanguageDraft;

            SettingsCommitValidationResult commitValidation = ValidateSettingsForCommit(newSettings);
            if (commitValidation.HasBlockingIssues)
            {
                await ReportSettingsSaveProblemAsync(feedbackScope, string.Join("\n", commitValidation.BlockingMessages),
                    reasonPresentation: commitValidation.MessagePresentation);
                if (!autoSave)
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                        language => language["Message_PleaseFixTheseSettingsBeforeApplying"] + "\n"
                            + commitValidation.MessagePresentation!(language),
                        DialogText.Captions.InvalidSettings, AppDialogKind.Warning));
                return;
            }

            SettingsFeedbackTabs confirmationTabs = autoSave ? SettingsFeedbackTabs.None
                : GetEditedDeviceSettingsTabs()
                    | (HasRoutineEdits() ? SettingsFeedbackTabs.Routines : SettingsFeedbackTabs.None);
            Settings? feedbackBaseline = cachedCopy;
            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            long themeRevisionAtStart = _themeEditRevision;
            long languageRevisionAtStart = _languageEditRevision;
            long outputEnabledRevisionAtStart = _outputHotkeysEnabledEditRevision;
            long inputEnabledRevisionAtStart = _inputHotkeysEnabledEditRevision;
            SettingsFeedbackTabs previousRetainedFeedbackTabs = _retainedApplyFeedbackTabs;
            if (!autoSave) _retainedApplyFeedbackTabs |= _unsavedFeedbackTabs;
            using var activeSave = BeginActiveSettingsSave();
            IsApplyingSettings = true;

            ApplySettingsSideEffectResult? applyDialogEffects = null;
            StartupRegistrationOutcome startupOutcome = StartupRegistrationOutcome.Applied;
            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    newSettings = RebaseSettingsForSave(cachedCopy, newSettings, out Settings latest);
                    cachedCopy = latest;
                    startupOutcome = await Task.Run(() => SaveSettingsWithStartupRegistration(newSettings));
                    if (CommitSavedSettingsDuringShutdown(newSettings, syncMirroredLiveFields: true)) return;
                    await _logger.ApplyLogLevelAsync(newSettings);
                    await InvokeOnDispatcherAsync(() =>
                    {
                        if (CommitSavedSettingsDuringShutdown(newSettings, syncMirroredLiveFields: true)) return;
                        bool preserveDrafts = Volatile.Read(ref _autoSaveDirtyRevision) != autoSaveRevisionAtStart;
                        AppTheme pendingThemeDraft = SettingsThemeDraft;
                        bool preserveOutputEnabled = _outputHotkeysEnabledEditRevision != outputEnabledRevisionAtStart;
                        bool preserveInputEnabled = _inputHotkeysEnabledEditRevision != inputEnabledRevisionAtStart;
                        Settings registrationSettings = newSettings;
                        if (preserveOutputEnabled || preserveInputEnabled)
                        {
                            registrationSettings = newSettings.Clone();
                            if (preserveOutputEnabled) registrationSettings.DeviceSwitching.Output.HotkeysEnabled = OutputHotkeysEnabled;
                            if (preserveInputEnabled) registrationSettings.DeviceSwitching.Input.HotkeysEnabled = InputHotkeysEnabled;
                        }
                        ApplySettingsSideEffectResult applyEffects = AppSettingsEffectsCoordinator.RunApplySideEffects(
                            cachedCopy,
                            newSettings,
                            outputRoles.Count == 0,
                            inputRoles.Count == 0,
                            persistUiState: () =>
                            {
                                using var suppression = SuppressAutoSave();
                                lock (_settingsLock)
                                {
                                    _cachedSettings = newSettings;
                                }

                                _quickDevicePicker?.RequestDeviceRefresh();
                                UpdateLastSettingsWriteTime();
                                SetRunAtStartupInternal(newSettings.Startup.Enabled);
                                OnPropertyChanged(nameof(RunAtStartup));
                                NotifyAutoSaveStateChanged();
                                _advancedTuning.Apply(newSettings.AdvancedTuning);

                                _preserveAudioLevelsBackingField = newSettings.DeviceSwitching.PreserveAudioLevels;
                                KeepMuteStateWhenSwitching = newSettings.DeviceSwitching.KeepMuteStateWhenSwitching;
                                OnPropertyChanged(nameof(PreserveAudioLevels));

                                if (!preserveOutputEnabled)
                                {
                                    _outputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Output.HotkeysEnabled;
                                    OnPropertyChanged(nameof(OutputHotkeysEnabled));
                                }
                                if (!preserveInputEnabled)
                                {
                                    _inputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Input.HotkeysEnabled;
                                    OnPropertyChanged(nameof(InputHotkeysEnabled));
                                }

                                if (_languageEditRevision == languageRevisionAtStart)
                                {
                                    LoadLanguage(newSettings.App.Language);
                                }

                                if (_themeEditRevision == themeRevisionAtStart)
                                {
                                    Theme = newSettings.App.Theme;
                                }

                                _overlayPositionBackingField = newSettings.Overlay.Position;
                                _overlayDurationSecondsTextBackingField = FormatOverlayDuration(newSettings.Overlay.DurationSeconds);
                                _overlayEnabledBackingField = newSettings.Overlay.Enabled;
                                OnPropertyChanged(nameof(OverlayEnabled));
                                OnPropertyChanged(nameof(OverlayPosition));
                                OnPropertyChanged(nameof(OverlayDurationSecondsText));
                            },
                            registerHotkeys: () =>
                            {
                                HotkeyRegistrationResult result = _hotkeyRegistrationCoordinator.RegisterChangedGlobalHotkeys(cachedCopy, registrationSettings);
                                RefreshRegistrationWarningsForAllHotkeys(registrationSettings);
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
                                    Updates.SetEnabled(newSettings.Updates.CheckAutomatically);
                                    _logger.Debug("AppViewModel", "settings-save-preserved-newer-drafts");
                                }
                                else
                                {
                                    SyncSettingsDraftFromCurrentState(resetHotkeyExpansion: false);
                                }
                            },
                            getHotkeyRegistrationWarnings: GetHotkeyRegistrationWarnings);

                        AcknowledgePersistedHotkeys(newSettings, deferWhileEditing: autoSave);
                        SettingsFeedbackTabs completed = SettingsFeedbackTabs.Settings;
                        ClearSettingsSaveProblems(completed, autoSaveRevisionAtStart);
                        PushToTalkModeChanged?.Invoke(newSettings.Hotkeys.Mute.PushToTalkEnabled);
                        applyDialogEffects = applyEffects;
                    });
                });

                _shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
                if (!IsRuntimeShutdownRequested
                    && !AppRuntimeHost.HasConfiguredMediaHotkeys(cachedCopy)
                    && AppRuntimeHost.HasConfiguredMediaHotkeys(newSettings))
                {
                    RunBackgroundWork(MediaKeyHelper.PrewarmSystemMediaCommandsAsync, "media-command-prewarm");
                }
                CancellationToken dialogShutdownToken = GetDialogShutdownToken();
                if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested && applyDialogEffects.HasValue)
                {
                    await AppSettingsEntryCoordinator.ShowApplyResultAsync(
                        applyDialogEffects.Value,
                        _dialogs,
                        DialogText.Captions.SettingsWarnings,
                        DialogText.Captions.Success,
                        message => ShowSettingsSaveFeedbackAsync(message, SettingsFeedbackTabs.Settings
                            | (confirmationTabs & GetChangedDeviceSaveTabs(feedbackBaseline, newSettings))),
                        dialogShutdownToken);
                }
                if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested && startupOutcome == StartupRegistrationOutcome.SchedulerUnavailable)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                        new LocalizedText("Message_StartupDisableSchedulerUnavailable"),
                        DialogText.Captions.Warning, AppDialogKind.Warning), cancellationToken: dialogShutdownToken);
                }
            }
            catch (OperationCanceledException) when (IsRuntimeShutdownRequested) { }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "apply-settings-failed", nameof(ApplySettingsAsync), ex);
                await ReportSettingsWriteFailureAsync(feedbackScope, ex);
                if (autoSave && !_isCleaningUp && !_backgroundWorkCts.IsCancellationRequested)
                {
                    await _shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
                }
                if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested)
                {
                    try
                    {
                        await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                            SettingsFailure.ForDialog(ex, new("Message_FailedToApplySettings")), DialogText.Captions.Error, AppDialogKind.Error), GetDialogShutdownToken());
                    }
                    catch (OperationCanceledException) when (IsRuntimeShutdownRequested) { }
                }
            }
            finally
            {
                IsApplyingSettings = false;
                try
                {
                    if (!autoSave && !IsRuntimeShutdownRequested && applyDialogEffects.HasValue && IsPersistedAutoSaveEnabled()
                        && confirmationTabs != SettingsFeedbackTabs.None)
                    {
                        SettingsFeedbackTabs savedTabs = await SavePendingAutoSaveTabsAsync();
                        savedTabs &= confirmationTabs;
                        if (savedTabs != SettingsFeedbackTabs.None)
                            await ShowSettingsSaveFeedbackAsync(DialogText.Messages.BuildSettingsSavedSuccessfully(), savedTabs);
                    }
                }
                finally
                {
                    _retainedApplyFeedbackTabs = previousRetainedFeedbackTabs;
                    if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested)
                    {
                        SetSettingsFeedback(GetUnsavedFeedbackTabs());
                    }
                    TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(ApplySettingsAsync));
                }
            }
        }

        private Settings RebaseSettingsForSave(Settings? baseline, Settings candidate, out Settings latest)
        {
            latest = GetCachedSettingsSnapshot() ?? new Settings();
            SettingsMigrationService.EnsureWritableVersion(latest.SchemaVersion);
            if (baseline != null && ReferenceEquals(latest, baseline))
            {
                return candidate;
            }

            Settings merged = SettingsSavePatch.Capture(baseline, candidate).Apply(latest);
            SettingsCommitValidationResult validation = ValidateSettingsForCommit(merged);
            if (validation.HasBlockingIssues)
                throw SettingsFailure.WithPresentation(
                    new InvalidDataException(DialogText.Messages.BuildInvalidSettingsBeforeSaving(validation.BlockingMessages)),
                    language => language["Message_PleaseFixTheseSettingsBeforeSaving"] + "\n" + validation.MessagePresentation!(language));
            return merged;
        }

        private StartupRegistrationOutcome SaveSettingsWithStartupRegistration(Settings settings, bool forceStartup = false)
        {
            try
            {
                Settings previous = GetCachedSettingsSnapshot() ?? _settings.LoadSettings();
                StartupRegistrationOutcome outcome = StartupRegistrationOutcome.Applied;
                SettingsPersistenceTransaction.Save(previous, settings, candidate => _settings.SaveSettings(candidate),
                    (candidate, persist) =>
                    {
                        string opId = AppStartupToggleCoordinator.CreateOperationId();
                        if (TryApplyStartupChangeOverrideForTests is { } apply)
                        {
                            if (!apply(candidate.Startup.Enabled, opId)) throw new InvalidOperationException("Failed to update startup registration.");
                            persist();
                            return;
                        }
                        outcome = _startup.ApplyRegistration(candidate.Startup.Enabled, candidate.Startup.UseTaskScheduler, persist, opId,
                            allowIncompleteDisable: !candidate.Startup.Enabled && (forceStartup || previous.Startup.Enabled));
                    }, forceStartup);
                return outcome;
            }
            catch (Exception ex) { _settings.RecordSaveFailure(ex); throw; }
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

        private async Task<SettingsFeedbackTabs> SaveSettingsAsync(bool autoSave = false)
        {
            if (IsResettingSettings) return SettingsFeedbackTabs.None;

            if (!TryPrepareSaveContext(out SaveEditState editState, out string currentOutputHotkey, out string currentInputHotkey, out SaveValidationResult validationResult))
            {
                await ReportSettingsSaveProblemAsync(SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input,
                    validationResult.WarningMessage ?? "Review device and hotkey settings.",
                    reasonPresentation: language => validationResult.WarningText == null ? validationResult.WarningMessage ?? string.Empty
                        : language.Format(validationResult.WarningText));
                if (!autoSave)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(validationResult.WarningText!,
                        validationResult.WarningCaption, AppDialogKind.Warning));
                }

                return SettingsFeedbackTabs.None;
            }

            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            CancellationTokenSource? startupDebounceToDispose = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts);
            startupDebounceToDispose?.Dispose();

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
                    currentOutputReverseHotkey,
                    currentInputReverseHotkey,
                    OutputHotkeysEnabled,
                    InputHotkeysEnabled,
                    RunAtStartup,
                    _preserveAudioLevelsBackingField,
                    _overlayEnabledBackingField,
                    _overlayPositionBackingField,
                    overlayDurationSeconds,
                    cachedCopy?.Diagnostics.RedactLogContent ?? true,
                    KeepMuteStateWhenSwitching));

            SettingsCommitValidationResult commitValidation = ValidateSettingsForCommit(newSettings);
            if (commitValidation.HasBlockingIssues)
            {
                await ReportSettingsSaveProblemAsync(SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input, string.Join("\n", commitValidation.BlockingMessages),
                    reasonPresentation: commitValidation.MessagePresentation);
                if (!autoSave)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                        language => language["Message_PleaseFixTheseSettingsBeforeSaving"] + "\n"
                            + commitValidation.MessagePresentation!(language),
                        DialogText.Captions.InvalidSettings, AppDialogKind.Warning));
                }

                return SettingsFeedbackTabs.None;
            }

            SettingsFeedbackTabs feedbackTabs = GetUnsavedFeedbackTabs() & (SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input);
            bool hadThemeDraft = cachedCopy != null && SettingsThemeDraft != cachedCopy.App.Theme;
            long themeRevisionAtStart = _themeEditRevision;
            if (!autoSave && feedbackTabs == SettingsFeedbackTabs.None)
                feedbackTabs = SelectedSettingsTabIndex == 1 ? SettingsFeedbackTabs.Input : SettingsFeedbackTabs.Output;
            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            using var activeSave = BeginActiveSettingsSave();
            IsSaving = true;

            SaveSettingsSideEffectResult? saveDialogEffects = null;
            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    newSettings = RebaseSettingsForSave(cachedCopy, newSettings, out Settings latest);
                    cachedCopy = latest;
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

                    if (CommitSavedSettingsDuringShutdown(newSettings)) return;
                    IReadOnlyList<string> disconnectedOutput = await Task.Run(() => GetDisconnectedConfiguredDeviceNames(newSettings.DeviceSwitching.Output.CycleDevices, output: true));
                    IReadOnlyList<string> disconnectedInput = await Task.Run(() => GetDisconnectedConfiguredDeviceNames(newSettings.DeviceSwitching.Input.CycleDevices, output: false));

                    await InvokeOnDispatcherAsync(() =>
                    {
                        if (CommitSavedSettingsDuringShutdown(newSettings)) return;
                        _logger.Info("AppViewModel", () => $"settings-save-applied | outputCycleCount={newSettings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={newSettings.DeviceSwitching.Input.CycleDevices.Count} startup={RunAtStartup} preserveAudioLevels={_preserveAudioLevelsBackingField} theme={newSettings.App.Theme}");

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

                                _quickDevicePicker?.RequestDeviceRefresh();
                                UpdateLastSettingsWriteTime();
                                NotifyAutoSaveStateChanged();
                                _advancedTuning.Apply(newSettings.AdvancedTuning);
                                if (Volatile.Read(ref _autoSaveDirtyRevision) == autoSaveRevisionAtStart)
                                {
                                    using var suppression = SuppressAutoSave();
                                    if (!AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(OutputCycleDevices, newSettings.DeviceSwitching.Output.CycleDevices))
                                        ApplyOutputCycleFromSettings(newSettings.DeviceSwitching.Output.CycleDevices);
                                    if (!AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(InputCycleDevices, newSettings.DeviceSwitching.Input.CycleDevices))
                                        ApplyInputCycleFromSettings(newSettings.DeviceSwitching.Input.CycleDevices);
                                    if (!AreHotkeyStringsEquivalent(Hotkey.ToHotkeyString(), newSettings.DeviceSwitching.Output.SwitchHotkey))
                                        Hotkey.LoadFromString(newSettings.DeviceSwitching.Output.SwitchHotkey);
                                    if (!AreHotkeyStringsEquivalent(OutputReverseHotkey.ToHotkeyString(), newSettings.DeviceSwitching.Output.ReverseSwitchHotkey))
                                        OutputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Output.ReverseSwitchHotkey);
                                    if (!AreHotkeyStringsEquivalent(InputHotkey.ToHotkeyString(), newSettings.DeviceSwitching.Input.SwitchHotkey))
                                        InputHotkey.LoadFromString(newSettings.DeviceSwitching.Input.SwitchHotkey);
                                    if (!AreHotkeyStringsEquivalent(InputReverseHotkey.ToHotkeyString(), newSettings.DeviceSwitching.Input.ReverseSwitchHotkey))
                                        InputReverseHotkey.LoadFromString(newSettings.DeviceSwitching.Input.ReverseSwitchHotkey);
                                    _outputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Output.HotkeysEnabled;
                                    _inputHotkeysEnabledBackingField = newSettings.DeviceSwitching.Input.HotkeysEnabled;
                                    _preserveAudioLevelsBackingField = newSettings.DeviceSwitching.PreserveAudioLevels;
                                    KeepMuteStateWhenSwitching = newSettings.DeviceSwitching.KeepMuteStateWhenSwitching;
                                    _overlayEnabledBackingField = newSettings.Overlay.Enabled;
                                    _overlayPositionBackingField = newSettings.Overlay.Position;
                                    _overlayDurationSecondsTextBackingField = FormatOverlayDuration(newSettings.Overlay.DurationSeconds);
                                    OnPropertyChanged(nameof(OutputHotkeysEnabled));
                                    OnPropertyChanged(nameof(InputHotkeysEnabled));
                                    OnPropertyChanged(nameof(PreserveAudioLevels));
                                    OnPropertyChanged(nameof(OverlayEnabled));
                                    OnPropertyChanged(nameof(OverlayPosition));
                                    OnPropertyChanged(nameof(OverlayDurationSecondsText));
                                }
                                if (!hadThemeDraft && _themeEditRevision == themeRevisionAtStart)
                                {
                                    using var suppression = SuppressAutoSave();
                                    Theme = newSettings.App.Theme;
                                }
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
                                _overlay.UpdateDisplayOptions(settings.Overlay.Position, settings.Overlay.DurationSeconds, settings.Overlay.MonitorMode);
                            },
                            getSwitchHotkeyRegistrationWarnings: GetSwitchHotkeyRegistrationWarnings);

                        AcknowledgePersistedHotkeys(newSettings, deferWhileEditing: autoSave, switchOnly: true);
                        ClearSettingsSaveProblems(SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input, autoSaveRevisionAtStart);
                        saveDialogEffects = saveEffects;
                    });
                });

                _shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
                CancellationToken dialogShutdownToken = GetDialogShutdownToken();
                if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested && saveDialogEffects.HasValue)
                {
                    await AppSettingsEntryCoordinator.ShowSaveResultAsync(
                        saveDialogEffects.Value,
                        _dialogs,
                        DialogText.Captions.SettingsWarnings,
                        DialogText.Captions.Success,
                        message => ShowSettingsSaveFeedbackAsync(message, feedbackTabs),
                        dialogShutdownToken);
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
                return feedbackTabs;
            }
            catch (OperationCanceledException) when (IsRuntimeShutdownRequested)
            {
                return feedbackTabs;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "save-failed", nameof(SaveSettingsAsync), ex);
                await ReportSettingsWriteFailureAsync(SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input, ex);
                if (autoSave && !_isCleaningUp && !_backgroundWorkCts.IsCancellationRequested)
                {
                    await _shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
                }
                if (!autoSave && !_isCleaningUp && !IsRuntimeShutdownRequested)
                {
                    try
                    {
                        await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                            SettingsFailure.ForDialog(ex, new("Message_FailedToSaveSettings")), DialogText.Captions.Error, AppDialogKind.Error), GetDialogShutdownToken());
                    }
                    catch (OperationCanceledException) when (IsRuntimeShutdownRequested) { }
                }
                return SettingsFeedbackTabs.None;
            }
            finally
            {
                IsSaving = false;
                TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(SaveSettingsAsync));
            }
        }

        private readonly record struct PostSaveMuteApplication(bool MuteMicrophone, bool MutePlayback);

        private CancellationToken GetDialogShutdownToken()
            => IsRuntimeShutdownRequested || !ShutdownToken.IsCancellationRequested ? ShutdownToken : CancellationToken.None;

        private bool CommitSavedSettingsDuringShutdown(Settings settings, bool syncMirroredLiveFields = false)
        {
            if (Volatile.Read(ref _runtimeShutdownStarted) == 0) return false;
            lock (_settingsLock) _cachedSettings = settings;
            if (syncMirroredLiveFields)
            {
                SetRunAtStartupInternal(settings.Startup.Enabled);
                _preserveAudioLevelsBackingField = settings.DeviceSwitching.PreserveAudioLevels;
                _audio.KeepMuteStateWhenSwitching = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
                _overlayEnabledBackingField = settings.Overlay.Enabled;
                _overlayPositionBackingField = settings.Overlay.Position;
                _overlayDurationSecondsTextBackingField = FormatOverlayDuration(settings.Overlay.DurationSeconds);
            }
            return true;
        }

        private async Task ApplyPostSaveHotkeyAndMuteStateAsync(CancellationToken shutdownToken)
        {
            if (IsUpdatePreparationBlocked) return;
            PostSaveMuteApplication? muteApplication = await InvokeOnDispatcherAsync(
                () => (PostSaveMuteApplication?)new PostSaveMuteApplication(
                    _muteMicBackingField || _deafenBackingField,
                    _muteSoundBackingField || _deafenBackingField),
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

        private bool _isResettingPerAppAudio;

        private bool CanWritePerAppAudioReset() => !_isCleaningUp && !IsRuntimeShutdownRequested && !IsUpdateAdmissionBlocked;

        private async Task<PerAppAudioRoutingResetResult> ResetPerAppAudioRoutingCoreAsync(CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken, cancellationToken);
            PerAppAudioRoutingResetResult result = default;
            await _routineRuntime.Lifetime.ApplyRoutingAsync(CanWritePerAppAudioReset, async () =>
            {
                _isResettingPerAppAudio = true;
                try
                {
                    result = await _audio.ResetAllPerAppAudioRoutingAsync(lifetime.Token);
                    if (result.Success)
                    {
                        _routineRuntime.Lifetime.MarkAllRoutingManuallyOverridden();
                        _routineRuntime.UpdateRoutingSessionMonitoring();
                    }
                    return result.Success;
                }
                finally { _isResettingPerAppAudio = false; }
            }, lifetime.Token);
            return result;
        }

        private async Task ResetPerAppAudioRoutingAsync()
        {
            if (!CanWritePerAppAudioReset()) return;
            CancellationToken shutdownToken = ShutdownToken;
            try
            {
                AppDialogResult result = await _dialogs.ShowAsync(AppDialogRequest.ConfirmResetPerAppAudio(), shutdownToken);

                if (!CanWritePerAppAudioReset() || shutdownToken.IsCancellationRequested) return;
                if (!AppResetCoordinator.ShouldProceed(result))
                {
                    _logger.Info("AppViewModel", "reset-per-app-audio-cancelled");
                    return;
                }

                PerAppAudioRoutingResetResult resetResult = await ResetPerAppAudioRoutingCoreAsync(shutdownToken);
                if (!CanWritePerAppAudioReset() || shutdownToken.IsCancellationRequested) return;
                ResetPerAppRoutingDialogPlan dialogPlan = AppResetCoordinator.BuildPerAppRoutingDialogPlan(resetResult);
                await AppResetCoordinator.ShowResetPerAppRoutingDialogAsync(
                    dialogPlan,
                    (message, caption) => _dialogs.ShowInformationAsync(message, caption, cancellationToken: shutdownToken),
                    (message, caption) => _dialogs.ShowSuccessAsync(message, caption, cancellationToken: shutdownToken),
                    (message, caption) => _dialogs.ShowErrorAsync(message, caption, cancellationToken: shutdownToken));
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_isCleaningUp || shutdownToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!CanWritePerAppAudioReset() || shutdownToken.IsCancellationRequested) return;
                _logger.Error("AppViewModel", "reset-per-app-audio-failed", nameof(ResetPerAppAudioRoutingAsync), ex);
                await _dialogs.ShowErrorAsync("Failed to reset per-application audio assignments.", DialogText.Captions.ResetPerAppAudio, cancellationToken: shutdownToken);
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
            return cachedCopy?.App.AutoSaveEnabled ?? false;
        }

        private void NotifyAutoSaveStateChanged()
        {
            OnPropertyChanged(nameof(IsAutoSaveActive));
            OnPropertyChanged(nameof(IsAutoSavePendingActivation));
        }

        private void QueueAutoSave(string trigger)
        {
            if (Services.Updates.AppUpdateLifecycleSession.Current?.StartupBlocked == true) return;
            if (_isInitializing || _isCleaningUp || IsResettingSettings)
            {
                return;
            }

            if (Volatile.Read(ref _autoSaveSuppressionCount) > 0)
            {
                return;
            }

            Interlocked.Increment(ref _autoSaveDirtyRevision);
            QueueSettingsFeedbackRefresh();
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
                        await Task.Delay(TimeSpan.FromMilliseconds(RuntimeTuningConfig.AutoSaveDebounceMs), _autoSaveTimeProvider, linkedToken);
                        await InvokeOnDispatcherAsync(() => RunAutoSaveAsync(trigger));
                    },
                    shutdownToken);
            }, $"auto-save:{trigger}", coalescingKey: nameof(ScheduleAutoSave),
                onDiscarded: () => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _autoSaveDebounceCts, nextDebounceCts));
        }

        private void TryQueueTrailingAutoSave(long revisionAtStart, string trigger)
        {
            if (Volatile.Read(ref _autoSaveDirtyRevision) == revisionAtStart ||
                _isInitializing ||
                _isCleaningUp ||
                IsRuntimeShutdownRequested ||
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

            await SavePendingAutoSaveTabsAsync();
        }

        /// <summary>Shares pending tab saves with the final shutdown flush, which runs before cleanup starts.</summary>
        private async Task<SettingsFeedbackTabs> SavePendingAutoSaveTabsAsync()
        {
            if (_isInitializing || _isCleaningUp || IsResettingSettings
                || _isApplyingSettings || _isSaving || IsSavingRoutines
                || Volatile.Read(ref _autoSaveSuppressionCount) > 0 || !IsPersistedAutoSaveEnabled())
            {
                return SettingsFeedbackTabs.None;
            }

            SettingsFeedbackTabs savedTabs = SettingsFeedbackTabs.None;
            if (HasUiSettingsDivergedFromCachedSettings())
            {
                savedTabs |= await SaveSettingsAsync(autoSave: true);
            }

            if (_isCleaningUp || IsResettingSettings
                || _isApplyingSettings || _isSaving || IsSavingRoutines
                || Volatile.Read(ref _autoSaveSuppressionCount) > 0 || !IsPersistedAutoSaveEnabled())
            {
                return savedTabs;
            }

            if (HasRoutineEdits() && await SaveRoutinesAsync(resetRemovedPerAppRouting: false, autoSave: true))
            {
                savedTabs |= SettingsFeedbackTabs.Routines;
            }
            return savedTabs;
        }

        private sealed class ActiveSettingsSaveScope(AppViewModel owner) : IDisposable
        {
            private readonly AppViewModel _owner = owner;
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.EndActiveSettingsSave();
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
