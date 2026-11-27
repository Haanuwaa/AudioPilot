using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PickerPreference_QueuedChangesKeepLatestChoiceAndPreserveCommittedSettings(bool latestChoice)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            Settings original = BuildCachedSettings();
            harness.SetCachedSettings(original);
            harness.SettingsService.SaveSettings(original);
            harness.ViewModel.OutputCycleDevices.Add(new() { Id = "unsaved-device", Name = "Unsaved" });
            var writeGate = TestPrivateAccess.GetField<SemaphoreSlim>(harness.ViewModel, "_settingsWriteSemaphore");
            writeGate.Wait();
            Task<bool> first;
            Task<bool> last;
            try
            {
                first = TestPrivateAccess.InvokeNonPublicTask<bool>(harness.ViewModel, "SavePickerViewPreferenceAsync", true);
                last = TestPrivateAccess.InvokeNonPublicTask<bool>(harness.ViewModel, "SavePickerViewPreferenceAsync", latestChoice);
                TestPrivateAccess.RunTaskOnDispatcher(first);
                Assert.True(first.Result);
                Settings committed = original.Clone();
                committed.Overlay.DurationSeconds = 7;
                harness.SettingsService.SaveSettings(committed);
                TestPrivateAccess.SetField(harness.ViewModel, "_cachedSettings", committed);
            }
            finally { writeGate.Release(); }
            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(first, last));
            Assert.True(last.Result);
            WaitForQueuedBackgroundTasks(harness.ViewModel);

            var restarted = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
            Settings saved = restarted.LoadSettings();
            Assert.Equal(latestChoice, saved.DeviceSwitching.PickerShowAllAvailableDevices);
            Assert.Equal(latestChoice, harness.ViewModel.Settings!.DeviceSwitching.PickerShowAllAvailableDevices);
            Assert.Equal(7, saved.Overlay.DurationSeconds);
            Assert.DoesNotContain(saved.DeviceSwitching.Output.CycleDevices, device => device.Id == "unsaved-device");
            Assert.Contains(harness.ViewModel.OutputCycleDevices, device => device.Id == "unsaved-device");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PickerPreference_FailureOrShutdownDoesNotPublishUncommittedPreference(bool shuttingDown)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            Settings original = BuildCachedSettings();
            harness.SetCachedSettings(original);
            harness.SettingsService.SaveSettings(original);
            if (shuttingDown) TestPrivateAccess.SetField(harness.ViewModel, "_isCleaningUp", true);
            else harness.SettingsService.OverrideWriteTargetsForTests(
                Path.Combine(_workspace.Root, "<>invalid-primary", "settings.json"),
                Path.Combine(_workspace.Root, "<>invalid-fallback", "settings.json"));

            Task<bool> save = TestPrivateAccess.InvokeNonPublicTask<bool>(harness.ViewModel, "SavePickerViewPreferenceAsync", true);
            TestPrivateAccess.RunTaskOnDispatcher(save);
            Assert.False(save.Result);
            Assert.False(harness.ViewModel.Settings!.DeviceSwitching.PickerShowAllAvailableDevices);
            if (shuttingDown) TestPrivateAccess.SetField(harness.ViewModel, "_isCleaningUp", false);
            WaitForQueuedBackgroundTasks(harness.ViewModel);
        });
    }
}
