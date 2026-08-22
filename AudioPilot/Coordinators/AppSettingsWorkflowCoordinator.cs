using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SaveEditState(
        string CurrentOutputHotkey,
        string CurrentInputHotkey,
        bool OutputEdited,
        bool InputEdited,
        bool OutputCleared,
        bool InputCleared);

    internal readonly record struct ApplySettingsBuildInput(
        string OutputReverseSwitchHotkey,
        bool OutputHotkeysEnabled,
        string InputReverseSwitchHotkey,
        bool InputHotkeysEnabled,
        IReadOnlyList<string> AdditionalStandaloneHotkeyKeys,
        IReadOnlyList<string> OutputSwitchRoles,
        IReadOnlyList<string> InputSwitchRoles,
        bool AutoSaveEnabled,
        bool PlayDialogSounds,
        bool RunAtStartup,
        string ToggleAppVisibilityHotkey,
        string ShowCurrentTrackHotkey,
        string PlayPauseHotkey,
        string NextTrackHotkey,
        string PreviousTrackHotkey,
        string MuteMicHotkey,
        string MuteSoundHotkey,
        string DeafenHotkey,
        string ListenToInputHotkey,
        string MasterVolumeUpHotkey,
        string MasterVolumeDownHotkey,
        string MicVolumeUpHotkey,
        string MicVolumeDownHotkey,
        int MasterVolumeStepPercent,
        int MicVolumeStepPercent,
        string ListenMonitorOutputDeviceId,
        string ListenMonitorOutputDeviceName,
        IReadOnlyList<CycleDevice> AvailableOutputDevices,
        bool PreserveAudioLevels,
        bool BluetoothReconnectEnabled,
        DeviceReferenceFileMode DeviceReferenceFileMode,
        bool OverlayEnabled,
        AppTheme Theme,
        string LogLevel,
        bool RedactLogContent,
        bool AutoScrollToMixerOnRestore,
        OverlayPosition OverlayPosition,
        double OverlayDurationSeconds);

    internal readonly record struct SaveSettingsBuildInput(
        IReadOnlyList<CycleDevice> OutputCycleDevices,
        IReadOnlyList<CycleDevice> InputCycleDevices,
        IReadOnlyList<CycleDevice> AvailableOutputDevices,
        IReadOnlyList<CycleDevice> AvailableInputDevices,
        SaveEditState EditState,
        bool CanWriteOutput,
        bool CanWriteInput,
            IReadOnlyList<string> AdditionalStandaloneHotkeyKeys,
        string CurrentOutputReverseHotkey,
        string CurrentInputReverseHotkey,
        bool OutputHotkeysEnabled,
        bool InputHotkeysEnabled,
        bool RunAtStartup,
        bool PreserveAudioLevels,
        bool OverlayEnabled,
        OverlayPosition OverlayPosition,
        double OverlayDurationSeconds,
        AppTheme Theme,
        bool RedactLogContent);

    internal static class AppSettingsWorkflowCoordinator
    {
        internal static Settings CloneSettings(Settings? source)
        {
            var defaults = new Settings();
            if (source == null)
            {
                return defaults;
            }

            return new Settings
            {
                SchemaVersion = source.SchemaVersion,
                Theme = source.Theme,
                RunAtStartup = source.RunAtStartup,
                Miscellaneous = MiscellaneousSettings.Clone(source.Miscellaneous),
                DeviceSwitching = DeviceSwitchingSettings.Clone(source.DeviceSwitching),
                Hotkeys = HotkeysSettings.Clone(source.Hotkeys),
                Routines = RoutinesSettings.Clone(source.Routines),
                Overlay = OverlaySettings.Clone(source.Overlay),
                AdvancedTuning = AdvancedTuningSettings.Clone(source.AdvancedTuning),
            };
        }

        internal static SaveEditState BuildSaveEditState(
            IEnumerable<CycleDevice> outputCycleDevices,
            IEnumerable<CycleDevice> inputCycleDevices,
            string currentOutputHotkey,
            string currentInputHotkey,
            bool outputHotkeysEnabled,
            bool inputHotkeysEnabled,
            Settings? cachedSettings)
        {
            bool outputEdited =
                !AreCycleListsEquivalent(outputCycleDevices, cachedSettings?.DeviceSwitching.Output.CycleDevices) ||
                !string.Equals(currentOutputHotkey, cachedSettings?.DeviceSwitching.Output.SwitchHotkey ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                outputHotkeysEnabled != (cachedSettings?.DeviceSwitching.Output.HotkeysEnabled ?? true);

            bool inputEdited =
                !AreCycleListsEquivalent(inputCycleDevices, cachedSettings?.DeviceSwitching.Input.CycleDevices) ||
                !string.Equals(currentInputHotkey, cachedSettings?.DeviceSwitching.Input.SwitchHotkey ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                inputHotkeysEnabled != (cachedSettings?.DeviceSwitching.Input.HotkeysEnabled ?? true);

            bool hadSavedOutputDevices = (cachedSettings?.DeviceSwitching.Output.CycleDevices?.Count ?? 0) > 0;
            bool hadSavedInputDevices = (cachedSettings?.DeviceSwitching.Input.CycleDevices?.Count ?? 0) > 0;
            bool outputCleared = outputEdited && !HasAnyValidCycleDevice(outputCycleDevices) && hadSavedOutputDevices;
            bool inputCleared = inputEdited && !HasAnyValidCycleDevice(inputCycleDevices) && hadSavedInputDevices;

            return new SaveEditState(
                currentOutputHotkey,
                currentInputHotkey,
                outputEdited,
                inputEdited,
                outputCleared,
                inputCleared);
        }

        internal static Settings BuildAppliedSettings(Settings? cachedSettings, ApplySettingsBuildInput input)
        {
            Settings newSettings = CloneSettings(cachedSettings);
            newSettings.Hotkeys.Global.AdditionalStandaloneKeys = [.. input.AdditionalStandaloneHotkeyKeys];
            newSettings.DeviceSwitching.Output.SwitchRoles = [.. input.OutputSwitchRoles];
            newSettings.DeviceSwitching.Input.SwitchRoles = [.. input.InputSwitchRoles];
            newSettings.Miscellaneous.AutoSaveEnabled = input.AutoSaveEnabled;
            newSettings.Miscellaneous.PlayDialogSounds = input.PlayDialogSounds;
            newSettings.DeviceSwitching.Output.ReverseSwitchHotkey = input.OutputReverseSwitchHotkey;
            newSettings.DeviceSwitching.Output.HotkeysEnabled = input.OutputHotkeysEnabled;
            newSettings.DeviceSwitching.Input.ReverseSwitchHotkey = input.InputReverseSwitchHotkey;
            newSettings.DeviceSwitching.Input.HotkeysEnabled = input.InputHotkeysEnabled;
            newSettings.RunAtStartup = input.RunAtStartup;
            newSettings.Hotkeys.App.ToggleAppVisibility = input.ToggleAppVisibilityHotkey;
            newSettings.Hotkeys.Media.ShowCurrentTrack = input.ShowCurrentTrackHotkey;
            newSettings.Hotkeys.Media.PlayPause = input.PlayPauseHotkey;
            newSettings.Hotkeys.Media.NextTrack = input.NextTrackHotkey;
            newSettings.Hotkeys.Media.PreviousTrack = input.PreviousTrackHotkey;
            newSettings.Hotkeys.Mute.Mic = input.MuteMicHotkey;
            newSettings.Hotkeys.Mute.Sound = input.MuteSoundHotkey;
            newSettings.Hotkeys.Mute.Deafen = input.DeafenHotkey;
            newSettings.Hotkeys.Listen.ListenToInput = input.ListenToInputHotkey;
            newSettings.Hotkeys.Volume.MasterUp = input.MasterVolumeUpHotkey;
            newSettings.Hotkeys.Volume.MasterDown = input.MasterVolumeDownHotkey;
            newSettings.Hotkeys.Volume.MicUp = input.MicVolumeUpHotkey;
            newSettings.Hotkeys.Volume.MicDown = input.MicVolumeDownHotkey;
            newSettings.Hotkeys.Volume.MasterVolumeStepPercent = input.MasterVolumeStepPercent;
            newSettings.Hotkeys.Volume.MicVolumeStepPercent = input.MicVolumeStepPercent;
            CycleDevice resolvedListenMonitorOutput = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                new CycleDevice
                {
                    Id = input.ListenMonitorOutputDeviceId,
                    Name = input.ListenMonitorOutputDeviceName,
                },
                input.AvailableOutputDevices);
            newSettings.Hotkeys.Listen.MonitorOutputDeviceId = resolvedListenMonitorOutput.Id;
            newSettings.Hotkeys.Listen.MonitorOutputDeviceName = string.IsNullOrWhiteSpace(resolvedListenMonitorOutput.Id)
                ? string.Empty
                : resolvedListenMonitorOutput.Name;
            newSettings.Miscellaneous.PreserveAudioLevels = input.PreserveAudioLevels;
            newSettings.Miscellaneous.BluetoothReconnectEnabled = input.BluetoothReconnectEnabled;
            newSettings.Miscellaneous.DeviceReferenceFileMode = input.DeviceReferenceFileMode;
            newSettings.Overlay.Enabled = input.OverlayEnabled;
            newSettings.Theme = input.Theme;
            newSettings.Miscellaneous.LogLevel = input.LogLevel;
            newSettings.Miscellaneous.RedactLogContent = input.RedactLogContent;
            newSettings.Miscellaneous.AutoScrollToMixerOnRestore = input.AutoScrollToMixerOnRestore;
            newSettings.Overlay.Position = input.OverlayPosition;
            newSettings.Overlay.DurationSeconds = input.OverlayDurationSeconds;
            return newSettings;
        }

        internal static Settings BuildSavedSettings(Settings? cachedSettings, SaveSettingsBuildInput input)
        {
            Settings newSettings = CloneSettings(cachedSettings);
            newSettings.Hotkeys.Global.AdditionalStandaloneKeys = [.. input.AdditionalStandaloneHotkeyKeys];
            newSettings.DeviceSwitching.Output.CycleDevices = input.EditState.OutputCleared
                ? []
                : (input.CanWriteOutput
                    ? AppViewModelDeviceCycleHelper.ReconcilePersistedCycleDevices(input.OutputCycleDevices, input.AvailableOutputDevices)
                    : AppViewModelDeviceCycleHelper.CloneCycleDevices(cachedSettings?.DeviceSwitching.Output.CycleDevices));
            newSettings.DeviceSwitching.Output.SwitchHotkey = input.EditState.CurrentOutputHotkey;
            newSettings.DeviceSwitching.Output.ReverseSwitchHotkey = input.CurrentOutputReverseHotkey;
            newSettings.DeviceSwitching.Output.HotkeysEnabled = input.OutputHotkeysEnabled;
            newSettings.DeviceSwitching.Input.CycleDevices = input.EditState.InputCleared
                ? []
                : (input.CanWriteInput
                    ? AppViewModelDeviceCycleHelper.ReconcilePersistedCycleDevices(input.InputCycleDevices, input.AvailableInputDevices)
                    : AppViewModelDeviceCycleHelper.CloneCycleDevices(cachedSettings?.DeviceSwitching.Input.CycleDevices));
            newSettings.DeviceSwitching.Input.SwitchHotkey = input.EditState.CurrentInputHotkey;
            newSettings.DeviceSwitching.Input.ReverseSwitchHotkey = input.CurrentInputReverseHotkey;
            newSettings.DeviceSwitching.Input.HotkeysEnabled = input.InputHotkeysEnabled;
            newSettings.RunAtStartup = input.RunAtStartup;
            newSettings.Miscellaneous.PreserveAudioLevels = input.PreserveAudioLevels;
            newSettings.Overlay.Enabled = input.OverlayEnabled;
            newSettings.Overlay.Position = input.OverlayPosition;
            newSettings.Overlay.DurationSeconds = input.OverlayDurationSeconds;
            newSettings.Theme = input.Theme;
            newSettings.Miscellaneous.RedactLogContent = input.RedactLogContent;
            return newSettings;
        }

        internal static bool AreCycleListsEquivalent(IEnumerable<CycleDevice>? current, IEnumerable<CycleDevice>? saved)
        {
            var currentList = BuildValidCycleList(current);
            var savedList = BuildValidCycleList(saved);

            if (currentList.Count != savedList.Count)
            {
                return false;
            }

            for (int index = 0; index < currentList.Count; index++)
            {
                if (!string.Equals(currentList[index].Id, savedList[index].Id, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        internal static int CountValidCycleDevices(IEnumerable<CycleDevice>? devices)
        {
            return BuildValidCycleList(devices).Count;
        }

        private static bool HasAnyValidCycleDevice(IEnumerable<CycleDevice> devices)
        {
            foreach (var device in devices)
            {
                if (device != null && !string.IsNullOrWhiteSpace(device.Id))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<CycleDevice> BuildValidCycleList(IEnumerable<CycleDevice>? devices)
        {
            var list = new List<CycleDevice>();
            if (devices == null)
            {
                return list;
            }

            foreach (var device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id))
                {
                    continue;
                }

                list.Add(device);
            }

            return list;
        }

        internal static bool ShouldSkipReset(bool settingsFileExists, bool hasDevicesSelected, bool hasRoutines, bool hasHotkey, bool hasStartup)
        {
            return !settingsFileExists && !hasDevicesSelected && !hasRoutines && !hasHotkey && !hasStartup;
        }

        internal static List<string> BuildResetSummary(bool hasDevicesSelected, bool hasRoutines, bool hasHotkey, bool hasStartup, bool settingsFileExists)
        {
            var summary = new List<string>();

            if (hasDevicesSelected)
            {
                summary.Add("- Clear all device selections");
            }

            if (hasRoutines)
            {
                summary.Add("- Delete all saved routines");
            }

            if (hasHotkey)
            {
                summary.Add("- Unregister all hotkeys");
            }

            if (hasStartup)
            {
                summary.Add("- Remove startup registry entry");
            }

            if (settingsFileExists)
            {
                summary.Add("- Delete all saved settings");
            }

            return summary;
        }
    }
}
