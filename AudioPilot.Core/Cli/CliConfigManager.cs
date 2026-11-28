using System.Globalization;
using AudioPilot.Constants;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static class CliConfigManager
    {
        private static readonly string[] SupportedSwitchRoles = ["Multimedia", "Communications", "Console"];
        private static readonly ConfigEntry[] Entries =
        [
            CreateBoolEntry("push-to-talk-enabled",
                settings => settings.Hotkeys.Mute.PushToTalkEnabled,
                (settings, parsed) => settings.Hotkeys.Mute.PushToTalkEnabled = parsed),
            CreateBoolEntry(
                "check-for-updates",
                settings => settings.Updates.CheckAutomatically,
                (settings, parsed) => settings.Updates.CheckAutomatically = parsed),
            CreateEnumEntry(
                "theme",
                settings => settings.App.Theme,
                (settings, parsed) => settings.App.Theme = parsed,
                "Invalid theme value. Use: System|Light|Dark."),
            CreateCustomEntry(
                "language",
                settings => LocalizationService.NormalizeLanguage(settings.App.Language),
                (settings, value) =>
                {
                    var language = LocalizationService.Languages.FirstOrDefault(option =>
                        option.Code.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (language == null)
                        return ConfigSetResult.FromFailure("Invalid language. Use: " + string.Join("|", LocalizationService.Languages.Select(option => option.Code)) + ".");
                    settings.App.Language = language.Code;
                    return ConfigSetResult.FromSuccess();
                },
                CliKeyMetadataFactory.Create("language", "string", string.Join("|", LocalizationService.Languages.Select(option => option.Code)),
                    "persisted", defaultValue: "System")),
            CreateBoolEntry(
                "auto-save-enabled",
                settings => settings.App.AutoSaveEnabled,
                (settings, parsed) => settings.App.AutoSaveEnabled = parsed),
            CreateBoolEntry(
                "play-app-sounds",
                settings => settings.App.PlayAppSounds,
                (settings, parsed) => settings.App.PlayAppSounds = parsed),
            CreateBoolEntry(
                "use-scheduled-startup",
                settings => settings.Startup.UseTaskScheduler,
                (settings, parsed) => settings.Startup.UseTaskScheduler = parsed),
            CreateBoolEntry(
                "run-at-startup",
                settings => settings.Startup.Enabled,
                (settings, parsed) => settings.Startup.Enabled = parsed),
            CreateEnumEntry(
                "start-in-tray",
                settings => settings.Startup.StartInTray,
                (settings, parsed) => settings.Startup.StartInTray = parsed,
                "Invalid startup tray behavior. Use: WindowsStartupOnly|Always|Never."),
            CreateBoolEntry(
                "preserve-audio-levels",
                settings => settings.DeviceSwitching.PreserveAudioLevels,
                (settings, parsed) => settings.DeviceSwitching.PreserveAudioLevels = parsed),
            CreateBoolEntry(
                "keep-mute-state-when-switching",
                settings => settings.DeviceSwitching.KeepMuteStateWhenSwitching,
                (settings, parsed) => settings.DeviceSwitching.KeepMuteStateWhenSwitching = parsed),
            CreateBoolEntry(
                "picker-show-all-available-devices",
                settings => settings.DeviceSwitching.PickerShowAllAvailableDevices,
                (settings, parsed) => settings.DeviceSwitching.PickerShowAllAvailableDevices = parsed),
            CreateBoolEntry(
                "auto-scroll-to-mixer-on-restore",
                settings => settings.App.AutoScrollToMixerOnRestore,
                (settings, parsed) => settings.App.AutoScrollToMixerOnRestore = parsed),
            CreateEnumEntry(
                "close-button-behavior",
                settings => settings.App.CloseButtonBehavior,
                (settings, parsed) => settings.App.CloseButtonBehavior = parsed,
                "Invalid close button behavior. Use: Exit|HideToTray."),
            CreateEnumEntry(
                "minimize-button-behavior",
                settings => settings.App.MinimizeButtonBehavior,
                (settings, parsed) => settings.App.MinimizeButtonBehavior = parsed,
                "Invalid minimize button behavior. Use: HideToTray|MinimizeToTaskbar."),
            CreateEnumEntry(
                "tray-left-click-action",
                settings => settings.App.TrayLeftClickAction,
                (settings, parsed) => settings.App.TrayLeftClickAction = parsed,
                "Invalid tray left-click action. Use: None|QuickMixer|QuickDevicePicker|ToggleWindow|SwitchOutput."),
            CreateBoolEntry(
                "suppress-device-startup-warnings",
                settings => settings.Startup.SuppressDeviceWarnings,
                (settings, parsed) => settings.Startup.SuppressDeviceWarnings = parsed),
            CreateEnumStringEntry<Logging.LogLevel>(
                "log-level",
                settings => settings.Diagnostics.LogLevel,
                (settings, parsed) => settings.Diagnostics.LogLevel = parsed.ToString(),
                "Invalid log level. Use: Trace|Debug|Info|Warning|Error|Fatal|None."),
            CreateBoolEntry(
                "redact-log-content",
                settings => settings.Diagnostics.RedactLogContent,
                (settings, parsed) => settings.Diagnostics.RedactLogContent = parsed),
            CreateEnumEntry(
                "overlay-position",
                settings => settings.Overlay.Position,
                (settings, parsed) => settings.Overlay.Position = parsed,
                "Invalid overlay position. Use: TopLeft|TopCenter|TopRight|BottomLeft|BottomCenter|BottomRight|Center."),
            CreateEnumEntry(
                "overlay-monitor",
                settings => settings.Overlay.MonitorMode,
                (settings, parsed) => settings.Overlay.MonitorMode = parsed,
                "Invalid overlay monitor. Use: Cursor|ActiveWindow|Primary."),
            CreateBoolEntry(
                "overlay-enabled",
                settings => settings.Overlay.Enabled,
                (settings, parsed) => settings.Overlay.Enabled = parsed),
            CreateBoolEntry(
                "overlay-hotplug-enabled",
                settings => settings.Overlay.ShowDeviceHotplugOverlays,
                (settings, parsed) => settings.Overlay.ShowDeviceHotplugOverlays = parsed),
            CreateDoubleRangeEntry(
                "overlay-duration-seconds",
                settings => settings.Overlay.DurationSeconds,
                (settings, parsed) => settings.Overlay.DurationSeconds = parsed,
                0.5,
                10.0,
                "Invalid overlay duration. Use a number between 0.5 and 10.",
                "Invalid overlay duration range. Use a value between 0.5 and 10."),
            CreateValidatedHotkeyEntry(
                "output-switch-hotkey",
                settings => settings.DeviceSwitching.Output.SwitchHotkey,
                (settings, parsed) => settings.DeviceSwitching.Output.SwitchHotkey = parsed,
                "output switch hotkey"),
            CreateValidatedHotkeyEntry(
                "output-reverse-switch-hotkey",
                settings => settings.DeviceSwitching.Output.ReverseSwitchHotkey,
                (settings, parsed) => settings.DeviceSwitching.Output.ReverseSwitchHotkey = parsed,
                "output reverse switch hotkey"),
            CreateBoolEntry(
                "output-hotkeys-enabled",
                settings => settings.DeviceSwitching.Output.HotkeysEnabled,
                (settings, parsed) => settings.DeviceSwitching.Output.HotkeysEnabled = parsed),
            CreateCustomEntry(
                "output-switch-roles",
                settings => string.Join(",", NormalizeRoles(settings.DeviceSwitching.Output.SwitchRoles)),
                (settings, rawValue) =>
                {
                    if (!TryParseSwitchRoles(rawValue, out List<string> roles, out string? error))
                    {
                        return ConfigSetResult.FromFailure(error);
                    }

                    settings.DeviceSwitching.Output.SwitchRoles = roles;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateValidatedHotkeyEntry(
                "input-switch-hotkey",
                settings => settings.DeviceSwitching.Input.SwitchHotkey,
                (settings, parsed) => settings.DeviceSwitching.Input.SwitchHotkey = parsed,
                "input switch hotkey"),
            CreateValidatedHotkeyEntry(
                "input-reverse-switch-hotkey",
                settings => settings.DeviceSwitching.Input.ReverseSwitchHotkey,
                (settings, parsed) => settings.DeviceSwitching.Input.ReverseSwitchHotkey = parsed,
                "input reverse switch hotkey"),
            CreateBoolEntry(
                "input-hotkeys-enabled",
                settings => settings.DeviceSwitching.Input.HotkeysEnabled,
                (settings, parsed) => settings.DeviceSwitching.Input.HotkeysEnabled = parsed),
            CreateCustomEntry(
                "input-switch-roles",
                settings => string.Join(",", NormalizeRoles(settings.DeviceSwitching.Input.SwitchRoles)),
                (settings, rawValue) =>
                {
                    if (!TryParseSwitchRoles(rawValue, out List<string> roles, out string? error))
                    {
                        return ConfigSetResult.FromFailure(error);
                    }

                    settings.DeviceSwitching.Input.SwitchRoles = roles;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateValidatedHotkeyEntry(
                "toggle-app-visibility-hotkey",
                settings => settings.Hotkeys.App.ToggleAppVisibility,
                (settings, parsed) => settings.Hotkeys.App.ToggleAppVisibility = parsed,
                "show/hide app hotkey"),
            CreateValidatedHotkeyEntry(
                "show-audio-status-hotkey",
                settings => settings.Hotkeys.App.ShowAudioStatus,
                (settings, parsed) => settings.Hotkeys.App.ShowAudioStatus = parsed,
                "show audio status hotkey"),
            CreateValidatedHotkeyEntry(
                "quick-device-picker-hotkey",
                settings => settings.Hotkeys.App.QuickDevicePicker,
                (settings, parsed) => settings.Hotkeys.App.QuickDevicePicker = parsed,
                "quick device picker hotkey"),
            CreateValidatedHotkeyEntry(
                "volume-mixer-hotkey",
                settings => settings.Hotkeys.App.VolumeMixer,
                (settings, parsed) => settings.Hotkeys.App.VolumeMixer = parsed,
                "volume mixer hotkey"),
            CreateValidatedHotkeyEntry(
                "show-current-track-hotkey",
                settings => settings.Hotkeys.Media.ShowCurrentTrack,
                (settings, parsed) => settings.Hotkeys.Media.ShowCurrentTrack = parsed,
                "show current track hotkey"),
            CreateValidatedHotkeyEntry(
                "play-pause-hotkey",
                settings => settings.Hotkeys.Media.PlayPause,
                (settings, parsed) => settings.Hotkeys.Media.PlayPause = parsed,
                "play/pause hotkey"),
            CreateValidatedHotkeyEntry(
                "next-track-hotkey",
                settings => settings.Hotkeys.Media.NextTrack,
                (settings, parsed) => settings.Hotkeys.Media.NextTrack = parsed,
                "next track hotkey"),
            CreateValidatedHotkeyEntry(
                "previous-track-hotkey",
                settings => settings.Hotkeys.Media.PreviousTrack,
                (settings, parsed) => settings.Hotkeys.Media.PreviousTrack = parsed,
                "previous track hotkey"),
            CreateValidatedHotkeyEntry(
                "seek-forward-hotkey",
                settings => settings.Hotkeys.Media.SeekForward,
                (settings, parsed) => settings.Hotkeys.Media.SeekForward = parsed,
                "seek forward hotkey"),
            CreateValidatedHotkeyEntry(
                "seek-backward-hotkey",
                settings => settings.Hotkeys.Media.SeekBackward,
                (settings, parsed) => settings.Hotkeys.Media.SeekBackward = parsed,
                "seek backward hotkey"),
            CreateCustomEntry(
                "media-seek-step-seconds",
                settings => settings.Hotkeys.Media.SeekStepSeconds.ToString("0.#", CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!MediaSeekStep.TryParse(rawValue, out decimal parsed))
                    {
                        return ConfigSetResult.FromFailure(MediaSeekStep.InputHelp);
                    }

                    settings.Hotkeys.Media.SeekStepSeconds = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateValidatedHotkeyEntry(
                "mute-mic-hotkey",
                settings => settings.Hotkeys.Mute.Mic,
                (settings, parsed) => settings.Hotkeys.Mute.Mic = parsed,
                "mute mic hotkey"),
            CreateValidatedHotkeyEntry(
                "mute-sound-hotkey",
                settings => settings.Hotkeys.Mute.Sound,
                (settings, parsed) => settings.Hotkeys.Mute.Sound = parsed,
                "mute sound hotkey"),
            CreateValidatedHotkeyEntry(
                "deafen-hotkey",
                settings => settings.Hotkeys.Mute.Deafen,
                (settings, parsed) => settings.Hotkeys.Mute.Deafen = parsed,
                "deafen hotkey"),
            CreateValidatedHotkeyEntry(
                "listen-to-input-hotkey",
                settings => settings.Hotkeys.Listen.ListenToInput,
                (settings, parsed) => settings.Hotkeys.Listen.ListenToInput = parsed,
                "listen to input hotkey"),
            CreateValidatedHotkeyEntry(
                "master-volume-up-hotkey",
                settings => settings.Hotkeys.Volume.MasterUp,
                (settings, parsed) => settings.Hotkeys.Volume.MasterUp = parsed,
                "master volume up hotkey"),
            CreateValidatedHotkeyEntry(
                "master-volume-down-hotkey",
                settings => settings.Hotkeys.Volume.MasterDown,
                (settings, parsed) => settings.Hotkeys.Volume.MasterDown = parsed,
                "master volume down hotkey"),
            CreateValidatedHotkeyEntry(
                "mic-volume-up-hotkey",
                settings => settings.Hotkeys.Volume.MicUp,
                (settings, parsed) => settings.Hotkeys.Volume.MicUp = parsed,
                "microphone volume up hotkey"),
            CreateValidatedHotkeyEntry(
                "foreground-volume-up-hotkey",
                settings => settings.Hotkeys.Volume.ForegroundUp,
                (settings, parsed) => settings.Hotkeys.Volume.ForegroundUp = parsed,
                "foreground app volume up hotkey"),
            CreateValidatedHotkeyEntry(
                "foreground-volume-down-hotkey",
                settings => settings.Hotkeys.Volume.ForegroundDown,
                (settings, parsed) => settings.Hotkeys.Volume.ForegroundDown = parsed,
                "foreground app volume down hotkey"),
            CreateValidatedHotkeyEntry(
                "foreground-mute-hotkey",
                settings => settings.Hotkeys.Volume.ForegroundMute,
                (settings, parsed) => settings.Hotkeys.Volume.ForegroundMute = parsed,
                "foreground app mute hotkey"),
            CreateValidatedHotkeyEntry(
                "push-to-talk-hotkey",
                settings => settings.Hotkeys.Mute.PushToTalk,
                (settings, parsed) => settings.Hotkeys.Mute.PushToTalk = parsed,
                "push-to-talk hotkey"),
            CreateValidatedHotkeyEntry(
                "hold-to-mute-hotkey",
                settings => settings.Hotkeys.Mute.HoldToMute,
                (settings, parsed) => settings.Hotkeys.Mute.HoldToMute = parsed,
                "hold-to-mute hotkey"),
            CreateValidatedHotkeyEntry(
                "mic-volume-down-hotkey",
                settings => settings.Hotkeys.Volume.MicDown,
                (settings, parsed) => settings.Hotkeys.Volume.MicDown = parsed,
                "microphone volume down hotkey"),
            CreateCustomEntry(
                "master-volume-step-percent",
                settings => settings.Hotkeys.Volume.MasterVolumeStepPercent.ToString("0.#", CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!VolumePercent.TryParseStep(rawValue, out decimal parsed))
                    {
                        return ConfigSetResult.FromFailure(VolumePercent.StepGuidance);
                    }

                    settings.Hotkeys.Volume.MasterVolumeStepPercent = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "mic-volume-step-percent",
                settings => settings.Hotkeys.Volume.MicVolumeStepPercent.ToString("0.#", CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!VolumePercent.TryParseStep(rawValue, out decimal parsed))
                    {
                        return ConfigSetResult.FromFailure(VolumePercent.StepGuidance);
                    }

                    settings.Hotkeys.Volume.MicVolumeStepPercent = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "foreground-volume-step-percent",
                settings => settings.Hotkeys.Volume.ForegroundVolumeStepPercent.ToString("0.#", CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!VolumePercent.TryParseStep(rawValue, out decimal parsed))
                    {
                        return ConfigSetResult.FromFailure(VolumePercent.StepGuidance);
                    }

                    settings.Hotkeys.Volume.ForegroundVolumeStepPercent = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateTrimmedStringEntry(
                "listen-monitor-output-device-id",
                settings => settings.Hotkeys.Listen.MonitorOutputDeviceId,
                (settings, parsed) =>
                {
                    if (!string.Equals(settings.Hotkeys.Listen.MonitorOutputDeviceId, parsed, StringComparison.OrdinalIgnoreCase))
                        settings.Hotkeys.Listen.MonitorOutputDeviceStableId = null;
                    settings.Hotkeys.Listen.MonitorOutputDeviceId = parsed;
                }),
            CreateCustomEntry(
                "listen-monitor-output-device-stable-id",
                settings => settings.Hotkeys.Listen.MonitorOutputDeviceStableId ?? string.Empty,
                (settings, rawValue) =>
                {
                    settings.Hotkeys.Listen.MonitorOutputDeviceStableId = rawValue.Length == 0 ? null : rawValue;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateTrimmedStringEntry(
                "listen-monitor-output-device-name",
                settings => settings.Hotkeys.Listen.MonitorOutputDeviceName,
                (settings, parsed) => settings.Hotkeys.Listen.MonitorOutputDeviceName = parsed),
            CreateBoolEntry(
                "bluetooth-reconnect-enabled",
                settings => settings.DeviceSwitching.BluetoothReconnectEnabled,
                (settings, parsed) => settings.DeviceSwitching.BluetoothReconnectEnabled = parsed),
            CreateIntRangeEntry(
                "auto-save-debounce-ms",
                settings => (settings.AdvancedTuning ??= new()).AutoSaveDebounceMs,
                (settings, parsed) => (settings.AdvancedTuning ??= new()).AutoSaveDebounceMs = parsed,
                AppConstants.Limits.AutoSaveDebounceMinMs,
                AppConstants.Limits.AutoSaveDebounceMaxMs,
                "Invalid auto-save debounce. Use a whole number between 100 and 10000."),
            CreateIntRangeEntry(
                "output-switch-debounce-ms",
                settings => (settings.AdvancedTuning ??= new()).OutputSwitchDebounceMs,
                (settings, parsed) => (settings.AdvancedTuning ??= new()).OutputSwitchDebounceMs = parsed,
                AppConstants.Limits.SwitchDebounceMinMs,
                AppConstants.Limits.SwitchDebounceMaxMs,
                "Invalid output switch debounce. Use a whole number between 25 and 2000."),
            CreateIntRangeEntry(
                "input-switch-debounce-ms",
                settings => (settings.AdvancedTuning ??= new()).InputSwitchDebounceMs,
                (settings, parsed) => (settings.AdvancedTuning ??= new()).InputSwitchDebounceMs = parsed,
                AppConstants.Limits.SwitchDebounceMinMs,
                AppConstants.Limits.SwitchDebounceMaxMs,
                "Invalid input switch debounce. Use a whole number between 25 and 2000."),
            CreateIntRangeEntry(
                "process-monitor-fallback-interval-ms",
                settings => (settings.AdvancedTuning ??= new()).ProcessMonitorFallbackIntervalMs,
                (settings, parsed) => (settings.AdvancedTuning ??= new()).ProcessMonitorFallbackIntervalMs = parsed,
                AppConstants.Limits.ProcessMonitorFallbackIntervalMinMs,
                AppConstants.Limits.ProcessMonitorFallbackIntervalMaxMs,
                "Invalid process monitor fallback interval. Use a whole number between 500 and 10000."),
            CreateIntRangeEntry(
                "bluetooth-reconnect-max-attempts",
                settings => GetBluetoothReconnectAdvancedTuning(settings).MaxAttempts,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).MaxAttempts = parsed,
                AppConstants.Limits.BluetoothReconnectMinAttempts,
                AppConstants.Limits.BluetoothReconnectMaxAttempts,
                "Invalid Bluetooth reconnect max attempts. Use a whole number between 1 and 3."),
            CreateIntRangeEntry(
                "bluetooth-reconnect-attempt-timeout-ms",
                settings => GetBluetoothReconnectAdvancedTuning(settings).AttemptTimeoutMs,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).AttemptTimeoutMs = parsed,
                AppConstants.Limits.BluetoothReconnectMinAttemptTimeoutMs,
                AppConstants.Limits.BluetoothReconnectMaxAttemptTimeoutMs,
                "Invalid Bluetooth reconnect attempt timeout. Use a whole number between 250 and 10000."),
            CreateIntRangeEntry(
                "bluetooth-reconnect-cooldown-ms",
                settings => GetBluetoothReconnectAdvancedTuning(settings).CooldownMs,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).CooldownMs = parsed,
                AppConstants.Limits.BluetoothReconnectMinCooldownMs,
                AppConstants.Limits.BluetoothReconnectMaxCooldownMs,
                "Invalid Bluetooth reconnect cooldown. Use a whole number between 500 and 30000."),
            CreateIntRangeEntry(
                "bluetooth-reconnect-success-stabilize-window-ms",
                settings => GetBluetoothReconnectAdvancedTuning(settings).SuccessStabilizeWindowMs,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).SuccessStabilizeWindowMs = parsed,
                AppConstants.Limits.BluetoothReconnectSuccessStabilizeWindowMinMs,
                AppConstants.Limits.BluetoothReconnectSuccessStabilizeWindowMaxMs,
                "Invalid Bluetooth endpoint wait window. Use a whole number between 1000 and 120000."),
            CreateBoolEntry(
                "bluetooth-reconnect-only-likely",
                settings => GetBluetoothReconnectAdvancedTuning(settings).OnlyLikelyBluetoothEndpoints,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).OnlyLikelyBluetoothEndpoints = parsed),
            CreateIntRangeEntry(
                "bluetooth-reconnect-cached-endpoint-probe-attempts",
                settings => GetBluetoothReconnectAdvancedTuning(settings).CachedEndpointVisibilityProbeAttempts,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).CachedEndpointVisibilityProbeAttempts = parsed,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinAttempts,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxAttempts,
                "Invalid Bluetooth reconnect cached-endpoint probe attempts. Use a whole number between 1 and 10."),
            CreateIntRangeEntry(
                "bluetooth-reconnect-cached-endpoint-probe-delay-ms",
                settings => GetBluetoothReconnectAdvancedTuning(settings).CachedEndpointVisibilityProbeDelayMs,
                (settings, parsed) => GetBluetoothReconnectAdvancedTuning(settings).CachedEndpointVisibilityProbeDelayMs = parsed,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinDelayMs,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxDelayMs,
                "Invalid Bluetooth reconnect cached-endpoint probe delay. Use a whole number between 25 and 1000."),
            CreateIntRangeEntry(
                "steam-big-picture-monitor-debounce-ms",
                settings => GetSteamBigPictureAdvancedTuning(settings).MonitorDebounceMs,
                (settings, parsed) => GetSteamBigPictureAdvancedTuning(settings).MonitorDebounceMs = parsed,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMinMs,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMaxMs,
                "Invalid Steam Big Picture monitor debounce. Use a whole number between 25 and 2000."),
            CreateIntRangeEntry(
                "steam-big-picture-confirmation-delay-ms",
                settings => GetSteamBigPictureAdvancedTuning(settings).ConfirmationDelayMs,
                (settings, parsed) => GetSteamBigPictureAdvancedTuning(settings).ConfirmationDelayMs = parsed,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMinMs,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMaxMs,
                "Invalid Steam Big Picture confirmation delay. Use a whole number between 50 and 5000."),
            CreateCustomEntry(
                "generate-device-reference-file",
                settings => settings.Diagnostics.DeviceReferenceFileMode switch
                {
                    DeviceReferenceFileMode.Off => "false",
                    DeviceReferenceFileMode.Plaintext => "true",
                    DeviceReferenceFileMode.Hashed => "hashed",
                    _ => "false",
                },
                (settings, rawValue) =>
                {
                    if (!TryParseDeviceReferenceFileMode(rawValue, out DeviceReferenceFileMode parsed))
                    {
                        return ConfigSetResult.FromFailure("Invalid value. Use: true|false|hashed.");
                    }

                    settings.Diagnostics.DeviceReferenceFileMode = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "schedule-timezone",
                settings => settings.Routines.DefaultTimeZoneId,
                (settings, rawValue) =>
                {
                    string normalized = rawValue.Trim();
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        return ConfigSetResult.FromFailure("Invalid time zone. Use a valid Windows time zone ID (e.g., 'Pacific Standard Time', 'UTC').");
                    }

                    try
                    {
                        TimeZoneInfo.FindSystemTimeZoneById(normalized);
                    }
                    catch
                    {
                        return ConfigSetResult.FromFailure($"Invalid time zone '{normalized}'. Use a valid Windows time zone ID (e.g., 'Pacific Standard Time', 'UTC').");
                    }

                    settings.Routines.DefaultTimeZoneId = normalized;
                    return ConfigSetResult.FromSuccess();
                }),
        ];
        private static readonly string[] KnownKeys = [.. Entries.Select(static entry => entry.Key)];
        private static readonly Dictionary<string, ConfigEntry> EntriesByKey = Entries.ToDictionary(
            static entry => entry.Key,
            StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> GetKnownKeys()
        {
            return KnownKeys;
        }

        public static IReadOnlyList<CliKeyMetadata> GetKeyMetadata()
        {
            var defaultSettings = new Settings();
            return [.. Entries.Select(entry => entry.Metadata with
            {
                DefaultValue = entry.GetValue(defaultSettings),
            })];
        }

        public static bool TryGet(Settings settings, string key, out string value, out string? error)
        {
            value = string.Empty;
            error = null;

            if (!TryGetEntry(key, out ConfigEntry? entry) || entry is null)
            {
                error = CliSuggestionHelper.BuildUnknownKeyError("config", key, KnownKeys);
                return false;
            }

            ConfigEntry resolvedEntry = entry;
            value = resolvedEntry.GetValue(settings);
            return true;
        }

        public static bool TrySet(Settings settings, string key, string value, out string? error)
        {
            if (!TryGetEntry(key, out ConfigEntry? entry) || entry is null)
            {
                error = CliSuggestionHelper.BuildUnknownKeyError("config", key, KnownKeys);
                return false;
            }

            ConfigEntry resolvedEntry = entry;
            ConfigSetResult result = resolvedEntry.TrySetValue(settings, value);
            error = result.Error;
            return result.Success;
        }

        private static bool TryGetEntry(string key, out ConfigEntry? entry)
        {
            return EntriesByKey.TryGetValue(NormalizeKey(key), out entry);
        }

        private static ConfigEntry CreateTrimmedStringEntry(
            string key,
            Func<Settings, string> getter,
            Action<Settings, string> setter)
        {
            return CreateCustomEntry(
                key,
                getter,
                (settings, rawValue) =>
                {
                    setter(settings, rawValue.Trim());
                    return ConfigSetResult.FromSuccess();
                });
        }

        private static ConfigEntry CreateValidatedHotkeyEntry(
            string key,
            Func<Settings, string> getter,
            Action<Settings, string> setter,
            string displayName)
        {
            return CreateCustomEntry(
                key,
                getter,
                (settings, rawValue) =>
                {
                    string trimmed = rawValue.Trim();
                    HotkeyValidationResult validation = SettingsValidationService.ValidateHotkey(trimmed);
                    if (!validation.IsValid)
                    {
                        return validation.IsReserved
                            ? ConfigSetResult.FromFailure($"{displayName} uses reserved Windows shortcut '{validation.ReservedShortcutName}'.")
                            : ConfigSetResult.FromFailure($"Invalid {displayName}. Use a valid combination such as Ctrl+Alt+H, or clear it with an empty value.");
                    }

                    if (key is "push-to-talk-hotkey" or "hold-to-mute-hotkey" && trimmed.Length > 0)
                    {
                        var parsed = HotkeySyntax.Parse(trimmed);
                        if (!parsed.HasValue || !HotkeySyntax.IsHoldable(parsed.Value.MainInput))
                            return ConfigSetResult.FromFailure("Choose a keyboard key or mouse button that can be held; wheel input, Pause, and Print Screen are not supported for microphone holds.");
                    }
                    setter(settings, trimmed);
                    return ConfigSetResult.FromSuccess();
                });
        }

        private static ConfigEntry CreateBoolEntry(
            string key,
            Func<Settings, bool> getter,
            Action<Settings, bool> setter)
        {
            return CreateCustomEntry(
                key,
                settings => getter(settings) ? "true" : "false",
                (settings, rawValue) =>
                {
                    if (!CliValueParser.TryParseBool(rawValue, out bool parsed))
                    {
                        return ConfigSetResult.FromFailure("Invalid boolean value. Use true/false.");
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                },
                CliKeyMetadataFactory.Create(key, "boolean", null, "persisted"));
        }

        private static ConfigEntry CreateDoubleRangeEntry(
            string key,
            Func<Settings, double> getter,
            Action<Settings, double> setter,
            double minValue,
            double maxValue,
            string parseError,
            string rangeError)
        {
            return CreateCustomEntry(
                key,
                settings => getter(settings).ToString("0.###", CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!double.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        return ConfigSetResult.FromFailure(parseError);
                    }

                    if (!double.IsFinite(parsed) || parsed < minValue || parsed > maxValue)
                    {
                        return ConfigSetResult.FromFailure(rangeError);
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                },
                CliKeyMetadataFactory.Create(
                    key,
                    "number",
                    $"{minValue.ToString(CultureInfo.InvariantCulture)}..{maxValue.ToString(CultureInfo.InvariantCulture)}",
                    "persisted"));
        }

        private static ConfigEntry CreateEnumEntry<TEnum>(
            string key,
            Func<Settings, TEnum> getter,
            Action<Settings, TEnum> setter,
            string invalidValueError)
            where TEnum : struct, Enum
        {
            return CreateCustomEntry(
                key,
                settings => getter(settings).ToString(),
                (settings, rawValue) =>
                {
                    if (!TryParseDefinedEnumName(rawValue, out TEnum parsed))
                    {
                        return ConfigSetResult.FromFailure(invalidValueError);
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                });
        }

        private static ConfigEntry CreateEnumStringEntry<TEnum>(
            string key,
            Func<Settings, string> getter,
            Action<Settings, TEnum> setter,
            string invalidValueError)
            where TEnum : struct, Enum
        {
            return CreateCustomEntry(
                key,
                getter,
                (settings, rawValue) =>
                {
                    if (!TryParseDefinedEnumName(rawValue, out TEnum parsed))
                    {
                        return ConfigSetResult.FromFailure(invalidValueError);
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                });
        }

        private static bool TryParseDefinedEnumName<TEnum>(string rawValue, out TEnum parsed)
            where TEnum : struct, Enum
        {
            parsed = default;
            string trimmed = rawValue.Trim();
            if (trimmed.Length == 0 || trimmed.Contains(',') || char.IsDigit(trimmed[0]) || trimmed[0] is '+' or '-')
            {
                return false;
            }

            return Enum.TryParse(trimmed, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
        }

        private static ConfigEntry CreateCustomEntry(
            string key,
            Func<Settings, string> getter,
            TrySetConfigValue setter,
            CliKeyMetadata? metadata = null)
        {
            return new ConfigEntry(
                key,
                getter,
                setter,
                metadata ?? CliKeyMetadataFactory.Create(key, "string", null, "persisted"));
        }

        private static ConfigEntry CreateIntRangeEntry(
            string key,
            Func<Settings, int> getter,
            Action<Settings, int> setter,
            int minValue,
            int maxValue,
            string invalidValueError)
        {
            return CreateCustomEntry(
                key,
                settings => getter(settings).ToString(CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!int.TryParse(rawValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                        || parsed < minValue
                        || parsed > maxValue)
                    {
                        return ConfigSetResult.FromFailure(invalidValueError);
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                },
                CliKeyMetadataFactory.Create(key, "integer", $"{minValue}..{maxValue}", "persisted"));
        }

        private static bool TryParseDeviceReferenceFileMode(string value, out DeviceReferenceFileMode mode)
        {
            mode = DeviceReferenceFileMode.Off;
            string normalized = value.Trim();

            if (string.Equals(normalized, "hashed", StringComparison.OrdinalIgnoreCase))
            {
                mode = DeviceReferenceFileMode.Hashed;
                return true;
            }

            if (!CliValueParser.TryParseBool(normalized, out bool enabled))
            {
                return false;
            }

            mode = enabled
                ? DeviceReferenceFileMode.Plaintext
                : DeviceReferenceFileMode.Off;
            return true;
        }

        private static bool TryParseSwitchRoles(string value, out List<string> roles, out string? error)
        {
            error = null;

            string normalized = value.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
            {
                roles = [.. SupportedSwitchRoles];
                return true;
            }

            string[] rawTokens = normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (rawTokens.Length == 0)
            {
                roles = [.. SupportedSwitchRoles];
                return true;
            }

            var requested = new HashSet<string>(rawTokens, StringComparer.OrdinalIgnoreCase);
            foreach (string token in requested)
            {
                if (!SupportedSwitchRoles.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    roles = [];
                    error = "Invalid switch roles value. Use a comma-separated subset of Multimedia,Communications,Console or 'all'.";
                    return false;
                }
            }

            roles = NormalizeRoles(requested);
            return true;
        }

        private static List<string> NormalizeRoles(IEnumerable<string>? roles)
        {
            var requested = roles == null
                ? [with(StringComparer.OrdinalIgnoreCase)]
                : new HashSet<string>(roles.Where(role => !string.IsNullOrWhiteSpace(role)), StringComparer.OrdinalIgnoreCase);

            var normalized = new List<string>(SupportedSwitchRoles.Length);
            foreach (string role in SupportedSwitchRoles)
            {
                if (requested.Contains(role))
                {
                    normalized.Add(role);
                }
            }

            return normalized.Count > 0 ? normalized : [.. SupportedSwitchRoles];
        }

        private static BluetoothReconnectAdvancedTuningSettings GetBluetoothReconnectAdvancedTuning(Settings settings)
        {
            settings.AdvancedTuning ??= new AdvancedTuningSettings();
            settings.AdvancedTuning.BluetoothReconnect ??= new BluetoothReconnectAdvancedTuningSettings();
            return settings.AdvancedTuning.BluetoothReconnect;
        }

        private static SteamBigPictureAdvancedTuningSettings GetSteamBigPictureAdvancedTuning(Settings settings)
        {
            settings.AdvancedTuning ??= new AdvancedTuningSettings();
            settings.AdvancedTuning.SteamBigPicture ??= new SteamBigPictureAdvancedTuningSettings();
            return settings.AdvancedTuning.SteamBigPicture;
        }

        private static string NormalizeKey(string key)
        {
            return key.Trim().ToLowerInvariant();
        }

        private delegate ConfigSetResult TrySetConfigValue(Settings settings, string value);

        private readonly record struct ConfigSetResult(bool Success, string? Error)
        {
            public static ConfigSetResult FromSuccess()
            {
                return new(true, null);
            }

            public static ConfigSetResult FromFailure(string? error)
            {
                return new(false, error);
            }
        }

        private sealed record ConfigEntry(
            string Key,
            Func<Settings, string> GetValue,
            TrySetConfigValue TrySetValue,
            CliKeyMetadata Metadata);
    }
}
