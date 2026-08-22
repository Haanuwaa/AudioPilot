using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelDispatcherSafetyTests
{
    [Fact]
    public async Task TryApplyStartupRegistryChangeAsync_WhenDispatcherShutdown_ReturnsFalse()
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
        Dispatcher dispatcher = CreateShutdownDispatcher();
        TestPrivateAccess.SetField(viewModel, "_dispatcher", dispatcher);

        bool applied = await TestPrivateAccess.InvokeNonPublicTask<bool>(
            viewModel,
            "TryApplyStartupRegistryChangeAsync",
            true,
            "startup-test");

        Assert.False(applied);
    }

    [Fact]
    public void UpdateStartupSettingInJsonAsync_WhenCalledFromWorkerThread_CompletesWithoutCrossThreadFailure()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelDispatcherSafetyTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);

            var baseline = new Settings
            {
                Miscellaneous = new MiscellaneousSettings { AutoSaveEnabled = true },
                RunAtStartup = false,
            };

            harness.SettingsService.SaveSettings(baseline);
            TestPrivateAccess.SetField(harness.ViewModel, "_cachedSettings", baseline);

            Task<bool> updateTask = Task.Run(async () =>
                await TestPrivateAccess.InvokeNonPublicTask<bool>(
                    harness.ViewModel,
                    "UpdateStartupSettingInJsonAsync",
                    true,
                    "startup-test-op"));

            TestPrivateAccess.RunTaskOnDispatcher(updateTask);

            Assert.True(updateTask.Result);
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
