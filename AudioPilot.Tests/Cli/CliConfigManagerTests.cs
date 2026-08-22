using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed class CliConfigManagerTests
{
    [Fact]
    public void TrySetThenGet_AutoSaveEnabled_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "auto-save-enabled", "true", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "auto-save-enabled", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.True(settings.Miscellaneous.AutoSaveEnabled);
        Assert.Equal("true", value);
    }

    [Fact]
    public void TrySetThenGet_PlayDialogSounds_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "play-dialog-sounds", "false", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "play-dialog-sounds", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.False(settings.Miscellaneous.PlayDialogSounds);
        Assert.Equal("false", value);
    }

    [Fact]
    public void TrySetThenGet_OutputReverseSwitchHotkey_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Ctrl+Alt+Shift+O";

        bool updated = CliConfigManager.TrySet(settings, "output-reverse-switch-hotkey", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "output-reverse-switch-hotkey", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_InputReverseSwitchHotkey_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Ctrl+Alt+Shift+I";

        bool updated = CliConfigManager.TrySet(settings, "input-reverse-switch-hotkey", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "input-reverse-switch-hotkey", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_ScheduleTimeZone_RoundTrips()
    {
        var settings = new Settings();
        string expected = TimeZoneInfo.Local.Id;

        bool updated = CliConfigManager.TrySet(settings, "schedule-timezone", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "schedule-timezone", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySet_ScheduleTimeZone_InvalidTimeZone_ReturnsError()
    {
        var settings = new Settings();
        const string invalidTimeZone = "InvalidTimeZone";

        bool updated = CliConfigManager.TrySet(settings, "schedule-timezone", invalidTimeZone, out string? setError);

        Assert.False(updated);
        Assert.NotNull(setError);
        Assert.Contains("Invalid time zone", setError, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySetThenGet_OutputSwitchRoles_RoundTripsCanonicalSubset()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "output-switch-roles", "console,multimedia", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "output-switch-roles", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(["Multimedia", "Console"], settings.DeviceSwitching.Output.SwitchRoles);
        Assert.Equal("Multimedia,Console", value);
    }

    [Fact]
    public void TrySet_InputSwitchRoles_All_RestoresDefaultRoles()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchRoles = ["Console"]
                }
            }
        };

        bool updated = CliConfigManager.TrySet(settings, "input-switch-roles", "all", out string? error);

        Assert.True(updated);
        Assert.Null(error);
        Assert.Equal(["Multimedia", "Communications", "Console"], settings.DeviceSwitching.Input.SwitchRoles);
    }

    [Fact]
    public void TrySet_SwitchRoles_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "output-switch-roles", "console,game", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySetThenGet_ListenToInputHotkey_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Ctrl+Alt+L";

        bool updated = CliConfigManager.TrySet(settings, "listen-to-input-hotkey", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "listen-to-input-hotkey", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_ShowCurrentTrackHotkey_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Ctrl+Alt+Y";

        bool updated = CliConfigManager.TrySet(settings, "show-current-track-hotkey", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "show-current-track-hotkey", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_MasterVolumeUpHotkey_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Ctrl+Alt+PageUp";

        bool updated = CliConfigManager.TrySet(settings, "master-volume-up-hotkey", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "master-volume-up-hotkey", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_AdditionalStandaloneHotkeyKeys_RoundTripsCanonicalValues()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "additional-standalone-hotkey-keys", "home,PRINTSCREEN", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "additional-standalone-hotkey-keys", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(["Home", "PrintScreen"], settings.Hotkeys.Global.AdditionalStandaloneKeys);
        Assert.Equal("Home,PrintScreen", value);
    }

    [Fact]
    public void TrySet_AdditionalStandaloneHotkeyKeys_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "additional-standalone-hotkey-keys", "a,home", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySet_Hotkey_ReservedShortcut_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "toggle-app-visibility-hotkey", "Win+S", out string? error);

        Assert.False(updated);
        Assert.Equal("Ctrl+Alt+H", settings.Hotkeys.App.ToggleAppVisibility);
        Assert.Equal("show/hide app hotkey uses reserved Windows shortcut 'Win+S'.", error);
    }

    [Fact]
    public void TrySet_MasterVolumeStepPercent_ValidValue_UpdatesSetting()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "master-volume-step-percent", "7", out string? error);

        Assert.True(updated);
        Assert.Null(error);
        Assert.Equal(7, settings.Hotkeys.Volume.MasterVolumeStepPercent);
    }

    [Fact]
    public void TrySet_MicVolumeStepPercent_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "mic-volume-step-percent", "0", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySetThenGet_ListenMonitorOutputDeviceId_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "{0.0.0.00000000}.\"listen-target\"";

        bool updated = CliConfigManager.TrySet(settings, "listen-monitor-output-device-id", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "listen-monitor-output-device-id", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TrySetThenGet_ListenMonitorOutputDeviceName_RoundTrips()
    {
        var settings = new Settings();
        const string expected = "Desk Speakers";

        bool updated = CliConfigManager.TrySet(settings, "listen-monitor-output-device-name", expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, "listen-monitor-output-device-name", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGet_LogLevel_ReturnsConfiguredValue()
    {
        var settings = new Settings
        {
            Miscellaneous = new MiscellaneousSettings { LogLevel = "Warning" },
        };

        bool found = CliConfigManager.TryGet(settings, "log-level", out string value, out string? error);

        Assert.True(found);
        Assert.Null(error);
        Assert.Equal("Warning", value);
    }

    [Fact]
    public void TrySet_LogLevel_None_UpdatesSetting()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "log-level", "none", out string? error);

        Assert.True(updated);
        Assert.Null(error);
        Assert.Equal("None", settings.Miscellaneous.LogLevel);
    }

    [Fact]
    public void TrySet_LogLevel_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "log-level", "verbose", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySetThenGet_RedactLogContent_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "redact-log-content", "false", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "redact-log-content", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("false", value);
        Assert.False(settings.Miscellaneous.RedactLogContent);
    }

    [Fact]
    public void TrySet_RedactLogContent_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "redact-log-content", "sometimes", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryGet_OverlayPosition_ReturnsConfiguredValue()
    {
        var settings = new Settings
        {
            Overlay = new OverlaySettings { Position = OverlayPosition.TopCenter },
        };

        bool found = CliConfigManager.TryGet(settings, "overlay-position", out string value, out string? error);

        Assert.True(found);
        Assert.Null(error);
        Assert.Equal("TopCenter", value);
    }

    [Fact]
    public void TrySet_OverlayPosition_ValidValue_UpdatesSetting()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-position", "center", out string? error);

        Assert.True(updated);
        Assert.Null(error);
        Assert.Equal(OverlayPosition.Center, settings.Overlay.Position);
    }

    [Fact]
    public void TrySet_OverlayPosition_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-position", "middle-right", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySetThenGet_OverlayEnabled_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-enabled", "false", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "overlay-enabled", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("false", value);
        Assert.False(settings.Overlay.Enabled);
    }

    [Fact]
    public void TrySet_OverlayEnabled_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-enabled", "maybe", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySet_OverlayDuration_ValidValue_UpdatesSetting()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-duration-seconds", "3.75", out string? error);

        Assert.True(updated);
        Assert.Null(error);
        Assert.Equal(3.75, settings.Overlay.DurationSeconds, 3);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0.4")]
    [InlineData("10.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void TrySet_OverlayDuration_InvalidValue_ReturnsError(string value)
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "overlay-duration-seconds", value, out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySetThenGet_BluetoothReconnectEnabled_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "bluetooth-reconnect-enabled", "false", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "bluetooth-reconnect-enabled", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("false", value);
        Assert.False(settings.Miscellaneous.BluetoothReconnectEnabled);
    }

    [Fact]
    public void TrySetThenGet_SuppressDeviceStartupWarnings_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "suppress-device-startup-warnings", "true", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "suppress-device-startup-warnings", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("true", value);
        Assert.True(settings.Miscellaneous.SuppressDeviceStartupWarnings);
    }

    [Fact]
    public void TrySetThenGet_GenerateDeviceReferenceFile_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "generate-device-reference-file", "true", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "generate-device-reference-file", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("true", value);
        Assert.Equal(DeviceReferenceFileMode.Plaintext, settings.Miscellaneous.DeviceReferenceFileMode);
    }

    [Fact]
    public void TrySetThenGet_GenerateDeviceReferenceFile_HashedMode_RoundTrips()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "generate-device-reference-file", "hashed", out string? setError);
        bool found = CliConfigManager.TryGet(settings, "generate-device-reference-file", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("hashed", value);
        Assert.Equal(DeviceReferenceFileMode.Hashed, settings.Miscellaneous.DeviceReferenceFileMode);
    }

    [Fact]
    public void TrySet_GenerateDeviceReferenceFile_InvalidValue_ReturnsError()
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, "generate-device-reference-file", "maybe", out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("bluetooth-reconnect-max-attempts", "2")]
    [InlineData("bluetooth-reconnect-attempt-timeout-ms", "1400")]
    [InlineData("bluetooth-reconnect-cooldown-ms", "1500")]
    [InlineData("bluetooth-reconnect-only-likely", "false")]
    [InlineData("bluetooth-reconnect-cached-endpoint-probe-attempts", "5")]
    [InlineData("bluetooth-reconnect-cached-endpoint-probe-delay-ms", "150")]
    [InlineData("steam-big-picture-monitor-debounce-ms", "200")]
    [InlineData("steam-big-picture-confirmation-delay-ms", "800")]
    public void TrySetThenGet_AdvancedTuningConfigKeys_RoundTrip(string key, string expected)
    {
        var settings = new Settings();

        bool updated = CliConfigManager.TrySet(settings, key, expected, out string? setError);
        bool found = CliConfigManager.TryGet(settings, key, out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expected, value);
    }
}
