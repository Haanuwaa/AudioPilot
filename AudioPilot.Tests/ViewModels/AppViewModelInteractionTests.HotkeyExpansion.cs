using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Fact]
    public void MicrophoneHoldCancellation_OnlyFollowsRequestedMuteChanges()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var vm = harness.ViewModel;
            var requests = new List<bool>();
            vm.MicrophoneMuteChangeRequested += requests.Add;
            typeof(AppViewModel).GetMethod("ApplyAuthoritativeMuteFlags", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [false, true, "test"]);
            Assert.True(vm.MuteMic);
            Assert.Empty(requests);
            vm.MuteMic = false;
            Assert.Equal([false], requests);
            vm.Deafen = true;
            Assert.Equal([false, true], requests);
        });
    }
    [Fact]
    public void ForegroundAndHoldDrafts_SaveRestoreAndExpandWithConfiguredBindings()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            Settings settings = BuildCachedSettings();
            harness.SetCachedSettings(settings);
            harness.SettingsService.SaveSettings(settings);
            var vm = harness.ViewModel;
            typeof(AppViewModel).GetMethod("SyncSettingsDraftFromCurrentState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [true]);
            Assert.False(vm.SettingsForegroundVolumeControlsExpanded);
            Assert.False(vm.SettingsMicrophoneHoldControlsExpanded);
            vm.SettingsForegroundVolumeUpHotkeyDraft = "Ctrl+F8";
            vm.SettingsForegroundVolumeDownHotkeyDraft = "Ctrl+F9";
            vm.SettingsForegroundMuteHotkeyDraft = "Ctrl+F10";
            vm.SettingsForegroundVolumeStepPercentDraft = "7";
            vm.SettingsPushToTalkHotkeyDraft = "F11";
            vm.SettingsPushToTalkEnabledDraft = true;
            vm.SettingsHoldToMuteHotkeyDraft = "F12";
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(vm, "ApplySettingsAsync", false));
            var saved = harness.SettingsService.LoadSettings();
            Assert.Equal("Ctrl+F8", saved.Hotkeys.Volume.ForegroundUp);
            Assert.Equal("Ctrl+F9", saved.Hotkeys.Volume.ForegroundDown);
            Assert.Equal("Ctrl+F10", saved.Hotkeys.Volume.ForegroundMute);
            Assert.Equal(7, saved.Hotkeys.Volume.ForegroundVolumeStepPercent);
            Assert.Equal("F11", saved.Hotkeys.Mute.PushToTalk);
            Assert.True(saved.Hotkeys.Mute.PushToTalkEnabled);
            Assert.Equal("F12", saved.Hotkeys.Mute.HoldToMute);
            typeof(AppViewModel).GetMethod("SyncSettingsDraftFromCurrentState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [true]);
            Assert.True(vm.SettingsForegroundVolumeControlsExpanded);
            Assert.True(vm.SettingsMicrophoneHoldControlsExpanded);
            Assert.False(vm.SettingsPushToTalkHotkeyDraftCapture.HasWarning);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySettings_AcknowledgesSavedStandaloneHotkeyAtEndOfEdit(bool autoSave)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            Settings settings = BuildCachedSettings();
            settings.Hotkeys.App.ShowAudioStatus = "Pause";
            harness.SetCachedSettings(settings);
            harness.SettingsService.SaveSettings(settings);
            typeof(AppViewModel).GetMethod("SyncSettingsDraftFromCurrentState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(harness.ViewModel, [true]);
            HotkeyViewModel draft = harness.ViewModel.SettingsShowAudioStatusHotkeyDraftCapture;
            Assert.False(draft.HasWarning);
            Assert.Contains("global shortcut", draft.HoverText);
            draft.SetEditing(true);
            draft.LoadFromString("Scroll");
            Assert.Equal(HotkeyWarningKind.Standalone, draft.WarningKind);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ApplySettingsAsync", autoSave));
            Assert.Equal("Scroll", harness.SettingsService.LoadSettings().Hotkeys.App.ShowAudioStatus);
            Assert.Equal(autoSave, draft.HasWarning);
            draft.SetEditing(false);
            Assert.False(draft.HasWarning);
        });
    }

    [Theory]
    [InlineData("none", false, false, false)]
    [InlineData("seek-forward", true, false, false)]
    [InlineData("seek-backward", true, false, false)]
    [InlineData("master-up", false, true, false)]
    [InlineData("master-down", false, true, false)]
    [InlineData("mic-up", false, false, true)]
    [InlineData("mic-down", false, false, true)]
    [InlineData("all", true, true, true)]
    public void SettingsHotkeySections_LoadExpandedOnlyForConfiguredBindings(string configured, bool seek, bool master, bool mic)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            Settings settings = BuildCachedSettings();
            settings.Hotkeys.Media.SeekForward = configured is "seek-forward" or "all" ? "Ctrl+Shift+Right" : "";
            settings.Hotkeys.Media.SeekBackward = configured == "seek-backward" ? "Ctrl+Shift+Left" : "";
            settings.Hotkeys.Volume.MasterUp = configured is "master-up" or "all" ? "Alt+Up" : "";
            settings.Hotkeys.Volume.MasterDown = configured == "master-down" ? "Alt+Down" : "";
            settings.Hotkeys.Volume.MicUp = configured is "mic-up" or "all" ? "Alt+PageUp" : "";
            settings.Hotkeys.Volume.MicDown = configured == "mic-down" ? "Alt+PageDown" : "";
            harness.SetCachedSettings(settings);

            MethodInfo? syncDrafts = typeof(AppViewModel).GetMethod("SyncSettingsDraftFromCurrentState", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(syncDrafts);
            syncDrafts.Invoke(harness.ViewModel, [true]);

            Assert.Equal(seek, harness.ViewModel.SettingsSeekControlsExpanded);
            Assert.Equal(master, harness.ViewModel.SettingsMasterVolumeControlsExpanded);
            Assert.Equal(mic, harness.ViewModel.SettingsMicVolumeControlsExpanded);
        });
    }

    [Fact]
    public void SettingsHotkeySections_KeepManualExpansionWhileEditingAndClearingBindings()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(BuildCachedSettings());
            harness.ViewModel.SettingsSeekControlsExpanded = true;
            harness.ViewModel.SettingsMasterVolumeControlsExpanded = true;
            harness.ViewModel.SettingsMicVolumeControlsExpanded = true;

            harness.ViewModel.SettingsSeekForwardHotkeyDraftCapture.LoadFromString("Ctrl+Shift+Right");
            harness.ViewModel.SettingsMasterVolumeUpHotkeyDraftCapture.LoadFromString("Alt+Up");
            harness.ViewModel.SettingsMicVolumeUpHotkeyDraftCapture.LoadFromString("Alt+PageUp");
            harness.ViewModel.SettingsSeekForwardHotkeyDraftCapture.Reset();
            harness.ViewModel.SettingsMasterVolumeUpHotkeyDraftCapture.Reset();
            harness.ViewModel.SettingsMicVolumeUpHotkeyDraftCapture.Reset();

            Assert.True(harness.ViewModel.SettingsSeekControlsExpanded);
            Assert.True(harness.ViewModel.SettingsMasterVolumeControlsExpanded);
            Assert.True(harness.ViewModel.SettingsMicVolumeControlsExpanded);

            harness.ViewModel.SettingsSeekControlsExpanded = false;
            harness.ViewModel.SettingsMasterVolumeControlsExpanded = false;
            harness.ViewModel.SettingsMicVolumeControlsExpanded = false;
            harness.ViewModel.SettingsSeekBackwardHotkeyDraft = "Ctrl+Shift+Left";
            harness.ViewModel.SettingsMasterVolumeDownHotkeyDraft = "Alt+Down";
            harness.ViewModel.SettingsMicVolumeDownHotkeyDraft = "Alt+PageDown";

            Assert.False(harness.ViewModel.SettingsSeekControlsExpanded);
            Assert.False(harness.ViewModel.SettingsMasterVolumeControlsExpanded);
            Assert.False(harness.ViewModel.SettingsMicVolumeControlsExpanded);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySettings_PreservesManuallyOpenedEmptyHotkeySections(bool autoSave)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            Settings settings = BuildCachedSettings();
            harness.SetCachedSettings(settings);
            harness.SettingsService.SaveSettings(settings);
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "1.5";
            harness.ViewModel.SettingsSeekControlsExpanded = true;
            harness.ViewModel.SettingsMasterVolumeControlsExpanded = true;
            harness.ViewModel.SettingsMicVolumeControlsExpanded = true;

            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ApplySettingsAsync", autoSave));

            Assert.Equal(AppTheme.Dark, harness.SettingsService.LoadSettings().Theme);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.True(harness.ViewModel.SettingsSeekControlsExpanded);
            Assert.True(harness.ViewModel.SettingsMasterVolumeControlsExpanded);
            Assert.True(harness.ViewModel.SettingsMicVolumeControlsExpanded);
        });
    }
}
