using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsValidationServiceTests
{
    [Fact]
    public void Normalize_ReplacesInvalidScheduledTimeZoneWithLocalTimeZone()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-schedule",
                        Name = "Morning",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Scheduled,
                        ScheduleTime = new TimeOnly(8, 0),
                        ScheduleTimeZoneId = "Invalid/TimeZone",
                        OutputDeviceId = "out-1",
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        Assert.Equal(TimeZoneInfo.Local.Id, Assert.Single(settings.Routines.Items).ScheduleTimeZoneId);
    }

    [Fact]
    public void Normalize_AssignsUniqueIdsToDuplicateRoutines()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine { Id = "duplicate", Name = "One", OutputDeviceId = "out-1" },
                    new AudioRoutine { Id = "DUPLICATE", Name = "Two", OutputDeviceId = "out-2" },
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        Assert.Equal(2, settings.Routines.Items.Select(static routine => routine.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("duplicate", settings.Routines.Items[0].Id);
        Assert.NotEqual("DUPLICATE", settings.Routines.Items[1].Id);
    }

    private static readonly string[] ExpectedOutputRoles = ["Console", "Multimedia", "Communications"];
    private static readonly string[] ExpectedInputRoles = ["Multimedia", "Communications", "Console"];

    [Fact]
    public void Normalize_CanonicalizesAndDeduplicatesRoleSelections()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchRoles = ["console", "MULTIMEDIA", "unknown", " communications ", "console"]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchRoles = null!
                }
            }
        };

        SettingsValidationService.Normalize(settings);

        Assert.Equal(ExpectedOutputRoles, settings.DeviceSwitching.Output.SwitchRoles);
        Assert.Equal(ExpectedInputRoles, settings.DeviceSwitching.Input.SwitchRoles);
    }

    [Fact]
    public void Normalize_PreservesPackagedAppRoutineTriggerAndPerAppRouting()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = " routine-1 ",
                        Name = " Spotify ",
                        Enabled = true,
                        UsesApplicationTrigger = true,
                        TriggerAppPath = "  SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify  ",
                        SwitchOutputPerApp = true,
                        OutputDeviceId = " out-1 ",
                        OutputDeviceName = " Speakers "
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal("routine-1", routine.Id);
        Assert.Equal("Spotify", routine.Name);
        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", routine.TriggerAppPath);
        Assert.True(routine.SwitchOutputPerApp);
        Assert.Equal("out-1", routine.OutputDeviceId);
        Assert.Equal("Speakers", routine.OutputDeviceName);
    }

    [Fact]
    public void Normalize_PreservesProcessFocusRoutineMetadata()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = " routine-1 ",
                        Name = " Spotify Focus ",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Application,
                        TriggerAppPath = "  C:\\Apps\\Spotify\\Spotify.exe  ",
                        ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                        ApplicationTriggerTitlePattern = "  playlist  ",
                        ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
                        OutputDeviceId = " out-1 ",
                        OutputDeviceName = " Speakers "
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal("routine-1", routine.Id);
        Assert.Equal("Spotify Focus", routine.Name);
        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", routine.TriggerAppPath);
        Assert.Equal(ApplicationTriggerMode.ProcessFocus, routine.ApplicationTriggerMode);
        Assert.Equal("playlist", routine.ApplicationTriggerTitlePattern);
        Assert.Equal(ApplicationTriggerTitleMatchMode.Regex, routine.ApplicationTriggerTitleMatchMode);
    }

    [Fact]
    public void Normalize_MigratesLegacyDeviceChangeEnforcement_ToDeviceChangeTrigger()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        EnforceTargetsOnDeviceChange = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.DeviceChange, routine.TriggerKind);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.True(routine.EnforceTargetsOnDeviceChange);
    }

    [Fact]
    public void Normalize_PreservesRoutineVolumeTargets()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = 35,
                        MicVolumePercent = 0
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(35, routine.MasterVolumePercent);
        Assert.Equal(0, routine.MicVolumePercent);
    }

    [Fact]
    public void Normalize_PreservesScheduledRoutineTriggerData()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-schedule",
                        Name = "Morning",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Scheduled,
                        ScheduleTime = new TimeOnly(9, 30),
                        ScheduleDays = [DayOfWeek.Monday, DayOfWeek.Wednesday],
                        ScheduleTimeZoneId = "Pacific Standard Time",
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.Scheduled, routine.TriggerKind);
        Assert.Equal(new TimeOnly(9, 30), routine.ScheduleTime);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], [.. routine.ScheduleDays.OrderBy(static day => (int)day)]);
        Assert.Equal("Pacific Standard Time", routine.ScheduleTimeZoneId);
    }

    [Fact]
    public void Normalize_PreservesWifiRoutineTriggerData()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-wifi",
                        Name = "Office",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Network,
                        TriggerNetworkName = "  Office WiFi  ",
                        NetworkTriggerDirection = NetworkTriggerDirection.Both,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.Network, routine.TriggerKind);
        Assert.Equal("Office WiFi", routine.TriggerNetworkName);
        Assert.Equal(NetworkTriggerDirection.Both, routine.NetworkTriggerDirection);
    }

    [Fact]
    public void Normalize_ClearsNetworkName_ForDisconnectOnlyRoutine()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-network-disconnect",
                        Name = "Leave Network",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Network,
                        TriggerNetworkName = "  Office WiFi  ",
                        NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.Network, routine.TriggerKind);
        Assert.Equal(string.Empty, routine.TriggerNetworkName);
        Assert.Equal(NetworkTriggerDirection.Disconnect, routine.NetworkTriggerDirection);
    }

    [Fact]
    public void ValidateCycle_ReturnsDuplicateAndDisconnectedConfiguredDeviceNames()
    {
        CycleDevice[] configured =
        [
            new CycleDevice { Id = "a", Name = "Speakers" },
            new CycleDevice { Id = "a", Name = "Speakers" },
            new CycleDevice { Id = "b", Name = "Headset" },
            new CycleDevice { Id = "c", Name = "USB DAC" },
        ];

        CycleDevice[] active =
        [
            new CycleDevice { Id = "b", Name = "Headset (Active)" },
        ];

        CycleValidationResult result = SettingsValidationService.ValidateCycle(configured, active);

        Assert.False(result.IsValid);
        Assert.Equal(["Speakers"], result.DuplicateDeviceNames);
        Assert.Equal(["Speakers", "USB DAC"], result.DisconnectedDeviceNames);
    }

    [Fact]
    public void EvaluateCycleSwitchPreflight_ReportsExpectedInputReasons()
    {
        CycleDevice[] configured =
        [
            new CycleDevice { Id = "a", Name = "Mic A" },
            new CycleDevice { Id = "b", Name = "Mic B" },
        ];

        CycleDevice[] active =
        [
            new CycleDevice { Id = "a", Name = "Mic A (Connected)" },
        ];

        CycleSwitchPreflightResult result = SettingsValidationService.EvaluateCycleSwitchPreflight(
            configured,
            active,
            hasDefaultInputDevice: false,
            output: false);

        Assert.False(result.CanSwitch);
        Assert.Equal(2, result.ConfiguredCount);
        Assert.Equal(1, result.ConnectedConfiguredCount);
        Assert.False(result.HasDefaultInputDevice);
        Assert.Equal(["no-alternate-connected-device", AppConstants.Audio.ErrorCodes.CyclePreflight.NoDefaultInputDevice], result.Reasons);
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsInvalidHotkey()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                },
                Listen = new HotkeysListenSettings
                {
                    ListenToInput = "Ctrl+BadToken+L"
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterUp = "Ctrl+BadToken+PageUp"
                }
            }
        };

        CycleDevice[] activeOutputs =
        [
            new CycleDevice { Id = "out-a", Name = "Speakers" },
        ];

        CycleDevice[] activeInputs =
        [
            new CycleDevice { Id = "in-a", Name = "Mic" },
        ];

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, activeOutputs, activeInputs);

        Assert.True(diagnostics.HasWarnings);
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-listen-to-input-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-master-volume-up-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsMissingWifiSsid()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-wifi",
                        Name = "Office",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Network,
                        TriggerNetworkName = string.Empty,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-routine-trigger-network-name-0");
    }

    [Fact]
    public void EvaluateDiagnostics_DoesNotReportMissingNetworkName_ForDisconnectOnlyNetworkRoutine()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-network-disconnect",
                        Name = "Leave Network",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Network,
                        NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                        TriggerNetworkName = string.Empty,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-routine-trigger-network-name-0");
    }

    [Fact]
    public void EvaluateDiagnostics_TreatsMouseHotkeysWithoutModifierAsInvalid()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "WheelUp"
                },
                Media = new HotkeysMediaSettings
                {
                    NextTrack = "Ctrl+MouseX1"
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-toggle-app-visibility-hotkey");
        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-next-track-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_TreatsBareTextKeysAsInvalid_ButAllowsStandaloneFunctionKeys()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "A"
                },
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "F8"
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-toggle-app-visibility-hotkey");
        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-play-pause-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsInvalidShowCurrentTrackHotkey()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                Media = new HotkeysMediaSettings
                {
                    ShowCurrentTrack = "Ctrl+BadToken+I"
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-show-current-track-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsReservedWindowsHotkeys()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Win+A"
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterUp = "Alt+Tab"
                },
                Mute = new HotkeysMuteSettings
                {
                    Mic = "Win+Shift+S",
                    Sound = "Win+Space",
                    Deafen = "Win+P"
                },
                Listen = new HotkeysListenSettings
                {
                    ListenToInput = "Alt+F4"
                },
                Media = new HotkeysMediaSettings
                {
                    NextTrack = "Alt+Space",
                    PreviousTrack = "Win+Left",
                    PlayPause = "Win+S"
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-toggle-app-visibility-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-master-volume-up-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-mute-mic-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-mute-sound-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-deafen-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-listen-to-input-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-next-track-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-previous-track-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-play-pause-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-hotkey-toggle-app-visibility-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_AllowsConfiguredStandaloneKeyExceptions()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "PrintScreen"
                },
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "Home"
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterUp = "Ctrl+Up",
                    MasterDown = "Ctrl+Down"
                },
                Mute = new HotkeysMuteSettings
                {
                    Mic = "Ctrl+Shift+M",
                    Sound = "Ctrl+Shift+S",
                    Deafen = "Ctrl+Shift+D"
                },
                Global = new HotkeysGlobalSettings
                {
                    AdditionalStandaloneKeys = ["PrintScreen"]
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-toggle-app-visibility-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-play-pause-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsInvalidStandaloneKeyAllowlistEntries()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                Global = new HotkeysGlobalSettings
                {
                    AdditionalStandaloneKeys = ["A", "NotAKey"]
                }
            },
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-additional-standalone-hotkey-keys");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsReservedRoutineHotkey()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Name = "Routine 1",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Hotkey,
                        OutputDeviceId = "out-1",
                        Hotkey = "Ctrl+Esc"
                    }
                ]
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "reserved-routine-hotkey-0");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsInvalidVolumeStep()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                Volume = new HotkeysVolumeSettings
                {
                    MasterVolumeStepPercent = 0,
                    MicVolumeStepPercent = 101
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-master-volume-step-percent");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-mic-volume-step-percent");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsDisconnectedCycleDevices()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-missing", Name = "Desk Speakers" }
                    ]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-missing", Name = "USB Mic" }
                    ]
                }
            }
        };

        CycleDevice[] activeOutputs =
        [
            new CycleDevice { Id = "out-a", Name = "Connected Output" },
        ];

        CycleDevice[] activeInputs =
        [
            new CycleDevice { Id = "in-a", Name = "Connected Input" },
        ];

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, activeOutputs, activeInputs);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "output-cycle-disconnected-devices");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "input-cycle-disconnected-devices");
    }

    [Fact]
    public void EvaluateDiagnostics_AllowsVolumeOnlyRoutineTargets()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.Hotkey,
                        Hotkey = "Ctrl+Alt+R",
                        MasterVolumePercent = 50
                    }
                ]
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-routine-target-0");
    }

    [Fact]
    public void Normalize_ClampsRoutineVolumeTargetsIntoRange()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = -1,
                        MicVolumePercent = 101
                    }
                ]
            }
        };

        SettingsValidationService.Normalize(settings);

        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(0, routine.MasterVolumePercent);
        Assert.Equal(100, routine.MicVolumePercent);
    }

    [Fact]
    public void EvaluateDiagnostics_IgnoresDisabledSwitchHotkeyWarnings()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = false,
                    SwitchHotkey = "Ctrl+BadToken+X",
                    ReverseSwitchHotkey = "Ctrl+BadToken+Y"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = false,
                    SwitchHotkey = "Ctrl+BadToken+Z",
                    ReverseSwitchHotkey = "Ctrl+BadToken+Q"
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-output-switch-hotkey");
        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-output-reverse-switch-hotkey");
        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-input-switch-hotkey");
        Assert.DoesNotContain(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-input-reverse-switch-hotkey");
    }

    [Fact]
    public void EvaluateDiagnostics_ReportsOutputAndInputSwitchHotkeyWarningsIndependently()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+BadToken+O",
                    ReverseSwitchHotkey = "Ctrl+BadToken+Shift+O",
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+BadToken+I",
                    ReverseSwitchHotkey = "Ctrl+BadToken+Shift+I",
                }
            }
        };

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []);

        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-output-switch-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-output-reverse-switch-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-input-switch-hotkey");
        Assert.Contains(diagnostics.Warnings, warning => warning.Code == "invalid-hotkey-input-reverse-switch-hotkey");
    }
}
