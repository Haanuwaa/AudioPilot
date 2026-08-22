using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData("save", "output-switch-debounce-ms", "333")]
    [InlineData("apply", "output-switch-debounce-ms", "333")]
    [InlineData("routines", "output-switch-debounce-ms", "333")]
    [InlineData("save", "overlay-duration-seconds", "4")]
    [InlineData("apply", "overlay-duration-seconds", "4")]
    [InlineData("routines", "overlay-duration-seconds", "4")]
    public void QueuedGuiSavePreservesEarlierCliChangeAndCapturedUserEdit(string action, string key, string value)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var model = harness.ViewModel;
            harness.SetCachedSettings(new Settings());
            harness.SettingsService.SaveSettings(new Settings());
            if (action == "save")
            {
                model.OutputCycleDevices.Add(new CycleDevice { Id = "selected-output", Name = "Selected output", DisplayOrder = 1 });
                model.Theme = AppTheme.Dark;
            }
            else if (action == "apply") model.SettingsPlayAppSoundsDraft = false;
            else model.Routines.Add(new AudioRoutine { Id = "saved", Name = "Saved", MasterVolumePercent = 50, Enabled = false });

            TestPrivateAccess.RunTaskOnDispatcher(model.EnterSettingsWriteLockForTestsAsync());
            Task<(bool Updated, string? Error)> cli;
            Task gui;
            try
            {
                cli = model.SetConfigFromCliAsync(key, value);
                gui = action switch
                {
                    "save" => model.SaveSettingsForTestsAsync(),
                    "apply" => model.ApplySettingsForTestsAsync(),
                    _ => model.SaveRoutinesAsync(),
                };
                Assert.False(cli.IsCompleted);
                Assert.False(gui.IsCompleted);
            }
            finally { model.ReleaseSettingsWriteLockForTests(); }

            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(cli, gui));
            Assert.True(cli.Result.Updated, cli.Result.Error);
            Assert.Empty(harness.Messages.ErrorMessages);
            foreach (Settings settings in new[] { harness.SettingsService.LoadSettings(), model.CurrentSettings! })
            {
                if (key == "output-switch-debounce-ms") Assert.Equal(333, settings.AdvancedTuning.OutputSwitchDebounceMs);
                else Assert.Equal(4, settings.Overlay.DurationSeconds);
                if (action == "save")
                {
                    Assert.Equal("selected-output", Assert.Single(settings.DeviceSwitching.Output.CycleDevices).Id);
                    Assert.Equal(AppTheme.Dark, settings.Theme);
                }
                else if (action == "apply") Assert.False(settings.Miscellaneous.PlayAppSounds);
                else Assert.Equal("saved", Assert.Single(settings.Routines.Items).Id);
            }
            if (action == "save")
            {
                Assert.Equal("selected-output", Assert.Single(model.OutputCycleDevices).Id);
                Assert.Equal(AppTheme.Dark, model.Theme);
            }
            else if (action == "apply") Assert.False(model.SettingsPlayAppSoundsDraft);
            else Assert.Equal("saved", Assert.Single(model.Routines).Id);
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(model.CleanupAsync());
        });
    }
}
