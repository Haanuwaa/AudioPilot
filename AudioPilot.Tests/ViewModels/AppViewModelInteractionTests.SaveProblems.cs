using System.Windows.Threading;
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
    public void AutomaticWriteFailurePersistsOnItsTabAndClearsAfterRetryWithoutRepeatedPopups(bool apply, bool visible)
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
            string primary = harness.SettingsService.GetSettingsPath();
            var shell = TestPrivateAccess.GetField<AppShellService>(model, "_shell");
            TestPrivateAccess.GetField<TestAppMainWindowManager>(shell, "_windowManager").IsVisibleValue = visible;
            var tray = TestPrivateAccess.GetField<TestAppTrayIconService>(shell, "_trayService");
            harness.SettingsService.OverrideWriteTargetsForTests(
                Path.Combine(_workspace.Root, "<>blocked-primary", "settings.json"),
                Path.Combine(_workspace.Root, "<>blocked-fallback", "settings.json"));
            if (apply) model.SettingsOverlayDurationSecondsDraft = "3.5";
            else model.OutputHotkeysEnabled = false;

            for (int retry = 0; retry < 2; retry++)
                TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "failure-feedback"));
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(TabSaveState.NotSaved, apply ? model.SettingsTabSaveState : model.OutputTabSaveState);
            Assert.Equal(TabSaveState.None, model.InputTabSaveState);
            Assert.Contains("Your edits are still here", apply ? model.SettingsTabFeedbackText : model.OutputTabFeedbackText, StringComparison.Ordinal);
            Assert.Equal(visible ? 0 : 1, tray.BalloonCalls);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.Empty(harness.Messages.WarningMessages);
            Assert.Null(TestPrivateAccess.GetField(model, "_settingsFeedbackTimer"));

            harness.SettingsService.OverrideWriteTargetsForTests(primary, Path.Combine(_workspace.FallbackDir, "settings.json"));
            TestPrivateAccess.RunTaskOnDispatcher(apply ? model.ApplySettingsForTestsAsync() : model.SaveSettingsForTestsAsync());
            Assert.Equal(TabSaveState.Saved, apply ? model.SettingsTabSaveState : model.OutputTabSaveState);
            Assert.DoesNotContain("weren't saved", apply ? model.SettingsTabFeedbackText : model.OutputTabFeedbackText, StringComparison.Ordinal);
            TestPrivateAccess.RunTaskOnDispatcher(model.CleanupAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Theory]
    [InlineData(nameof(AppViewModel.SettingsOverlayDurationSecondsDraft), "99", "3.5", "0.5 to 10")]
    [InlineData(nameof(AppViewModel.SettingsSeekStepSecondsDraft), "0", "30s", "1 second to 60 minutes")]
    [InlineData(nameof(AppViewModel.SettingsMasterVolumeStepPercentDraft), "101", "10", "1 to 100")]
    [InlineData(nameof(AppViewModel.SettingsMicVolumeStepPercentDraft), "0", "10", "1 to 100")]
    [InlineData(nameof(AppViewModel.SettingsForegroundVolumeStepPercentDraft), "101", "10", "1 to 100")]
    public void InvalidAutoSavedFieldsExplainTheRangeAndClearAfterCorrection(string name, string invalid, string valid, string guidance)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            var saved = new Settings();
            saved.Miscellaneous.AutoSaveEnabled = true;
            saved.Hotkeys.App.ToggleAppVisibility = string.Empty;
            if (name == nameof(AppViewModel.SettingsMasterVolumeStepPercentDraft)) saved.Hotkeys.Volume.MasterUp = "Ctrl+Alt+Shift+F11";
            if (name == nameof(AppViewModel.SettingsMicVolumeStepPercentDraft)) saved.Hotkeys.Volume.MicUp = "Ctrl+Alt+Shift+F11";
            harness.SetCachedSettings(saved);
            model.SettingsMasterVolumeUpHotkeyDraft = saved.Hotkeys.Volume.MasterUp;
            model.SettingsMicVolumeUpHotkeyDraft = saved.Hotkeys.Volume.MicUp;
            harness.SettingsService.SaveSettings(saved);
            var property = typeof(AppViewModel).GetProperty(name)!;
            property.SetValue(model, invalid);
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.True(model.HasErrors);
            Assert.Contains(guidance, Assert.Single(model.GetErrors(name).Cast<string>()), StringComparison.Ordinal);
            Assert.Contains(guidance, model.SettingsFieldGuidance[name], StringComparison.Ordinal);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "invalid-field"));
            Assert.Equal(TabSaveState.NotSaved, model.SettingsTabSaveState);
            Assert.Empty(harness.Messages.WarningMessages);
            Assert.Empty(harness.Messages.ErrorMessages);

            property.SetValue(model, valid);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "corrected-field"));
            Assert.False(model.HasErrors);
            Assert.Empty(model.GetErrors(name));
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
            TestPrivateAccess.RunTaskOnDispatcher(model.CleanupAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Fact]
    public void ConflictTooltipsNameActionsAndRefreshWhenTheConflictingKeySetStaysTheSame()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var model = harness.ViewModel;
            harness.SetCachedSettings(new Settings());
            model.SettingsPlayPauseHotkeyDraft = "Ctrl+Alt+Shift+F11";
            model.SettingsNextTrackHotkeyDraft = "Ctrl+Alt+Shift+F11";
            Assert.Contains("Play/Pause", model.SettingsPlayPauseHotkeyDraftCapture.HoverText, StringComparison.Ordinal);
            Assert.Contains("Next track", model.SettingsPlayPauseHotkeyDraftCapture.HoverText, StringComparison.Ordinal);
            model.SettingsPreviousTrackHotkeyDraft = "Ctrl+Alt+Shift+F11";
            model.SettingsNextTrackHotkeyDraft = string.Empty;
            Assert.Contains("Previous track", model.SettingsPlayPauseHotkeyDraftCapture.HoverText, StringComparison.Ordinal);
            Assert.DoesNotContain("Next track", model.SettingsPlayPauseHotkeyDraftCapture.HoverText, StringComparison.Ordinal);
            model.SettingsPreviousTrackHotkeyDraft = string.Empty;
            Assert.False(model.SettingsPlayPauseHotkeyDraftCapture.HasWarning);
        });
    }
}
