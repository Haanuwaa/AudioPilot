using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSettingsWorkflowCoordinatorTests
{
    [Fact]
    public void BuildAppliedSettings_PreservesCyclesAndRoutines_AndAppliesSelectedDraftSettings()
    {
        Settings cached = BuildCachedSettings();

        Settings built = AppSettingsWorkflowCoordinator.BuildAppliedSettings(
            cached,
            new ApplySettingsBuildInput(
                OutputReverseSwitchHotkey: "Ctrl+Alt+Shift+O",
                OutputHotkeysEnabled: false,
                InputReverseSwitchHotkey: "Ctrl+Alt+Shift+I",
                InputHotkeysEnabled: true,
                                AdditionalStandaloneHotkeyKeys: ["PrintScreen"],
                OutputSwitchRoles: ["Console"],
                InputSwitchRoles: ["Communications"],
                AutoSaveEnabled: true,
                PlayDialogSounds: false,
                RunAtStartup: true,
                ToggleAppVisibilityHotkey: "Ctrl+Alt+H",
                ShowCurrentTrackHotkey: "Ctrl+Alt+Y",
                PlayPauseHotkey: "Ctrl+Alt+P",
                NextTrackHotkey: "Ctrl+Alt+.",
                PreviousTrackHotkey: "Ctrl+Alt+,",
                MuteMicHotkey: "Ctrl+Alt+M",
                MuteSoundHotkey: "Ctrl+Alt+S",
                DeafenHotkey: "Ctrl+Alt+D",
                ListenToInputHotkey: "Ctrl+Alt+L",
                MasterVolumeUpHotkey: "Ctrl+Alt+PageUp",
                MasterVolumeDownHotkey: "Ctrl+Alt+PageDown",
                MicVolumeUpHotkey: "Ctrl+Alt+Home",
                MicVolumeDownHotkey: "Ctrl+Alt+End",
                MasterVolumeStepPercent: 7,
                MicVolumeStepPercent: 9,
                ListenMonitorOutputDeviceId: "monitor-out",
                ListenMonitorOutputDeviceName: "Monitor Speakers",
                AvailableOutputDevices: [],
                PreserveAudioLevels: false,
                BluetoothReconnectEnabled: false,
                DeviceReferenceFileMode: DeviceReferenceFileMode.Hashed,
                OverlayEnabled: false,
                Theme: AppTheme.Dark,
                LogLevel: "Trace",
                RedactLogContent: false,
                AutoScrollToMixerOnRestore: false,
                OverlayPosition: OverlayPosition.Center,
                OverlayDurationSeconds: 5.5));

        Assert.Equal(cached.DeviceSwitching.Output.CycleDevices[0].Id, built.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Equal(cached.DeviceSwitching.Input.CycleDevices[0].Id, built.DeviceSwitching.Input.CycleDevices[0].Id);
        Assert.Equal(cached.Routines.Items[0].Id, built.Routines.Items[0].Id);
        Assert.Equal(["Console"], built.DeviceSwitching.Output.SwitchRoles);
        Assert.Equal(["Communications"], built.DeviceSwitching.Input.SwitchRoles);
        Assert.Equal(["PrintScreen"], built.Hotkeys.Global.AdditionalStandaloneKeys);
        Assert.Equal("monitor-out", built.Hotkeys.Listen.MonitorOutputDeviceId);
        Assert.Equal("Monitor Speakers", built.Hotkeys.Listen.MonitorOutputDeviceName);
        Assert.True(built.Miscellaneous.AutoSaveEnabled);
        Assert.False(built.Miscellaneous.PlayDialogSounds);
        Assert.Equal(OverlayPosition.Center, built.Overlay.Position);
        Assert.Equal(5.5, built.Overlay.DurationSeconds);
        Assert.False(built.Miscellaneous.RedactLogContent);
    }

    [Fact]
    public void BuildAppliedSettings_ReconcilesListenMonitorOutputAgainstAvailableDevices()
    {
        Settings cached = BuildCachedSettings();

        Settings built = AppSettingsWorkflowCoordinator.BuildAppliedSettings(
            cached,
            new ApplySettingsBuildInput(
                OutputReverseSwitchHotkey: "",
                OutputHotkeysEnabled: true,
                InputReverseSwitchHotkey: "",
                InputHotkeysEnabled: true,
                AdditionalStandaloneHotkeyKeys: [],
                OutputSwitchRoles: [],
                InputSwitchRoles: [],
                AutoSaveEnabled: false,
                PlayDialogSounds: true,
                RunAtStartup: false,
                ToggleAppVisibilityHotkey: cached.Hotkeys.App.ToggleAppVisibility,
                ShowCurrentTrackHotkey: string.Empty,
                PlayPauseHotkey: string.Empty,
                NextTrackHotkey: string.Empty,
                PreviousTrackHotkey: string.Empty,
                MuteMicHotkey: string.Empty,
                MuteSoundHotkey: string.Empty,
                DeafenHotkey: string.Empty,
                ListenToInputHotkey: string.Empty,
                MasterVolumeUpHotkey: string.Empty,
                MasterVolumeDownHotkey: string.Empty,
                MicVolumeUpHotkey: string.Empty,
                MicVolumeDownHotkey: string.Empty,
                MasterVolumeStepPercent: 5,
                MicVolumeStepPercent: 5,
                ListenMonitorOutputDeviceId: "missing-monitor",
                ListenMonitorOutputDeviceName: "Desk Speakers",
                AvailableOutputDevices: [new CycleDevice { Id = "fresh-monitor", Name = "Desk Speakers (USB)" }],
                PreserveAudioLevels: true,
                BluetoothReconnectEnabled: true,
                DeviceReferenceFileMode: DeviceReferenceFileMode.Off,
                OverlayEnabled: true,
                Theme: AppTheme.System,
                LogLevel: "Info",
                RedactLogContent: true,
                AutoScrollToMixerOnRestore: true,
                OverlayPosition: OverlayPosition.TopRight,
                OverlayDurationSeconds: 2.0));

        Assert.Equal("fresh-monitor", built.Hotkeys.Listen.MonitorOutputDeviceId);
        Assert.Equal("Desk Speakers (USB)", built.Hotkeys.Listen.MonitorOutputDeviceName);
    }

    [Fact]
    public void BuildSavedSettings_AppliesEditableFields_AndPreservesNonEditableFallbacks()
    {
        Settings cached = BuildCachedSettings();
        SaveEditState editState = new(
            CurrentOutputHotkey: "Ctrl+Alt+1",
            CurrentInputHotkey: "Ctrl+Alt+2",
            OutputEdited: true,
            InputEdited: true,
            OutputCleared: false,
            InputCleared: true);

        Settings built = AppSettingsWorkflowCoordinator.BuildSavedSettings(
            cached,
            new SaveSettingsBuildInput(
                OutputCycleDevices: [new CycleDevice { Id = "new-out", Name = "Desk" }],
                InputCycleDevices: [new CycleDevice { Id = "new-in", Name = "Mic" }],
                AvailableOutputDevices: [],
                AvailableInputDevices: [],
                EditState: editState,
                CanWriteOutput: true,
                CanWriteInput: false,
                    AdditionalStandaloneHotkeyKeys: ["Home"],
                CurrentOutputReverseHotkey: "Ctrl+Alt+Shift+1",
                CurrentInputReverseHotkey: "Ctrl+Alt+Shift+2",
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: false,
                RunAtStartup: true,
                PreserveAudioLevels: false,
                OverlayEnabled: false,
                OverlayPosition: OverlayPosition.BottomCenter,
                OverlayDurationSeconds: 3.5,
                Theme: AppTheme.Light,
                RedactLogContent: false));

        Assert.Equal("new-out", built.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Empty(built.DeviceSwitching.Input.CycleDevices);
        Assert.Equal("Ctrl+Alt+1", built.DeviceSwitching.Output.SwitchHotkey);
        Assert.Equal("Ctrl+Alt+2", built.DeviceSwitching.Input.SwitchHotkey);
        Assert.Equal(cached.Hotkeys.App.ToggleAppVisibility, built.Hotkeys.App.ToggleAppVisibility);
        Assert.Equal(cached.Miscellaneous.LogLevel, built.Miscellaneous.LogLevel);
        Assert.Equal(cached.Miscellaneous.BluetoothReconnectEnabled, built.Miscellaneous.BluetoothReconnectEnabled);
        Assert.Equal(["Home"], built.Hotkeys.Global.AdditionalStandaloneKeys);
        Assert.False(built.Miscellaneous.RedactLogContent);
    }

    [Fact]
    public void BuildSavedSettings_ReconcilesCycleDevicesAgainstAvailableDevices()
    {
        Settings cached = BuildCachedSettings();
        SaveEditState editState = new(
            CurrentOutputHotkey: "Ctrl+Alt+1",
            CurrentInputHotkey: "Ctrl+Alt+2",
            OutputEdited: true,
            InputEdited: true,
            OutputCleared: false,
            InputCleared: false);

        Settings built = AppSettingsWorkflowCoordinator.BuildSavedSettings(
            cached,
            new SaveSettingsBuildInput(
                OutputCycleDevices: [new CycleDevice { Id = "stale-out", Name = "Desk Speakers" }],
                InputCycleDevices: [new CycleDevice { Id = "in-1", Name = "Old Mic" }],
                AvailableOutputDevices: [new CycleDevice { Id = "fresh-out", Name = "Desk Speakers (USB)" }],
                AvailableInputDevices: [new CycleDevice { Id = "in-1", Name = "Renamed Mic" }],
                EditState: editState,
                CanWriteOutput: true,
                CanWriteInput: true,
                    AdditionalStandaloneHotkeyKeys: [],
                CurrentOutputReverseHotkey: "",
                CurrentInputReverseHotkey: "",
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                RunAtStartup: false,
                PreserveAudioLevels: true,
                OverlayEnabled: true,
                OverlayPosition: OverlayPosition.TopRight,
                OverlayDurationSeconds: 2.0,
                Theme: AppTheme.System,
                RedactLogContent: true));

        Assert.Equal("fresh-out", Assert.Single(built.DeviceSwitching.Output.CycleDevices).Id);
        Assert.Equal("Desk Speakers (USB)", built.DeviceSwitching.Output.CycleDevices[0].Name);
        Assert.Equal("in-1", Assert.Single(built.DeviceSwitching.Input.CycleDevices).Id);
        Assert.Equal("Renamed Mic", built.DeviceSwitching.Input.CycleDevices[0].Name);
    }

    [Fact]
    public void BuildSavedSettings_PreservesDisabledSwitchHotkeys_WhenCycleIsCleared()
    {
        Settings cached = BuildCachedSettings();
        SaveEditState editState = new(
            CurrentOutputHotkey: "Ctrl+Alt+1",
            CurrentInputHotkey: string.Empty,
            OutputEdited: true,
            InputEdited: false,
            OutputCleared: true,
            InputCleared: false);

        Settings built = AppSettingsWorkflowCoordinator.BuildSavedSettings(
            cached,
            new SaveSettingsBuildInput(
                OutputCycleDevices: [],
                InputCycleDevices: cached.DeviceSwitching.Input.CycleDevices,
                AvailableOutputDevices: [],
                AvailableInputDevices: [],
                EditState: editState,
                CanWriteOutput: false,
                CanWriteInput: true,
                AdditionalStandaloneHotkeyKeys: [],
                CurrentOutputReverseHotkey: "Ctrl+Alt+Shift+1",
                CurrentInputReverseHotkey: cached.DeviceSwitching.Input.ReverseSwitchHotkey,
                OutputHotkeysEnabled: false,
                InputHotkeysEnabled: cached.DeviceSwitching.Input.HotkeysEnabled,
                RunAtStartup: cached.RunAtStartup,
                PreserveAudioLevels: cached.Miscellaneous.PreserveAudioLevels,
                OverlayEnabled: cached.Overlay.Enabled,
                OverlayPosition: cached.Overlay.Position,
                OverlayDurationSeconds: cached.Overlay.DurationSeconds,
                Theme: cached.Theme,
                RedactLogContent: cached.Miscellaneous.RedactLogContent));

        Assert.Empty(built.DeviceSwitching.Output.CycleDevices);
        Assert.False(built.DeviceSwitching.Output.HotkeysEnabled);
        Assert.Equal("Ctrl+Alt+1", built.DeviceSwitching.Output.SwitchHotkey);
        Assert.Equal("Ctrl+Alt+Shift+1", built.DeviceSwitching.Output.ReverseSwitchHotkey);
    }

    [Fact]
    public void AreCycleListsEquivalent_ReturnsTrue_WhenSameIdsDifferOnlyByCase()
    {
        var left = new[]
        {
            new CycleDevice { Id = "A", Name = "One" },
            new CycleDevice { Id = "b", Name = "Two" }
        };
        var right = new[]
        {
            new CycleDevice { Id = "a", Name = "Different" },
            new CycleDevice { Id = "B", Name = "Ignored" }
        };

        bool equal = AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(left, right);

        Assert.True(equal);
    }

    [Fact]
    public void AreCycleListsEquivalent_ReturnsFalse_WhenOrderOrCountDiffers()
    {
        var left = new[]
        {
            new CycleDevice { Id = "A", Name = "One" },
            new CycleDevice { Id = "B", Name = "Two" }
        };

        var rightDifferentOrder = new[]
        {
            new CycleDevice { Id = "B", Name = "Two" },
            new CycleDevice { Id = "A", Name = "One" }
        };

        var rightDifferentCount = new[]
        {
            new CycleDevice { Id = "A", Name = "One" }
        };

        Assert.False(AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(left, rightDifferentOrder));
        Assert.False(AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(left, rightDifferentCount));
    }

    [Fact]
    public void CountValidCycleDevices_IgnoresEmptyEntries()
    {
        int count = AppSettingsWorkflowCoordinator.CountValidCycleDevices(
        [
            new CycleDevice { Id = "one", Name = "One" },
            new CycleDevice { Id = " ", Name = "Blank" },
            new CycleDevice { Id = "two", Name = "Two" },
        ]);

        Assert.Equal(2, count);
    }

    [Fact]
    public void BuildSaveEditState_DetectsClearedOutputCycle()
    {
        var cached = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-1", Name = "Speakers" },
                        new CycleDevice { Id = "out-2", Name = "Headset" }
                    ],
                    SwitchHotkey = "Ctrl+Alt+1",
                    HotkeysEnabled = true
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-1", Name = "Mic" }
                    ],
                    SwitchHotkey = "Ctrl+Alt+2",
                    HotkeysEnabled = true
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                }
            }
        };

        SaveEditState state = AppSettingsWorkflowCoordinator.BuildSaveEditState(
            outputCycleDevices: [],
            inputCycleDevices: cached.DeviceSwitching.Input.CycleDevices,
            currentOutputHotkey: "Ctrl+Alt+O",
            currentInputHotkey: "Ctrl+Alt+2",
            outputHotkeysEnabled: true,
            inputHotkeysEnabled: true,
            cachedSettings: cached);

        Assert.True(state.OutputEdited);
        Assert.True(state.OutputCleared);
        Assert.False(state.InputEdited);
    }

    [Fact]
    public void BuildSaveEditState_DetectsToggleOnlyChangesAsEdits()
    {
        var cached = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices = [],
                    SwitchHotkey = "Ctrl+Alt+1",
                    HotkeysEnabled = true
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices = [],
                    SwitchHotkey = "Ctrl+Alt+2",
                    HotkeysEnabled = true
                }
            }
        };

        SaveEditState state = AppSettingsWorkflowCoordinator.BuildSaveEditState(
            outputCycleDevices: cached.DeviceSwitching.Output.CycleDevices,
            inputCycleDevices: cached.DeviceSwitching.Input.CycleDevices,
            currentOutputHotkey: cached.DeviceSwitching.Output.SwitchHotkey,
            currentInputHotkey: cached.DeviceSwitching.Input.SwitchHotkey,
            outputHotkeysEnabled: false,
            inputHotkeysEnabled: false,
            cachedSettings: cached);

        Assert.True(state.OutputEdited);
        Assert.True(state.InputEdited);
    }

    private static Settings BuildCachedSettings()
    {
        return new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "cached-out", Name = "Speakers" },
                        new CycleDevice { Id = "cached-out-2", Name = "Headset" },
                    ],
                    SwitchHotkey = "Ctrl+Alt+1",
                    HotkeysEnabled = true
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "cached-in", Name = "Microphone" },
                    ],
                    SwitchHotkey = "Ctrl+Alt+2",
                    HotkeysEnabled = true
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine { Id = "routine-1", Name = "Routine 1", Enabled = true },
                ]
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                },
                Listen = new HotkeysListenSettings
                {
                    ListenToInput = string.Empty,
                    MonitorOutputDeviceId = string.Empty,
                    MonitorOutputDeviceName = "Cached Monitor"
                }
            },
            Miscellaneous = new MiscellaneousSettings
            {
                LogLevel = "Debug",
                BluetoothReconnectEnabled = true
            },
            Overlay = new OverlaySettings
            {
                Enabled = true,
                Position = OverlayPosition.TopRight,
                DurationSeconds = 2.0
            }
        };
    }
}
