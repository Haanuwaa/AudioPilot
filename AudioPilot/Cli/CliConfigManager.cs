using System.Globalization;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static class CliConfigManager
    {
        private static readonly string[] SupportedSwitchRoles = ["Multimedia", "Communications", "Console"];
        private static readonly ConfigEntry[] Entries =
        [
            CreateEnumEntry(
                "theme",
                settings => settings.Theme,
                (settings, parsed) => settings.Theme = parsed,
                "Invalid theme value. Use: System|Light|Dark."),
            CreateBoolEntry(
                "auto-save-enabled",
                settings => settings.Miscellaneous.AutoSaveEnabled,
                (settings, parsed) => settings.Miscellaneous.AutoSaveEnabled = parsed),
            CreateBoolEntry(
                "play-dialog-sounds",
                settings => settings.Miscellaneous.PlayDialogSounds,
                (settings, parsed) => settings.Miscellaneous.PlayDialogSounds = parsed),
            CreateBoolEntry(
                "run-at-startup",
                settings => settings.RunAtStartup,
                (settings, parsed) => settings.RunAtStartup = parsed),
            CreateBoolEntry(
                "preserve-audio-levels",
                settings => settings.Miscellaneous.PreserveAudioLevels,
                (settings, parsed) => settings.Miscellaneous.PreserveAudioLevels = parsed),
            CreateBoolEntry(
                "auto-scroll-to-mixer-on-restore",
                settings => settings.Miscellaneous.AutoScrollToMixerOnRestore,
                (settings, parsed) => settings.Miscellaneous.AutoScrollToMixerOnRestore = parsed),
            CreateBoolEntry(
                "suppress-device-startup-warnings",
                settings => settings.Miscellaneous.SuppressDeviceStartupWarnings,
                (settings, parsed) => settings.Miscellaneous.SuppressDeviceStartupWarnings = parsed),
            CreateEnumStringEntry<Logging.LogLevel>(
                "log-level",
                settings => settings.Miscellaneous.LogLevel,
                (settings, parsed) => settings.Miscellaneous.LogLevel = parsed.ToString(),
                "Invalid log level. Use: Trace|Debug|Info|Warning|Error|Fatal|None."),
            CreateBoolEntry(
                "redact-log-content",
                settings => settings.Miscellaneous.RedactLogContent,
                (settings, parsed) => settings.Miscellaneous.RedactLogContent = parsed),
            CreateEnumEntry(
                "overlay-position",
                settings => settings.Overlay.Position,
                (settings, parsed) => settings.Overlay.Position = parsed,
                "Invalid overlay position. Use: TopLeft|TopCenter|TopRight|BottomLeft|BottomCenter|BottomRight|Center."),
            CreateBoolEntry(
                "overlay-enabled",
                settings => settings.Overlay.Enabled,
                (settings, parsed) => settings.Overlay.Enabled = parsed),
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
                "mic-volume-down-hotkey",
                settings => settings.Hotkeys.Volume.MicDown,
                (settings, parsed) => settings.Hotkeys.Volume.MicDown = parsed,
                "microphone volume down hotkey"),
            CreateCustomEntry(
                "additional-standalone-hotkey-keys",
                settings => string.Join(",", HotkeyStandaloneKeyPolicy.Analyze(settings.Hotkeys.Global.AdditionalStandaloneKeys).EffectiveTokens),
                (settings, rawValue) =>
                {
                    if (!HotkeyStandaloneKeyPolicy.TryParseCliValue(rawValue, out List<string> parsed, out string? error))
                    {
                        return ConfigSetResult.FromFailure(error);
                    }

                    settings.Hotkeys.Global.AdditionalStandaloneKeys = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "master-volume-step-percent",
                settings => settings.Hotkeys.Volume.MasterVolumeStepPercent.ToString(CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!TryParseVolumeStepPercent(rawValue, out int parsed))
                    {
                        return ConfigSetResult.FromFailure("Invalid master volume step. Use a whole number between 1 and 100.");
                    }

                    settings.Hotkeys.Volume.MasterVolumeStepPercent = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "mic-volume-step-percent",
                settings => settings.Hotkeys.Volume.MicVolumeStepPercent.ToString(CultureInfo.InvariantCulture),
                (settings, rawValue) =>
                {
                    if (!TryParseVolumeStepPercent(rawValue, out int parsed))
                    {
                        return ConfigSetResult.FromFailure("Invalid microphone volume step. Use a whole number between 1 and 100.");
                    }

                    settings.Hotkeys.Volume.MicVolumeStepPercent = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateTrimmedStringEntry(
                "listen-monitor-output-device-id",
                settings => settings.Hotkeys.Listen.MonitorOutputDeviceId,
                (settings, parsed) => settings.Hotkeys.Listen.MonitorOutputDeviceId = parsed),
            CreateTrimmedStringEntry(
                "listen-monitor-output-device-name",
                settings => settings.Hotkeys.Listen.MonitorOutputDeviceName,
                (settings, parsed) => settings.Hotkeys.Listen.MonitorOutputDeviceName = parsed),
            CreateBoolEntry(
                "bluetooth-reconnect-enabled",
                settings => settings.Miscellaneous.BluetoothReconnectEnabled,
                (settings, parsed) => settings.Miscellaneous.BluetoothReconnectEnabled = parsed),
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
                settings => settings.Miscellaneous.DeviceReferenceFileMode switch
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

                    settings.Miscellaneous.DeviceReferenceFileMode = parsed;
                    return ConfigSetResult.FromSuccess();
                }),
            CreateCustomEntry(
                "schedule-timezone",
                settings => settings.Miscellaneous.ScheduleTimeZoneId,
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

                    settings.Miscellaneous.ScheduleTimeZoneId = normalized;
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
                    HotkeyValidationResult validation = SettingsValidationService.ValidateHotkey(trimmed, settings.Hotkeys.Global.AdditionalStandaloneKeys);
                    if (!validation.IsValid)
                    {
                        return validation.IsReserved
                            ? ConfigSetResult.FromFailure($"{displayName} uses reserved Windows shortcut '{validation.ReservedShortcutName}'.")
                            : ConfigSetResult.FromFailure($"Invalid {displayName}. Use a valid combination such as Ctrl+Alt+H, or clear it with an empty value.");
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
                    if (!TryParseBool(rawValue, out bool parsed))
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

                    if (parsed < minValue || parsed > maxValue)
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
                    if (!Enum.TryParse(rawValue, ignoreCase: true, out TEnum parsed))
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
                    if (!Enum.TryParse(rawValue, ignoreCase: true, out TEnum parsed))
                    {
                        return ConfigSetResult.FromFailure(invalidValueError);
                    }

                    setter(settings, parsed);
                    return ConfigSetResult.FromSuccess();
                });
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

            if (!TryParseBool(normalized, out bool enabled))
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

        private static bool TryParseVolumeStepPercent(string value, out int stepPercent)
        {
            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stepPercent))
            {
                return false;
            }

            return stepPercent is >= 1 and <= 100;
        }

        private static List<string> NormalizeRoles(IEnumerable<string>? roles)
        {
            var requested = roles == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

        private static bool TryParseBool(string value, out bool parsed)
        {
            string normalized = value.Trim();
            if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
            {
                parsed = true;
                return true;
            }

            if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
            {
                parsed = false;
                return true;
            }

            return bool.TryParse(normalized, out parsed);
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
