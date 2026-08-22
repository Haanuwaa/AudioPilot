using System.Reflection;
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PendingStartupAction_DoesNotShowManualSaveFeedbackOrHideDeviceEdits(bool enabled, bool deviceEdit)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var saved = new Settings { RunAtStartup = !enabled };
            harness.SetCachedSettings(saved);
            var model = harness.ViewModel;
            if (deviceEdit) model.InputCycleDevices.Add(new() { Id = "new-input", Name = "Example microphone" });
            model.RunAtStartup = enabled;
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal(enabled, model.SettingsRunAtStartupDraft);
            Assert.Equal(!enabled, TestPrivateAccess.GetField<Settings>(model, "_cachedSettings").RunAtStartup);
            Assert.Equal(TabSaveState.None, model.OutputTabSaveState);
            Assert.Equal(deviceEdit ? TabSaveState.Unsaved : TabSaveState.None, model.InputTabSaveState);
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
            Assert.DoesNotContain("Run at startup", model.InputTabFeedbackText, StringComparison.Ordinal);
            if (deviceEdit) Assert.Contains("Device order", model.InputTabFeedbackText, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("device", false)]
    [InlineData("setting", false)]
    [InlineData("hotkey", false)]
    [InlineData("routine", false)]
    [InlineData("device", true)]
    [InlineData("setting", true)]
    [InlineData("hotkey", true)]
    [InlineData("routine", true)]
    public void ManualSavesConfirmInEitherAutoSaveMode_WhileOnlyManualModeMarksEdits(string edit, bool autoSave)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.Miscellaneous.AutoSaveEnabled = autoSave;
            saved.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(saved);
            harness.SettingsService.SaveSettings(saved);
            TestPrivateAccess.RunTaskOnDispatcher(model.SaveSettingsForTestsAsync());
            Assert.Equal(TabSaveState.Saved, model.OutputTabSaveState);
            if (edit == "device") model.InputCycleDevices.Add(new CycleDevice { Id = "new-input", Name = "New microphone" });
            else if (edit == "setting") model.SettingsOverlayDurationSecondsDraft = "3.5";
            else if (edit == "hotkey") model.SettingsShowCurrentTrackHotkeyDraft = "Ctrl+Alt+Shift+F11";
            else model.Routines.Add(new AudioRoutine { Id = "new", Name = "New routine", Enabled = false, MasterVolumePercent = 50 });
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal(TabSaveState.None, model.OutputTabSaveState);
            Assert.Equal(!autoSave && edit == "device" ? TabSaveState.Unsaved : TabSaveState.None, model.InputTabSaveState);
            Assert.Equal(!autoSave && edit is "setting" or "hotkey" ? TabSaveState.Unsaved : TabSaveState.None, model.SettingsTabSaveState);
            Assert.Equal(!autoSave && edit == "routine" ? TabSaveState.Unsaved : TabSaveState.None, model.RoutinesTabSaveState);
            if (!autoSave)
            {
                string tooltip = edit == "device" ? model.InputTabFeedbackText : edit == "routine" ? model.RoutinesTabFeedbackText : model.SettingsTabFeedbackText;
                Assert.Contains(edit switch { "device" => "Device order", "setting" => "Overlay duration", "hotkey" => "Current track hotkey", _ => "Added routines" }, tooltip, StringComparison.Ordinal);
            }
            TestPrivateAccess.RunTaskOnDispatcher(edit switch
            {
                "setting" or "hotkey" => model.ApplySettingsForTestsAsync(),
                "routine" => model.SaveRoutinesAsync(),
                _ => model.SaveSettingsForTestsAsync(),
            });
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.NotEqual(TabSaveState.Unsaved, state));
            Assert.Equal(edit == "device" ? TabSaveState.None : TabSaveState.Saved, edit switch
            {
                "setting" or "hotkey" => model.SettingsTabSaveState,
                "routine" => model.RoutinesTabSaveState,
                _ => model.InputTabSaveState,
            });
            Assert.Equal(edit != "device", TestPrivateAccess.GetField<DispatcherTimer>(model, "_settingsFeedbackTimer").IsEnabled);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void PendingSummaryTracksFurtherEdits_TruncatesAtThree_AndDropsRevertedChanges()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            harness.SetCachedSettings(saved);
            string originalSeek = model.SettingsSeekStepSecondsDraft;
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal("Unsaved: Overlay duration.\nClick Apply settings to save.", model.SettingsTabFeedbackText);
            int tooltipChanges = 0;
            model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppViewModel.SettingsTabFeedbackText)) tooltipChanges++; };
            model.SettingsMuteMicHotkeyDraft = "Ctrl+Alt+Shift+F11";
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Contains("Overlay duration, Microphone mute hotkey", model.SettingsTabFeedbackText, StringComparison.Ordinal);
            model.SettingsPlayAppSoundsDraft = false;
            model.SettingsOverlayEnabledDraft = false;
            model.SettingsSeekStepSecondsDraft = "invalid";
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal("Unsaved: Overlay enabled, App sounds, Overlay duration, and 2 more.\nClick Apply settings to save.", model.SettingsTabFeedbackText);
            model.SettingsOverlayDurationSecondsDraft = saved.Overlay.DurationSeconds.ToString("0.0");
            model.SettingsOverlayEnabledDraft = saved.Overlay.Enabled;
            model.SettingsPlayAppSoundsDraft = saved.Miscellaneous.PlayAppSounds;
            model.SettingsSeekStepSecondsDraft = originalSeek;
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal("Unsaved: Microphone mute hotkey.\nClick Apply settings to save.", model.SettingsTabFeedbackText);
            Assert.True(tooltipChanges >= 3);
            model.SettingsMuteMicHotkeyDraft = saved.Hotkeys.Mute.Mic;
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Empty(model.SettingsTabFeedbackText);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.NotEqual(TabSaveState.Unsaved, state));
        });
    }

    [Fact]
    public void PendingSummariesScopeSwitchEnablementAndReverseHotkeysToTheirEditors()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(saved);
            harness.SettingsService.SaveSettings(saved);
            model.OutputHotkeysEnabled = false;
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Contains("Hotkeys enabled", model.OutputTabFeedbackText, StringComparison.Ordinal);
            Assert.Empty(model.InputTabFeedbackText);
            model.OutputHotkeysEnabled = true;
            model.OutputReverseHotkey.LoadFromString("Ctrl+Alt+Shift+F11");
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal("Unsaved: Reverse output hotkey.\nClick Apply settings to save.", model.SettingsTabFeedbackText);
            Assert.Empty(model.OutputTabFeedbackText);
            TestPrivateAccess.RunTaskOnDispatcher(model.ApplySettingsForTestsAsync());
            Assert.Equal("Ctrl+Alt+Shift+F11", harness.SettingsService.LoadSettings().DeviceSwitching.Output.ReverseSwitchHotkey);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.NotEqual(TabSaveState.Unsaved, state));
        });
    }

    [Fact]
    public void PendingRoutineSummaryDistinguishesListAndOptionChanges()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.DeviceSwitching.Output.SwitchHotkey = null!;
            saved.Routines.Items = [new AudioRoutine { Id = "a", Name = "A", Enabled = false, MasterVolumePercent = 10 },
                new AudioRoutine { Id = "b", Name = "B", Enabled = false, MasterVolumePercent = 20 },
                new AudioRoutine { Id = "c", Name = "C", Enabled = false, MasterVolumePercent = 30 }];
            harness.SetCachedSettings(saved);
            model.ApplyRoutinesFromSettings(saved.Routines.Items);
            model.Routines[0].Name = "Edited";
            model.Routines.RemoveAt(1);
            model.Routines.Move(1, 0);
            model.Routines.Add(new AudioRoutine { Id = "d", Name = "D", Enabled = false, MasterVolumePercent = 40 });
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal("Unsaved: Added routines, Removed routines, Routine order, and 1 more.\nClick Save routines to save.", model.RoutinesTabFeedbackText);
            Assert.Empty(model.OutputTabFeedbackText);
            model.Routines.Clear();
            foreach (AudioRoutine routine in saved.Routines.Items) model.Routines.Add(routine.Clone());
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Empty(model.RoutinesTabFeedbackText);
        });
    }

    [Fact]
    public void SaveConfirmationExpires_RepeatedMessagesRestart_AndUnsavedEditsSurviveExpiry()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(saved);
            harness.SettingsService.SaveSettings(saved);
            var reportMethod = typeof(AppViewModel).GetMethod("CaptureDiagnosticsReport", BindingFlags.Instance | BindingFlags.NonPublic)!;
            TestPrivateAccess.SetField(model, "_lastSettingsWriteTime", DateTime.FromFileTime(0));
            Assert.Null(Assert.IsType<DiagnosticsReport>(reportMethod.Invoke(model, null)).SettingsLastWritten);
            TestPrivateAccess.RunTaskOnDispatcher(model.SaveSettingsForTestsAsync());
            var timer = TestPrivateAccess.GetField<DispatcherTimer>(model, "_settingsFeedbackTimer");
            Assert.Equal(TimeSpan.FromSeconds(4), timer.Interval);
            Assert.True(timer.IsEnabled);
            var report = Assert.IsType<DiagnosticsReport>(reportMethod.Invoke(model, null));
            DateTime written = File.GetLastWriteTime(harness.SettingsService.GetSettingsPath());
            Assert.Equal(written, report.SettingsLastWritten);
            var expire = typeof(AppViewModel).GetMethod("SaveConfirmationTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            expire.Invoke(model, [timer, EventArgs.Empty]);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.NotEqual(TabSaveState.Saved, state));
            Assert.False(timer.IsEnabled);
            Assert.Equal(written, Assert.IsType<DiagnosticsReport>(reportMethod.Invoke(model, null)).SettingsLastWritten);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "ShowSettingsSaveFeedbackAsync", "Saved.", SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input));
            timer.Stop();
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "ShowSettingsSaveFeedbackAsync", "Saved.", SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input));
            Assert.True(timer.IsEnabled);
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
            Assert.False(timer.IsEnabled);
            expire.Invoke(model, [timer, EventArgs.Empty]);
            Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
            model.SettingsOverlayDurationSecondsDraft = saved.Overlay.DurationSeconds.ToString("0.0");
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "ShowSettingsSaveFeedbackAsync", "Saved.", SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input));
            Assert.True(timer.IsEnabled);
            TestPrivateAccess.RunTaskOnDispatcher(model.CleanupAsync());
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void AutoSaveKeepsTabFeedbackNeutralBeforeAndAfterItsCommit()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.Miscellaneous.AutoSaveEnabled = true;
            saved.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(saved);
            harness.SettingsService.SaveSettings(saved);
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.Equal(TabSaveState.None, state));
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "feedback"));
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.Equal(TabSaveState.None, state));
            Assert.Equal(3.5, harness.SettingsService.LoadSettings().Overlay.DurationSeconds);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoSave_ToggleStaysManual_AndImportWarnsAboutItsUnsavedDraft(bool enabled)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            var cached = new Settings();
            cached.Miscellaneous.AutoSaveEnabled = enabled;
            cached.Hotkeys.App.ToggleAppVisibility = string.Empty;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            model.SettingsAutoSaveEnabledDraft = !enabled;
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal(enabled ? TabSaveState.None : TabSaveState.Unsaved, model.SettingsTabSaveState);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "toggle"));
            string importPath = Path.Combine(_workspace.Root, "declined.json");
            SettingsTransferService.ExportSettings(cached, importPath);
            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            TestPrivateAccess.RunTaskOnDispatcher(model.ImportSettingsForTestsAsync());

            Assert.Equal(enabled, harness.SettingsService.LoadSettings().Miscellaneous.AutoSaveEnabled);
            Assert.Equal(!enabled, model.SettingsAutoSaveEnabledDraft);
            Assert.StartsWith(DialogText.Messages.BuildImportSettingsReplaceConfirmation(importPath, discardUnsavedEdits: true),
                Assert.Single(harness.Messages.YesNoMessages).message);
            TestPrivateAccess.RunTaskOnDispatcher(model.ApplySettingsForTestsAsync());
            Assert.Equal(!enabled, harness.SettingsService.LoadSettings().Miscellaneous.AutoSaveEnabled);
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.All([model.OutputTabSaveState, model.InputTabSaveState, model.RoutinesTabSaveState, model.SettingsTabSaveState], state => Assert.NotEqual(TabSaveState.Unsaved, state));
            Assert.Equal(TabSaveState.Saved, model.SettingsTabSaveState);
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
            string pendingDuration = model.SettingsOverlayDurationSecondsDraft;
            TabSaveState feedback = model.SettingsTabSaveState;
            bool pendingKeepMute = model.SettingsKeepMuteStateWhenSwitchingDraft;
            Settings firstCommit = harness.SettingsService.LoadSettings();
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("3.5", pendingDuration);
            Assert.Equal(autoSave ? TabSaveState.None : TabSaveState.Unsaved, feedback);
            Assert.False(pendingKeepMute);
            Assert.Equal(2.5, firstCommit.Overlay.DurationSeconds);
            Assert.False(firstCommit.DeviceSwitching.KeepMuteStateWhenSwitching);
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
            TestPrivateAccess.SetField(model, "_disconnectedOutputSelection", new CycleDevice { Id = "previous-output" });
            TestPrivateAccess.SetField(model, "_disconnectedInputSelection", new CycleDevice { Id = "previous-input" });

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
            Assert.Null(TestPrivateAccess.GetField<CycleDevice?>(model, "_disconnectedOutputSelection"));
            Assert.Null(TestPrivateAccess.GetField<CycleDevice?>(model, "_disconnectedInputSelection"));
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
