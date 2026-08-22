using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSettingsEffectsCoordinatorTests
{
    [Fact]
    public void RunApplySideEffects_ExecutesCallbacksInOrder_AndAggregatesWarnings()
    {
        var calls = new List<string>();
        var settings = new Settings();

        ApplySettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunApplySideEffects(
            previousSettings: null,
            settings,
            outputRolesFallbackApplied: true,
            inputRolesFallbackApplied: false,
            persistUiState: () => calls.Add("persist"),
            registerHotkeys: () =>
            {
                calls.Add("register-hotkeys");
                return new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true);
            },
            logHotkeyResults: _ => calls.Add("log-hotkeys"),
            registerRoutineHotkeys: applied =>
            {
                Assert.Same(settings, applied);
                calls.Add("register-routines");
            },
            updateAudioConfiguration: applied =>
            {
                Assert.Same(settings, applied);
                calls.Add("update-audio");
            },
            updateOverlayState: applied =>
            {
                Assert.Same(settings, applied);
                calls.Add("update-overlay");
            },
            generateDeviceReferenceFile: () => calls.Add("generate-device-reference"),
            syncSettingsDrafts: () => calls.Add("sync-drafts"),
            getHotkeyRegistrationWarnings: _ => ["hotkey warning"]);

        Assert.Equal([
            "persist",
            "register-hotkeys",
            "log-hotkeys",
            "register-routines",
            "update-audio",
            "update-overlay",
            "generate-device-reference",
            "sync-drafts"
        ], calls);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("Output roles were all unchecked", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("hotkey warning", result.Warnings[1]);
    }

    [Fact]
    public void RunApplySideEffects_IncludesBothRoleFallbackWarnings_WhenBothFallbacksApplied()
    {
        ApplySettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunApplySideEffects(
            previousSettings: null,
            new Settings(),
            outputRolesFallbackApplied: true,
            inputRolesFallbackApplied: true,
            persistUiState: static () => { },
            registerHotkeys: static () => new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true),
            logHotkeyResults: static _ => { },
            registerRoutineHotkeys: static _ => { },
            updateAudioConfiguration: static _ => { },
            updateOverlayState: static _ => { },
            generateDeviceReferenceFile: static () => { },
            syncSettingsDrafts: static () => { },
            getHotkeyRegistrationWarnings: static _ => []);

        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("Output roles were all unchecked", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Input roles were all unchecked", StringComparison.Ordinal));
    }

    [Fact]
    public void RunSaveSideEffects_ExecutesCallbacksInOrder_AndBuildsDisconnectedWarnings()
    {
        var calls = new List<string>();
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I"
                }
            }
        };

        SaveSettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunSaveSideEffects(
            previousSettings: null,
            settings,
            disconnectedOutput: ["Speakers"],
            disconnectedInput: ["Mic"],
            persistUiState: () => calls.Add("persist"),
            registerSwitchHotkeys: () =>
            {
                calls.Add("register-switch-hotkeys");
                return new SwitchHotkeyRegistrationResult(true, true, true, true);
            },
            logSwitchHotkeyResults: _ => calls.Add("log-switch-hotkeys"),
            updateAudioConfiguration: applied =>
            {
                Assert.Same(settings, applied);
                calls.Add("update-audio");
            },
            updateOverlayState: applied =>
            {
                Assert.Same(settings, applied);
                calls.Add("update-overlay");
            },
            getSwitchHotkeyRegistrationWarnings: static _ => ["switch warning"]);

        Assert.Equal([
            "register-switch-hotkeys",
            "log-switch-hotkeys",
            "persist",
            "update-audio",
            "update-overlay"
        ], calls);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("Output disconnected: Speakers", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Input disconnected: Mic", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("switch warning", result.Warnings[1]);
    }

    [Fact]
    public void RunSaveSideEffects_DoesNotAddNoiseForDisabledBlankShortcuts()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = false,
                    SwitchHotkey = string.Empty
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = false,
                    SwitchHotkey = string.Empty
                }
            }
        };

        SaveSettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunSaveSideEffects(
            previousSettings: null,
            settings,
            disconnectedOutput: [],
            disconnectedInput: [],
            persistUiState: static () => { },
            registerSwitchHotkeys: static () => new SwitchHotkeyRegistrationResult(true, true, true, true),
            logSwitchHotkeyResults: static _ => { },
            updateAudioConfiguration: static _ => { },
            updateOverlayState: static _ => { },
            getSwitchHotkeyRegistrationWarnings: static _ => []);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RunApplySideEffects_WhenRelevantSettingsUnchanged_SkipsExpensiveSideEffects()
    {
        var calls = new List<string>();
        var previous = new Settings
        {
            Theme = AppTheme.Dark,
            Overlay = new OverlaySettings
            {
                Enabled = true,
                Position = OverlayPosition.TopCenter,
                DurationSeconds = 2.5
            },
            Miscellaneous = new MiscellaneousSettings
            {
                DeviceReferenceFileMode = DeviceReferenceFileMode.Off
            },
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchRoles = ["Multimedia", "Communications", "Console"],
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Shift+Alt+O",
                    HotkeysEnabled = true
                },
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchRoles = ["Multimedia", "Communications", "Console"],
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Shift+Alt+I",
                    HotkeysEnabled = true
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                },
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+.",
                    PreviousTrack = "Ctrl+Alt+,"
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterVolumeStepPercent = 5,
                    MicVolumeStepPercent = 5
                }
            }
        };
        var current = AppSettingsWorkflowCoordinator.CloneSettings(previous);
        current.Theme = AppTheme.Light;

        ApplySettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunApplySideEffects(
            previous,
            current,
            outputRolesFallbackApplied: false,
            inputRolesFallbackApplied: false,
            persistUiState: () => calls.Add("persist"),
            registerHotkeys: () =>
            {
                calls.Add("register-hotkeys");
                return new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true);
            },
            logHotkeyResults: _ => calls.Add("log-hotkeys"),
            registerRoutineHotkeys: _ => calls.Add("register-routines"),
            updateAudioConfiguration: _ => calls.Add("update-audio"),
            updateOverlayState: _ => calls.Add("update-overlay"),
            generateDeviceReferenceFile: () => calls.Add("generate-device-reference"),
            syncSettingsDrafts: () => calls.Add("sync-drafts"),
            getHotkeyRegistrationWarnings: _ => ["hotkey warning"]);

        Assert.Equal(["persist", "sync-drafts"], calls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RunApplySideEffects_WhenOnlyShowCurrentTrackHotkeyChanges_ReRegistersHotkeys()
    {
        var calls = new List<string>();
        var previous = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = string.Empty
                },
                Media = new HotkeysMediaSettings
                {
                    ShowCurrentTrack = string.Empty,
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+.",
                    PreviousTrack = "Ctrl+Alt+,"
                }
            }
        };
        var current = AppSettingsWorkflowCoordinator.CloneSettings(previous);
        current.Hotkeys.Media.ShowCurrentTrack = "Ctrl+Alt+Y";

        ApplySettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunApplySideEffects(
            previous,
            current,
            outputRolesFallbackApplied: false,
            inputRolesFallbackApplied: false,
            persistUiState: () => calls.Add("persist"),
            registerHotkeys: () =>
            {
                calls.Add("register-hotkeys");
                return new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true);
            },
            logHotkeyResults: _ => calls.Add("log-hotkeys"),
            registerRoutineHotkeys: _ => calls.Add("register-routines"),
            updateAudioConfiguration: _ => calls.Add("update-audio"),
            updateOverlayState: _ => calls.Add("update-overlay"),
            generateDeviceReferenceFile: () => calls.Add("generate-device-reference"),
            syncSettingsDrafts: () => calls.Add("sync-drafts"),
            getHotkeyRegistrationWarnings: _ => []);

        Assert.Equal(["persist", "register-hotkeys", "log-hotkeys", "register-routines", "sync-drafts"], calls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RunSaveSideEffects_WhenRelevantSettingsUnchanged_SkipsExpensiveSideEffects()
    {
        var calls = new List<string>();
        var previous = new Settings
        {
            Theme = AppTheme.Dark,
            Overlay = new OverlaySettings
            {
                Enabled = true,
                Position = OverlayPosition.BottomRight,
                DurationSeconds = 3.0
            },
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchRoles = ["Multimedia", "Communications", "Console"],
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Shift+Alt+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchRoles = ["Multimedia", "Communications", "Console"],
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Shift+Alt+I"
                }
            }
        };
        var current = AppSettingsWorkflowCoordinator.CloneSettings(previous);
        current.DeviceSwitching.Output.CycleDevices =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" }
        ];

        SaveSettingsSideEffectResult result = AppSettingsEffectsCoordinator.RunSaveSideEffects(
            previous,
            current,
            disconnectedOutput: [],
            disconnectedInput: [],
            persistUiState: () => calls.Add("persist"),
            registerSwitchHotkeys: () =>
            {
                calls.Add("register-switch-hotkeys");
                return new SwitchHotkeyRegistrationResult(true, true, true, true);
            },
            logSwitchHotkeyResults: _ => calls.Add("log-switch-hotkeys"),
            updateAudioConfiguration: _ => calls.Add("update-audio"),
            updateOverlayState: _ => calls.Add("update-overlay"),
            getSwitchHotkeyRegistrationWarnings: _ => ["switch warning"]);

        Assert.Equal(["persist"], calls);
        Assert.Empty(result.Warnings);
    }
}
