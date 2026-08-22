using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ApplySettingsSideEffectResult(IReadOnlyList<string> Warnings);

    internal readonly record struct SaveSettingsSideEffectResult(IReadOnlyList<string> Warnings);

    internal static class AppSettingsEffectsCoordinator
    {
        internal static ApplySettingsSideEffectResult RunApplySideEffects(
            Settings? previousSettings,
            Settings newSettings,
            bool outputRolesFallbackApplied,
            bool inputRolesFallbackApplied,
            Action persistUiState,
            Func<HotkeyRegistrationResult> registerHotkeys,
            Action<HotkeyRegistrationResult> logHotkeyResults,
            Action<Settings> registerRoutineHotkeys,
            Action<Settings> updateAudioConfiguration,
            Action<Settings> updateOverlayState,
            Action generateDeviceReferenceFile,
            Action syncSettingsDrafts,
            Func<HotkeyRegistrationResult, List<string>> getHotkeyRegistrationWarnings)
        {
            persistUiState();

            bool hotkeysChanged = HaveGlobalHotkeysChanged(previousSettings, newSettings);
            bool roleConfigurationChanged = HaveRoleSelectionsChanged(previousSettings, newSettings);
            bool overlayStateChanged = HaveOverlaySettingsChanged(previousSettings, newSettings);
            bool deviceReferenceModeChanged = HaveDeviceReferenceModeChanged(previousSettings, newSettings);
            HotkeyRegistrationResult? hotkeyRegistrationResult = null;

            if (hotkeysChanged)
            {
                hotkeyRegistrationResult = registerHotkeys();
                logHotkeyResults(hotkeyRegistrationResult.Value);
                registerRoutineHotkeys(newSettings);
            }

            if (roleConfigurationChanged)
            {
                updateAudioConfiguration(newSettings);
            }

            if (overlayStateChanged)
            {
                updateOverlayState(newSettings);
            }

            if (deviceReferenceModeChanged)
            {
                generateDeviceReferenceFile();
            }

            syncSettingsDrafts();

            List<string> warnings = [];
            if (outputRolesFallbackApplied)
            {
                warnings.Add("Output roles were all unchecked, so all output roles were restored (Multimedia, Communications, Console).");
            }

            if (inputRolesFallbackApplied)
            {
                warnings.Add("Input roles were all unchecked, so all input roles were restored (Multimedia, Communications, Console).");
            }

            if (hotkeyRegistrationResult.HasValue)
            {
                warnings.AddRange(getHotkeyRegistrationWarnings(hotkeyRegistrationResult.Value));
            }

            return new ApplySettingsSideEffectResult(warnings);
        }

        internal static SaveSettingsSideEffectResult RunSaveSideEffects(
            Settings? previousSettings,
            Settings newSettings,
            IReadOnlyList<string> disconnectedOutput,
            IReadOnlyList<string> disconnectedInput,
            Action persistUiState,
            Func<SwitchHotkeyRegistrationResult> registerSwitchHotkeys,
            Action<SwitchHotkeyRegistrationResult> logSwitchHotkeyResults,
            Action<Settings> updateAudioConfiguration,
            Action<Settings> updateOverlayState,
            Func<SwitchHotkeyRegistrationResult, List<string>> getSwitchHotkeyRegistrationWarnings)
        {
            bool switchHotkeysChanged = HaveSwitchHotkeysChanged(previousSettings, newSettings);
            bool roleConfigurationChanged = HaveRoleSelectionsChanged(previousSettings, newSettings);
            bool overlayStateChanged = HaveOverlaySettingsChanged(previousSettings, newSettings);
            SwitchHotkeyRegistrationResult? switchRegistrationResult = null;

            if (switchHotkeysChanged)
            {
                switchRegistrationResult = registerSwitchHotkeys();
                logSwitchHotkeyResults(switchRegistrationResult.Value);
            }

            persistUiState();

            if (roleConfigurationChanged)
            {
                updateAudioConfiguration(newSettings);
            }

            if (overlayStateChanged)
            {
                updateOverlayState(newSettings);
            }

            List<string> warnings = [];
            if (disconnectedOutput.Count > 0 || disconnectedInput.Count > 0)
            {
                var sections = new List<string>();
                if (disconnectedOutput.Count > 0)
                {
                    sections.Add($"Output disconnected: {string.Join(", ", disconnectedOutput)}");
                }

                if (disconnectedInput.Count > 0)
                {
                    sections.Add($"Input disconnected: {string.Join(", ", disconnectedInput)}");
                }

                warnings.Add("Some configured devices are disconnected: " + string.Join(" | ", sections));
            }

            if (switchRegistrationResult.HasValue)
            {
                warnings.AddRange(getSwitchHotkeyRegistrationWarnings(switchRegistrationResult.Value));
            }

            return new SaveSettingsSideEffectResult(warnings);
        }

        private static bool HaveGlobalHotkeysChanged(Settings? previousSettings, Settings newSettings)
        {
            if (previousSettings == null)
            {
                return true;
            }

            return !HaveEquivalentHotkey(previousSettings.Hotkeys.App.ToggleAppVisibility, newSettings.Hotkeys.App.ToggleAppVisibility) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Media.ShowCurrentTrack, newSettings.Hotkeys.Media.ShowCurrentTrack) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Media.PlayPause, newSettings.Hotkeys.Media.PlayPause) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Media.NextTrack, newSettings.Hotkeys.Media.NextTrack) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Media.PreviousTrack, newSettings.Hotkeys.Media.PreviousTrack) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Mute.Mic, newSettings.Hotkeys.Mute.Mic) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Mute.Sound, newSettings.Hotkeys.Mute.Sound) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Mute.Deafen, newSettings.Hotkeys.Mute.Deafen) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Listen.ListenToInput, newSettings.Hotkeys.Listen.ListenToInput) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Volume.MasterUp, newSettings.Hotkeys.Volume.MasterUp) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Volume.MasterDown, newSettings.Hotkeys.Volume.MasterDown) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Volume.MicUp, newSettings.Hotkeys.Volume.MicUp) ||
                !HaveEquivalentHotkey(previousSettings.Hotkeys.Volume.MicDown, newSettings.Hotkeys.Volume.MicDown) ||
                previousSettings.Hotkeys.Volume.MasterVolumeStepPercent != newSettings.Hotkeys.Volume.MasterVolumeStepPercent ||
                previousSettings.Hotkeys.Volume.MicVolumeStepPercent != newSettings.Hotkeys.Volume.MicVolumeStepPercent ||
                !HaveEquivalentStringSet(previousSettings.Hotkeys.Global.AdditionalStandaloneKeys, newSettings.Hotkeys.Global.AdditionalStandaloneKeys) ||
                HaveSwitchHotkeysChanged(previousSettings, newSettings);
        }

        private static bool HaveSwitchHotkeysChanged(Settings? previousSettings, Settings newSettings)
        {
            if (previousSettings == null)
            {
                return true;
            }

            return previousSettings.DeviceSwitching.Output.HotkeysEnabled != newSettings.DeviceSwitching.Output.HotkeysEnabled ||
                previousSettings.DeviceSwitching.Input.HotkeysEnabled != newSettings.DeviceSwitching.Input.HotkeysEnabled ||
                !HaveEquivalentHotkey(previousSettings.DeviceSwitching.Output.SwitchHotkey, newSettings.DeviceSwitching.Output.SwitchHotkey) ||
                !HaveEquivalentHotkey(previousSettings.DeviceSwitching.Output.ReverseSwitchHotkey, newSettings.DeviceSwitching.Output.ReverseSwitchHotkey) ||
                !HaveEquivalentHotkey(previousSettings.DeviceSwitching.Input.SwitchHotkey, newSettings.DeviceSwitching.Input.SwitchHotkey) ||
                !HaveEquivalentHotkey(previousSettings.DeviceSwitching.Input.ReverseSwitchHotkey, newSettings.DeviceSwitching.Input.ReverseSwitchHotkey);
        }

        private static bool HaveRoleSelectionsChanged(Settings? previousSettings, Settings newSettings)
        {
            if (previousSettings == null)
            {
                return true;
            }

            return !HaveEquivalentStringSet(previousSettings.DeviceSwitching.Output.SwitchRoles, newSettings.DeviceSwitching.Output.SwitchRoles) ||
                !HaveEquivalentStringSet(previousSettings.DeviceSwitching.Input.SwitchRoles, newSettings.DeviceSwitching.Input.SwitchRoles);
        }

        private static bool HaveOverlaySettingsChanged(Settings? previousSettings, Settings newSettings)
        {
            if (previousSettings == null)
            {
                return true;
            }

            return previousSettings.Overlay.Enabled != newSettings.Overlay.Enabled ||
                previousSettings.Overlay.Position != newSettings.Overlay.Position ||
                Math.Abs(previousSettings.Overlay.DurationSeconds - newSettings.Overlay.DurationSeconds) > 0.001;
        }

        private static bool HaveDeviceReferenceModeChanged(Settings? previousSettings, Settings newSettings)
        {
            return previousSettings == null || previousSettings.Miscellaneous.DeviceReferenceFileMode != newSettings.Miscellaneous.DeviceReferenceFileMode;
        }

        private static bool HaveEquivalentHotkey(string? left, string? right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HaveEquivalentStringSet(IEnumerable<string>? left, IEnumerable<string>? right)
        {
            string[] leftNormalized = NormalizeStrings(left);
            string[] rightNormalized = NormalizeStrings(right);

            if (leftNormalized.Length != rightNormalized.Length)
            {
                return false;
            }

            for (int index = 0; index < leftNormalized.Length; index++)
            {
                if (!string.Equals(leftNormalized[index], rightNormalized[index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] NormalizeStrings(IEnumerable<string>? values)
        {
            if (values == null)
            {
                return [];
            }

            return [.. values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)];
        }
    }
}
