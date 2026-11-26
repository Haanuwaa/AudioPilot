using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkRoutineEditsRefreshConflictsAndScheduleOneAutoSavePerEdit(bool conflicting)
    {
        using var clock = new ManualTimeProvider();
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true, autoSaveTimeProvider: clock);
            var cached = new Settings();
            cached.App.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            cached.Routines.Items = [Create("quiet", 20, "Player"), Create("loud", 80, conflicting ? "Player" : "Game")];
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            var model = harness.ViewModel;
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            CompleteAutoSave(model, clock);
            foreach (AudioRoutine routine in model.Routines) model.SelectedRoutines.Add(routine);
            Assert.All(model.Routines, routine => Assert.Equal(conflicting, routine.HasConflict));

            long before = model.GetAutoSaveDirtyRevisionForTests();
            model.DisableSelectedRoutines();
            Assert.Equal(before + 1, model.GetAutoSaveDirtyRevisionForTests());
            Assert.All(model.Routines, routine => { Assert.False(routine.Enabled); Assert.False(routine.HasConflict); });
            CompleteAutoSave(model, clock);
            Assert.All(harness.SettingsService.LoadSettings().Routines.Items, routine => Assert.False(routine.Enabled));

            before = model.GetAutoSaveDirtyRevisionForTests();
            model.EnableSelectedRoutines();
            Assert.Equal(before + 1, model.GetAutoSaveDirtyRevisionForTests());
            Assert.All(model.Routines, routine => { Assert.True(routine.Enabled); Assert.Equal(conflicting, routine.HasConflict); });
            CompleteAutoSave(model, clock);
            Assert.All(harness.SettingsService.LoadSettings().Routines.Items, routine => Assert.Equal(!conflicting, routine.Enabled));
            Assert.Equal(conflicting, model.HasUnsavedRoutineChanges);
        });

        static AudioRoutine Create(string name, int volume, string app) => new()
        {
            Id = name,
            Name = name,
            MasterVolumePercent = volume,
            Triggers = [new() { Kind = RoutineTriggerKind.Application, AppPath = $@"C:\Apps\{app}.exe" }],
        };
    }

    [Fact]
    public void EditingAnUnrelatedRoutinePreservesShortcutRegistrationWarnings()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            model.Routines.Add(new() { Name = "Shortcut", Hotkey = "Ctrl+Alt+R", MasterVolumePercent = 20 });
            model.Routines.Add(new() { Name = "Other", MasterVolumePercent = 50 });
            var shortcut = model.Routines[0];
            shortcut.HotkeyWarningKind = HotkeyWarningKind.ExternalConflict;
            shortcut.HotkeyWarningSummary = "Already registered";

            model.Routines[1].Name = "Renamed";

            Assert.Equal(HotkeyWarningKind.ExternalConflict, shortcut.HotkeyWarningKind);
            Assert.Equal("Already registered", shortcut.HotkeyWarningSummary);
        });
    }

    [Fact]
    public void ScheduleMonitoring_UsesSavedRoutinesUntilDraftChangesAreApplied()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var cached = new Settings();
            var routine = new AudioRoutine
            {
                Id = "scheduled",
                Name = "Scheduled",
                MasterVolumePercent = 50,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(1)),
            };
            harness.SetCachedSettings(cached);
            var model = harness.ViewModel;
            var scheduler = TestPrivateAccess.GetField<Lazy<AudioPilot.Coordinators.ScheduleTriggerCoordinator>>(TestPrivateAccess.GetField<AudioPilot.Services.Routines.RoutineRuntimeCoordinator>(model, "_routineRuntime"), "_scheduleTriggerCoordinator");
            model.Routines.Add(routine.Clone());
            Assert.False(scheduler.IsValueCreated);

            cached.Routines.Items = [routine.Clone()];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            Timer timer = TestPrivateAccess.GetField<Timer>(scheduler.Value, "_timer");
            model.Routines.Clear();
            Assert.Same(timer, TestPrivateAccess.GetField<Timer>(scheduler.Value, "_timer"));

            cached.Routines.Items = [];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            Assert.Null(TestPrivateAccess.GetField<Timer?>(scheduler.Value, "_timer"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingUnsavedRoutine_RestoresCleanEmptyState(bool autoSave)
    {
        using var autoSaveClock = new ManualTimeProvider();
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: autoSave, autoSaveTimeProvider: autoSaveClock);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.App.AutoSaveEnabled = autoSave;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            CompleteAutoSave(model, autoSaveClock);
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
            CompleteAutoSave(model, autoSaveClock);
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
