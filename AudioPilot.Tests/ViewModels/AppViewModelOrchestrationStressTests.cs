using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class AppViewModelOrchestrationStressTests
{
    [StressFact]
    public void QueueSteamBigPictureSignalEvaluation_RepeatedBurst_CoalescesAndDrainsWithoutLeakingDebounce_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(QueueSteamBigPictureSignalEvaluation_RepeatedBurst_CoalescesAndDrainsWithoutLeakingDebounce_WhenStressEnabled)))
        {
            return;
        }

        TestExecutionGuards.RunSta(() =>
        {
            using var viewModelWorkspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationStressTests));
            using var viewModelHarness = AppViewModelHarnessBuilder.CreateInteractionHarness(viewModelWorkspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            viewModelHarness.SetCachedSettings(new Settings());
            AppViewModel viewModel = viewModelHarness.ViewModel;
            viewModel.MarkStartupVisibilityResolved();
            _ = viewModel.Mixer;
            _ = viewModel.InputMixer;
            viewModel.ConfigureSteamBigPictureFallbackForTests([], monitoringEnabled: false);

            const int burstCount = 100;
            for (int iteration = 0; iteration < burstCount; iteration++)
            {
                TestPrivateAccess.InvokeNonPublic(viewModel, "QueueSteamBigPictureSignalEvaluation");
            }

            Assert.True(viewModel.GetPendingSteamBigPictureSignalCountForTests() > 0);
            Assert.True(viewModel.HasSteamBigPictureDebounceForTests());

            WaitForQueuedBackgroundTasks(viewModel);

            Assert.Equal(0, viewModel.GetPendingSteamBigPictureSignalCountForTests());
            Assert.False(viewModel.HasSteamBigPictureDebounceForTests());
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
        });
    }

    [StressFact]
    public void HandleWindowVisibilityChanged_RepeatedPendingSignalBurst_DrainsSharedMixerSignalsWithoutLeavingBackgroundWork_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(HandleWindowVisibilityChanged_RepeatedPendingSignalBurst_DrainsSharedMixerSignalsWithoutLeavingBackgroundWork_WhenStressEnabled)))
        {
            return;
        }

        TestExecutionGuards.RunSta(() =>
        {
            using var viewModelWorkspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationStressTests));
            using var viewModelHarness = AppViewModelHarnessBuilder.CreateInteractionHarness(viewModelWorkspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            viewModelHarness.SetCachedSettings(new Settings());
            AppViewModel viewModel = viewModelHarness.ViewModel;
            viewModel.MarkStartupVisibilityResolved();
            _ = viewModel.Mixer;
            _ = viewModel.InputMixer;

            const int burstCount = 50;
            for (int iteration = 0; iteration < burstCount; iteration++)
            {
                viewModel.SetPendingSessionCreatedSignalsForTests(iteration + 1);
                viewModel.HandleWindowVisibilityChanged(true);
            }

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.True(pendingSignals.PendingShowWindowMixerRefreshSignals > 0);
            Assert.True(pendingSignals.HasSessionRefreshDebounce);

            WaitForQueuedBackgroundTasks(viewModel);

            pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(0, pendingSignals.PendingSessionCreatedSignals);
            Assert.Equal(0, pendingSignals.PendingSessionLifecycleSignals);
            Assert.Equal(0, pendingSignals.PendingShowWindowMixerRefreshSignals);
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
        });
    }

    private static void WaitForQueuedBackgroundTasks(AppViewModel viewModel)
    {
        TestPrivateAccess.RunTaskOnDispatcher(viewModel.WaitForQueuedBackgroundTasksForTestsAsync());
    }
}
