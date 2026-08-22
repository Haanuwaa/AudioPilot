using System.ComponentModel;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private void SetSettingsListenMonitorOutputDraft(string? deviceId, string? deviceName)
        {
            string normalizedId = deviceId?.Trim() ?? string.Empty;
            string resolvedName = ResolveSettingsListenMonitorOutputDeviceName(normalizedId, deviceName);

            bool idChanged = !string.Equals(_settingsListenMonitorOutputDeviceIdDraft, normalizedId, StringComparison.Ordinal);
            bool nameChanged = !string.Equals(_settingsListenMonitorOutputDeviceNameDraft, resolvedName, StringComparison.Ordinal);
            if (!idChanged && !nameChanged)
            {
                return;
            }

            _settingsListenMonitorOutputDeviceIdDraft = normalizedId;
            _settingsListenMonitorOutputDeviceNameDraft = resolvedName;

            if (idChanged)
            {
                OnPropertyChanged(nameof(SettingsListenMonitorOutputDeviceIdDraft));
            }
        }

        private string ResolveSettingsListenMonitorOutputDeviceName(string deviceId, string? fallbackName)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return string.Empty;
            }

            CycleDevice resolved = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                new CycleDevice { Id = deviceId, Name = fallbackName ?? string.Empty },
                _outputDevices);

            return resolved.Name?.Trim() ?? string.Empty;
        }

        private void SetSettingsHotkeyDraft(HotkeyViewModel draft, string? value, string propertyName)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(draft.ToHotkeyString(), normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrEmpty(normalized))
            {
                draft.Reset();
            }
            else
            {
                draft.LoadFromString(normalized);
            }

            OnPropertyChanged(propertyName);

            if (IsVolumeControlHotkeyProperty(propertyName))
            {
                RefreshVolumeControlExpansionState();
            }
        }

        private void WireSettingsHotkeyDraft(HotkeyViewModel draft, string propertyName)
        {
            void handler(object? _, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(HotkeyViewModel.DisplayText))
                {
                    OnPropertyChanged(propertyName);
                    RefreshHotkeyConflictIndicators();
                }
            }

            draft.PropertyChanged += handler;
            _hotkeyDraftHandlers.Add((draft, handler));
        }

        private static (bool MasterExpanded, bool MicExpanded) ResolveVolumeControlExpansionState(bool hasMasterHotkeys, bool hasMicHotkeys)
        {
            if (!hasMasterHotkeys && !hasMicHotkeys)
            {
                return (true, false);
            }

            return (hasMasterHotkeys, hasMicHotkeys);
        }

        private static bool IsVolumeControlHotkeyProperty(string propertyName)
        {
            return string.Equals(propertyName, nameof(SettingsMasterVolumeUpHotkeyDraft), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(SettingsMasterVolumeDownHotkeyDraft), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(SettingsMicVolumeUpHotkeyDraft), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(SettingsMicVolumeDownHotkeyDraft), StringComparison.Ordinal);
        }

        private bool HasConfiguredMasterVolumeHotkeys()
        {
            return !string.IsNullOrWhiteSpace(SettingsMasterVolumeUpHotkeyDraft) ||
                   !string.IsNullOrWhiteSpace(SettingsMasterVolumeDownHotkeyDraft);
        }

        private bool HasConfiguredMicVolumeHotkeys()
        {
            return !string.IsNullOrWhiteSpace(SettingsMicVolumeUpHotkeyDraft) ||
                   !string.IsNullOrWhiteSpace(SettingsMicVolumeDownHotkeyDraft);
        }

        private void RefreshVolumeControlExpansionState()
        {
            (bool masterExpanded, bool micExpanded) = ResolveVolumeControlExpansionState(
                HasConfiguredMasterVolumeHotkeys(),
                HasConfiguredMicVolumeHotkeys());

            SettingsMasterVolumeControlsExpanded = masterExpanded;
            SettingsMicVolumeControlsExpanded = micExpanded;
        }

        private void WireHotkeyDraft(HotkeyViewModel draft, string propertyName)
        {
            void handler(object? _, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(HotkeyViewModel.DisplayText))
                {
                    OnPropertyChanged(propertyName);
                    RefreshHotkeyConflictIndicators();
                }
            }

            draft.PropertyChanged += handler;
            _hotkeyDraftHandlers.Add((draft, handler));
        }

        private void RefreshHotkeyConflictIndicators()
        {
            var updated = BuildDuplicateHotkeyKeySet(BuildCurrentHotkeySnapshot());
            if (_hotkeyConflictKeys.SetEquals(updated))
            {
                return;
            }

            _hotkeyConflictKeys.Clear();
            _hotkeyConflictKeys.UnionWith(updated);

            foreach (HotkeyViewModel draft in EnumerateHotkeyViewModels())
            {
                bool hasDuplicate = TryNormalizeHotkey(draft.ToHotkeyString(), out string normalized)
                    && _hotkeyConflictKeys.Contains(normalized);
                draft.SetDuplicateWarning(hasDuplicate);
            }

            OnPropertyChanged(nameof(OutputHotkeyHasConflict));
            OnPropertyChanged(nameof(OutputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(InputHotkeyHasConflict));
            OnPropertyChanged(nameof(InputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsToggleAppVisibilityHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsShowCurrentTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPlayPauseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsNextTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPreviousTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteMicHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteSoundHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsDeafenHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsListenToInputHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeDownHotkeyHasConflict));
        }

        private void RefreshRegistrationWarningsForAllHotkeys(Settings settings)
        {
            ApplyRegistrationWarning(Hotkey, AppConstants.Hotkeys.OutputSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.SwitchHotkey));
            ApplyRegistrationWarning(OutputReverseHotkey, AppConstants.Hotkeys.OutputReverseSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.ReverseSwitchHotkey));
            ApplyRegistrationWarning(InputHotkey, AppConstants.Hotkeys.InputSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.SwitchHotkey));
            ApplyRegistrationWarning(InputReverseHotkey, AppConstants.Hotkeys.InputReverseSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.ReverseSwitchHotkey));
            ApplyRegistrationWarning(SettingsToggleAppVisibilityHotkeyDraftCapture, AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.App.ToggleAppVisibility));
            ApplyRegistrationWarning(SettingsShowCurrentTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaShowCurrentTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.ShowCurrentTrack));
            ApplyRegistrationWarning(SettingsPlayPauseHotkeyDraftCapture, AppConstants.Hotkeys.MediaPlayPauseId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.PlayPause));
            ApplyRegistrationWarning(SettingsNextTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaNextTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.NextTrack));
            ApplyRegistrationWarning(SettingsPreviousTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaPrevTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.PreviousTrack));
            ApplyRegistrationWarning(SettingsMuteMicHotkeyDraftCapture, AppConstants.Hotkeys.MuteMicId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Mic));
            ApplyRegistrationWarning(SettingsMuteSoundHotkeyDraftCapture, AppConstants.Hotkeys.MuteSoundId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Sound));
            ApplyRegistrationWarning(SettingsDeafenHotkeyDraftCapture, AppConstants.Hotkeys.DeafenId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Deafen));
            ApplyRegistrationWarning(SettingsListenToInputHotkeyDraftCapture, AppConstants.Hotkeys.ListenToInputHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Listen.ListenToInput));
            ApplyRegistrationWarning(SettingsMasterVolumeUpHotkeyDraftCapture, AppConstants.Hotkeys.MasterVolumeUpHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MasterUp));
            ApplyRegistrationWarning(SettingsMasterVolumeDownHotkeyDraftCapture, AppConstants.Hotkeys.MasterVolumeDownHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MasterDown));
            ApplyRegistrationWarning(SettingsMicVolumeUpHotkeyDraftCapture, AppConstants.Hotkeys.MicVolumeUpHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MicUp));
            ApplyRegistrationWarning(SettingsMicVolumeDownHotkeyDraftCapture, AppConstants.Hotkeys.MicVolumeDownHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MicDown));

            RaiseHotkeyWarningPropertyChanged();
        }

        private void RefreshRegistrationWarningsForSwitchHotkeys(Settings settings)
        {
            ApplyRegistrationWarning(Hotkey, AppConstants.Hotkeys.OutputSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.SwitchHotkey));
            ApplyRegistrationWarning(OutputReverseHotkey, AppConstants.Hotkeys.OutputReverseSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.ReverseSwitchHotkey));
            ApplyRegistrationWarning(InputHotkey, AppConstants.Hotkeys.InputSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.SwitchHotkey));
            ApplyRegistrationWarning(InputReverseHotkey, AppConstants.Hotkeys.InputReverseSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.ReverseSwitchHotkey));

            RaiseHotkeyWarningPropertyChanged();
        }

        private void ApplyRegistrationWarning(HotkeyViewModel draft, int hotkeyId, bool isConfigured)
        {
            if (!isConfigured)
            {
                draft.ClearRegistrationWarning();
                return;
            }

            HotkeyRegistrationOutcome outcome = _hotkeys.GetLastRegistrationOutcome(hotkeyId);
            switch (outcome.Kind)
            {
                case HotkeyRegistrationOutcomeKind.ExternalConflict:
                    draft.SetRegistrationWarning(HotkeyWarningKind.ExternalConflict, "Unavailable on this system right now. Windows or another app may already be using this hotkey.");
                    break;

                case HotkeyRegistrationOutcomeKind.Fallback:
                    draft.SetRegistrationWarning(HotkeyWarningKind.Fallback, "Registered with degraded delivery. AudioPilot had to fall back to hook-only capture for this hotkey.");
                    break;

                default:
                    draft.ClearRegistrationWarning();
                    break;
            }
        }

        private void RaiseHotkeyWarningPropertyChanged()
        {
            OnPropertyChanged(nameof(OutputHotkeyHasConflict));
            OnPropertyChanged(nameof(OutputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(InputHotkeyHasConflict));
            OnPropertyChanged(nameof(InputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsToggleAppVisibilityHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsShowCurrentTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPlayPauseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsNextTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPreviousTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteMicHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteSoundHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsDeafenHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsListenToInputHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeDownHotkeyHasConflict));
        }

        private Settings BuildCurrentHotkeySnapshot()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            return new Settings
            {
                Hotkeys = new HotkeysSettings
                {
                    App = new HotkeysAppSettings { ToggleAppVisibility = SettingsToggleAppVisibilityHotkeyDraft },
                    Media = new HotkeysMediaSettings
                    {
                        ShowCurrentTrack = SettingsShowCurrentTrackHotkeyDraft,
                        PlayPause = SettingsPlayPauseHotkeyDraft,
                        NextTrack = SettingsNextTrackHotkeyDraft,
                        PreviousTrack = SettingsPreviousTrackHotkeyDraft,
                    },
                    Mute = new HotkeysMuteSettings
                    {
                        Mic = SettingsMuteMicHotkeyDraft,
                        Sound = SettingsMuteSoundHotkeyDraft,
                        Deafen = SettingsDeafenHotkeyDraft,
                    },
                    Volume = new HotkeysVolumeSettings
                    {
                        MasterUp = SettingsMasterVolumeUpHotkeyDraft,
                        MasterDown = SettingsMasterVolumeDownHotkeyDraft,
                        MicUp = SettingsMicVolumeUpHotkeyDraft,
                        MicDown = SettingsMicVolumeDownHotkeyDraft,
                        MasterVolumeStepPercent = TryGetVolumeStepPercent(SettingsMasterVolumeStepPercentDraft, out int masterStepPercent) ? masterStepPercent : 5,
                        MicVolumeStepPercent = TryGetVolumeStepPercent(SettingsMicVolumeStepPercentDraft, out int micStepPercent) ? micStepPercent : 5,
                    },
                    Listen = new HotkeysListenSettings { ListenToInput = SettingsListenToInputHotkeyDraft },
                    Global = new HotkeysGlobalSettings { AdditionalStandaloneKeys = [.. _additionalStandaloneHotkeyKeys] },
                },
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        HotkeysEnabled = OutputHotkeysEnabled,
                        SwitchHotkey = Hotkey.ToHotkeyString(),
                        ReverseSwitchHotkey = OutputReverseHotkey.ToHotkeyString(),
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        HotkeysEnabled = InputHotkeysEnabled,
                        SwitchHotkey = InputHotkey.ToHotkeyString(),
                        ReverseSwitchHotkey = InputReverseHotkey.ToHotkeyString(),
                    },
                },
                Routines = new RoutinesSettings { Items = CloneRoutines(Routines) },
                Theme = cachedCopy?.Theme ?? Theme,
                RunAtStartup = cachedCopy?.RunAtStartup ?? RunAtStartup,
                Miscellaneous = MiscellaneousSettings.Clone(cachedCopy?.Miscellaneous),
                Overlay = OverlaySettings.Clone(cachedCopy?.Overlay),
            };
        }

        private bool TryGetOverlayDurationSeconds(out double value)
        {
            if (!double.TryParse(OverlayDurationSecondsText, out value))
            {
                return false;
            }

            if (!double.IsFinite(value) || value < 0.5 || value > 10.0)
            {
                return false;
            }

            return true;
        }

        private void ApplyOverlayDisplaySettings()
        {
            _overlay.UpdateEnabled(OverlayEnabled);

            if (!TryGetOverlayDurationSeconds(out double durationSeconds))
            {
                return;
            }

            _overlay.UpdateDisplayOptions(OverlayPosition, durationSeconds);
        }

        private void ApplySwitchHotkeyRegistrationFromCurrentUiState()
        {
            if (_isInitializing || _isCleaningUp)
            {
                return;
            }

            Settings? cachedCopy;
            lock (_settingsLock)
            {
                cachedCopy = _cachedSettings;
            }

            if (cachedCopy == null)
            {
                return;
            }

            Settings savedSettings = cachedCopy;

            var registrationSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        HotkeysEnabled = OutputHotkeysEnabled,
                        SwitchHotkey = savedSettings.DeviceSwitching.Output.SwitchHotkey,
                        ReverseSwitchHotkey = savedSettings.DeviceSwitching.Output.ReverseSwitchHotkey,
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        HotkeysEnabled = InputHotkeysEnabled,
                        SwitchHotkey = savedSettings.DeviceSwitching.Input.SwitchHotkey,
                        ReverseSwitchHotkey = savedSettings.DeviceSwitching.Input.ReverseSwitchHotkey,
                    },
                },
                Hotkeys = new HotkeysSettings
                {
                    Global = new HotkeysGlobalSettings
                    {
                        AdditionalStandaloneKeys = [.. savedSettings.Hotkeys.Global.AdditionalStandaloneKeys]
                    }
                },
            };

            var switchRegistrationResult = _hotkeyRegistrationCoordinator.RegisterChangedSwitchHotkeys(savedSettings, registrationSettings);
            _hotkeyRegistrationCoordinator.LogSwitchOnlyFailure(switchRegistrationResult, context: "toggle-update");
        }

        private void SyncMirroredSettingsDraftsFromLiveState(bool? runAtStartup = null, AppTheme? theme = null)
        {
            using (SuppressAutoSave())
            {
                if (runAtStartup.HasValue && SettingsRunAtStartupDraft != runAtStartup.Value)
                {
                    SettingsRunAtStartupDraft = runAtStartup.Value;
                }

                if (theme.HasValue && SettingsThemeDraft != theme.Value)
                {
                    SettingsThemeDraft = theme.Value;
                }
            }
        }

        private void SyncSettingsDraftFromCurrentState()
        {
            using (SuppressAutoSave())
            {
                UpdateAdditionalStandaloneHotkeyKeys(_cachedSettings?.Hotkeys.Global.AdditionalStandaloneKeys);
                SettingsAutoSaveEnabledDraft = _cachedSettings?.Miscellaneous.AutoSaveEnabled ?? false;
                SettingsPlayDialogSoundsDraft = _cachedSettings?.Miscellaneous.PlayDialogSounds ?? true;
                SettingsRunAtStartupDraft = _runAtStartup;
                SettingsThemeDraft = _themeBackingField;
                SettingsPreserveAudioLevelsDraft = _preserveAudioLevelsBackingField;
                SettingsAutoScrollToMixerOnRestoreDraft = _cachedSettings?.Miscellaneous.AutoScrollToMixerOnRestore ?? true;
                SettingsOverlayEnabledDraft = _overlayEnabledBackingField;
                SettingsBluetoothReconnectEnabledDraft = _cachedSettings?.Miscellaneous.BluetoothReconnectEnabled ?? true;
                SettingsDeviceReferenceFileModeDraft = _cachedSettings?.Miscellaneous.DeviceReferenceFileMode ?? DeviceReferenceFileMode.Off;
                SettingsLogLevelDraft = Enum.TryParse<LogLevel>(_cachedSettings?.Miscellaneous.LogLevel, true, out var parsedLevel)
                    ? parsedLevel
                    : LogLevel.Info;
                SettingsRedactLogContentDraft = _cachedSettings?.Miscellaneous.RedactLogContent ?? true;
                SettingsToggleAppVisibilityHotkeyDraft = _cachedSettings?.Hotkeys.App.ToggleAppVisibility ?? "Ctrl+Alt+H";
                SettingsShowCurrentTrackHotkeyDraft = _cachedSettings?.Hotkeys.Media.ShowCurrentTrack ?? string.Empty;
                SettingsPlayPauseHotkeyDraft = _cachedSettings?.Hotkeys.Media.PlayPause ?? "Ctrl+Alt+P";
                SettingsNextTrackHotkeyDraft = _cachedSettings?.Hotkeys.Media.NextTrack ?? "Ctrl+Alt+.";
                SettingsPreviousTrackHotkeyDraft = _cachedSettings?.Hotkeys.Media.PreviousTrack ?? "Ctrl+Alt+,";
                SettingsMuteMicHotkeyDraft = _cachedSettings?.Hotkeys.Mute.Mic ?? string.Empty;
                SettingsMuteSoundHotkeyDraft = _cachedSettings?.Hotkeys.Mute.Sound ?? string.Empty;
                SettingsDeafenHotkeyDraft = _cachedSettings?.Hotkeys.Mute.Deafen ?? string.Empty;
                SettingsListenToInputHotkeyDraft = _cachedSettings?.Hotkeys.Listen.ListenToInput ?? string.Empty;
                SettingsMasterVolumeUpHotkeyDraft = _cachedSettings?.Hotkeys.Volume.MasterUp ?? string.Empty;
                SettingsMasterVolumeDownHotkeyDraft = _cachedSettings?.Hotkeys.Volume.MasterDown ?? string.Empty;
                SettingsMicVolumeUpHotkeyDraft = _cachedSettings?.Hotkeys.Volume.MicUp ?? string.Empty;
                SettingsMicVolumeDownHotkeyDraft = _cachedSettings?.Hotkeys.Volume.MicDown ?? string.Empty;
                SettingsMasterVolumeStepPercentDraft = (_cachedSettings?.Hotkeys.Volume.MasterVolumeStepPercent ?? 5).ToString();
                SettingsMicVolumeStepPercentDraft = (_cachedSettings?.Hotkeys.Volume.MicVolumeStepPercent ?? 5).ToString();
                RefreshVolumeControlExpansionState();
                SetSettingsListenMonitorOutputDraft(
                    _cachedSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty,
                    _cachedSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty);
                List<string> outputRoles = CloneRoleSelections(_cachedSettings?.DeviceSwitching.Output.SwitchRoles, new Settings().DeviceSwitching.Output.SwitchRoles);
                SettingsOutputRoleMultimediaDraft = outputRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
                SettingsOutputRoleCommunicationsDraft = outputRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
                SettingsOutputRoleConsoleDraft = outputRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);

                List<string> inputRoles = CloneRoleSelections(_cachedSettings?.DeviceSwitching.Input.SwitchRoles, new Settings().DeviceSwitching.Input.SwitchRoles);
                SettingsInputRoleMultimediaDraft = inputRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
                SettingsInputRoleCommunicationsDraft = inputRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
                SettingsInputRoleConsoleDraft = inputRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);
                SettingsOverlayPositionDraft = _overlayPositionBackingField;
                SettingsOverlayDurationSecondsDraft = _overlayDurationSecondsTextBackingField;

                RefreshListenMonitorOutputOptions(SettingsListenMonitorOutputDeviceIdDraft, _settingsListenMonitorOutputDeviceNameDraft);
            }
        }

        private bool TryGetSettingsOverlayDurationSeconds(out double value)
        {
            if (!double.TryParse(SettingsOverlayDurationSecondsDraft, out value))
            {
                return false;
            }

            if (!double.IsFinite(value) || value < 0.5 || value > 10.0)
            {
                return false;
            }

            return true;
        }

        private static bool TryGetVolumeStepPercent(string rawValue, out int value)
        {
            if (!int.TryParse(rawValue, out value))
            {
                return false;
            }

            return value is >= 1 and <= 100;
        }

        private static bool HasConfiguredHotkeyDraft(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryResolveVolumeStepPercent(string rawValue, int fallbackValue, bool required, out int value)
        {
            if (TryGetVolumeStepPercent(rawValue, out value))
            {
                return true;
            }

            value = fallbackValue;
            return !required;
        }

        internal static List<string> CloneRoleSelections(IEnumerable<string>? roles, IReadOnlyList<string> fallback)
        {
            if (roles == null)
            {
                return [.. fallback];
            }

            var cloned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in roles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                string trimmed = role.Trim();
                if (seen.Add(trimmed))
                {
                    cloned.Add(trimmed);
                }
            }

            return cloned.Count > 0 ? cloned : [.. fallback];
        }

        internal readonly record struct SettingsCommitValidationResult(
            bool HasBlockingIssues,
            IReadOnlyList<string> BlockingMessages);

        internal static SettingsCommitValidationResult ValidateSettingsForCommit(Settings candidate)
        {
            var blocking = new List<string>();

            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                candidate,
                activeOutputDevices: null,
                activeInputDevices: null);

            foreach (SettingsDiagnostic warning in diagnostics.Warnings)
            {
                if (warning.Code.StartsWith("invalid-hotkey-", StringComparison.OrdinalIgnoreCase))
                {
                    blocking.Add($"- {warning.Message}");
                }

                if (warning.Code.StartsWith("reserved-hotkey-", StringComparison.OrdinalIgnoreCase))
                {
                    blocking.Add($"- {warning.Message}");
                }

                if (warning.Code.StartsWith("invalid-routine-", StringComparison.OrdinalIgnoreCase))
                {
                    blocking.Add($"- {warning.Message}");
                }

                if (warning.Code.StartsWith("reserved-routine-hotkey-", StringComparison.OrdinalIgnoreCase))
                {
                    blocking.Add($"- {warning.Message}");
                }
            }

            foreach (string duplicate in BuildDuplicateHotkeyConflicts(candidate))
            {
                blocking.Add($"- Duplicate hotkey: {duplicate}");
            }

            return new SettingsCommitValidationResult(
                HasBlockingIssues: blocking.Count > 0,
                BlockingMessages: blocking);
        }

        internal static List<string> BuildDuplicateHotkeyConflicts(Settings settings)
        {
            IReadOnlyList<string> additionalStandaloneHotkeyKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(settings.Hotkeys.Global.AdditionalStandaloneKeys).EffectiveTokens];
            List<(string Label, string Hotkey)> assignments = [];

            void Add(string label, string? rawHotkey)
            {
                if (!TryNormalizeHotkey(rawHotkey, additionalStandaloneHotkeyKeys, out string normalized) || string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                assignments.Add((label, normalized));
            }

            if (settings.DeviceSwitching.Output.HotkeysEnabled)
            {
                Add("Output switch", settings.DeviceSwitching.Output.SwitchHotkey);
                Add("Output reverse switch", settings.DeviceSwitching.Output.ReverseSwitchHotkey);
            }

            if (settings.DeviceSwitching.Input.HotkeysEnabled)
            {
                Add("Input switch", settings.DeviceSwitching.Input.SwitchHotkey);
                Add("Input reverse switch", settings.DeviceSwitching.Input.ReverseSwitchHotkey);
            }

            List<(string Label, string Hotkey)> routineAssignments = [];
            foreach (AudioRoutine routine in settings.Routines.Items.Where(static routine => routine.Enabled))
            {
                if (!TryNormalizeHotkey(routine.Hotkey, additionalStandaloneHotkeyKeys, out string normalized) || string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                string label = string.IsNullOrWhiteSpace(routine.Name)
                    ? "Routine"
                    : $"Routine: {routine.Name}";
                routineAssignments.Add((label, normalized));
            }

            Add("Show/hide app", settings.Hotkeys.App.ToggleAppVisibility);
            Add("Show current track", settings.Hotkeys.Media.ShowCurrentTrack);
            Add("Play/Pause", settings.Hotkeys.Media.PlayPause);
            Add("Next track", settings.Hotkeys.Media.NextTrack);
            Add("Previous track", settings.Hotkeys.Media.PreviousTrack);
            Add("Mute mic", settings.Hotkeys.Mute.Mic);
            Add("Mute sound", settings.Hotkeys.Mute.Sound);
            Add("Deafen", settings.Hotkeys.Mute.Deafen);
            Add("Listen to input", settings.Hotkeys.Listen.ListenToInput);
            Add("Master volume up", settings.Hotkeys.Volume.MasterUp);
            Add("Master volume down", settings.Hotkeys.Volume.MasterDown);
            Add("Mic volume up", settings.Hotkeys.Volume.MicUp);
            Add("Mic volume down", settings.Hotkeys.Volume.MicDown);

            var labelsByHotkey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < assignments.Count; index++)
            {
                var assignment = assignments[index];
                if (!labelsByHotkey.TryGetValue(assignment.Hotkey, out var labels))
                {
                    labels = [];
                    labelsByHotkey[assignment.Hotkey] = labels;
                }

                labels.Add(assignment.Label);
            }

            foreach (var routineAssignment in routineAssignments)
            {
                if (!labelsByHotkey.TryGetValue(routineAssignment.Hotkey, out var labels))
                {
                    labels = [];
                    labelsByHotkey[routineAssignment.Hotkey] = labels;
                }
            }

            var conflicts = new List<string>();
            foreach (var entry in labelsByHotkey)
            {
                bool hasBuiltInConflict = entry.Value.Count > 1;
                int routineConflictCount = routineAssignments.Count(routine => string.Equals(routine.Hotkey, entry.Key, StringComparison.OrdinalIgnoreCase));
                bool hasRoutineConflict = routineConflictCount > 0;
                bool hasDuplicateRoutineConflict = routineConflictCount > 1;
                if (!hasBuiltInConflict && !hasDuplicateRoutineConflict && !(entry.Value.Count > 0 && hasRoutineConflict))
                {
                    continue;
                }

                List<string> labels = [.. entry.Value];
                labels.AddRange(routineAssignments
                    .Where(routine => string.Equals(routine.Hotkey, entry.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(static routine => routine.Label));

                if (labels.Count <= 1)
                {
                    continue;
                }

                conflicts.Add($"{entry.Key} ({string.Join(", ", labels)})");
            }

            conflicts.Sort(StringComparer.OrdinalIgnoreCase);
            return conflicts;
        }

        internal static HashSet<string> BuildAssignedHotkeyKeySet(Settings settings, string? excludedRoutineId = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            IReadOnlyList<string> additionalStandaloneHotkeyKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(settings.Hotkeys.Global.AdditionalStandaloneKeys).EffectiveTokens];
            HashSet<string> assignedKeys = new(StringComparer.OrdinalIgnoreCase);

            void Add(HashSet<string> items, string? rawHotkey)
            {
                if (!TryNormalizeHotkey(rawHotkey, additionalStandaloneHotkeyKeys, out string normalized) || string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                items.Add(normalized);
            }

            if (settings.DeviceSwitching.Output.HotkeysEnabled)
            {
                Add(assignedKeys, settings.DeviceSwitching.Output.SwitchHotkey);
                Add(assignedKeys, settings.DeviceSwitching.Output.ReverseSwitchHotkey);
            }

            if (settings.DeviceSwitching.Input.HotkeysEnabled)
            {
                Add(assignedKeys, settings.DeviceSwitching.Input.SwitchHotkey);
                Add(assignedKeys, settings.DeviceSwitching.Input.ReverseSwitchHotkey);
            }

            Add(assignedKeys, settings.Hotkeys.App.ToggleAppVisibility);
            Add(assignedKeys, settings.Hotkeys.Media.ShowCurrentTrack);
            Add(assignedKeys, settings.Hotkeys.Media.PlayPause);
            Add(assignedKeys, settings.Hotkeys.Media.NextTrack);
            Add(assignedKeys, settings.Hotkeys.Media.PreviousTrack);
            Add(assignedKeys, settings.Hotkeys.Mute.Mic);
            Add(assignedKeys, settings.Hotkeys.Mute.Sound);
            Add(assignedKeys, settings.Hotkeys.Mute.Deafen);
            Add(assignedKeys, settings.Hotkeys.Listen.ListenToInput);
            Add(assignedKeys, settings.Hotkeys.Volume.MasterUp);
            Add(assignedKeys, settings.Hotkeys.Volume.MasterDown);
            Add(assignedKeys, settings.Hotkeys.Volume.MicUp);
            Add(assignedKeys, settings.Hotkeys.Volume.MicDown);

            foreach (AudioRoutine routine in settings.Routines.Items.Where(routine =>
                routine.Enabled &&
                !string.Equals(routine.Id, excludedRoutineId, StringComparison.OrdinalIgnoreCase)))
            {
                Add(assignedKeys, routine.Hotkey);
            }

            return assignedKeys;
        }

        internal static HashSet<string> BuildDuplicateHotkeyKeySet(Settings settings)
        {
            IReadOnlyList<string> additionalStandaloneHotkeyKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(settings.Hotkeys.Global.AdditionalStandaloneKeys).EffectiveTokens];
            List<string> assignments = [];

            void Add(List<string> items, string? rawHotkey)
            {
                if (!TryNormalizeHotkey(rawHotkey, additionalStandaloneHotkeyKeys, out string normalized) || string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                items.Add(normalized);
            }

            if (settings.DeviceSwitching.Output.HotkeysEnabled)
            {
                Add(assignments, settings.DeviceSwitching.Output.SwitchHotkey);
                Add(assignments, settings.DeviceSwitching.Output.ReverseSwitchHotkey);
            }

            if (settings.DeviceSwitching.Input.HotkeysEnabled)
            {
                Add(assignments, settings.DeviceSwitching.Input.SwitchHotkey);
                Add(assignments, settings.DeviceSwitching.Input.ReverseSwitchHotkey);
            }

            Add(assignments, settings.Hotkeys.App.ToggleAppVisibility);
            Add(assignments, settings.Hotkeys.Media.ShowCurrentTrack);
            Add(assignments, settings.Hotkeys.Media.PlayPause);
            Add(assignments, settings.Hotkeys.Media.NextTrack);
            Add(assignments, settings.Hotkeys.Media.PreviousTrack);
            Add(assignments, settings.Hotkeys.Mute.Mic);
            Add(assignments, settings.Hotkeys.Mute.Sound);
            Add(assignments, settings.Hotkeys.Mute.Deafen);
            Add(assignments, settings.Hotkeys.Listen.ListenToInput);
            Add(assignments, settings.Hotkeys.Volume.MasterUp);
            Add(assignments, settings.Hotkeys.Volume.MasterDown);
            Add(assignments, settings.Hotkeys.Volume.MicUp);
            Add(assignments, settings.Hotkeys.Volume.MicDown);

            var builtInCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < assignments.Count; index++)
            {
                string assignment = assignments[index];
                builtInCounts[assignment] = builtInCounts.TryGetValue(assignment, out int count) ? count + 1 : 1;
            }

            var routineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioRoutine routine in settings.Routines.Items.Where(static routine => routine.Enabled))
            {
                if (!TryNormalizeHotkey(routine.Hotkey, additionalStandaloneHotkeyKeys, out string normalized) || string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                routineCounts[normalized] = routineCounts.TryGetValue(normalized, out int count) ? count + 1 : 1;
            }

            HashSet<string> duplicates = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in builtInCounts)
            {
                if (entry.Value > 1 || routineCounts.ContainsKey(entry.Key))
                {
                    duplicates.Add(entry.Key);
                }
            }

            foreach (var entry in routineCounts)
            {
                if (entry.Value > 1)
                {
                    duplicates.Add(entry.Key);
                }
            }

            return duplicates;
        }

        private static bool TryNormalizeHotkey(string? raw, out string normalized)
        {
            return TryNormalizeHotkey(raw, null, out normalized);
        }

        private static bool TryNormalizeHotkey(string? raw, IEnumerable<string>? additionalStandaloneHotkeyKeys, out string normalized)
        {
            normalized = string.Empty;
            string trimmed = raw?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                return false;
            }

            var parser = new HotkeyViewModel();
            parser.UpdateAdditionalStandaloneHotkeyKeys(additionalStandaloneHotkeyKeys);
            if (!parser.LoadFromString(trimmed))
            {
                return false;
            }

            normalized = parser.ToHotkeyString();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private void UpdateAdditionalStandaloneHotkeyKeys(IEnumerable<string>? additionalStandaloneHotkeyKeys)
        {
            IReadOnlyList<string> effectiveKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(additionalStandaloneHotkeyKeys).EffectiveTokens];

            _hotkeys.UpdateAdditionalStandaloneHotkeyKeys(effectiveKeys);

            foreach (HotkeyViewModel draft in EnumerateHotkeyViewModels())
            {
                draft.UpdateAdditionalStandaloneHotkeyKeys(effectiveKeys);
            }
        }

        private IEnumerable<HotkeyViewModel> EnumerateHotkeyViewModels()
        {
            yield return Hotkey;
            yield return OutputReverseHotkey;
            yield return InputHotkey;
            yield return InputReverseHotkey;
            yield return SettingsToggleAppVisibilityHotkeyDraftCapture;
            yield return SettingsShowCurrentTrackHotkeyDraftCapture;
            yield return SettingsPlayPauseHotkeyDraftCapture;
            yield return SettingsNextTrackHotkeyDraftCapture;
            yield return SettingsPreviousTrackHotkeyDraftCapture;
            yield return SettingsMuteMicHotkeyDraftCapture;
            yield return SettingsMuteSoundHotkeyDraftCapture;
            yield return SettingsDeafenHotkeyDraftCapture;
            yield return SettingsListenToInputHotkeyDraftCapture;
            yield return SettingsMasterVolumeUpHotkeyDraftCapture;
            yield return SettingsMasterVolumeDownHotkeyDraftCapture;
            yield return SettingsMicVolumeUpHotkeyDraftCapture;
            yield return SettingsMicVolumeDownHotkeyDraftCapture;
        }

        internal void RefreshHotkeyDisplayLabels()
        {
            foreach (HotkeyViewModel hotkey in EnumerateHotkeyViewModels())
            {
                hotkey.RefreshDisplayText();
            }
        }

        internal static bool AreHotkeyStringsEquivalent(string? left, string? right)
        {
            bool leftParsed = TryNormalizeHotkey(left, out string leftNormalized);
            bool rightParsed = TryNormalizeHotkey(right, out string rightNormalized);

            if (leftParsed && rightParsed)
            {
                return string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
            }

            string leftTrimmed = left?.Trim() ?? string.Empty;
            string rightTrimmed = right?.Trim() ?? string.Empty;
            return string.Equals(leftTrimmed, rightTrimmed, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetHotkeyRegistrationWarnings(HotkeyRegistrationResult result)
        {
            List<string> warnings = [];
            if (result.ToggleAppVisibilityAttempted && !result.ToggleAppVisibilityRegistered)
            {
                warnings.Add("Show / Hide App hotkey could not be registered.");
            }

            if (result.MediaHotkeysAttempted && !result.MediaHotkeysRegistered)
            {
                warnings.Add("One or more media hotkeys could not be registered.");
            }

            if (result.MuteHotkeysAttempted && !result.MuteHotkeysRegistered)
            {
                warnings.Add("One or more mute/deafen hotkeys could not be registered.");
            }

            if (result.ListenToInputAttempted && !result.ListenToInputRegistered)
            {
                warnings.Add("Listen to input hotkey could not be registered.");
            }

            if (result.VolumeStepHotkeysAttempted && !result.VolumeStepHotkeysRegistered)
            {
                warnings.Add("One or more volume step hotkeys could not be registered.");
            }

            warnings.AddRange(GetSwitchHotkeyRegistrationWarnings(result.SwitchResult));
            return warnings;
        }

        private static List<string> GetSwitchHotkeyRegistrationWarnings(SwitchHotkeyRegistrationResult result)
        {
            List<string> warnings = [];
            if (result.OutputSwitchAttempted && !result.OutputSwitchRegistered)
            {
                warnings.Add("Output switch hotkey could not be registered.");
            }

            if (result.InputSwitchAttempted && !result.InputSwitchRegistered)
            {
                warnings.Add("Input switch hotkey could not be registered.");
            }

            if (result.OutputReverseSwitchAttempted && !result.OutputReverseSwitchRegistered)
            {
                warnings.Add("Output reverse hotkey could not be registered.");
            }

            if (result.InputReverseSwitchAttempted && !result.InputReverseSwitchRegistered)
            {
                warnings.Add("Input reverse hotkey could not be registered.");
            }

            return warnings;
        }
    }
}
