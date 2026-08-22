using System.Text.Json;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySettings_PreservesDraftEditedWhileSaveWasPending(bool autoSave)
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
            model.SettingsOverlayDurationSecondsDraft = "2.5";
            TestPrivateAccess.RunTaskOnDispatcher(model.EnterSettingsWriteLockForTestsAsync());
            Task save;
            try
            {
                save = model.ApplySettingsForTestsAsync();
                Assert.True(model.IsApplyingSettings);
                Assert.False(model.ResetToDefaultsCommand.CanExecute(null));
                model.SettingsOverlayDurationSecondsDraft = "3.5";
            }
            finally { model.ReleaseSettingsWriteLockForTests(); }

            TestPrivateAccess.RunTaskOnDispatcher(save.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("3.5", model.SettingsOverlayDurationSecondsDraft);
            Assert.Equal(2.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(autoSave ? 3.5 : 2.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SaveRoutines_PreservesEditsMadeWhileSaveWasPending(bool autoSave, bool removeRoutine)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: autoSave);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = autoSave;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            cached.Routines.Items = [new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50, Enabled = false }];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            harness.SettingsService.SaveSettings(cached);
            model.Routines[0].Name = "First edit";
            TestPrivateAccess.RunTaskOnDispatcher(model.EnterSettingsWriteLockForTestsAsync());
            Task save;
            try
            {
                save = model.SaveRoutinesAsync();
                Assert.True(model.IsSavingRoutines);
                Assert.False(model.ResetToDefaultsCommand.CanExecute(null));
                if (removeRoutine) model.Routines.Clear();
                else model.Routines[0].Name = "Newer edit";
            }
            finally { model.ReleaseSettingsWriteLockForTests(); }

            TestPrivateAccess.RunTaskOnDispatcher(save.WaitAsync(TimeSpan.FromSeconds(10)));
            if (removeRoutine) Assert.Empty(model.Routines);
            else Assert.Equal("Newer edit", Assert.Single(model.Routines).Name);
            Assert.True(model.HasUnsavedRoutineChanges);
            Assert.Equal("First edit", Assert.Single(harness.SettingsService.LoadSettings().Routines.Items).Name);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            if (removeRoutine) Assert.Empty(harness.SettingsService.LoadSettings().Routines.Items);
            else Assert.Equal(autoSave ? "Newer edit" : "First edit", Assert.Single(harness.SettingsService.LoadSettings().Routines.Items).Name);
            Assert.Equal(!autoSave, model.HasUnsavedRoutineChanges);
        });
    }

    [Fact]
    public void ResetToDefaults_CancelsPendingAutoSave_RestoresCompleteDefaults_AndAllowsNewRoutineSave()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            StartupService startup = InMemoryStartupTaskStore.CreateStartupService();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, startupService: startup, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            cached.DeviceSwitching.Output.ReverseSwitchHotkey = "Ctrl+Alt+F10";
            cached.DeviceSwitching.Input.ReverseSwitchHotkey = "Ctrl+Alt+F11";
            cached.Hotkeys.Media.SeekStepSeconds = 45;
            cached.Routines.Items = [new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50, Enabled = false }];
            harness.SetCachedSettings(cached);
            model.ApplyRoutinesFromSettings(cached.Routines.Items);
            harness.SettingsService.SaveSettings(cached);
            model.Theme = AppTheme.Dark;
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            TestPrivateAccess.RunTaskOnDispatcher(model.EnterSettingsWriteLockForTestsAsync());
            try
            {
                model.ResetToDefaultsCommand.Execute(null);
                Assert.True(model.IsResettingSettings);
                Assert.True(harness.SettingsService.SettingsFileExists());
                Assert.False(model.SaveRoutinesCommand.CanExecute(null));
                Assert.False(model.ApplySettingsCommand.CanExecute(null));
                Assert.False(model.ResetToDefaultsCommand.CanExecute(null));
            }
            finally { model.ReleaseSettingsWriteLockForTests(); }
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)model.ResetToDefaultsCommand).LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(10)));
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.False(model.IsResettingSettings);
            Assert.False(model.IsAutoSaveActive);
            Assert.False(harness.SettingsService.SettingsFileExists());
            Assert.Empty(model.Routines);
            Assert.False(model.HasRoutineEdits());
            Assert.False(model.OutputReverseHotkey.HasMainInput);
            Assert.False(model.InputReverseHotkey.HasMainInput);
            Assert.Equal(JsonSerializer.Serialize(new Settings(), SettingsJson.Options), JsonSerializer.Serialize(model.CurrentSettings, SettingsJson.Options));
            Assert.Empty(harness.Messages.ErrorMessages);

            model.Routines.Add(new AudioRoutine { Id = "new", Name = "New", MasterVolumePercent = 25, Enabled = false });
            TestPrivateAccess.RunTaskOnDispatcher(model.SaveRoutinesAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("new", Assert.Single(harness.SettingsService.LoadSettings().Routines.Items).Id);
        });
    }

    [Fact]
    public void CancelReset_ResumesPendingAutoSave()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            model.Theme = AppTheme.Dark;
            harness.Messages.YesNoResponse = AppDialogResult.Declined;

            model.ResetToDefaultsCommand.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)model.ResetToDefaultsCommand).LastExecutionTaskForTests);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.False(model.IsResettingSettings);
            Assert.True(model.IsAutoSaveActive);
            Assert.Equal(AppTheme.Dark, harness.SettingsService.LoadSettings().Theme);
            Assert.Single(harness.Messages.YesNoMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ResetToDefaults_RecognizesUnsavedSettingsWithoutASettingsFile()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, startupService: InMemoryStartupTaskStore.CreateStartupService());
            harness.SetCachedSettings(new Settings());
            harness.ViewModel.SettingsSeekStepSecondsDraft = "30";
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            harness.ViewModel.ResetToDefaultsCommand.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)harness.ViewModel.ResetToDefaultsCommand).LastExecutionTaskForTests);

            Assert.Single(harness.Messages.YesNoMessages);
            Assert.Empty(harness.Messages.InformationMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.Equal("10s", harness.ViewModel.SettingsSeekStepSecondsDraft);
        });
    }

    [Fact]
    public void FailedReset_PreservesLiveConfiguration_AndRestoresScheduledStartup()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            var taskStore = new InMemoryStartupTaskStore { Xml = "existing task" };
            var startup = new StartupService("Software\\AudioPilot.Tests\\Reset", "AudioPilotTest", logger: null,
                new InMemoryUserRegistryAccessor(), taskStore: taskStore);
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, startupService: startup);
            var cached = new Settings { Theme = AppTheme.Dark, RunAtStartup = true };
            cached.Miscellaneous.UseScheduledStartup = true;
            cached.Routines.Items = [new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50, Enabled = false }];
            harness.SetCachedSettings(cached);
            harness.ViewModel.ApplyRoutinesFromSettings(cached.Routines.Items);
            harness.SettingsService.SaveSettings(cached);
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;
            using var locked = File.Open(harness.SettingsService.GetSettingsPath(), FileMode.Open, FileAccess.Read, FileShare.Read);

            harness.ViewModel.ResetToDefaultsCommand.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)harness.ViewModel.ResetToDefaultsCommand).LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Single(harness.Messages.ErrorMessages);
            Assert.False(harness.ViewModel.IsResettingSettings);
            Assert.Equal(AppTheme.Dark, harness.ViewModel.Theme);
            Assert.Equal("saved", Assert.Single(harness.ViewModel.Routines).Id);
            Assert.Equal("existing task", taskStore.Xml);
            Assert.True(harness.SettingsService.SettingsFileExists());
        });
    }
}
