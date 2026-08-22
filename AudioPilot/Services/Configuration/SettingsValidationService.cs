using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration
{
    public readonly record struct CycleValidationResult(
        bool IsValid,
        IReadOnlyList<string> DuplicateDeviceNames,
        IReadOnlyList<string> DisconnectedDeviceNames);

    public readonly record struct CycleSwitchPreflightResult(
        bool CanSwitch,
        int ConfiguredCount,
        int ConnectedConfiguredCount,
        bool HasDefaultInputDevice,
        IReadOnlyList<string> Reasons);

    public readonly record struct SettingsDiagnostic(
        string Code,
        string Message,
        string SuggestedAction);

    public readonly record struct SettingsDiagnosticsResult(
        IReadOnlyList<SettingsDiagnostic> Warnings)
    {
        public bool HasWarnings => Warnings.Count > 0;
    }

    internal readonly record struct HotkeyValidationResult(
        bool IsValid,
        bool IsReserved,
        string ReservedShortcutName);

    public static class SettingsValidationService
    {
        private const string OutputSwitchHotkeyFieldKey = "DeviceSwitching.Output.SwitchHotkey";
        private const string OutputReverseSwitchHotkeyFieldKey = "DeviceSwitching.Output.ReverseSwitchHotkey";
        private const string InputSwitchHotkeyFieldKey = "DeviceSwitching.Input.SwitchHotkey";
        private const string InputReverseSwitchHotkeyFieldKey = "DeviceSwitching.Input.ReverseSwitchHotkey";

        private static readonly Dictionary<string, (string DisplayName, string CliKey)> HotkeyFields = new(StringComparer.Ordinal)
        {
            [nameof(Settings.Hotkeys.App.ToggleAppVisibility)] = ("Show/hide app hotkey", "toggle-app-visibility-hotkey"),
            [nameof(Settings.Hotkeys.App.ShowAudioStatus)] = ("Show audio status hotkey", "show-audio-status-hotkey"),
            [nameof(Settings.Hotkeys.Media.ShowCurrentTrack)] = ("Show current track hotkey", "show-current-track-hotkey"),
            [nameof(Settings.Hotkeys.Media.PlayPause)] = ("Play/pause hotkey", "play-pause-hotkey"),
            [nameof(Settings.Hotkeys.Media.NextTrack)] = ("Next track hotkey", "next-track-hotkey"),
            [nameof(Settings.Hotkeys.Media.PreviousTrack)] = ("Previous track hotkey", "previous-track-hotkey"),
            [nameof(Settings.Hotkeys.Media.SeekForward)] = ("Seek forward hotkey", "seek-forward-hotkey"),
            [nameof(Settings.Hotkeys.Media.SeekBackward)] = ("Seek backward hotkey", "seek-backward-hotkey"),
            [nameof(Settings.Hotkeys.Mute.Mic)] = ("Mute mic hotkey", "mute-mic-hotkey"),
            [nameof(Settings.Hotkeys.Mute.Sound)] = ("Mute sound hotkey", "mute-sound-hotkey"),
            [nameof(Settings.Hotkeys.Mute.Deafen)] = ("Deafen hotkey", "deafen-hotkey"),
            [nameof(Settings.Hotkeys.Listen.ListenToInput)] = ("Listen to input hotkey", "listen-to-input-hotkey"),
            [nameof(Settings.Hotkeys.Volume.MasterUp)] = ("Master volume up hotkey", "master-volume-up-hotkey"),
            [nameof(Settings.Hotkeys.Volume.MasterDown)] = ("Master volume down hotkey", "master-volume-down-hotkey"),
            [nameof(Settings.Hotkeys.Volume.MicUp)] = ("Microphone volume up hotkey", "mic-volume-up-hotkey"),
            [nameof(Settings.Hotkeys.Volume.MicDown)] = ("Microphone volume down hotkey", "mic-volume-down-hotkey"),
            [nameof(Settings.Hotkeys.Mute.HoldToMute)] = ("Hold-to-mute hotkey", "hold-to-mute-hotkey"),
            [nameof(Settings.Hotkeys.Mute.PushToTalk)] = ("Push-to-talk hotkey", "push-to-talk-hotkey"),
            [nameof(Settings.Hotkeys.Volume.ForegroundMute)] = ("Foreground app mute hotkey", "foreground-mute-hotkey"),
            [nameof(Settings.Hotkeys.Volume.ForegroundDown)] = ("Foreground app volume down hotkey", "foreground-volume-down-hotkey"),
            [nameof(Settings.Hotkeys.Volume.ForegroundUp)] = ("Foreground app volume up hotkey", "foreground-volume-up-hotkey"),
            [OutputSwitchHotkeyFieldKey] = ("Output switch hotkey", "output-switch-hotkey"),
            [OutputReverseSwitchHotkeyFieldKey] = ("Output reverse switch hotkey", "output-reverse-switch-hotkey"),
            [InputSwitchHotkeyFieldKey] = ("Input switch hotkey", "input-switch-hotkey"),
            [InputReverseSwitchHotkeyFieldKey] = ("Input reverse switch hotkey", "input-reverse-switch-hotkey"),
        };

        public static void EnsureRequiredStructure(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            settings.DeviceSwitching ??= new DeviceSwitchingSettings();
            settings.DeviceSwitching.Output ??= new DeviceSwitchingOutputSettings();
            settings.DeviceSwitching.Input ??= new DeviceSwitchingInputSettings();
            settings.DeviceSwitching.Output.CycleDevices ??= [];
            settings.DeviceSwitching.Input.CycleDevices ??= [];
            settings.Hotkeys ??= new HotkeysSettings();
            settings.Hotkeys.App ??= new HotkeysAppSettings();
            settings.Hotkeys.Media ??= new HotkeysMediaSettings();
            settings.Hotkeys.Mute ??= new HotkeysMuteSettings();
            settings.Hotkeys.Listen ??= new HotkeysListenSettings();
            settings.Hotkeys.Volume ??= new HotkeysVolumeSettings();
            settings.Routines ??= new RoutinesSettings();
            settings.Routines.Items ??= [];
            settings.Overlay ??= new OverlaySettings();
            settings.Miscellaneous ??= new MiscellaneousSettings();
            settings.AdvancedTuning ??= new AdvancedTuningSettings();
        }

        public static void Normalize(Settings settings)
        {
            EnsureRequiredStructure(settings);

            settings.Overlay.DurationSeconds = double.IsFinite(settings.Overlay.DurationSeconds)
                ? Math.Clamp(settings.Overlay.DurationSeconds, 0.5, 10.0)
                : AppConstants.Timing.OverlayAutoHideSeconds;

            settings.DeviceSwitching.Output.SwitchRoles = NormalizeRoleList(
                settings.DeviceSwitching.Output.SwitchRoles,
                ["Multimedia", "Communications", "Console"]);

            settings.DeviceSwitching.Input.SwitchRoles = NormalizeRoleList(
                settings.DeviceSwitching.Input.SwitchRoles,
                ["Multimedia", "Communications", "Console"]);

            settings.Routines.Items = NormalizeRoutines(settings.Routines.Items);
            settings.Hotkeys.Volume.MasterVolumeStepPercent = NormalizeVolumeStepPercent(settings.Hotkeys.Volume.MasterVolumeStepPercent);
            settings.Hotkeys.Volume.MicVolumeStepPercent = NormalizeVolumeStepPercent(settings.Hotkeys.Volume.MicVolumeStepPercent);
            settings.Hotkeys.Volume.ForegroundVolumeStepPercent = NormalizeVolumeStepPercent(settings.Hotkeys.Volume.ForegroundVolumeStepPercent);
            settings.Hotkeys.Media.SeekStepSeconds = MediaSeekStep.Normalize(settings.Hotkeys.Media.SeekStepSeconds);
            NormalizeAdvancedTuning(settings);
        }

        public static SettingsDiagnosticsResult EvaluateDiagnostics(
            Settings settings,
            IEnumerable<CycleDevice>? activeOutputDevices,
            IEnumerable<CycleDevice>? activeInputDevices)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var warnings = new List<SettingsDiagnostic>();

            AddInvalidHotkeyWarnings(settings, warnings);
            AddInvalidRoutineWarnings(settings, warnings);

            var activeOutput = NormalizeConfiguredCycle(activeOutputDevices);
            var activeInput = NormalizeConfiguredCycle(activeInputDevices);

            CycleValidationResult outputCycleValidation = ValidateCycle(settings.DeviceSwitching.Output.CycleDevices, activeOutput);
            if (outputCycleValidation.DisconnectedDeviceNames.Count > 0)
            {
                int disconnectedCount = outputCycleValidation.DisconnectedDeviceNames.Count;
                warnings.Add(new SettingsDiagnostic(
                    Code: "output-cycle-disconnected-devices",
                    Message: BuildDisconnectedCycleMessage("Output", outputCycleValidation.DisconnectedDeviceNames),
                    SuggestedAction: BuildDisconnectedCycleSuggestedAction("output", disconnectedCount)));
            }

            CycleValidationResult inputCycleValidation = ValidateCycle(settings.DeviceSwitching.Input.CycleDevices, activeInput);
            if (inputCycleValidation.DisconnectedDeviceNames.Count > 0)
            {
                int disconnectedCount = inputCycleValidation.DisconnectedDeviceNames.Count;
                warnings.Add(new SettingsDiagnostic(
                    Code: "input-cycle-disconnected-devices",
                    Message: BuildDisconnectedCycleMessage("Input", inputCycleValidation.DisconnectedDeviceNames),
                    SuggestedAction: BuildDisconnectedCycleSuggestedAction("input", disconnectedCount)));
            }

            return new SettingsDiagnosticsResult(warnings);
        }

        private static string BuildDisconnectedCycleMessage(string deviceKind, IReadOnlyList<string> disconnectedNames)
        {
            string label = disconnectedNames.Count == 1 ? "device" : "devices";
            return $"{deviceKind} cycle includes disconnected {label}: {string.Join(", ", disconnectedNames)}.";
        }

        private static string BuildDisconnectedCycleSuggestedAction(string deviceKind, int disconnectedCount)
        {
            if (disconnectedCount == 1)
            {
                return $"Reconnect that {deviceKind} device to switch to it, or remove it from the {deviceKind} cycle.";
            }

            return $"Reconnect those {deviceKind} devices to switch to them, or remove them from the {deviceKind} cycle.";
        }

        public static CycleValidationResult ValidateCycle(
            IEnumerable<CycleDevice>? configuredCycle,
            IEnumerable<CycleDevice>? activeDevices)
        {
            var configured = NormalizeConfiguredCycle(configuredCycle);
            var normalizedActive = NormalizeConfiguredCycle(activeDevices);
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < normalizedActive.Count; index++)
            {
                activeIds.Add(normalizedActive[index].Id);
            }

            var countsById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstNameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < configured.Count; index++)
            {
                CycleDevice device = configured[index];

                if (!countsById.TryGetValue(device.Id, out int count))
                {
                    countsById[device.Id] = 1;
                    firstNameById[device.Id] = device.Name;
                }
                else
                {
                    countsById[device.Id] = count + 1;
                }
            }

            var duplicateNames = new List<string>();
            var seenDuplicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in countsById)
            {
                if (entry.Value <= 1)
                {
                    continue;
                }

                string firstName = firstNameById[entry.Key];
                if (!string.IsNullOrWhiteSpace(firstName) && seenDuplicateNames.Add(firstName))
                {
                    duplicateNames.Add(firstName);
                }
            }

            duplicateNames.Sort(StringComparer.OrdinalIgnoreCase);

            var disconnectedNames = new List<string>();
            var seenDisconnectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < configured.Count; index++)
            {
                CycleDevice device = configured[index];
                if (activeIds.Contains(device.Id) || string.IsNullOrWhiteSpace(device.Name) || !seenDisconnectedNames.Add(device.Name))
                {
                    continue;
                }

                disconnectedNames.Add(device.Name);
            }

            disconnectedNames.Sort(StringComparer.OrdinalIgnoreCase);

            bool isValid = duplicateNames.Count == 0 && disconnectedNames.Count == 0;
            return new CycleValidationResult(isValid, duplicateNames, disconnectedNames);
        }

        public static CycleSwitchPreflightResult EvaluateCycleSwitchPreflight(
            IEnumerable<CycleDevice>? configuredCycle,
            IEnumerable<CycleDevice>? activeDevices,
            bool hasDefaultInputDevice,
            bool output)
        {
            var configured = NormalizeConfiguredCycle(configuredCycle);
            var normalizedActive = NormalizeConfiguredCycle(activeDevices);
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < normalizedActive.Count; index++)
            {
                activeIds.Add(normalizedActive[index].Id);
            }

            int configuredCount = configured.Count;
            var connectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < configured.Count; index++)
            {
                CycleDevice device = configured[index];
                if (activeIds.Contains(device.Id))
                {
                    connectedIds.Add(device.Id);
                }
            }

            int connectedCount = connectedIds.Count;

            bool effectiveHasDefaultInput = output || hasDefaultInputDevice;

            var reasons = new List<string>();
            if (configuredCount == 0)
            {
                reasons.Add("no-configured-devices");
            }

            if (configuredCount > 0 && connectedCount == 0)
            {
                reasons.Add("no-connected-configured-devices");
            }

            if (connectedCount == 1)
            {
                reasons.Add("no-alternate-connected-device");
            }

            if (!output && !effectiveHasDefaultInput)
            {
                reasons.Add(AppConstants.Audio.ErrorCodes.CyclePreflight.NoDefaultInputDevice);
            }

            return new CycleSwitchPreflightResult(
                reasons.Count == 0,
                configuredCount,
                connectedCount,
                effectiveHasDefaultInput,
                reasons);
        }

        private static List<CycleDevice> NormalizeConfiguredCycle(IEnumerable<CycleDevice>? devices)
        {
            if (devices == null)
            {
                return [];
            }

            var normalized = new List<CycleDevice>();
            foreach (var device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id))
                {
                    continue;
                }

                normalized.Add(new CycleDevice
                {
                    Id = device.Id,
                    Name = device.Name,
                    StableId = device.StableId
                });
            }

            return normalized;
        }

        private static List<string> NormalizeRoleList(IEnumerable<string>? roles, IReadOnlyList<string> fallback)
        {
            if (roles == null)
            {
                return [.. fallback];
            }

            var normalized = new List<string>();
            foreach (var role in roles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                string? canonical = role.Trim().ToLowerInvariant() switch
                {
                    "console" => "Console",
                    "multimedia" => "Multimedia",
                    "communications" => "Communications",
                    _ => null,
                };

                if (canonical != null && !normalized.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(canonical);
                }
            }

            return normalized.Count > 0 ? normalized : [.. fallback];
        }

        private static void AddInvalidHotkeyWarnings(Settings settings, List<SettingsDiagnostic> warnings)
        {
            if (settings.Hotkeys.Mute.PushToTalkEnabled && string.IsNullOrWhiteSpace(settings.Hotkeys.Mute.PushToTalk))
                warnings.Add(new SettingsDiagnostic("invalid-hotkey-push-to-talk-hotkey", "Push-to-talk requires a hotkey before it can be enabled.", "Assign a push-to-talk hotkey or disable the mode."));
            var values = new Dictionary<string, string?>
            {
                [nameof(Settings.Hotkeys.App.ToggleAppVisibility)] = settings.Hotkeys.App.ToggleAppVisibility,
                [nameof(Settings.Hotkeys.App.ShowAudioStatus)] = settings.Hotkeys.App.ShowAudioStatus,
                [nameof(Settings.Hotkeys.Media.ShowCurrentTrack)] = settings.Hotkeys.Media.ShowCurrentTrack,
                [nameof(Settings.Hotkeys.Media.PlayPause)] = settings.Hotkeys.Media.PlayPause,
                [nameof(Settings.Hotkeys.Media.NextTrack)] = settings.Hotkeys.Media.NextTrack,
                [nameof(Settings.Hotkeys.Media.PreviousTrack)] = settings.Hotkeys.Media.PreviousTrack,
                [nameof(Settings.Hotkeys.Media.SeekForward)] = settings.Hotkeys.Media.SeekForward,
                [nameof(Settings.Hotkeys.Media.SeekBackward)] = settings.Hotkeys.Media.SeekBackward,
                [nameof(Settings.Hotkeys.Mute.Mic)] = settings.Hotkeys.Mute.Mic,
                [nameof(Settings.Hotkeys.Mute.Sound)] = settings.Hotkeys.Mute.Sound,
                [nameof(Settings.Hotkeys.Mute.Deafen)] = settings.Hotkeys.Mute.Deafen,
                [nameof(Settings.Hotkeys.Listen.ListenToInput)] = settings.Hotkeys.Listen.ListenToInput,
                [nameof(Settings.Hotkeys.Volume.MasterUp)] = settings.Hotkeys.Volume.MasterUp,
                [nameof(Settings.Hotkeys.Volume.MasterDown)] = settings.Hotkeys.Volume.MasterDown,
                [nameof(Settings.Hotkeys.Volume.MicUp)] = settings.Hotkeys.Volume.MicUp,
                [nameof(Settings.Hotkeys.Volume.MicDown)] = settings.Hotkeys.Volume.MicDown,
                [nameof(Settings.Hotkeys.Mute.HoldToMute)] = settings.Hotkeys.Mute.HoldToMute,
                [nameof(Settings.Hotkeys.Mute.PushToTalk)] = settings.Hotkeys.Mute.PushToTalk,
                [nameof(Settings.Hotkeys.Volume.ForegroundMute)] = settings.Hotkeys.Volume.ForegroundMute,
                [nameof(Settings.Hotkeys.Volume.ForegroundDown)] = settings.Hotkeys.Volume.ForegroundDown,
                [nameof(Settings.Hotkeys.Volume.ForegroundUp)] = settings.Hotkeys.Volume.ForegroundUp,
                [OutputSwitchHotkeyFieldKey] = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.SwitchHotkey : string.Empty,
                [OutputReverseSwitchHotkeyFieldKey] = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.ReverseSwitchHotkey : string.Empty,
                [InputSwitchHotkeyFieldKey] = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.SwitchHotkey : string.Empty,
                [InputReverseSwitchHotkeyFieldKey] = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.ReverseSwitchHotkey : string.Empty,
            };

            foreach (var entry in values)
            {
                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                HotkeyValidationResult validation = ValidateHotkey(entry.Value);
                if (validation.IsValid)
                {
                    if (entry.Key is nameof(HotkeysMuteSettings.PushToTalk) or nameof(HotkeysMuteSettings.HoldToMute))
                    {
                        var parsed = new HotkeyParsingService().ParseHotkeyString(entry.Value);
                        if (parsed.HasValue && !MicrophoneHoldService.IsHoldable(parsed.Value.mainInput))
                        {
                            var (DisplayName, CliKey) = HotkeyFields[entry.Key];
                            warnings.Add(new SettingsDiagnostic($"invalid-hotkey-{CliKey}", $"{DisplayName} requires a key or button that can be held.", "Choose a keyboard key or mouse button other than Pause or Print Screen; wheel input cannot be held."));
                        }
                    }
                    continue;
                }

                (string displayName, string cliKey) = HotkeyFields[entry.Key];
                if (validation.IsReserved)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"reserved-hotkey-{cliKey}",
                        Message: $"{displayName} value '{entry.Value}' uses reserved Windows shortcut '{validation.ReservedShortcutName}'.",
                        SuggestedAction: "Choose a different hotkey that is not reserved by Windows or the shell."));
                    continue;
                }

                warnings.Add(new SettingsDiagnostic(
                    Code: $"invalid-hotkey-{cliKey}",
                    Message: $"{displayName} value '{entry.Value}' is invalid.",
                    SuggestedAction: $"Set a valid combination (example: Ctrl+Alt+H) or clear it with config set {cliKey} \"\"."));
            }

            AddInvalidVolumeStepWarnings(settings, warnings);
            if (settings.Hotkeys.Media.SeekStepSeconds is < 1 or > MediaSeekStep.MaximumSeconds)
            {
                warnings.Add(new SettingsDiagnostic("invalid-media-seek-step-seconds", "Media seek step is invalid.", "Set media-seek-step-seconds to a whole number between 1 and 3600."));
            }
        }

        private static void AddInvalidVolumeStepWarnings(Settings settings, List<SettingsDiagnostic> warnings)
        {
            AddInvalidVolumeStepWarning(
                settings.Hotkeys.Volume.MasterVolumeStepPercent,
                "master-volume-step-percent",
                "Master volume step",
                warnings);

            AddInvalidVolumeStepWarning(
                settings.Hotkeys.Volume.MicVolumeStepPercent,
                "mic-volume-step-percent",
                "Microphone volume step",
                warnings);
            AddInvalidVolumeStepWarning(settings.Hotkeys.Volume.ForegroundVolumeStepPercent, "foreground-volume-step-percent", "Foreground app volume step", warnings);
        }

        private static void AddInvalidVolumeStepWarning(int value, string cliKey, string displayName, List<SettingsDiagnostic> warnings)
        {
            if (value is >= 1 and <= 100)
            {
                return;
            }

            warnings.Add(new SettingsDiagnostic(
                Code: $"invalid-{cliKey}",
                Message: $"{displayName} value '{value}' is invalid.",
                SuggestedAction: $"Set {cliKey} to a whole number between 1 and 100."));
        }

        private static int NormalizeVolumeStepPercent(int value)
        {
            return value switch
            {
                < 1 => 5,
                > 100 => 100,
                _ => value,
            };
        }

        private static void NormalizeAdvancedTuning(Settings settings)
        {
            settings.AdvancedTuning ??= new AdvancedTuningSettings();
            settings.AdvancedTuning.BluetoothReconnect ??= new BluetoothReconnectAdvancedTuningSettings();
            settings.AdvancedTuning.SteamBigPicture ??= new SteamBigPictureAdvancedTuningSettings();

            BluetoothReconnectAdvancedTuningSettings bluetoothReconnect = settings.AdvancedTuning.BluetoothReconnect;
            bluetoothReconnect.MaxAttempts = Math.Clamp(
                bluetoothReconnect.MaxAttempts,
                AppConstants.Limits.BluetoothReconnectMinAttempts,
                AppConstants.Limits.BluetoothReconnectMaxAttempts);
            bluetoothReconnect.AttemptTimeoutMs = Math.Clamp(
                bluetoothReconnect.AttemptTimeoutMs,
                AppConstants.Limits.BluetoothReconnectMinAttemptTimeoutMs,
                AppConstants.Limits.BluetoothReconnectMaxAttemptTimeoutMs);
            bluetoothReconnect.CooldownMs = Math.Clamp(
                bluetoothReconnect.CooldownMs,
                AppConstants.Limits.BluetoothReconnectMinCooldownMs,
                AppConstants.Limits.BluetoothReconnectMaxCooldownMs);
            bluetoothReconnect.CachedEndpointVisibilityProbeAttempts = Math.Clamp(
                bluetoothReconnect.CachedEndpointVisibilityProbeAttempts,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinAttempts,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxAttempts);
            bluetoothReconnect.CachedEndpointVisibilityProbeDelayMs = Math.Clamp(
                bluetoothReconnect.CachedEndpointVisibilityProbeDelayMs,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinDelayMs,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxDelayMs);

            SteamBigPictureAdvancedTuningSettings steamBigPicture = settings.AdvancedTuning.SteamBigPicture;
            steamBigPicture.MonitorDebounceMs = Math.Clamp(
                steamBigPicture.MonitorDebounceMs,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMinMs,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMaxMs);
            steamBigPicture.ConfirmationDelayMs = Math.Clamp(
                steamBigPicture.ConfirmationDelayMs,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMinMs,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMaxMs);
        }

        private static void AddInvalidRoutineWarnings(Settings settings, List<SettingsDiagnostic> warnings)
        {
            for (int index = 0; index < settings.Routines.Items.Count; index++)
            {
                AudioRoutine routine = settings.Routines.Items[index];
                if (AudioPilot.Services.Routines.RoutineHotkeyGroups.Validate(routine, settings.Routines.Items) is { } groupError)
                    warnings.Add(new SettingsDiagnostic($"invalid-routine-cycle-group-{index}", groupError, "Choose one hotkey for all members of the group, or leave the group blank."));
                string? triggerError = routine.ValidateTriggers()
                    ?? routine.Conditions.Validate();
                if (triggerError != null) warnings.Add(new SettingsDiagnostic($"invalid-routine-triggers-{index}", triggerError, "Edit the routine automatic triggers."));
                string routineLabel = string.IsNullOrWhiteSpace(routine.Name)
                    ? $"Routine #{index + 1}"
                    : $"Routine '{routine.Name}'";

                if (string.IsNullOrWhiteSpace(routine.Name))
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-name-{index}",
                        Message: $"{routineLabel} must have a name.",
                        SuggestedAction: "Give the routine a name before saving routines."));
                }

                if (routine.Name?.Length > AudioRoutine.MaxNameLength)
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-name-{index}",
                        Message: $"Routine name must be {AudioRoutine.MaxNameLength} characters or fewer.",
                        SuggestedAction: "Shorten the routine name before saving routines."));

                if (routine.ValidateCommunicationsTargets() is { } communicationsError)
                    warnings.Add(new SettingsDiagnostic(Code: $"invalid-routine-communications-{index}", Message: communicationsError, SuggestedAction: "Select a device of the matching kind or leave the target unchanged."));
                bool hasOutputTarget = !string.IsNullOrWhiteSpace(routine.OutputDeviceId);
                bool hasInputTarget = !string.IsNullOrWhiteSpace(routine.InputDeviceId);
                if (!Enum.IsDefined(routine.OutputMuteAction) || !Enum.IsDefined(routine.InputMuteAction))
                    warnings.Add(new SettingsDiagnostic(Code: $"invalid-routine-mute-{index}",
                        Message: $"{routineLabel} contains an unknown mute action.",
                        SuggestedAction: "Choose Unchanged, Mute, or Unmute for each endpoint."));
                bool hasMasterVolumeTarget = routine.MasterVolumePercent.HasValue;
                bool hasMicVolumeTarget = routine.MicVolumePercent.HasValue;

                if (!hasOutputTarget && !hasInputTarget && !hasMasterVolumeTarget && !hasMicVolumeTarget && !routine.HasMuteTarget && !routine.HasCommunicationsTarget)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-target-{index}",
                    Message: $"{routineLabel} must target at least one output device, input device, volume target, or mute action.",
                    SuggestedAction: "Choose an output device, input device, endpoint volume target, or mute action before saving routines."));
                }

                if (routine.RestorePreviousAudioOnDeactivate && !routine.HasStatefulTriggers)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-stateful-options-{index}",
                    Message: $"{routineLabel} can only restore on exit for stateful triggers.",
                    SuggestedAction: "Use an Application or Steam Big Picture trigger, or turn off restore on exit."));
                }

                if (routine.RestorePreviousAudioOnDeactivate && !hasOutputTarget && !hasInputTarget && !hasMasterVolumeTarget && !hasMicVolumeTarget && !routine.HasMuteTarget && !routine.HasCommunicationsTarget)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-stateful-restore-target-{index}",
                        Message: $"{routineLabel} must change at least one device, volume target, or mute action before restore on exit can apply.",
                        SuggestedAction: "Add an output device, input device, endpoint volume target, or mute action, or turn off restore on exit."));
                }

                if (routine.TriggerKind == RoutineTriggerKind.Application && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath))
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-trigger-app-path-{index}",
                    Message: $"{routineLabel} must use a full .exe path or packaged app AUMID for Application triggers.",
                    SuggestedAction: "Choose an executable with Browse, pick a packaged app, or enter a full .exe path or packaged app AUMID."));
                }

                if (routine.TriggerKind == RoutineTriggerKind.Network &&
                    routine.NetworkTriggerDirection != NetworkTriggerDirection.Disconnect &&
                    string.IsNullOrWhiteSpace(routine.TriggerNetworkName))
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-trigger-network-name-{index}",
                        Message: $"{routineLabel} must specify a network name for network triggers.",
                        SuggestedAction: "Enter the exact network name that should trigger the routine."));
                }

                if (routine.SwitchOutputPerApp && (!(hasOutputTarget || hasInputTarget) || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TargetAppPath)))
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-app-audio-only-{index}",
                        Message: $"{routineLabel} requires a target application and at least one output or input device for application routing.",
                        SuggestedAction: "Choose a target executable or packaged app, and an output or input device, or turn off application audio routing."));
                }

                if (routine.Enabled && routine.TriggerKind == RoutineTriggerKind.Hotkey && routine.Triggers.Count == 0 && string.IsNullOrWhiteSpace(routine.Hotkey) && !routine.ShowInTrayMenu)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"invalid-routine-hotkey-missing-{index}",
                    Message: $"{routineLabel} needs a hotkey or tray entry.",
                    SuggestedAction: "Set a routine hotkey, enable its tray entry, or choose an automatic trigger."));
                }

                if (!routine.Enabled || string.IsNullOrWhiteSpace(routine.Hotkey))
                {
                    continue;
                }

                HotkeyValidationResult validation = ValidateHotkey(routine.Hotkey);
                if (validation.IsValid)
                {
                    continue;
                }

                if (validation.IsReserved)
                {
                    warnings.Add(new SettingsDiagnostic(
                        Code: $"reserved-routine-hotkey-{index}",
                        Message: $"{routineLabel} hotkey value '{routine.Hotkey}' uses reserved Windows shortcut '{validation.ReservedShortcutName}'.",
                        SuggestedAction: "Set a different routine hotkey that is not reserved by Windows or the shell."));
                    continue;
                }

                warnings.Add(new SettingsDiagnostic(
                    Code: $"invalid-routine-hotkey-{index}",
                    Message: $"{routineLabel} hotkey value '{routine.Hotkey}' is invalid.",
                    SuggestedAction: "Set a valid hotkey such as Ctrl+Shift+R."));
            }
        }

        private static List<AudioRoutine> NormalizeRoutines(IEnumerable<AudioRoutine>? routines)
        {
            if (routines == null)
            {
                return [];
            }

            var normalized = new List<AudioRoutine>();
            var seenRoutineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int displayOrder = 1;
            foreach (AudioRoutine? routine in routines)
            {
                if (routine == null)
                {
                    continue;
                }

                string id = string.IsNullOrWhiteSpace(routine.Id)
                    ? Guid.NewGuid().ToString("N")
                    : routine.Id.Trim();
                while (!seenRoutineIds.Add(id))
                {
                    id = Guid.NewGuid().ToString("N");
                }

                normalized.Add(new AudioRoutine
                {
                    Id = id,
                    Name = routine.Name?.Trim() ?? string.Empty,
                    Enabled = routine.Enabled,
                    CommunicationsOutput = routine.CommunicationsOutput,
                    CommunicationsInput = routine.CommunicationsInput,
                    OutputDeviceId = routine.OutputDeviceId?.Trim() ?? string.Empty,
                    OutputDeviceStableId = routine.OutputDeviceStableId,
                    OutputDeviceName = routine.OutputDeviceName?.Trim() ?? string.Empty,
                    InputDeviceId = routine.InputDeviceId?.Trim() ?? string.Empty,
                    InputDeviceStableId = routine.InputDeviceStableId,
                    InputDeviceName = routine.InputDeviceName?.Trim() ?? string.Empty,
                    MasterVolumePercent = NormalizeRoutineVolumePercent(routine.MasterVolumePercent),
                    MicVolumePercent = NormalizeRoutineVolumePercent(routine.MicVolumePercent),
                    OutputMuteAction = routine.OutputMuteAction,
                    InputMuteAction = routine.InputMuteAction,
                    Hotkey = routine.Hotkey?.Trim() ?? string.Empty,
                    HotkeyCycleGroup = routine.HotkeyCycleGroup,
                    Triggers = [.. routine.Triggers.Select(static trigger => trigger?.Normalize()!)],
                    Conditions = routine.Conditions,
                    SwitchOutputPerApp = routine.SwitchOutputPerApp,
                    TargetAppPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TargetAppPath),
                    ShowInTrayMenu = routine.ShowInTrayMenu,
                    RestorePreviousAudioOnDeactivate = routine.HasStatefulTriggers && routine.RestorePreviousAudioOnDeactivate,
                    DisplayOrder = displayOrder++,
                });
            }

            return normalized;
        }

        private static int? NormalizeRoutineVolumePercent(int? value)
        {
            return value.HasValue
                ? Math.Clamp(value.Value, 0, 100)
                : null;
        }

        internal static HotkeyValidationResult ValidateHotkey(string? hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                return new HotkeyValidationResult(true, false, string.Empty);
            }

            var parsed = new HotkeyParsingService().ParseHotkeyString(hotkey);
            if (!parsed.HasValue)
            {
                return new HotkeyValidationResult(false, false, string.Empty);
            }

            if (HotkeyReservedShortcutPolicy.IsReserved(parsed.Value.mainInput, parsed.Value.modifiers, out string reservedShortcutName))
            {
                return new HotkeyValidationResult(false, true, reservedShortcutName);
            }

            bool isSupported = parsed.Value.mainInput.IsSupportedModifierCount(parsed.Value.modifiers.Count);
            return new HotkeyValidationResult(isSupported, false, string.Empty);
        }

        private static bool IsModifier(Key key)
        {
            return key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin;
        }
    }
}
