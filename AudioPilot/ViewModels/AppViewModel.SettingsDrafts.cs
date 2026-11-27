using System.ComponentModel;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private void SetSettingsListenMonitorOutputDraft(string? deviceId, string? deviceName, string? stableId = null)
        {
            string normalizedId = deviceId?.Trim() ?? string.Empty;
            CycleDevice resolved = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                new CycleDevice { Id = normalizedId, Name = deviceName ?? string.Empty, StableId = stableId }, _outputDevices);
            string resolvedId = string.IsNullOrWhiteSpace(normalizedId) ? string.Empty : resolved.Id?.Trim() ?? normalizedId;
            string resolvedName = string.IsNullOrWhiteSpace(resolvedId) ? string.Empty : resolved.Name?.Trim() ?? string.Empty;
            string? resolvedStableId = string.IsNullOrWhiteSpace(resolvedId) ? null : resolved.StableId;

            bool idChanged = !string.Equals(_settingsListenMonitorOutputDeviceIdDraft, resolvedId, StringComparison.Ordinal);
            bool nameChanged = !string.Equals(_settingsListenMonitorOutputDeviceNameDraft, resolvedName, StringComparison.Ordinal);
            bool stableIdChanged = !string.Equals(_settingsListenMonitorOutputDeviceStableIdDraft, resolvedStableId, StringComparison.Ordinal);
            if (!idChanged && !nameChanged && !stableIdChanged)
            {
                return;
            }

            _settingsListenMonitorOutputDeviceIdDraft = resolvedId;
            _settingsListenMonitorOutputDeviceNameDraft = resolvedName;
            _settingsListenMonitorOutputDeviceStableIdDraft = resolvedStableId;

            OnPropertyChanged(nameof(SettingsListenMonitorOutputDeviceIdDraft));
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
        }

        private void RefreshSettingsHotkeyExpansionState()
        {
            SettingsAppShortcutsExpanded = !string.IsNullOrWhiteSpace(SettingsToggleAppVisibilityHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsShowAudioStatusHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsQuickDevicePickerHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsVolumeMixerHotkeyDraft);
            SettingsMediaShortcutsExpanded = !string.IsNullOrWhiteSpace(SettingsShowCurrentTrackHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsPlayPauseHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsNextTrackHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsPreviousTrackHotkeyDraft);
            SettingsMuteShortcutsExpanded = !string.IsNullOrWhiteSpace(SettingsMuteMicHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsMuteSoundHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsDeafenHotkeyDraft);
            SettingsForegroundVolumeControlsExpanded = !string.IsNullOrWhiteSpace(SettingsForegroundVolumeUpHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsForegroundVolumeDownHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsForegroundMuteHotkeyDraft);
            SettingsMicrophoneHoldControlsExpanded = !string.IsNullOrWhiteSpace(SettingsPushToTalkHotkeyDraft) || !string.IsNullOrWhiteSpace(SettingsHoldToMuteHotkeyDraft);
            SettingsSeekControlsExpanded = !string.IsNullOrWhiteSpace(SettingsSeekForwardHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsSeekBackwardHotkeyDraft);
            SettingsMasterVolumeControlsExpanded = !string.IsNullOrWhiteSpace(SettingsMasterVolumeUpHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsMasterVolumeDownHotkeyDraft);
            SettingsMicVolumeControlsExpanded = !string.IsNullOrWhiteSpace(SettingsMicVolumeUpHotkeyDraft) ||
                !string.IsNullOrWhiteSpace(SettingsMicVolumeDownHotkeyDraft);
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
            var details = BuildDuplicateHotkeyLabels(BuildCurrentHotkeySnapshot());
            var updated = details.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool keysChanged = !_hotkeyConflictKeys.SetEquals(updated);
            if (!keysChanged && details.Count == 0) return;

            _hotkeyConflictKeys.Clear();
            _hotkeyConflictKeys.UnionWith(updated);

            foreach (HotkeyViewModel draft in EnumerateHotkeyViewModels())
            {
                bool hasDuplicate = TryNormalizeHotkey(draft.ToHotkeyString(), out string normalized)
                    && _hotkeyConflictKeys.Contains(normalized);
                string? actions = null;
                if (hasDuplicate)
                {
                    var labels = details[normalized];
                    actions = string.Join(", ", labels.Take(3)) + (labels.Count > 3 ? $", and {labels.Count - 3} more" : string.Empty);
                }
                draft.SetDuplicateWarning(hasDuplicate, actions);
            }

            if (!keysChanged) return;
            OnPropertyChanged(nameof(OutputHotkeyHasConflict));
            OnPropertyChanged(nameof(OutputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(InputHotkeyHasConflict));
            OnPropertyChanged(nameof(InputReverseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsToggleAppVisibilityHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsShowAudioStatusHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsQuickDevicePickerHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsVolumeMixerHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsShowCurrentTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPlayPauseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsNextTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPreviousTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsSeekForwardHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsSeekBackwardHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteMicHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteSoundHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsDeafenHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsListenToInputHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsHoldToMuteHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPushToTalkHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundMuteHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundVolumeUpHotkeyHasConflict));
        }

        private void RefreshRegistrationWarningsForAllHotkeys(Settings settings)
        {
            ApplyRegistrationWarning(Hotkey, AppConstants.Hotkeys.OutputSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.SwitchHotkey));
            ApplyRegistrationWarning(OutputReverseHotkey, AppConstants.Hotkeys.OutputReverseSwitchHotkeyId, settings.DeviceSwitching.Output.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Output.ReverseSwitchHotkey));
            ApplyRegistrationWarning(InputHotkey, AppConstants.Hotkeys.InputSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.SwitchHotkey));
            ApplyRegistrationWarning(InputReverseHotkey, AppConstants.Hotkeys.InputReverseSwitchHotkeyId, settings.DeviceSwitching.Input.HotkeysEnabled && !string.IsNullOrWhiteSpace(settings.DeviceSwitching.Input.ReverseSwitchHotkey));
            ApplyRegistrationWarning(SettingsToggleAppVisibilityHotkeyDraftCapture, AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.App.ToggleAppVisibility));
            ApplyRegistrationWarning(SettingsShowAudioStatusHotkeyDraftCapture, AppConstants.Hotkeys.ShowAudioStatusHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.App.ShowAudioStatus));
            ApplyRegistrationWarning(SettingsQuickDevicePickerHotkeyDraftCapture, AppConstants.Hotkeys.QuickDevicePickerHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.App.QuickDevicePicker));
            ApplyRegistrationWarning(SettingsVolumeMixerHotkeyDraftCapture, AppConstants.Hotkeys.VolumeMixerHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.App.VolumeMixer));
            ApplyRegistrationWarning(SettingsShowCurrentTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaShowCurrentTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.ShowCurrentTrack));
            ApplyRegistrationWarning(SettingsPlayPauseHotkeyDraftCapture, AppConstants.Hotkeys.MediaPlayPauseId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.PlayPause));
            ApplyRegistrationWarning(SettingsNextTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaNextTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.NextTrack));
            ApplyRegistrationWarning(SettingsPreviousTrackHotkeyDraftCapture, AppConstants.Hotkeys.MediaPrevTrackId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.PreviousTrack));
            ApplyRegistrationWarning(SettingsSeekForwardHotkeyDraftCapture, AppConstants.Hotkeys.MediaSeekForwardId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.SeekForward));
            ApplyRegistrationWarning(SettingsSeekBackwardHotkeyDraftCapture, AppConstants.Hotkeys.MediaSeekBackwardId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Media.SeekBackward));
            ApplyRegistrationWarning(SettingsMuteMicHotkeyDraftCapture, AppConstants.Hotkeys.MuteMicId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Mic));
            ApplyRegistrationWarning(SettingsMuteSoundHotkeyDraftCapture, AppConstants.Hotkeys.MuteSoundId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Sound));
            ApplyRegistrationWarning(SettingsDeafenHotkeyDraftCapture, AppConstants.Hotkeys.DeafenId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.Deafen));
            ApplyRegistrationWarning(SettingsListenToInputHotkeyDraftCapture, AppConstants.Hotkeys.ListenToInputHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Listen.ListenToInput));
            ApplyRegistrationWarning(SettingsMasterVolumeUpHotkeyDraftCapture, AppConstants.Hotkeys.MasterVolumeUpHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MasterUp));
            ApplyRegistrationWarning(SettingsMasterVolumeDownHotkeyDraftCapture, AppConstants.Hotkeys.MasterVolumeDownHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MasterDown));
            ApplyRegistrationWarning(SettingsMicVolumeUpHotkeyDraftCapture, AppConstants.Hotkeys.MicVolumeUpHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MicUp));
            ApplyRegistrationWarning(SettingsMicVolumeDownHotkeyDraftCapture, AppConstants.Hotkeys.MicVolumeDownHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.MicDown));
            ApplyRegistrationWarning(SettingsHoldToMuteHotkeyDraftCapture, AppConstants.Hotkeys.HoldToMuteHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.HoldToMute));
            ApplyRegistrationWarning(SettingsPushToTalkHotkeyDraftCapture, AppConstants.Hotkeys.PushToTalkHotkeyId, settings.Hotkeys.Mute.PushToTalkEnabled && !string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.PushToTalk));
            ApplyRegistrationWarning(SettingsForegroundMuteHotkeyDraftCapture, AppConstants.Hotkeys.ForegroundMuteHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.ForegroundMute));
            ApplyRegistrationWarning(SettingsForegroundVolumeDownHotkeyDraftCapture, AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.ForegroundDown));
            ApplyRegistrationWarning(SettingsForegroundVolumeUpHotkeyDraftCapture, AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId, !string.IsNullOrWhiteSpace(settings.Hotkeys.Volume.ForegroundUp));

            RaiseHotkeyWarningPropertyChanged();
        }

        private void AcknowledgePersistedHotkeys(Settings settings, bool deferWhileEditing, bool switchOnly = false)
        {
            ReportHotkeyAvailability();
            Hotkey.AcknowledgeSavedBinding(settings.DeviceSwitching.Output.SwitchHotkey, deferWhileEditing);
            OutputReverseHotkey.AcknowledgeSavedBinding(settings.DeviceSwitching.Output.ReverseSwitchHotkey, deferWhileEditing);
            InputHotkey.AcknowledgeSavedBinding(settings.DeviceSwitching.Input.SwitchHotkey, deferWhileEditing);
            InputReverseHotkey.AcknowledgeSavedBinding(settings.DeviceSwitching.Input.ReverseSwitchHotkey, deferWhileEditing);
            if (switchOnly) return;

            SettingsToggleAppVisibilityHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.App.ToggleAppVisibility, deferWhileEditing);
            SettingsShowAudioStatusHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.App.ShowAudioStatus, deferWhileEditing);
            SettingsQuickDevicePickerHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.App.QuickDevicePicker, deferWhileEditing);
            SettingsVolumeMixerHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.App.VolumeMixer, deferWhileEditing);
            SettingsShowCurrentTrackHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.ShowCurrentTrack, deferWhileEditing);
            SettingsPlayPauseHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.PlayPause, deferWhileEditing);
            SettingsNextTrackHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.NextTrack, deferWhileEditing);
            SettingsPreviousTrackHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.PreviousTrack, deferWhileEditing);
            SettingsSeekForwardHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.SeekForward, deferWhileEditing);
            SettingsSeekBackwardHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Media.SeekBackward, deferWhileEditing);
            SettingsMuteMicHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Mute.Mic, deferWhileEditing);
            SettingsMuteSoundHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Mute.Sound, deferWhileEditing);
            SettingsDeafenHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Mute.Deafen, deferWhileEditing);
            SettingsListenToInputHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Listen.ListenToInput, deferWhileEditing);
            SettingsMasterVolumeUpHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.MasterUp, deferWhileEditing);
            SettingsMasterVolumeDownHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.MasterDown, deferWhileEditing);
            SettingsMicVolumeUpHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.MicUp, deferWhileEditing);
            SettingsMicVolumeDownHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.MicDown, deferWhileEditing);
            SettingsHoldToMuteHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Mute.HoldToMute, deferWhileEditing);
            SettingsPushToTalkHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Mute.PushToTalk, deferWhileEditing);
            SettingsForegroundMuteHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.ForegroundMute, deferWhileEditing);
            SettingsForegroundVolumeDownHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.ForegroundDown, deferWhileEditing);
            SettingsForegroundVolumeUpHotkeyDraftCapture.AcknowledgeSavedBinding(settings.Hotkeys.Volume.ForegroundUp, deferWhileEditing);
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
            OnPropertyChanged(nameof(SettingsShowAudioStatusHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsQuickDevicePickerHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsVolumeMixerHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsShowCurrentTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPlayPauseHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsNextTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPreviousTrackHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsSeekForwardHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsSeekBackwardHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteMicHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMuteSoundHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsDeafenHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsListenToInputHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMasterVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeUpHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsMicVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsHoldToMuteHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsPushToTalkHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundMuteHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundVolumeDownHotkeyHasConflict));
            OnPropertyChanged(nameof(SettingsForegroundVolumeUpHotkeyHasConflict));
        }

        private Settings BuildCurrentHotkeySnapshot()
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            AppSettings app = AppSettings.Clone(cachedCopy?.App);
            app.Theme = cachedCopy?.App.Theme ?? Theme;
            StartupSettings startup = StartupSettings.Clone(cachedCopy?.Startup);
            startup.Enabled = cachedCopy?.Startup.Enabled ?? RunAtStartup;
            return new Settings
            {
                Hotkeys = new HotkeysSettings
                {
                    App = new HotkeysAppSettings { ToggleAppVisibility = SettingsToggleAppVisibilityHotkeyDraft, ShowAudioStatus = SettingsShowAudioStatusHotkeyDraft, QuickDevicePicker = SettingsQuickDevicePickerHotkeyDraft, VolumeMixer = SettingsVolumeMixerHotkeyDraft },
                    Media = new HotkeysMediaSettings
                    {
                        ShowCurrentTrack = SettingsShowCurrentTrackHotkeyDraft,
                        PlayPause = SettingsPlayPauseHotkeyDraft,
                        NextTrack = SettingsNextTrackHotkeyDraft,
                        PreviousTrack = SettingsPreviousTrackHotkeyDraft,
                        SeekForward = SettingsSeekForwardHotkeyDraft,
                        SeekBackward = SettingsSeekBackwardHotkeyDraft,
                        SeekStepSeconds = MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out decimal seekStep, allowDecimalComma: true) ? seekStep : MediaSeekStep.DefaultSeconds,
                    },
                    Mute = new HotkeysMuteSettings
                    {
                        Mic = SettingsMuteMicHotkeyDraft,
                        Sound = SettingsMuteSoundHotkeyDraft,
                        Deafen = SettingsDeafenHotkeyDraft,
                        HoldToMute = SettingsHoldToMuteHotkeyDraft,
                        PushToTalk = SettingsPushToTalkHotkeyDraft,
                        PushToTalkEnabled = SettingsPushToTalkEnabledDraft,
                    },
                    Volume = new HotkeysVolumeSettings
                    {
                        MasterUp = SettingsMasterVolumeUpHotkeyDraft,
                        MasterDown = SettingsMasterVolumeDownHotkeyDraft,
                        MicUp = SettingsMicVolumeUpHotkeyDraft,
                        MicDown = SettingsMicVolumeDownHotkeyDraft,
                        ForegroundMute = SettingsForegroundMuteHotkeyDraft,
                        ForegroundDown = SettingsForegroundVolumeDownHotkeyDraft,
                        ForegroundUp = SettingsForegroundVolumeUpHotkeyDraft,
                        MasterVolumeStepPercent = TryGetVolumeStepPercent(SettingsMasterVolumeStepPercentDraft, out decimal masterStepPercent) ? masterStepPercent : VolumePercent.DefaultStep,
                        MicVolumeStepPercent = TryGetVolumeStepPercent(SettingsMicVolumeStepPercentDraft, out decimal micStepPercent) ? micStepPercent : VolumePercent.DefaultStep,
                        ForegroundVolumeStepPercent = TryGetVolumeStepPercent(SettingsForegroundVolumeStepPercentDraft, out decimal foregroundStepPercent) ? foregroundStepPercent : VolumePercent.DefaultStep,
                    },
                    Listen = new HotkeysListenSettings { ListenToInput = SettingsListenToInputHotkeyDraft },
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
                Routines = new RoutinesSettings { Items = CloneRoutines(Routines), DefaultTimeZoneId = cachedCopy?.Routines.DefaultTimeZoneId ?? TimeZoneInfo.Local.Id },
                App = app,
                Startup = startup,
                Updates = UpdateSettings.Clone(cachedCopy?.Updates),
                Diagnostics = DiagnosticsSettings.Clone(cachedCopy?.Diagnostics),
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

            _overlay.UpdateDisplayOptions(OverlayPosition, durationSeconds, CurrentSettings?.Overlay.MonitorMode ?? OverlayMonitorMode.Cursor);
        }

        private void ApplySwitchHotkeyRegistrationFromCurrentUiState(bool output, bool previousEnabled)
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
            };

            Settings previousRegistrationSettings = registrationSettings.Clone();
            if (output) previousRegistrationSettings.DeviceSwitching.Output.HotkeysEnabled = previousEnabled;
            else previousRegistrationSettings.DeviceSwitching.Input.HotkeysEnabled = previousEnabled;
            var switchRegistrationResult = _hotkeyRegistrationCoordinator.RegisterChangedSwitchHotkeys(previousRegistrationSettings, registrationSettings);
            _hotkeyRegistrationCoordinator.LogSwitchOnlyFailure(switchRegistrationResult, context: "toggle-update");
            RefreshRegistrationWarningsForSwitchHotkeys(registrationSettings);
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

        internal void SyncSettingsDraftFromCurrentState(bool resetHotkeyExpansion, Settings? previous = null)
        {
            // Only clean fields follow a CLI write; raw invalid drafts and selections remain editable.
            T Sync<T>(T current, T saved, T next, bool hotkey = false)
            {
                if (previous == null) return next;
                bool clean = hotkey ? AreHotkeyStringsEquivalent(current as string, saved as string) : EqualityComparer<T>.Default.Equals(current, saved);
                return !clean || EqualityComparer<T>.Default.Equals(saved, next) ? current : next;
            }

            PushToTalkModeChanged?.Invoke(_cachedSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
            Updates.SetEnabled(_cachedSettings?.Updates.CheckAutomatically ?? false);
            using (SuppressAutoSave())
            {
                SettingsAutoSaveEnabledDraft = Sync(SettingsAutoSaveEnabledDraft, previous?.App.AutoSaveEnabled ?? false, _cachedSettings?.App.AutoSaveEnabled ?? false);
                SettingsPlayAppSoundsDraft = Sync(SettingsPlayAppSoundsDraft, previous?.App.PlayAppSounds ?? true, _cachedSettings?.App.PlayAppSounds ?? true);
                SettingsRunAtStartupDraft = Sync(SettingsRunAtStartupDraft, previous?.Startup.Enabled ?? false, _runAtStartup);
                SettingsUseScheduledStartupDraft = Sync(SettingsUseScheduledStartupDraft, previous?.Startup.UseTaskScheduler ?? false, _cachedSettings?.Startup.UseTaskScheduler ?? false);
                SettingsStartInTrayDraft = Sync(SettingsStartInTrayDraft, previous?.Startup.StartInTray ?? StartupTrayBehavior.WindowsStartupOnly, _cachedSettings?.Startup.StartInTray ?? StartupTrayBehavior.WindowsStartupOnly);
                SettingsCheckForUpdatesDraft = Sync(SettingsCheckForUpdatesDraft, previous?.Updates.CheckAutomatically ?? false, _cachedSettings?.Updates.CheckAutomatically ?? false);
                SettingsThemeDraft = Sync(SettingsThemeDraft, previous?.App.Theme ?? AppTheme.System, _themeBackingField);
                SettingsPreserveAudioLevelsDraft = Sync(SettingsPreserveAudioLevelsDraft, previous?.DeviceSwitching.PreserveAudioLevels ?? true, _preserveAudioLevelsBackingField);
                SettingsKeepMuteStateWhenSwitchingDraft = Sync(SettingsKeepMuteStateWhenSwitchingDraft, previous?.DeviceSwitching.KeepMuteStateWhenSwitching ?? true, KeepMuteStateWhenSwitching);
                SettingsAutoScrollToMixerOnRestoreDraft = Sync(SettingsAutoScrollToMixerOnRestoreDraft, previous?.App.AutoScrollToMixerOnRestore ?? true, _cachedSettings?.App.AutoScrollToMixerOnRestore ?? true);
                SettingsCloseButtonBehaviorDraft = Sync(SettingsCloseButtonBehaviorDraft, previous?.App.CloseButtonBehavior ?? CloseButtonBehavior.Exit, _cachedSettings?.App.CloseButtonBehavior ?? CloseButtonBehavior.Exit);
                SettingsMinimizeButtonBehaviorDraft = Sync(SettingsMinimizeButtonBehaviorDraft, previous?.App.MinimizeButtonBehavior ?? MinimizeButtonBehavior.HideToTray, _cachedSettings?.App.MinimizeButtonBehavior ?? MinimizeButtonBehavior.HideToTray);
                SettingsTrayLeftClickActionDraft = Sync(SettingsTrayLeftClickActionDraft, previous?.App.TrayLeftClickAction ?? TrayLeftClickAction.QuickMixer, _cachedSettings?.App.TrayLeftClickAction ?? TrayLeftClickAction.QuickMixer);
                SettingsSuppressDeviceStartupWarningsDraft = Sync(SettingsSuppressDeviceStartupWarningsDraft, previous?.Startup.SuppressDeviceWarnings ?? false, _cachedSettings?.Startup.SuppressDeviceWarnings ?? false);
                SettingsOverlayEnabledDraft = Sync(SettingsOverlayEnabledDraft, previous?.Overlay.Enabled ?? true, _overlayEnabledBackingField);
                SettingsOverlayHotplugEnabledDraft = Sync(SettingsOverlayHotplugEnabledDraft, previous?.Overlay.ShowDeviceHotplugOverlays ?? true, _cachedSettings?.Overlay.ShowDeviceHotplugOverlays ?? true);
                SettingsOverlayMonitorModeDraft = Sync(SettingsOverlayMonitorModeDraft, previous?.Overlay.MonitorMode ?? OverlayMonitorMode.Cursor, _cachedSettings?.Overlay.MonitorMode ?? OverlayMonitorMode.Cursor);
                SettingsBluetoothReconnectEnabledDraft = Sync(SettingsBluetoothReconnectEnabledDraft, previous?.DeviceSwitching.BluetoothReconnectEnabled ?? true, _cachedSettings?.DeviceSwitching.BluetoothReconnectEnabled ?? true);
                SettingsDeviceReferenceFileModeDraft = Sync(SettingsDeviceReferenceFileModeDraft, previous?.Diagnostics.DeviceReferenceFileMode ?? DeviceReferenceFileMode.Off, _cachedSettings?.Diagnostics.DeviceReferenceFileMode ?? DeviceReferenceFileMode.Off);
                SettingsLogLevelDraft = Sync(SettingsLogLevelDraft, Logger.ParseLogLevel(previous?.Diagnostics.LogLevel), Logger.ParseLogLevel(_cachedSettings?.Diagnostics.LogLevel));
                SettingsRedactLogContentDraft = Sync(SettingsRedactLogContentDraft, previous?.Diagnostics.RedactLogContent ?? true, _cachedSettings?.Diagnostics.RedactLogContent ?? true);
                SettingsToggleAppVisibilityHotkeyDraft = Sync(SettingsToggleAppVisibilityHotkeyDraft, previous?.Hotkeys.App.ToggleAppVisibility ?? "Ctrl+Alt+H", _cachedSettings?.Hotkeys.App.ToggleAppVisibility ?? "Ctrl+Alt+H", hotkey: true);
                SettingsShowAudioStatusHotkeyDraft = Sync(SettingsShowAudioStatusHotkeyDraft, previous?.Hotkeys.App.ShowAudioStatus ?? string.Empty, _cachedSettings?.Hotkeys.App.ShowAudioStatus ?? string.Empty, hotkey: true);
                SettingsQuickDevicePickerHotkeyDraft = Sync(SettingsQuickDevicePickerHotkeyDraft, previous?.Hotkeys.App.QuickDevicePicker ?? string.Empty, _cachedSettings?.Hotkeys.App.QuickDevicePicker ?? string.Empty, hotkey: true);
                SettingsVolumeMixerHotkeyDraft = Sync(SettingsVolumeMixerHotkeyDraft, previous?.Hotkeys.App.VolumeMixer ?? string.Empty, _cachedSettings?.Hotkeys.App.VolumeMixer ?? string.Empty, hotkey: true);
                SettingsShowCurrentTrackHotkeyDraft = Sync(SettingsShowCurrentTrackHotkeyDraft, previous?.Hotkeys.Media.ShowCurrentTrack ?? string.Empty, _cachedSettings?.Hotkeys.Media.ShowCurrentTrack ?? string.Empty, hotkey: true);
                SettingsPlayPauseHotkeyDraft = Sync(SettingsPlayPauseHotkeyDraft, previous?.Hotkeys.Media.PlayPause ?? string.Empty, _cachedSettings?.Hotkeys.Media.PlayPause ?? string.Empty, hotkey: true);
                SettingsNextTrackHotkeyDraft = Sync(SettingsNextTrackHotkeyDraft, previous?.Hotkeys.Media.NextTrack ?? string.Empty, _cachedSettings?.Hotkeys.Media.NextTrack ?? string.Empty, hotkey: true);
                SettingsPreviousTrackHotkeyDraft = Sync(SettingsPreviousTrackHotkeyDraft, previous?.Hotkeys.Media.PreviousTrack ?? string.Empty, _cachedSettings?.Hotkeys.Media.PreviousTrack ?? string.Empty, hotkey: true);
                SettingsSeekForwardHotkeyDraft = Sync(SettingsSeekForwardHotkeyDraft, previous?.Hotkeys.Media.SeekForward ?? "", _cachedSettings?.Hotkeys.Media.SeekForward ?? "", hotkey: true);
                SettingsSeekBackwardHotkeyDraft = Sync(SettingsSeekBackwardHotkeyDraft, previous?.Hotkeys.Media.SeekBackward ?? "", _cachedSettings?.Hotkeys.Media.SeekBackward ?? "", hotkey: true);
                SettingsMuteMicHotkeyDraft = Sync(SettingsMuteMicHotkeyDraft, previous?.Hotkeys.Mute.Mic ?? string.Empty, _cachedSettings?.Hotkeys.Mute.Mic ?? string.Empty, hotkey: true);
                SettingsMuteSoundHotkeyDraft = Sync(SettingsMuteSoundHotkeyDraft, previous?.Hotkeys.Mute.Sound ?? string.Empty, _cachedSettings?.Hotkeys.Mute.Sound ?? string.Empty, hotkey: true);
                SettingsDeafenHotkeyDraft = Sync(SettingsDeafenHotkeyDraft, previous?.Hotkeys.Mute.Deafen ?? string.Empty, _cachedSettings?.Hotkeys.Mute.Deafen ?? string.Empty, hotkey: true);
                SettingsListenToInputHotkeyDraft = Sync(SettingsListenToInputHotkeyDraft, previous?.Hotkeys.Listen.ListenToInput ?? string.Empty, _cachedSettings?.Hotkeys.Listen.ListenToInput ?? string.Empty, hotkey: true);
                SettingsMasterVolumeUpHotkeyDraft = Sync(SettingsMasterVolumeUpHotkeyDraft, previous?.Hotkeys.Volume.MasterUp ?? string.Empty, _cachedSettings?.Hotkeys.Volume.MasterUp ?? string.Empty, hotkey: true);
                SettingsMasterVolumeDownHotkeyDraft = Sync(SettingsMasterVolumeDownHotkeyDraft, previous?.Hotkeys.Volume.MasterDown ?? string.Empty, _cachedSettings?.Hotkeys.Volume.MasterDown ?? string.Empty, hotkey: true);
                SettingsMicVolumeUpHotkeyDraft = Sync(SettingsMicVolumeUpHotkeyDraft, previous?.Hotkeys.Volume.MicUp ?? string.Empty, _cachedSettings?.Hotkeys.Volume.MicUp ?? string.Empty, hotkey: true);
                SettingsMicVolumeDownHotkeyDraft = Sync(SettingsMicVolumeDownHotkeyDraft, previous?.Hotkeys.Volume.MicDown ?? string.Empty, _cachedSettings?.Hotkeys.Volume.MicDown ?? string.Empty, hotkey: true);
                SettingsHoldToMuteHotkeyDraft = Sync(SettingsHoldToMuteHotkeyDraft, previous?.Hotkeys.Mute.HoldToMute ?? string.Empty, _cachedSettings?.Hotkeys.Mute.HoldToMute ?? string.Empty, hotkey: true);
                SettingsPushToTalkHotkeyDraft = Sync(SettingsPushToTalkHotkeyDraft, previous?.Hotkeys.Mute.PushToTalk ?? string.Empty, _cachedSettings?.Hotkeys.Mute.PushToTalk ?? string.Empty, hotkey: true);
                SettingsPushToTalkEnabledDraft = Sync(SettingsPushToTalkEnabledDraft, previous?.Hotkeys.Mute.PushToTalkEnabled ?? false, _cachedSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
                SettingsForegroundMuteHotkeyDraft = Sync(SettingsForegroundMuteHotkeyDraft, previous?.Hotkeys.Volume.ForegroundMute ?? string.Empty, _cachedSettings?.Hotkeys.Volume.ForegroundMute ?? string.Empty, hotkey: true);
                SettingsForegroundVolumeDownHotkeyDraft = Sync(SettingsForegroundVolumeDownHotkeyDraft, previous?.Hotkeys.Volume.ForegroundDown ?? string.Empty, _cachedSettings?.Hotkeys.Volume.ForegroundDown ?? string.Empty, hotkey: true);
                SettingsForegroundVolumeUpHotkeyDraft = Sync(SettingsForegroundVolumeUpHotkeyDraft, previous?.Hotkeys.Volume.ForegroundUp ?? string.Empty, _cachedSettings?.Hotkeys.Volume.ForegroundUp ?? string.Empty, hotkey: true);
                SettingsSeekStepSecondsDraft = Sync(SettingsSeekStepSecondsDraft, MediaSeekStep.Format(previous?.Hotkeys.Media.SeekStepSeconds ?? MediaSeekStep.DefaultSeconds), MediaSeekStep.Format(_cachedSettings?.Hotkeys.Media.SeekStepSeconds ?? MediaSeekStep.DefaultSeconds));
                SettingsMasterVolumeStepPercentDraft = Sync(SettingsMasterVolumeStepPercentDraft, (previous?.Hotkeys.Volume.MasterVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"), (_cachedSettings?.Hotkeys.Volume.MasterVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"));
                SettingsMicVolumeStepPercentDraft = Sync(SettingsMicVolumeStepPercentDraft, (previous?.Hotkeys.Volume.MicVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"), (_cachedSettings?.Hotkeys.Volume.MicVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"));
                SettingsForegroundVolumeStepPercentDraft = Sync(SettingsForegroundVolumeStepPercentDraft, (previous?.Hotkeys.Volume.ForegroundVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"), (_cachedSettings?.Hotkeys.Volume.ForegroundVolumeStepPercent ?? VolumePercent.DefaultStep).ToString("0.#"));
                if (resetHotkeyExpansion)
                {
                    RefreshSettingsHotkeyExpansionState();
                }
                if (previous == null || (string.Equals(SettingsListenMonitorOutputDeviceIdDraft, previous.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_settingsListenMonitorOutputDeviceNameDraft, previous.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(_settingsListenMonitorOutputDeviceStableIdDraft, previous.Hotkeys.Listen.MonitorOutputDeviceStableId, StringComparison.Ordinal)))
                    SetSettingsListenMonitorOutputDraft(
                        _cachedSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty,
                        _cachedSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty,
                        _cachedSettings?.Hotkeys.Listen.MonitorOutputDeviceStableId);
                List<string> outputRoles = CloneRoleSelections(_cachedSettings?.DeviceSwitching.Output.SwitchRoles, new Settings().DeviceSwitching.Output.SwitchRoles);
                SettingsOutputRoleMultimediaDraft = Sync(SettingsOutputRoleMultimediaDraft, previous?.DeviceSwitching.Output.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase) ?? false, outputRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase));
                SettingsOutputRoleCommunicationsDraft = Sync(SettingsOutputRoleCommunicationsDraft, previous?.DeviceSwitching.Output.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase) ?? false, outputRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase));
                SettingsOutputRoleConsoleDraft = Sync(SettingsOutputRoleConsoleDraft, previous?.DeviceSwitching.Output.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase) ?? false, outputRoles.Contains("Console", StringComparer.OrdinalIgnoreCase));

                List<string> inputRoles = CloneRoleSelections(_cachedSettings?.DeviceSwitching.Input.SwitchRoles, new Settings().DeviceSwitching.Input.SwitchRoles);
                SettingsInputRoleMultimediaDraft = Sync(SettingsInputRoleMultimediaDraft, previous?.DeviceSwitching.Input.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase) ?? false, inputRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase));
                SettingsInputRoleCommunicationsDraft = Sync(SettingsInputRoleCommunicationsDraft, previous?.DeviceSwitching.Input.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase) ?? false, inputRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase));
                SettingsInputRoleConsoleDraft = Sync(SettingsInputRoleConsoleDraft, previous?.DeviceSwitching.Input.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase) ?? false, inputRoles.Contains("Console", StringComparer.OrdinalIgnoreCase));
                SettingsOverlayPositionDraft = Sync(SettingsOverlayPositionDraft, previous?.Overlay.Position ?? default, _overlayPositionBackingField);
                SettingsOverlayDurationSecondsDraft = Sync(SettingsOverlayDurationSecondsDraft, FormatOverlayDuration(previous?.Overlay.DurationSeconds ?? 3), _overlayDurationSecondsTextBackingField);

                RefreshListenMonitorOutputOptions(SettingsListenMonitorOutputDeviceIdDraft, _settingsListenMonitorOutputDeviceNameDraft, _settingsListenMonitorOutputDeviceStableIdDraft);
                if (_cachedSettings is { } persisted)
                    AcknowledgePersistedHotkeys(persisted, deferWhileEditing: true);
            }
            RefreshSettingsFieldValidation();
            QueueSettingsFeedbackRefresh();
        }

        private static string FormatOverlayDuration(double seconds) => seconds.ToString("G");

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

        private static bool TryGetVolumeStepPercent(string rawValue, out decimal value) =>
            VolumePercent.TryParseStep(rawValue, out value, allowDecimalComma: true);

        private static bool HasConfiguredHotkeyDraft(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryResolveVolumeStepPercent(string rawValue, decimal fallbackValue, bool required, out decimal value)
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

        internal static SettingsCommitValidationResult ValidateSettingsForCommit(Settings candidate) =>
            SettingsCommitValidator.Validate(candidate);

        internal static List<string> BuildDuplicateHotkeyConflicts(Settings settings) =>
            SettingsCommitValidator.BuildDuplicateHotkeyConflicts(settings);

        private static Dictionary<string, List<string>> BuildDuplicateHotkeyLabels(Settings settings) =>
            SettingsCommitValidator.BuildDuplicateHotkeyLabels(settings);

        internal static HashSet<string> BuildAssignedHotkeyKeySet(Settings settings, string? excludedRoutineId = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            HashSet<string> assignedKeys = [with(StringComparer.OrdinalIgnoreCase)];

            static void Add(HashSet<string> items, string? rawHotkey)
            {
                if (!TryNormalizeHotkey(rawHotkey, out string normalized) || string.IsNullOrWhiteSpace(normalized))
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
            Add(assignedKeys, settings.Hotkeys.App.ShowAudioStatus);
            Add(assignedKeys, settings.Hotkeys.App.QuickDevicePicker);
            Add(assignedKeys, settings.Hotkeys.App.VolumeMixer);
            Add(assignedKeys, settings.Hotkeys.Media.ShowCurrentTrack);
            Add(assignedKeys, settings.Hotkeys.Media.PlayPause);
            Add(assignedKeys, settings.Hotkeys.Media.NextTrack);
            Add(assignedKeys, settings.Hotkeys.Media.PreviousTrack);
            Add(assignedKeys, settings.Hotkeys.Media.SeekForward);
            Add(assignedKeys, settings.Hotkeys.Media.SeekBackward);
            Add(assignedKeys, settings.Hotkeys.Mute.Mic);
            Add(assignedKeys, settings.Hotkeys.Mute.Sound);
            Add(assignedKeys, settings.Hotkeys.Mute.Deafen);
            Add(assignedKeys, settings.Hotkeys.Listen.ListenToInput);
            Add(assignedKeys, settings.Hotkeys.Volume.MasterUp);
            Add(assignedKeys, settings.Hotkeys.Volume.MasterDown);
            Add(assignedKeys, settings.Hotkeys.Volume.MicUp);
            Add(assignedKeys, settings.Hotkeys.Volume.MicDown);
            Add(assignedKeys, settings.Hotkeys.Mute.HoldToMute);
            Add(assignedKeys, settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty);
            Add(assignedKeys, settings.Hotkeys.Volume.ForegroundMute);
            Add(assignedKeys, settings.Hotkeys.Volume.ForegroundDown);
            Add(assignedKeys, settings.Hotkeys.Volume.ForegroundUp);

            foreach (AudioRoutine routine in settings.Routines.Items.Where(routine =>
                routine.Enabled &&
                !string.Equals(routine.Id, excludedRoutineId, StringComparison.OrdinalIgnoreCase)))
            {
                Add(assignedKeys, routine.Hotkey);
            }

            return assignedKeys;
        }

        internal static HashSet<string> BuildDuplicateHotkeyKeySet(Settings settings)
            => BuildDuplicateHotkeyLabels(settings).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool TryNormalizeHotkey(string? raw, out string normalized)
        {
            normalized = string.Empty;
            string trimmed = raw?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                return false;
            }

            var parser = new HotkeyViewModel();
            if (!parser.LoadFromString(trimmed))
            {
                return false;
            }

            normalized = HotkeyParsingService.BuildComboKey(parser.Modifiers, parser.MainInput);
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private IEnumerable<HotkeyViewModel> EnumerateHotkeyViewModels()
        {
            yield return Hotkey;
            yield return OutputReverseHotkey;
            yield return InputHotkey;
            yield return InputReverseHotkey;
            yield return SettingsToggleAppVisibilityHotkeyDraftCapture;
            yield return SettingsShowAudioStatusHotkeyDraftCapture;
            yield return SettingsQuickDevicePickerHotkeyDraftCapture;
            yield return SettingsVolumeMixerHotkeyDraftCapture;
            yield return SettingsShowCurrentTrackHotkeyDraftCapture;
            yield return SettingsPlayPauseHotkeyDraftCapture;
            yield return SettingsNextTrackHotkeyDraftCapture;
            yield return SettingsPreviousTrackHotkeyDraftCapture;
            yield return SettingsSeekForwardHotkeyDraftCapture;
            yield return SettingsSeekBackwardHotkeyDraftCapture;
            yield return SettingsMuteMicHotkeyDraftCapture;
            yield return SettingsMuteSoundHotkeyDraftCapture;
            yield return SettingsDeafenHotkeyDraftCapture;
            yield return SettingsListenToInputHotkeyDraftCapture;
            yield return SettingsMasterVolumeUpHotkeyDraftCapture;
            yield return SettingsMasterVolumeDownHotkeyDraftCapture;
            yield return SettingsMicVolumeUpHotkeyDraftCapture;
            yield return SettingsMicVolumeDownHotkeyDraftCapture;
            yield return SettingsHoldToMuteHotkeyDraftCapture;
            yield return SettingsPushToTalkHotkeyDraftCapture;
            yield return SettingsForegroundMuteHotkeyDraftCapture;
            yield return SettingsForegroundVolumeDownHotkeyDraftCapture;
            yield return SettingsForegroundVolumeUpHotkeyDraftCapture;
        }

        internal void RefreshHotkeyDisplayLabels()
        {
            foreach (HotkeyViewModel hotkey in EnumerateHotkeyViewModels())
            {
                hotkey.RefreshDisplayText();
            }
        }

        internal void ReportHotkeyAvailability() => _shell.ReportHotkeyAvailability(_hotkeys.CaptureRuntimeSnapshot());

        internal static bool AreHotkeyStringsEquivalent(string? left, string? right)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

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

            if (result.ShowAudioStatusAttempted && !result.ShowAudioStatusRegistered)
            {
                warnings.Add("Show Audio Status hotkey could not be registered.");
            }
            if (result.QuickDevicePickerAttempted && !result.QuickDevicePickerRegistered)
            {
                warnings.Add("Quick Device Picker hotkey could not be registered.");
            }
            if (result.VolumeMixerAttempted && !result.VolumeMixerRegistered)
            {
                warnings.Add("Volume Mixer hotkey could not be registered.");
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
