using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private IEnumerable<string> EnumerateDeviceSettingChanges(Settings saved, bool output, bool includeStartup = true)
    {
        if (!AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(output ? OutputCycleDevices : InputCycleDevices,
            output ? saved.DeviceSwitching.Output.CycleDevices : saved.DeviceSwitching.Input.CycleDevices)) yield return "Device order";
        if (!string.Equals(output ? Hotkey.ToHotkeyString() : InputHotkey.ToHotkeyString(),
            (output ? saved.DeviceSwitching.Output.SwitchHotkey : saved.DeviceSwitching.Input.SwitchHotkey) ?? string.Empty, StringComparison.OrdinalIgnoreCase)) yield return "Switching hotkey";
        if ((output ? OutputHotkeysEnabled : InputHotkeysEnabled) != (output ? saved.DeviceSwitching.Output.HotkeysEnabled : saved.DeviceSwitching.Input.HotkeysEnabled)) yield return "Hotkeys enabled";
        if (includeStartup && RunAtStartup != saved.RunAtStartup) yield return "Run at startup";
        if (PreserveAudioLevels != saved.DeviceSwitching.PreserveAudioLevels) yield return "Preserve audio levels";
        if (KeepMuteStateWhenSwitching != saved.DeviceSwitching.KeepMuteStateWhenSwitching) yield return "Preserve mute state";
        if (OverlayEnabled != saved.Overlay.Enabled) yield return "Overlay enabled";
        if (OverlayPosition != saved.Overlay.Position) yield return "Overlay position";
        if (!double.TryParse(OverlayDurationSecondsText, out double seconds) || Math.Abs(seconds - saved.Overlay.DurationSeconds) > 0.001) yield return "Overlay duration";
        if (Theme != saved.Theme) yield return "Theme";
    }

    /// <summary>Shares change comparisons between pending-state detection and its display labels, including invalid drafts.</summary>
    private IEnumerable<string> EnumerateSettingsDraftChanges(Settings saved, bool includeToolbarMirrors = true)
    {
        if (!string.Equals(SettingsLogLevelDraft.ToString(), saved.Miscellaneous.LogLevel ?? "Info", StringComparison.OrdinalIgnoreCase)) yield return "Log level";
        if (SettingsAutoSaveEnabledDraft != saved.Miscellaneous.AutoSaveEnabled) yield return "Auto-save";
        if (includeToolbarMirrors && SettingsRunAtStartupDraft != saved.RunAtStartup) yield return "Run at startup";
        if (includeToolbarMirrors && SettingsThemeDraft != saved.Theme) yield return "Theme";
        if (SettingsPreserveAudioLevelsDraft != saved.DeviceSwitching.PreserveAudioLevels) yield return "Preserve audio levels";
        if (SettingsKeepMuteStateWhenSwitchingDraft != saved.DeviceSwitching.KeepMuteStateWhenSwitching) yield return "Preserve mute state";
        if (SettingsAutoScrollToMixerOnRestoreDraft != saved.Miscellaneous.AutoScrollToMixerOnRestore) yield return "Auto-scroll to mixer";
        if (SettingsOverlayEnabledDraft != saved.Overlay.Enabled) yield return "Overlay enabled";
        if (SettingsBluetoothReconnectEnabledDraft != saved.DeviceSwitching.BluetoothReconnectEnabled) yield return "Bluetooth reconnect";
        if (SettingsDeviceReferenceFileModeDraft != saved.Miscellaneous.DeviceReferenceFileMode) yield return "Device reference file";
        if (SettingsRedactLogContentDraft != saved.Miscellaneous.RedactLogContent) yield return "Log redaction";
        if (SettingsPlayAppSoundsDraft != saved.Miscellaneous.PlayAppSounds) yield return "App sounds";
        if (SettingsUseScheduledStartupDraft != saved.Miscellaneous.UseScheduledStartup) yield return "Startup method";
        if (SettingsCheckForUpdatesDraft != saved.Miscellaneous.CheckForUpdates) yield return "Update checks";
        if (SettingsOverlayPositionDraft != saved.Overlay.Position) yield return "Overlay position";
        if (!string.Equals(SettingsOverlayDurationSecondsDraft, saved.Overlay.DurationSeconds.ToString("0.0"), StringComparison.Ordinal)) yield return "Overlay duration";
        if (!MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out int seekStep) || seekStep != saved.Hotkeys.Media.SeekStepSeconds) yield return "Seek step";
        if (!string.Equals(SettingsMasterVolumeStepPercentDraft, saved.Hotkeys.Volume.MasterVolumeStepPercent.ToString(), StringComparison.Ordinal)) yield return "Master volume step";
        if (!string.Equals(SettingsMicVolumeStepPercentDraft, saved.Hotkeys.Volume.MicVolumeStepPercent.ToString(), StringComparison.Ordinal)) yield return "Microphone volume step";
        if (!string.Equals(SettingsForegroundVolumeStepPercentDraft, saved.Hotkeys.Volume.ForegroundVolumeStepPercent.ToString(), StringComparison.Ordinal)) yield return "Foreground app volume step";
        if (!string.Equals(SettingsListenMonitorOutputDeviceIdDraft, saved.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_settingsListenMonitorOutputDeviceNameDraft, saved.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(_settingsListenMonitorOutputDeviceStableIdDraft, saved.Hotkeys.Listen.MonitorOutputDeviceStableId, StringComparison.Ordinal)) yield return "Monitoring output";
        if (SettingsOutputRoleMultimediaDraft != saved.DeviceSwitching.Output.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase)
            || SettingsOutputRoleCommunicationsDraft != saved.DeviceSwitching.Output.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase)
            || SettingsOutputRoleConsoleDraft != saved.DeviceSwitching.Output.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase)) yield return "Output device roles";
        if (SettingsInputRoleMultimediaDraft != saved.DeviceSwitching.Input.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase)
            || SettingsInputRoleCommunicationsDraft != saved.DeviceSwitching.Input.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase)
            || SettingsInputRoleConsoleDraft != saved.DeviceSwitching.Input.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase)) yield return "Input device roles";
        if (SettingsPushToTalkEnabledDraft != saved.Hotkeys.Mute.PushToTalkEnabled) yield return "Push-to-talk mode";
        if (!AreHotkeyStringsEquivalent(SettingsShowAudioStatusHotkeyDraft, saved.Hotkeys.App.ShowAudioStatus)) yield return "Audio status hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsQuickDevicePickerHotkeyDraft, saved.Hotkeys.App.QuickDevicePicker)) yield return "Device picker hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsToggleAppVisibilityHotkeyDraft, saved.Hotkeys.App.ToggleAppVisibility)) yield return "Show/hide hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsShowCurrentTrackHotkeyDraft, saved.Hotkeys.Media.ShowCurrentTrack)) yield return "Current track hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsPlayPauseHotkeyDraft, saved.Hotkeys.Media.PlayPause)) yield return "Play/pause hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsNextTrackHotkeyDraft, saved.Hotkeys.Media.NextTrack)) yield return "Next track hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsPreviousTrackHotkeyDraft, saved.Hotkeys.Media.PreviousTrack)) yield return "Previous track hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsSeekForwardHotkeyDraft, saved.Hotkeys.Media.SeekForward)) yield return "Seek forward hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsSeekBackwardHotkeyDraft, saved.Hotkeys.Media.SeekBackward)) yield return "Seek backward hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMuteMicHotkeyDraft, saved.Hotkeys.Mute.Mic)) yield return "Microphone mute hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMuteSoundHotkeyDraft, saved.Hotkeys.Mute.Sound)) yield return "Output mute hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsDeafenHotkeyDraft, saved.Hotkeys.Mute.Deafen)) yield return "Deafen hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsListenToInputHotkeyDraft, saved.Hotkeys.Listen.ListenToInput)) yield return "Listen-monitor hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMasterVolumeUpHotkeyDraft, saved.Hotkeys.Volume.MasterUp)) yield return "Master volume up hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMasterVolumeDownHotkeyDraft, saved.Hotkeys.Volume.MasterDown)) yield return "Master volume down hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMicVolumeUpHotkeyDraft, saved.Hotkeys.Volume.MicUp)) yield return "Microphone volume up hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsMicVolumeDownHotkeyDraft, saved.Hotkeys.Volume.MicDown)) yield return "Microphone volume down hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsForegroundVolumeUpHotkeyDraft, saved.Hotkeys.Volume.ForegroundUp)) yield return "Foreground app volume up hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsForegroundVolumeDownHotkeyDraft, saved.Hotkeys.Volume.ForegroundDown)) yield return "Foreground app volume down hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsForegroundMuteHotkeyDraft, saved.Hotkeys.Volume.ForegroundMute)) yield return "Foreground app mute hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsPushToTalkHotkeyDraft, saved.Hotkeys.Mute.PushToTalk)) yield return "Push-to-talk hotkey";
        if (!AreHotkeyStringsEquivalent(SettingsHoldToMuteHotkeyDraft, saved.Hotkeys.Mute.HoldToMute)) yield return "Hold-to-mute hotkey";
        if (!AreHotkeyStringsEquivalent(OutputReverseHotkey.ToHotkeyString(), saved.DeviceSwitching.Output.ReverseSwitchHotkey)) yield return "Reverse output hotkey";
        if (!AreHotkeyStringsEquivalent(InputReverseHotkey.ToHotkeyString(), saved.DeviceSwitching.Input.ReverseSwitchHotkey)) yield return "Reverse input hotkey";
    }

    private IEnumerable<string> EnumerateRoutineChanges(Settings saved)
    {
        var current = Routines.OfType<AudioRoutine>().ToList();
        var previous = saved.Routines.Items?.OfType<AudioRoutine>().ToList() ?? [];
        var currentIds = current.Select(routine => routine.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedIds = previous.Select(routine => routine.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentIds.Except(savedIds, StringComparer.OrdinalIgnoreCase).Any()) yield return "Added routines";
        if (savedIds.Except(currentIds, StringComparer.OrdinalIgnoreCase).Any()) yield return "Removed routines";
        var common = current.Where(routine => savedIds.Contains(routine.Id)).ToList();
        var previousCommon = previous.Where(routine => currentIds.Contains(routine.Id)).ToList();
        if (!common.Select(routine => routine.Id).SequenceEqual(previousCommon.Select(routine => routine.Id), StringComparer.OrdinalIgnoreCase)) yield return "Routine order";
        var savedById = previous.GroupBy(routine => routine.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!AreRoutineListsEquivalent(common, common.Select(routine => savedById[routine.Id]))) yield return "Routine options";
    }
}
