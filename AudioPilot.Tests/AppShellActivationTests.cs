using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class AppShellActivationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QueuedJumpListAction_RechecksShutdownBeforeShowingAnyView(int actionValue)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppShellActivationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var vm = harness.ViewModel;
            var originalLifetime = TestPrivateAccess.GetField<CancellationTokenSource>(vm, "_backgroundWorkCts");
            using var lifetime = new CancellationTokenSource();
            var manager = new AppMainWindowManager(Application.Current, harness.Messages);
            int presentations = 0;
            manager.ShowCoreOverrideForTests = () => { presentations++; return Task.FromResult(true); };
            var host = (AppRuntimeHost)RuntimeHelpers.GetUninitializedObject(typeof(AppRuntimeHost));
            TestPrivateAccess.SetField(host, "_appVm", vm);
            TestPrivateAccess.SetField(host, "_windowManager", manager);
            TestPrivateAccess.SetField(vm, "_backgroundWorkCts", lifetime);
            var action = (ShellAction)actionValue;
            try
            {
                if (action != ShellAction.DevicePicker)
                {
                    Task<bool> healthy = App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, action, lifetime.Token);
                    TestPrivateAccess.RunTaskOnDispatcher(healthy);
                    Assert.True(healthy.Result);
                    Assert.Equal(action == ShellAction.VolumeMixer ? MainWindowOpenTarget.VolumeMixer : MainWindowOpenTarget.Routines,
                        manager.PendingOpenTargetForTests);
                    presentations = 0;
                }
                Task<bool> queued = App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, action, lifetime.Token);
                Assert.False(queued.IsCompleted);
                vm.BeginRuntimeShutdown();
                Assert.False(lifetime.IsCancellationRequested);
                TestPrivateAccess.RunTaskOnDispatcher(queued);
                Assert.False(queued.Result);
                Assert.Equal(0, presentations);
                Assert.Null(TestPrivateAccess.GetField(vm, "_quickDevicePicker"));
                Assert.False(vm.ShowWindowAsync().GetAwaiter().GetResult());
                Assert.False(App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, action, lifetime.Token).GetAwaiter().GetResult());
            }
            finally
            {
                TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
                TestPrivateAccess.SetField(vm, "_backgroundWorkCts", originalLifetime);
            }
        });
    }
}
