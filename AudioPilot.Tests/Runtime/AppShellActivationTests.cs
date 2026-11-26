using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Runtime;

[Collection("WpfApplicationIsolation")]
public sealed class AppShellActivationTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void QueuedJumpListAction_RechecksShutdownAndUpdateBeforeShowingAnyView(int actionValue, bool preparingUpdate)
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
                if (action == ShellAction.Routines)
                {
                    Task<bool> healthy = App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, action, lifetime.Token);
                    TestPrivateAccess.RunTaskOnDispatcher(healthy);
                    Assert.True(healthy.Result);
                    Assert.Equal(MainWindowOpenTarget.Routines, manager.PendingOpenTargetForTests);
                    presentations = 0;
                }
                Task<bool> queued = App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, action, lifetime.Token);
                Assert.False(queued.IsCompleted);
                if (preparingUpdate) TestPrivateAccess.SetField(host, "_updatePreparationActive", true);
                else vm.BeginRuntimeShutdown();
                Assert.Equal(!preparingUpdate, lifetime.IsCancellationRequested);
                TestPrivateAccess.RunTaskOnDispatcher(queued);
                Assert.False(queued.Result);
                Assert.Equal(0, presentations);
                Assert.Null(TestPrivateAccess.GetField(vm, "_quickDevicePicker"));
                if (!preparingUpdate) Assert.False(vm.ShowWindowAsync().GetAwaiter().GetResult());
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
