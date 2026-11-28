using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelDispatcherSafetyTests
{
    [Fact]
    public async Task ApplyStartupToggleAsync_WhenDispatcherShutdown_ReturnsFalse()
    {
        using var viewModelWorkspace = new TestSettingsWorkspace(nameof(AppViewModelDispatcherSafetyTests));
        using var viewModelHarness = AppViewModelHarnessBuilder.CreateInteractionHarness(viewModelWorkspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: false);
        viewModelHarness.SetCachedSettings(new Settings());
        AppViewModel viewModel = viewModelHarness.ViewModel;
        Dispatcher originalDispatcher = Dispatcher.CurrentDispatcher;
        TestPrivateAccess.SetField(viewModel, "_dispatcher", CreateShutdownDispatcher());
        try
        {
            bool applied = await TestPrivateAccess.InvokeNonPublicTask<bool>(
                viewModel,
                "ApplyStartupToggleAsync",
                true,
                "startup-test",
                (Func<bool>)(() => false),
                false);

            Assert.False(applied);
        }
        finally
        {
            TestPrivateAccess.SetField(viewModel, "_dispatcher", originalDispatcher);
        }
    }

    [Fact]
    public void SyncStartupSettingsAsync_WhenCalledFromWorkerThread_PersistsBothStartupSettings()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelDispatcherSafetyTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);

            var baseline = new Settings
            {
                App = new() { AutoSaveEnabled = true },
                Startup = new() { Enabled = false }
            };

            harness.SettingsService.SaveSettings(baseline);
            TestPrivateAccess.SetField(harness.ViewModel, "_cachedSettings", baseline);

            Task<bool> updateTask = Task.Run(async () =>
                await TestPrivateAccess.InvokeNonPublicTask<bool>(
                    harness.ViewModel,
                    "SyncStartupSettingsAsync",
                    new StartupInitPlan(StartupInitAction.None, "first-run-scheduled-task", RunAtStartupValue: true, UseTaskScheduler: true),
                    "startup-test-op"));

            TestPrivateAccess.RunTaskOnDispatcher(updateTask);

            Assert.True(updateTask.Result);
            Settings updated = harness.SettingsService.LoadSettings();
            Assert.True(updated.Startup.Enabled);
            Assert.True(updated.Startup.UseTaskScheduler);
        });
    }

    internal static Dispatcher CreateShutdownDispatcher()
    {
        Dispatcher? dispatcher = null;
        Exception? threadFailure = null;
        using var ready = new ManualResetEventSlim(false);

        Thread thread = new(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                dispatcher.InvokeShutdown();
            }
            catch (Exception ex)
            {
                threadFailure = ex;
                ready.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting for the shutdown dispatcher to initialize.");
        }

        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting for the shutdown dispatcher thread to exit.");
        }

        if (threadFailure != null)
        {
            ExceptionDispatchInfo.Capture(threadFailure).Throw();
        }

        return dispatcher!;
    }
}
