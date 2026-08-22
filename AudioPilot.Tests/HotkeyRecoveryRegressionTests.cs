using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class HotkeyRecoveryRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DelayedMutePreservationKeepsNewerMuteChoice(bool input, bool newerMuted)
    {
        bool initial = !newerMuted;
        bool current = initial;
        var guard = new EndpointMutePreservationState(initial);
        if (input)
        {
            var snapshot = new TaskCompletionSource<SessionVolumeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task work = PostSwitchCoordinator.RestoreInputStateAsync(snapshot.Task, "selected-input",
                (_, _, muted) => { if (muted.HasValue) current = muted.Value; }, () => true,
                TestContext.Current.CancellationToken, muted: initial, canRestoreMute: () => guard.CanRestore(current));
            current = newerMuted;
            snapshot.SetResult(new SessionVolumeSnapshot());
            await work;
        }
        else
        {
            current = newerMuted;
            await PostSwitchCoordinator.ExecuteAsync(() => false, Logger.Instance, null!, "test", "selected-output", NRole.Multimedia,
                null, initial, false, false, false, false, null, TestContext.Current.CancellationToken,
                runMuteApplyWorkAsync: (_, muted, _, _) => { if (muted.HasValue) current = muted.Value; return Task.CompletedTask; },
                canRestorePlaybackMute: () => guard.CanRestore(current));
        }
        Assert.Equal(newerMuted, current);
    }

    [Fact]
    public async Task FailedInputSnapshotDoesNotRestoreStaleMute()
    {
        bool? written = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostSwitchCoordinator.RestoreInputStateAsync(
            Task.FromException<SessionVolumeSnapshot>(new InvalidOperationException("capture failed")), "input",
            (_, _, muted) => written = muted, () => true, TestContext.Current.CancellationToken,
            muted: false, canRestoreMute: () => false));
        Assert.True(written);
    }

    [Fact]
    public async Task LiveDeafenStillOverridesRejectedPreservation()
    {
        bool? written = null;
        await PostSwitchCoordinator.ExecuteAsync(() => false, Logger.Instance, null!, "test", "output", NRole.Multimedia,
            null, false, false, false, false, false, null, TestContext.Current.CancellationToken,
            runMuteApplyWorkAsync: (_, muted, _, _) => { written = muted; return Task.CompletedTask; },
            resolveMuteOverride: (_, _) => true, canRestorePlaybackMute: () => false);
        Assert.True(written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MuteNotificationRejectsPreservationAfterAwayAndBack(bool initial)
    {
        var state = new EndpointMutePreservationState(initial);
        state.Observe(!initial);
        state.Observe(initial);
        Assert.False(state.CanRestore(initial));
    }

    [Theory]
    [InlineData("shutdown", false)]
    [InlineData("capture", false)]
    [InlineData("completed-capture", false)]
    [InlineData("normal", false)]
    [InlineData("shutdown", true)]
    [InlineData("capture", true)]
    [InlineData("completed-capture", true)]
    [InlineData("normal", true)]
    public void QueuedUiHotkeysRecheckShutdownAndCaptureGeneration(string change, bool asynchronous)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var capture = new HotkeyCaptureSession(_ => false);
            using var hotkeys = new HotkeyService(null, null, captureSession: capture);
            AppRuntimeHost host = Host(hotkeys);
            int calls = 0;
            if (asynchronous)
                Invoke(host, "DispatchUiHotkeyActionAsync", (Func<Task>)(() => { calls++; return Task.CompletedTask; }), "test error");
            else Invoke(host, "DispatchUiHotkeyAction", (Action)(() => calls++), true);
            IDisposable? lease = null;
            try
            {
                if (change == "shutdown") TestPrivateAccess.SetField(host, "_shutdownStarted", 1);
                if (change is "capture" or "completed-capture") lease = capture.Acquire();
                if (change == "completed-capture") { lease!.Dispose(); lease = null; }
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                Assert.Equal(change == "normal" ? 1 : 0, calls);
            }
            finally { lease?.Dispose(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopRecoveryRequiresPhysicalReleaseBeforeRearmingHold(bool mouse)
    {
        int calls = 0;
        Func<bool>? firstPressHeld = null;
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = held => firstPressHeld ??= held };
        using var keyboard = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (binding, _) => { calls++; binding.Callback!(); });
        using var mouseHost = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (binding, _) => { calls++; binding.Callback!(); });
        keyboard.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
            [new(1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Hold", HoldState: state)]));
        mouseHost.UpdateSnapshot(MouseHotkeySnapshot.Create(
            [new(2, HotkeyMainInput.FromMouseButton(MouseButton.XButton1), HotkeyModifierMask.None, null, "Hold", HoldState: state)]));
        TestPrivateAccess.SetField(keyboard, "_hookId", (nint)1);
        TestPrivateAccess.SetField(mouseHost, "_hookId", (nint)1);
        nint data = Marshal.AllocHGlobal(32);
        for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(data, offset, 0);
        if (mouse) Marshal.WriteInt32(data, 8, 1 << 16);
        else Marshal.WriteInt32(data, 0, 0x87);
        try
        {
            int down = mouse ? 0x020B : 0x0100;
            nint Send() => mouse ? mouseHost.InvokeHookCallbackForTests(0, down, data) : keyboard.InvokeHookCallbackForTests(0, down, data);
            bool Recover(bool held) => mouse ? mouseHost.RecoverReleasedHeldInputs(_ => held) : keyboard.RecoverReleasedHeldInputs(_ => held);
            Assert.Equal((nint)1, Send());
            Assert.True(firstPressHeld!());
            if (mouse) mouseHost.ReleaseHeldInputsForDesktopSwitch();
            else keyboard.ReleaseHeldInputsForDesktopSwitch();
            Assert.False(firstPressHeld());
            Assert.False(Recover(true));
            Assert.Equal((nint)1, Send());
            Assert.Equal(1, calls);
            Assert.True(Recover(false));
            Assert.Equal((nint)1, Send());
            Assert.Equal(2, calls);
            Assert.True(state.IsHeld());
            Assert.False(firstPressHeld());
        }
        finally
        {
            TestPrivateAccess.SetField(keyboard, "_hookId", nint.Zero);
            TestPrivateAccess.SetField(mouseHost, "_hookId", nint.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData("mic", "failure")]
    [InlineData("sound", "failure")]
    [InlineData("deafen", "failure")]
    [InlineData("deafen", "partial")]
    [InlineData("deafen", "partial-output")]
    [InlineData("mic", "success")]
    [InlineData("sound", "success")]
    [InlineData("deafen", "success")]
    public void MuteHotkeyFeedbackMatchesCompletedWrite(string target, string outcome)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(HotkeyRecoveryRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var presenter = new RecordingOverlayPresenter();
            using var overlay = new OverlayService(action => action(), _ => presenter);
            AppRuntimeHost host = Host(harness.Hotkeys);
            TestPrivateAccess.SetField(host, "_appVm", harness.ViewModel);
            TestPrivateAccess.SetField(host, "_overlayService", overlay);
            string title = target == "mic" ? "Microphone muted" : target == "sound" ? "Sound muted" : "Deafened";
            Func<bool> read = target == "mic" ? () => harness.ViewModel.MuteMic : target == "sound" ? () => harness.ViewModel.MuteSound : () => harness.ViewModel.Deafen;
            Action toggle = target == "mic" ? () => harness.ViewModel.MuteMic = true : target == "sound" ? () => harness.ViewModel.MuteSound = true : () => harness.ViewModel.Deafen = true;
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ => { if (outcome is "failure" or "partial-output") throw new InvalidOperationException("Simulated mute failure"); };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ => { if (outcome is "failure" or "partial") throw new InvalidOperationException("Simulated mute failure"); };
            try
            {
                Invoke(host, "ToggleFlagWithOverlay", toggle, read, title, "Unmuted");
                Assert.Empty(presenter.ActionMessages);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                if (outcome == "success") Assert.Equal(title, Assert.Single(presenter.ActionMessages).message);
                else
                {
                    Assert.Empty(presenter.ActionMessages);
                    Assert.Contains(outcome.StartsWith("partial", StringComparison.Ordinal) ? "partially applied" : "Could not", Assert.Single(presenter.Messages).header);
                }
            }
            finally
            {
                AudioDeviceService.ResetTestHooks();
            }
        });
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    public void NativeDesktopRecoveryTimerStopsAfterReleaseAndDisposal()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            bool activeDesktop = (bool)typeof(HotkeyDesktopSwitchMonitor).GetMethod("IsInputDesktop", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            Assert.SkipWhen(!activeDesktop, "Native recovery requires access to this thread's input desktop.");
            int releases = 0;
            int reads = 0;
            bool autoRelease = true;
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitor = new HotkeyDesktopSwitchMonitor(() => releases++, () =>
            {
                if (++reads < 3 || !autoRelease) return false;
                recovered.TrySetResult();
                return true;
            });
            var callback = TestPrivateAccess.GetField<Delegate>(monitor, "_callback");
            void DesktopChanged() => callback.DynamicInvoke((nint)0, (uint)0x20, (nint)0, 0, 0, (uint)0, (uint)0);
            DesktopChanged();
            Assert.NotEqual((nuint)0, TestPrivateAccess.GetField<nuint>(monitor, "_timer"));
            TestPrivateAccess.RunTaskOnDispatcher(recovered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(3, reads);
            Assert.Equal((nuint)0, TestPrivateAccess.GetField<nuint>(monitor, "_timer"));
            autoRelease = false;
            DesktopChanged();
            Assert.NotEqual((nuint)0, TestPrivateAccess.GetField<nuint>(monitor, "_timer"));
            monitor.Dispose();
            Assert.Equal((nuint)0, TestPrivateAccess.GetField<nuint>(monitor, "_timer"));
            Assert.Equal(nint.Zero, TestPrivateAccess.GetField<nint>(monitor, "_handle"));
            DesktopChanged();
            Assert.Equal(2, releases);
            Assert.Equal(4, reads);
        });
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("superseded")]
    [InlineData("shutdown")]
    [InlineData("capture")]
    public void DelayedMuteFeedbackWaitsForCompletionAndRejectsStaleDelivery(string change)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(HotkeyRecoveryRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var capture = new HotkeyCaptureSession(_ => false);
            using var hotkeys = new HotkeyService(null, null, captureSession: capture);
            var presenter = new RecordingOverlayPresenter();
            using var overlay = new OverlayService(action => action(), _ => presenter);
            AppRuntimeHost host = Host(hotkeys);
            TestPrivateAccess.SetField(host, "_appVm", harness.ViewModel);
            TestPrivateAccess.SetField(host, "_overlayService", overlay);
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = muted =>
            {
                if (!muted) return;
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            try
            {
                Invoke(host, "ToggleFlagWithOverlay", (Action)(() => harness.ViewModel.MuteSound = true),
                    (Func<bool>)(() => harness.ViewModel.MuteSound), "Sound muted", "Sound unmuted");
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.Empty(presenter.ActionMessages);
                if (change == "superseded") harness.ViewModel.MuteSound = false;
                if (change == "shutdown") TestPrivateAccess.SetField(host, "_shutdownStarted", 1);
                if (change == "capture") { using var lease = capture.Acquire(); }
                release.Set();
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                Assert.Equal(change == "normal" ? 1 : 0, presenter.ActionMessages.Count);
                Assert.Empty(presenter.Messages);
            }
            finally
            {
                release.Set();
                AudioDeviceService.SetMicrophoneMuteOverrideForTests = null;
                AudioDeviceService.SetPlaybackMuteOverrideForTests = null;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoalescedMuteRequestsCompleteSupersededWaiters(bool cleanup)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(HotkeyRecoveryRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int writes = 0;
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ =>
            {
                if (Interlocked.Increment(ref writes) != 1) return;
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            try
            {
                var first = harness.ViewModel.ChangeMuteStateAsync(() => harness.ViewModel.MuteSound = true);
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                var second = harness.ViewModel.ChangeMuteStateAsync(() => harness.ViewModel.MuteMic = true);
                var latest = harness.ViewModel.ChangeMuteStateAsync(() => harness.ViewModel.MuteMic = false);
                Assert.True(second.IsCompletedSuccessfully);
                Assert.False(second.Result.IsCurrent);
                if (cleanup) TestPrivateAccess.SetField(harness.ViewModel, "_isCleaningUp", true);
                release.Set();
                TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(first, second, latest));
                Assert.False(first.Result.IsCurrent);
                Assert.Equal(!cleanup, latest.Result.IsCurrent);
                Assert.Equal(!cleanup, latest.Result.Succeeded);
                Assert.Equal(cleanup ? 1 : 2, writes);
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
            }
            finally
            {
                release.Set();
                AudioDeviceService.SetMicrophoneMuteOverrideForTests = null;
                AudioDeviceService.SetPlaybackMuteOverrideForTests = null;
            }
        });
    }

    [Fact]
    public void RejectedMuteWorkCompletesWithFailure()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(HotkeyRecoveryRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var completion = harness.ViewModel.ChangeMuteStateAsync(() => harness.ViewModel.MuteMic = true);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.False(completion.Result.Succeeded);
            Assert.True(completion.Result.IsCurrent);
            Assert.False(harness.ViewModel.MuteMic);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MuteHotkeyDuringDeafenReportsEffectiveMutedState(bool microphone)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(HotkeyRecoveryRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var presenter = new RecordingOverlayPresenter();
            using var overlay = new OverlayService(action => action(), _ => presenter);
            AppRuntimeHost host = Host(harness.Hotkeys);
            TestPrivateAccess.SetField(host, "_appVm", harness.ViewModel);
            TestPrivateAccess.SetField(host, "_overlayService", overlay);
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ => { };
            var bindings = (AppRuntimeHotkeyBindings)typeof(AppRuntimeHost).GetMethod("CreateHotkeyBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null)!;
            bindings.Wire();
            try
            {
                harness.ViewModel.Deafen = true;
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
                TestPrivateAccess.GetField<Action>(harness.Hotkeys, microphone ? "OnMuteMicPressed" : "OnMuteSoundPressed")();
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                Assert.Equal(microphone ? "Microphone muted" : "Sound muted", Assert.Single(presenter.ActionMessages).message);
                Assert.True(harness.ViewModel.Deafen);
            }
            finally
            {
                bindings.Unwire();
                AudioDeviceService.SetMicrophoneMuteOverrideForTests = null;
                AudioDeviceService.SetPlaybackMuteOverrideForTests = null;
            }
        });
    }

    private static AppRuntimeHost Host(HotkeyService hotkeys)
    {
        var host = (AppRuntimeHost)RuntimeHelpers.GetUninitializedObject(typeof(AppRuntimeHost));
        TestPrivateAccess.SetField(host, "_application", Application.Current);
        TestPrivateAccess.SetField(host, "_logger", Logger.Instance);
        TestPrivateAccess.SetField(host, "_hotkeyService", hotkeys);
        return host;
    }

    private static void Invoke(AppRuntimeHost host, string method, params object[] args) =>
        typeof(AppRuntimeHost).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args);
}
