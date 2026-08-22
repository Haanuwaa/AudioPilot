using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeviceDragReorderingUpdatesCycleOrderAndSelection(bool output)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var collection = output ? model.OutputCycleDevices : model.InputCycleDevices;
            collection.Clear();
            var a = new CycleDevice { Id = "a", Name = "A" };
            var b = new CycleDevice { Id = "b", Name = "B" };
            var c = new CycleDevice { Id = "c", Name = "C" };
            collection.Add(a);
            collection.Add(b);
            collection.Add(c);
            var command = output ? model.ReorderOutputCycleDevicesCommand : model.ReorderInputCycleDevicesCommand;
            command.Execute(new ListReorderRequest([a], 3));
            Assert.Equal(new[] { b, c, a }, collection);
            Assert.Equal([1, 2, 3], collection.Select(device => device.DisplayOrder));
            Assert.Equal(2, output ? model.SelectedOutputCycleIndex : model.SelectedInputCycleIndex);
        });
    }

    [Fact]
    public void RoutineDragReorderingTracksSavedOrder_AndNoOpLeavesItClean()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            Settings cached = BuildCachedSettings();
            cached.Routines.Items =
            [
                new AudioRoutine { Id = "a", Name = "A" },
                new AudioRoutine { Id = "b", Name = "B" },
                new AudioRoutine { Id = "c", Name = "C" },
            ];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            var a = model.Routines[0];
            var b = model.Routines[1];
            var c = model.Routines[2];
            model.ReorderRoutinesCommand.Execute(new ListReorderRequest([a], 0));
            Assert.False(model.HasUnsavedRoutineChanges);
            model.ReorderRoutinesCommand.Execute(new ListReorderRequest([a, c], 3));
            Assert.Equal(new[] { b, a, c }, model.Routines);
            Assert.Equal([1, 2, 3], model.Routines.Select(routine => routine.DisplayOrder));
            Assert.Equal(1, model.SelectedRoutineIndex);
            Assert.True(model.HasUnsavedRoutineChanges);
            model.ReorderRoutinesCommand.Execute(new ListReorderRequest([a], 0));
            Assert.Equal(new[] { a, b, c }, model.Routines);
            Assert.False(model.HasUnsavedRoutineChanges);
            Assert.False(model.HasRoutineEdits());
        });
    }

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("routines")]
    public void DragReordering_AutoSavesFinalGroupOrder(string list)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            cached.DeviceSwitching.Output.HotkeysEnabled = false;
            cached.DeviceSwitching.Input.HotkeysEnabled = false;
            cached.DeviceSwitching.Output.CycleDevices =
            [
                new CycleDevice { Id = "out-a", Name = "A" },
                new CycleDevice { Id = "out-b", Name = "B" },
                new CycleDevice { Id = "out-c", Name = "C" },
            ];
            cached.DeviceSwitching.Input.CycleDevices =
            [
                new CycleDevice { Id = "in-a", Name = "A" },
                new CycleDevice { Id = "in-b", Name = "B" },
                new CycleDevice { Id = "in-c", Name = "C" },
            ];
            cached.Routines.Items =
            [
                new AudioRoutine { Id = "a", Name = "A", MasterVolumePercent = 10, Enabled = false },
                new AudioRoutine { Id = "b", Name = "B", MasterVolumePercent = 20, Enabled = false },
                new AudioRoutine { Id = "c", Name = "C", MasterVolumePercent = 30, Enabled = false },
            ];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            harness.SettingsService.SaveSettings(cached);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            long revision = model.GetAutoSaveDirtyRevisionForTests();

            switch (list)
            {
                case "output":
                    model.ReorderOutputCycleDevicesCommand.Execute(new ListReorderRequest([model.OutputCycleDevices[0], model.OutputCycleDevices[2]], 3));
                    break;
                case "input":
                    model.ReorderInputCycleDevicesCommand.Execute(new ListReorderRequest([model.InputCycleDevices[0], model.InputCycleDevices[2]], 3));
                    break;
                case "routines":
                    model.ReorderRoutinesCommand.Execute(new ListReorderRequest([model.Routines[0], model.Routines[2]], 3));
                    break;
            }

            Assert.True(model.GetAutoSaveDirtyRevisionForTests() > revision);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(list == "output" ? ["out-b", "out-a", "out-c"] : ["out-a", "out-b", "out-c"], persisted.DeviceSwitching.Output.CycleDevices.Select(device => device.Id));
            Assert.Equal(list == "input" ? ["in-b", "in-a", "in-c"] : ["in-a", "in-b", "in-c"], persisted.DeviceSwitching.Input.CycleDevices.Select(device => device.Id));
            Assert.Equal(list == "routines" ? ["b", "a", "c"] : ["a", "b", "c"], persisted.Routines.Items.Select(routine => routine.Id));
            Assert.False(model.HasUnsavedRoutineChanges);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.Empty(harness.Messages.WarningMessages);
            Assert.Empty(harness.Messages.SuccessMessages);
            Assert.Empty(harness.OverlayMessages);
        });
    }
}
