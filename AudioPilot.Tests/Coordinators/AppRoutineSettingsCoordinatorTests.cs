using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineSettingsCoordinatorTests
{
    [Fact]
    public void BuildSavedRoutineSettings_PreservesCachedNonRoutineSettings_AndClonesRoutines()
    {
        Settings cached = new()
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-1", Name = "Speakers" },
                    ]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-1", Name = "Mic" },
                    ]
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine { Id = "cached-routine", Name = "Cached" },
                ]
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                }
            }
        };

        List<AudioRoutine> routines =
        [
            new AudioRoutine { Id = "routine-1", Name = "Desk", DisplayOrder = 5 },
            new AudioRoutine { Id = "routine-2", Name = "Gaming", DisplayOrder = 9 },
        ];

        Settings built = AppRoutineSettingsCoordinator.BuildSavedRoutineSettings(cached, routines, [], []);

        Assert.Equal(AppTheme.Dark, built.Theme);
        Assert.True(built.RunAtStartup);
        Assert.Equal("out-1", Assert.Single(built.DeviceSwitching.Output.CycleDevices).Id);
        Assert.Equal("in-1", Assert.Single(built.DeviceSwitching.Input.CycleDevices).Id);
        Assert.Equal("Ctrl+Alt+H", built.Hotkeys.App.ToggleAppVisibility);
        Assert.Collection(
            built.Routines.Items,
            first =>
            {
                Assert.Equal("routine-1", first.Id);
                Assert.Equal(1, first.DisplayOrder);
            },
            second =>
            {
                Assert.Equal("routine-2", second.Id);
                Assert.Equal(2, second.DisplayOrder);
            });
        Assert.NotSame(routines[0], built.Routines.Items[0]);
    }

    [Fact]
    public void BuildSavedRoutineSettings_ReconcilesRoutineTargetsAgainstAvailableDevices()
    {
        Settings cached = new();
        List<AudioRoutine> routines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                OutputDeviceId = "stale-out",
                OutputDeviceName = "Desk Speakers",
                InputDeviceId = "in-1",
                InputDeviceName = "Old Mic",
            },
        ];

        Settings built = AppRoutineSettingsCoordinator.BuildSavedRoutineSettings(
            cached,
            routines,
            [new CycleDevice { Id = "fresh-out", Name = "Desk Speakers (USB)" }],
            [new CycleDevice { Id = "in-1", Name = "Renamed Mic" }]);

        AudioRoutine routine = Assert.Single(built.Routines.Items);
        Assert.Equal("fresh-out", routine.OutputDeviceId);
        Assert.Equal("Desk Speakers (USB)", routine.OutputDeviceName);
        Assert.Equal("in-1", routine.InputDeviceId);
        Assert.Equal("Renamed Mic", routine.InputDeviceName);
    }

    [Fact]
    public void RunSaveSideEffects_PersistsRegistersAndAppliesInOrder()
    {
        Settings settings = new()
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine { Id = "routine-1", Name = "Desk" },
                ]
            },
        };
        List<string> calls = [];

        AppRoutineSettingsCoordinator.RunSaveSideEffects(
            settings,
            persistUiState: () => calls.Add("persist"),
            registerRoutineHotkeys: _ => calls.Add("register"),
            applyRoutinesFromSettings: _ => calls.Add("apply"));

        Assert.Equal(["persist", "register", "apply"], calls);
    }
}
