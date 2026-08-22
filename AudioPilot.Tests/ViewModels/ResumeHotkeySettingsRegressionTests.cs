using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

[Collection("DeviceCacheHelperIsolation")]
public sealed class ResumeHotkeySettingsRegressionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void QueuedResumeRegistrationHonorsLatestSettingsAndShutdown(bool enabled, bool cancelBeforeDispatch)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using var workspace = new TestSettingsWorkspace(nameof(ResumeHotkeySettingsRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, dispatcher, allowBackgroundWork: true);
            var original = new Settings();
            original.DeviceSwitching.Output.SwitchHotkey = "Ctrl+Alt+F23";
            TestPrivateAccess.SetField(harness.ViewModel, "_cachedSettings", original);
            var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPosted(object? sender, DispatcherHookEventArgs e) => posted.TrySetResult();
            dispatcher.Hooks.OperationPosted += OnPosted;
            Task registration = Task.Run(() => TestPrivateAccess.InvokeNonPublicTask(
                harness.ViewModel, "ReRegisterHotkeysAfterResumeAsync", "test-resume-settings"));
            List<(int Id, string Description)> registrations = [];
            try
            {
                posted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                Settings current = original.Clone();
                current.DeviceSwitching.Output.SwitchHotkey = "Ctrl+Alt+F24";
                current.DeviceSwitching.Output.HotkeysEnabled = enabled;
                TestPrivateAccess.SetField(harness.ViewModel, "_cachedSettings", current);
                if (cancelBeforeDispatch)
                    TestPrivateAccess.GetField<CancellationTokenSource>(harness.ViewModel, "_backgroundWorkCts").Cancel();
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= OnPosted;
                try
                {
                    if (cancelBeforeDispatch)
                        Assert.ThrowsAny<OperationCanceledException>(() => TestPrivateAccess.RunTaskOnDispatcher(registration, cancellationToken: TestContext.Current.CancellationToken));
                    else
                        TestPrivateAccess.RunTaskOnDispatcher(registration, cancellationToken: TestContext.Current.CancellationToken);
                    registrations = TestPrivateAccess.GetRegisteredHotkeys(harness.Hotkeys);
                }
                finally
                {
                    TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.CleanupAsync(), cancellationToken: TestContext.Current.CancellationToken);
                }
            }
            if (!enabled || cancelBeforeDispatch)
            {
                Assert.DoesNotContain(registrations, entry => entry.Id == AppConstants.Hotkeys.OutputSwitchHotkeyId);
                return;
            }
            var (Id, Description) = Assert.Single(registrations,
                entry => entry.Id == AppConstants.Hotkeys.OutputSwitchHotkeyId);
            Assert.Contains("Ctrl+Alt+F24", Description, StringComparison.OrdinalIgnoreCase);
        });
    }
}
