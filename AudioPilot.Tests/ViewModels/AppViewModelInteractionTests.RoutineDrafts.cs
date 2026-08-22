using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingUnsavedRoutine_RestoresCleanEmptyState(bool autoSave)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: autoSave);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = autoSave;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            string settingsPath = harness.SettingsService.GetSettingsPath();
            DateTime lastWrite = File.GetLastWriteTimeUtc(settingsPath);
            var added = new AudioRoutine { Id = "new", Name = "New routine", MasterVolumePercent = 50 };
            model.Routines.Add(added);
            model.SelectedRoutines.Add(added);
            model.SelectedRoutineIndex = 0;
            Assert.True(model.HasUnsavedRoutineChanges);
            Assert.True(model.SaveRoutinesCommand.CanExecute(null));

            model.RemoveRoutineCommand.Execute(null);

            Assert.Empty(model.Routines);
            Assert.False(model.HasUnsavedRoutineChanges);
            Assert.False(model.HasRoutineEdits());
            Assert.False(model.SaveRoutinesCommand.CanExecute(null));
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(settingsPath));
            Assert.Empty(harness.SettingsService.LoadSettings().Routines.Items);
        });
    }

    [Fact]
    public void RemovingSavedRoutine_KeepsDeletionPending()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var cached = new Settings();
            cached.Routines.Items = [new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50 }];
            harness.SetCachedSettings(cached);
            var model = harness.ViewModel;
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            model.SelectedRoutines.Add(model.Routines[0]);

            model.RemoveRoutineCommand.Execute(null);

            Assert.Empty(model.Routines);
            Assert.True(model.HasUnsavedRoutineChanges);
            Assert.True(model.HasRoutineEdits());
            Assert.True(model.SaveRoutinesCommand.CanExecute(null));
        });
    }

    [Fact]
    public void RemovingUnsavedAddition_PreservesOtherEdits_UntilTheyAreReverted()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var cached = new Settings();
            cached.Routines.Items = [new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50 }];
            harness.SetCachedSettings(cached);
            var model = harness.ViewModel;
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            model.Routines[0].Name = "Edited";
            var added = new AudioRoutine { Id = "new", Name = "New" };
            model.Routines.Add(added);
            model.SelectedRoutines.Add(added);
            model.SelectedRoutineIndex = 1;

            model.RemoveRoutineCommand.Execute(null);

            Assert.True(model.HasUnsavedRoutineChanges);
            model.Routines[0].Name = "Saved";
            Assert.False(model.HasUnsavedRoutineChanges);
            Assert.False(model.HasRoutineEdits());
        });
    }
}
