using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class RoutineMutationCoordinatorTests
{
    [Fact]
    public void Create_AddsValidatedRoutineToSettings()
    {
        Settings settings = new();

        RoutineMutationCoordinator.RoutineMutationResult result = RoutineMutationCoordinator.Create(settings, new AudioRoutine
        {
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            TriggerKind = RoutineTriggerKind.Hotkey,
            Hotkey = "Ctrl+Alt+D",
        });

        Assert.True(result.Success);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal("Desk", routine.Name);
        Assert.Equal("out-1", routine.OutputDeviceId);
    }

    [Fact]
    public void Create_AllowsVolumeOnlyRoutine()
    {
        Settings settings = new();

        RoutineMutationCoordinator.RoutineMutationResult result = RoutineMutationCoordinator.Create(settings, new AudioRoutine
        {
            Name = "Desk",
            Enabled = true,
            MasterVolumePercent = 25,
            TriggerKind = RoutineTriggerKind.Hotkey,
            Hotkey = "Ctrl+Alt+D",
        });

        Assert.True(result.Success);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(25, routine.MasterVolumePercent);
        Assert.True(routine.HasExecutionTarget);
    }

    [Fact]
    public void Update_PreservesExistingRoutineId()
    {
        Settings settings = new()
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
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        TriggerKind = RoutineTriggerKind.Hotkey,
                        Hotkey = "Ctrl+Alt+D"
                    }
                ]
            }
        };

        RoutineMutationCoordinator.RoutineMutationResult result = RoutineMutationCoordinator.Update(settings, "routine-1", new AudioRoutine
        {
            Name = "Desk Updated",
            Enabled = true,
            OutputDeviceId = "out-2",
            OutputDeviceName = "Headset",
            TriggerKind = RoutineTriggerKind.Hotkey,
            Hotkey = "Ctrl+Alt+J",
        });

        Assert.True(result.Success);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal("routine-1", routine.Id);
        Assert.Equal("Desk Updated", routine.Name);
        Assert.Equal("out-2", routine.OutputDeviceId);
    }

    [Fact]
    public void Import_Merge_ReplacesMatchingIdsAndAppendsNewRoutines()
    {
        Settings settings = new()
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
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        TriggerKind = RoutineTriggerKind.Hotkey,
                        Hotkey = "Ctrl+Alt+D"
                    }
                ]
            }
        };

        RoutineMutationCoordinator.RoutineMutationResult result = RoutineMutationCoordinator.Import(settings,
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk Updated",
                Enabled = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
                TriggerKind = RoutineTriggerKind.Hotkey,
                Hotkey = "Ctrl+Alt+J",
            },
            new AudioRoutine
            {
                Id = "routine-2",
                Name = "Mic",
                Enabled = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
                TriggerKind = RoutineTriggerKind.Hotkey,
                Hotkey = "Ctrl+Alt+K",
            }
        ], replaceImport: false);

        Assert.True(result.Success);
        Assert.Equal(2, settings.Routines.Items.Count);
        Assert.Equal("Desk Updated", settings.Routines.Items[0].Name);
        Assert.Equal("routine-2", settings.Routines.Items[1].Id);
    }
}
