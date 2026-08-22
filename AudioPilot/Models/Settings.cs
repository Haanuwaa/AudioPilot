using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AppTheme
    {
        System,
        Light,
        Dark
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OverlayPosition
    {
        TopLeft,
        TopCenter,
        TopRight,
        BottomLeft,
        BottomCenter,
        BottomRight,
        Center
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum DeviceReferenceFileMode
    {
        Off,
        Plaintext,
        Hashed
    }

    public sealed class BluetoothReconnectAdvancedTuningSettings
    {
        public int MaxAttempts { get; set; } = AudioPilot.Constants.AppConstants.Bluetooth.ReconnectMaxAttemptsDefault;
        public int AttemptTimeoutMs { get; set; } = AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectAttemptTimeoutMs;
        public int CooldownMs { get; set; } = AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectCooldownMs;
        public int CachedEndpointVisibilityProbeAttempts { get; set; } = AudioPilot.Constants.AppConstants.Bluetooth.CachedEndpointVisibilityProbeAttempts;
        public int CachedEndpointVisibilityProbeDelayMs { get; set; } = AudioPilot.Constants.AppConstants.Bluetooth.CachedEndpointVisibilityProbeDelayMs;
        public bool OnlyLikelyBluetoothEndpoints { get; set; } = AudioPilot.Constants.AppConstants.Bluetooth.OnlyLikelyBluetoothEndpointsDefault;

        public static BluetoothReconnectAdvancedTuningSettings Clone(BluetoothReconnectAdvancedTuningSettings? source)
        {
            if (source == null)
            {
                return new BluetoothReconnectAdvancedTuningSettings();
            }

            return new BluetoothReconnectAdvancedTuningSettings
            {
                MaxAttempts = source.MaxAttempts,
                AttemptTimeoutMs = source.AttemptTimeoutMs,
                CooldownMs = source.CooldownMs,
                CachedEndpointVisibilityProbeAttempts = source.CachedEndpointVisibilityProbeAttempts,
                CachedEndpointVisibilityProbeDelayMs = source.CachedEndpointVisibilityProbeDelayMs,
                OnlyLikelyBluetoothEndpoints = source.OnlyLikelyBluetoothEndpoints,
            };
        }
    }

    public sealed class SteamBigPictureAdvancedTuningSettings
    {
        public int MonitorDebounceMs { get; set; } = AudioPilot.Constants.AppConstants.Routines.SteamBigPictureMonitorDebounceMs;
        public int ConfirmationDelayMs { get; set; } = AudioPilot.Constants.AppConstants.Routines.SteamBigPictureConfirmationDelayMs;

        public static SteamBigPictureAdvancedTuningSettings Clone(SteamBigPictureAdvancedTuningSettings? source)
        {
            if (source == null)
            {
                return new SteamBigPictureAdvancedTuningSettings();
            }

            return new SteamBigPictureAdvancedTuningSettings
            {
                MonitorDebounceMs = source.MonitorDebounceMs,
                ConfirmationDelayMs = source.ConfirmationDelayMs,
            };
        }
    }

    public sealed class AdvancedTuningSettings
    {
        public BluetoothReconnectAdvancedTuningSettings BluetoothReconnect { get; set; } = new();
        public SteamBigPictureAdvancedTuningSettings SteamBigPicture { get; set; } = new();

        public static AdvancedTuningSettings Clone(AdvancedTuningSettings? source)
        {
            if (source == null)
            {
                return new AdvancedTuningSettings();
            }

            return new AdvancedTuningSettings
            {
                BluetoothReconnect = BluetoothReconnectAdvancedTuningSettings.Clone(source.BluetoothReconnect),
                SteamBigPicture = SteamBigPictureAdvancedTuningSettings.Clone(source.SteamBigPicture),
            };
        }
    }

    public sealed class DeviceSwitchingOutputSettings
    {
        public List<CycleDevice> CycleDevices { get; set; } = [];
        public List<string> SwitchRoles { get; set; } =
        [
            "Multimedia",
            "Communications",
            "Console"
        ];
        public bool HotkeysEnabled { get; set; } = true;
        public string SwitchHotkey { get; set; } = "";
        public string ReverseSwitchHotkey { get; set; } = "";

        public static DeviceSwitchingOutputSettings Clone(DeviceSwitchingOutputSettings? source)
        {
            if (source == null)
            {
                return new DeviceSwitchingOutputSettings();
            }

            return new DeviceSwitchingOutputSettings
            {
                CycleDevices = [.. source.CycleDevices.Select(d => d.Clone())],
                SwitchRoles = [.. source.SwitchRoles],
                HotkeysEnabled = source.HotkeysEnabled,
                SwitchHotkey = source.SwitchHotkey,
                ReverseSwitchHotkey = source.ReverseSwitchHotkey,
            };
        }
    }

    public sealed class DeviceSwitchingInputSettings
    {
        public List<CycleDevice> CycleDevices { get; set; } = [];
        public List<string> SwitchRoles { get; set; } =
        [
            "Multimedia",
            "Communications",
            "Console"
        ];
        public bool HotkeysEnabled { get; set; } = true;
        public string SwitchHotkey { get; set; } = "";
        public string ReverseSwitchHotkey { get; set; } = "";

        public static DeviceSwitchingInputSettings Clone(DeviceSwitchingInputSettings? source)
        {
            if (source == null)
            {
                return new DeviceSwitchingInputSettings();
            }

            return new DeviceSwitchingInputSettings
            {
                CycleDevices = [.. source.CycleDevices.Select(d => d.Clone())],
                SwitchRoles = [.. source.SwitchRoles],
                HotkeysEnabled = source.HotkeysEnabled,
                SwitchHotkey = source.SwitchHotkey,
                ReverseSwitchHotkey = source.ReverseSwitchHotkey,
            };
        }
    }

    public sealed class DeviceSwitchingSettings
    {
        public DeviceSwitchingOutputSettings Output { get; set; } = new();
        public DeviceSwitchingInputSettings Input { get; set; } = new();

        public static DeviceSwitchingSettings Clone(DeviceSwitchingSettings? source)
        {
            if (source == null)
            {
                return new DeviceSwitchingSettings();
            }

            return new DeviceSwitchingSettings
            {
                Output = DeviceSwitchingOutputSettings.Clone(source.Output),
                Input = DeviceSwitchingInputSettings.Clone(source.Input),
            };
        }
    }

    public sealed class HotkeysAppSettings
    {
        public string ToggleAppVisibility { get; set; } = "Ctrl+Alt+H";

        public static HotkeysAppSettings Clone(HotkeysAppSettings? source)
        {
            if (source == null)
            {
                return new HotkeysAppSettings();
            }

            return new HotkeysAppSettings
            {
                ToggleAppVisibility = source.ToggleAppVisibility,
            };
        }
    }

    public sealed class HotkeysMediaSettings
    {
        public string ShowCurrentTrack { get; set; } = "";
        public string PlayPause { get; set; } = "Ctrl+Alt+P";
        public string NextTrack { get; set; } = "Ctrl+Alt+.";
        public string PreviousTrack { get; set; } = "Ctrl+Alt+,";

        public static HotkeysMediaSettings Clone(HotkeysMediaSettings? source)
        {
            if (source == null)
            {
                return new HotkeysMediaSettings();
            }

            return new HotkeysMediaSettings
            {
                ShowCurrentTrack = source.ShowCurrentTrack,
                PlayPause = source.PlayPause,
                NextTrack = source.NextTrack,
                PreviousTrack = source.PreviousTrack,
            };
        }
    }

    public sealed class HotkeysMuteSettings
    {
        public string Mic { get; set; } = "";
        public string Sound { get; set; } = "";
        public string Deafen { get; set; } = "";

        public static HotkeysMuteSettings Clone(HotkeysMuteSettings? source)
        {
            if (source == null)
            {
                return new HotkeysMuteSettings();
            }

            return new HotkeysMuteSettings
            {
                Mic = source.Mic,
                Sound = source.Sound,
                Deafen = source.Deafen,
            };
        }
    }

    public sealed class HotkeysVolumeSettings
    {
        public string MasterUp { get; set; } = "";
        public string MasterDown { get; set; } = "";
        public int MasterVolumeStepPercent { get; set; } = 5;
        public string MicUp { get; set; } = "";
        public string MicDown { get; set; } = "";
        public int MicVolumeStepPercent { get; set; } = 5;

        public static HotkeysVolumeSettings Clone(HotkeysVolumeSettings? source)
        {
            if (source == null)
            {
                return new HotkeysVolumeSettings();
            }

            return new HotkeysVolumeSettings
            {
                MasterUp = source.MasterUp,
                MasterDown = source.MasterDown,
                MasterVolumeStepPercent = source.MasterVolumeStepPercent,
                MicUp = source.MicUp,
                MicDown = source.MicDown,
                MicVolumeStepPercent = source.MicVolumeStepPercent,
            };
        }
    }

    public sealed class HotkeysListenSettings
    {
        public string ListenToInput { get; set; } = "";
        public string MonitorOutputDeviceId { get; set; } = "";
        public string MonitorOutputDeviceName { get; set; } = "";

        public static HotkeysListenSettings Clone(HotkeysListenSettings? source)
        {
            if (source == null)
            {
                return new HotkeysListenSettings();
            }

            return new HotkeysListenSettings
            {
                ListenToInput = source.ListenToInput,
                MonitorOutputDeviceId = source.MonitorOutputDeviceId,
                MonitorOutputDeviceName = source.MonitorOutputDeviceName,
            };
        }
    }

    public sealed class HotkeysGlobalSettings
    {
        public List<string> AdditionalStandaloneKeys { get; set; } = [];

        public static HotkeysGlobalSettings Clone(HotkeysGlobalSettings? source)
        {
            if (source == null)
            {
                return new HotkeysGlobalSettings();
            }

            return new HotkeysGlobalSettings
            {
                AdditionalStandaloneKeys = [.. source.AdditionalStandaloneKeys],
            };
        }
    }

    public sealed class HotkeysSettings
    {
        public HotkeysAppSettings App { get; set; } = new();
        public HotkeysMediaSettings Media { get; set; } = new();
        public HotkeysMuteSettings Mute { get; set; } = new();
        public HotkeysVolumeSettings Volume { get; set; } = new();
        public HotkeysListenSettings Listen { get; set; } = new();
        public HotkeysGlobalSettings Global { get; set; } = new();

        public static HotkeysSettings Clone(HotkeysSettings? source)
        {
            if (source == null)
            {
                return new HotkeysSettings();
            }

            return new HotkeysSettings
            {
                App = HotkeysAppSettings.Clone(source.App),
                Media = HotkeysMediaSettings.Clone(source.Media),
                Mute = HotkeysMuteSettings.Clone(source.Mute),
                Volume = HotkeysVolumeSettings.Clone(source.Volume),
                Listen = HotkeysListenSettings.Clone(source.Listen),
                Global = HotkeysGlobalSettings.Clone(source.Global),
            };
        }
    }

    public sealed class OverlaySettings
    {
        public bool Enabled { get; set; } = true;
        public OverlayPosition Position { get; set; } = OverlayPosition.TopRight;
        public double DurationSeconds { get; set; } = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;

        public static OverlaySettings Clone(OverlaySettings? source)
        {
            if (source == null)
            {
                return new OverlaySettings();
            }

            return new OverlaySettings
            {
                Enabled = source.Enabled,
                Position = source.Position,
                DurationSeconds = source.DurationSeconds,
            };
        }
    }

    [JsonConverter(typeof(RoutinesSettingsJsonConverter))]
    public sealed class RoutinesSettings
    {
        public List<AudioRoutine> Items { get; set; } = [];

        public static RoutinesSettings Clone(RoutinesSettings? source)
        {
            if (source == null)
            {
                return new RoutinesSettings();
            }

            return new RoutinesSettings
            {
                Items = [.. source.Items.Select(r => r.Clone())],
            };
        }
    }

    public class RoutinesSettingsJsonConverter : JsonConverter<RoutinesSettings>
    {
        public override RoutinesSettings ReadJson(JsonReader reader, Type objectType, RoutinesSettings? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.StartObject)
            {
                var settings = new RoutinesSettings();
                serializer.Populate(reader, settings);
                return settings;
            }

            if (reader.TokenType == JsonToken.Null)
            {
                return new RoutinesSettings();
            }

            throw new JsonSerializationException($"Unexpected token type {reader.TokenType} when deserializing RoutinesSettings");
        }

        public override void WriteJson(JsonWriter writer, RoutinesSettings? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("Items");
            serializer.Serialize(writer, value.Items);
            writer.WriteEndObject();
        }
    }

    public sealed class MiscellaneousSettings
    {
        public bool BluetoothReconnectEnabled { get; set; } = true;
        public bool PreserveAudioLevels { get; set; } = true;
        public DeviceReferenceFileMode DeviceReferenceFileMode { get; set; } = DeviceReferenceFileMode.Off;
        public string LogLevel { get; set; } = "Info";
        public bool RedactLogContent { get; set; } = true;
        public bool AutoSaveEnabled { get; set; }
        public bool PlayDialogSounds { get; set; } = true;
        public string ScheduleTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
        public bool AutoScrollToMixerOnRestore { get; set; } = true;
        public bool SuppressDeviceStartupWarnings { get; set; }

        public static MiscellaneousSettings Clone(MiscellaneousSettings? source)
        {
            if (source == null)
            {
                return new MiscellaneousSettings();
            }

            return new MiscellaneousSettings
            {
                BluetoothReconnectEnabled = source.BluetoothReconnectEnabled,
                PreserveAudioLevels = source.PreserveAudioLevels,
                DeviceReferenceFileMode = source.DeviceReferenceFileMode,
                LogLevel = source.LogLevel,
                RedactLogContent = source.RedactLogContent,
                AutoSaveEnabled = source.AutoSaveEnabled,
                PlayDialogSounds = source.PlayDialogSounds,
                ScheduleTimeZoneId = source.ScheduleTimeZoneId,
                AutoScrollToMixerOnRestore = source.AutoScrollToMixerOnRestore,
                SuppressDeviceStartupWarnings = source.SuppressDeviceStartupWarnings,
            };
        }
    }

    public class Settings
    {
        public const string CurrentSchemaVersion = "1.0.0";

        public string SchemaVersion { get; set; } = CurrentSchemaVersion;
        public AppTheme Theme { get; set; } = AppTheme.System;
        public bool RunAtStartup { get; set; }

        public DeviceSwitchingSettings DeviceSwitching { get; set; } = new();
        public HotkeysSettings Hotkeys { get; set; } = new();
        public RoutinesSettings Routines { get; set; } = new();
        public OverlaySettings Overlay { get; set; } = new();
        public MiscellaneousSettings Miscellaneous { get; set; } = new();
        public AdvancedTuningSettings AdvancedTuning { get; set; } = new();

        [JsonExtensionData]
        public Dictionary<string, JToken>? ExtensionData { get; set; }

        public Settings Clone()
        {
            return new Settings
            {
                SchemaVersion = SchemaVersion,
                Theme = Theme,
                RunAtStartup = RunAtStartup,
                DeviceSwitching = DeviceSwitchingSettings.Clone(DeviceSwitching),
                Hotkeys = HotkeysSettings.Clone(Hotkeys),
                Routines = RoutinesSettings.Clone(Routines),
                Overlay = OverlaySettings.Clone(Overlay),
                Miscellaneous = MiscellaneousSettings.Clone(Miscellaneous),
                AdvancedTuning = AdvancedTuningSettings.Clone(AdvancedTuning),
                ExtensionData = ExtensionData?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.DeepClone()),
            };
        }
    }
}
