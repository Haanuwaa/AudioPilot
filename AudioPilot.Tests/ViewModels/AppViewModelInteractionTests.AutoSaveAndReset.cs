using System.Text.Json;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoSave_ToggleStaysManual_AndImportWarnsAboutItsUnsavedDraft(bool enabled)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = enabled;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            model.SettingsAutoSaveEnabledDraft = !enabled;
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "toggle"));
            string importPath = Path.Combine(_workspace.Root, "declined.json");
            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            TestPrivateAccess.RunTaskOnDispatcher(model.ImportSettingsForTestsAsync());

            Assert.Equal(enabled, harness.SettingsService.LoadSettings().Miscellaneous.AutoSaveEnabled);
            Assert.Equal(!enabled, model.SettingsAutoSaveEnabledDraft);
            Assert.Equal(DialogText.Messages.BuildImportSettingsReplaceConfirmation(importPath, discardUnsavedEdits: true),
                Assert.Single(harness.Messages.YesNoMessages).message);
            TestPrivateAccess.RunTaskOnDispatcher(model.ApplySettingsForTestsAsync());
            Assert.Equal(!enabled, harness.SettingsService.LoadSettings().Miscellaneous.AutoSaveEnabled);
        });
    }

    [Fact]
    public void ImportSettings_CancelledFileSelection_ResumesPendingAutoSave()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            AppViewModel.ImportSettingsDialogForTests = _ =>
            {
                Assert.True(model.IsApplyingSettings);
                TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "file-dialog"));
                Assert.Equal(cached.Overlay.DurationSeconds, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
                return (false, string.Empty);
            };

            TestPrivateAccess.RunTaskOnDispatcher(model.ImportSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(3.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
            Assert.Empty(harness.Messages.Requests);
            Assert.False(model.IsApplyingSettings);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImportSettings_PausesAutoSaveDuringConfirmation_AndResumesRetainedEdits(bool confirm, bool invalidFile)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            string importPath = Path.Combine(_workspace.Root, "pending-import.json");
            var imported = cached.Clone();
            imported.Overlay.DurationSeconds = 5;
            if (invalidFile) File.WriteAllText(importPath, "{");
            else SettingsTransferService.ExportSettings(imported, importPath);
            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            var confirmation = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Messages.PendingConfirmation = confirmation.Task;
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            Task import = model.ImportSettingsForTestsAsync();
            bool applyingDuringConfirmation = model.IsApplyingSettings;
            double durationDuringConfirmation;
            try
            {
                TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "during-import"));
                durationDuringConfirmation = harness.SettingsService.LoadSettings().Overlay.DurationSeconds;
            }
            finally
            {
                confirmation.TrySetResult(confirm ? AppDialogResult.Confirmed : AppDialogResult.Declined);
            }
            TestPrivateAccess.RunTaskOnDispatcher(import.WaitAsync(TimeSpan.FromSeconds(10)));
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(applyingDuringConfirmation);
            Assert.Equal(cached.Overlay.DurationSeconds, durationDuringConfirmation);
            Assert.Equal(confirm && !invalidFile ? 5 : 3.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
            Assert.False(model.IsApplyingSettings);
            Assert.Equal(invalidFile && confirm ? 1 : 0, harness.Messages.ErrorMessages.Count);
        });
    }

    [Theory]
    [InlineData(nameof(AppViewModel.SettingsRunAtStartupDraft), true)]
    [InlineData(nameof(AppViewModel.SettingsThemeDraft), AppTheme.Light)]
    [InlineData(nameof(AppViewModel.SettingsPreserveAudioLevelsDraft), false)]
    [InlineData(nameof(AppViewModel.SettingsAutoScrollToMixerOnRestoreDraft), false)]
    [InlineData(nameof(AppViewModel.SettingsOverlayEnabledDraft), false)]
    [InlineData(nameof(AppViewModel.SettingsBluetoothReconnectEnabledDraft), false)]
    [InlineData(nameof(AppViewModel.SettingsDeviceReferenceFileModeDraft), DeviceReferenceFileMode.Hashed)]
    public void AutoSave_PersistsStandaloneSettingsDraftChanges(string propertyName, object value)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher,
                startupService: InMemoryStartupTaskStore.CreateStartupService());
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            var property = typeof(AppViewModel).GetProperty(propertyName)!;
            Assert.NotEqual(value, property.GetValue(model));
            property.SetValue(model, value);

            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "standalone-draft"));

            Settings saved = harness.SettingsService.LoadSettings();
            object actual = propertyName switch
            {
                nameof(AppViewModel.SettingsRunAtStartupDraft) => saved.RunAtStartup,
                nameof(AppViewModel.SettingsThemeDraft) => saved.Theme,
                nameof(AppViewModel.SettingsPreserveAudioLevelsDraft) => saved.DeviceSwitching.PreserveAudioLevels,
                nameof(AppViewModel.SettingsAutoScrollToMixerOnRestoreDraft) => saved.Miscellaneous.AutoScrollToMixerOnRestore,
                nameof(AppViewModel.SettingsOverlayEnabledDraft) => saved.Overlay.Enabled,
                nameof(AppViewModel.SettingsBluetoothReconnectEnabledDraft) => saved.DeviceSwitching.BluetoothReconnectEnabled,
                nameof(AppViewModel.SettingsDeviceReferenceFileModeDraft) => saved.Miscellaneous.DeviceReferenceFileMode,
                _ => throw new InvalidOperationException(propertyName)
            };
            Assert.Equal(value, actual);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

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
            model.SettingsKeepMuteStateWhenSwitchingDraft = false;
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
            Assert.False(model.SettingsKeepMuteStateWhenSwitchingDraft);
            Assert.Equal(2.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
            Assert.False(harness.SettingsService.LoadSettings().DeviceSwitching.KeepMuteStateWhenSwitching);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(autoSave ? 3.5 : 2.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
            Assert.False(harness.SettingsService.LoadSettings().DeviceSwitching.KeepMuteStateWhenSwitching);
            Assert.False(model.KeepMuteStateWhenSwitching);
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

            AppDialogRequest confirmation = Assert.Single(harness.Messages.Requests);
            Assert.Equal(AppDialogResult.Declined, Assert.Single(confirmation.Actions, action => action.IsDefault).Result);
            Assert.True(Assert.Single(confirmation.Actions, action => action.Result == AppDialogResult.Declined).IsCancel);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReset_PreservesLiveConfiguration_AndRestoresScheduledStartup(bool lockBackup)
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
            string settingsPath = harness.SettingsService.GetSettingsPath();
            string originalJson = File.ReadAllText(settingsPath);
            string backupPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "backups", "settings.json.bak");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, originalJson);
            using var locked = File.Open(lockBackup ? backupPath : settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            harness.ViewModel.ResetToDefaultsCommand.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)harness.ViewModel.ResetToDefaultsCommand).LastExecutionTaskForTests.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Single(harness.Messages.ErrorMessages);
            Assert.False(harness.ViewModel.IsResettingSettings);
            Assert.Equal(AppTheme.Dark, harness.ViewModel.Theme);
            Assert.Equal("saved", Assert.Single(harness.ViewModel.Routines).Id);
            Assert.Equal("existing task", taskStore.Xml);
            Assert.True(harness.SettingsService.SettingsFileExists());
            Assert.Equal(originalJson, File.ReadAllText(settingsPath));
            Assert.Equal(originalJson, File.ReadAllText(backupPath));
        });
    }

    [Fact]
    public void ResetToDefaults_RemovesRecoveryBackups_WhenNoSettingsFileOrDraftsExist()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, startupService: InMemoryStartupTaskStore.CreateStartupService());
            harness.SetCachedSettings(new Settings());
            harness.SettingsService.DeleteSettingsFiles();
            string backupPath = Path.Combine(Path.GetDirectoryName(harness.SettingsService.GetSettingsPath())!, "backups", "settings.json.bak");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, "invalid recovery data");
            Assert.False(File.Exists(harness.SettingsService.GetSettingsPath()));
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            harness.ViewModel.ResetToDefaultsCommand.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(((RelayCommand)harness.ViewModel.ResetToDefaultsCommand).LastExecutionTaskForTests);

            Assert.Single(harness.Messages.YesNoMessages);
            Assert.Empty(harness.Messages.InformationMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.False(File.Exists(backupPath));
            Assert.False(harness.SettingsService.SettingsFileExists());
        });
    }
}
