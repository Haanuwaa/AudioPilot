using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

[Collection("CoreAudioWorkerIsolation")]
public sealed class MicrophoneHoldServiceTests
{
    [Fact]
    public void TemporaryMute_ExternalUnmuteThenRemutePreventsRestoringOverNewChoice()
    {
        var ownership = new MicrophoneMuteOwnership(false);
        Assert.True(ApplyOwnership(ownership, false, true, enforce: false));
        ownership.Observe(ownership.Context, true);
        ownership.Observe(Guid.NewGuid(), false);
        ownership.Observe(Guid.NewGuid(), true);
        Assert.False(ownership.ShouldRestore(true));
        Assert.False(ApplyOwnership(ownership, true, true, enforce: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryMute_DelayedBaselineNotificationDoesNotCancelRestoration(bool original)
    {
        var ownership = new MicrophoneMuteOwnership(original);
        Assert.True(ApplyOwnership(ownership, original, !original, enforce: false));
        ownership.Observe(Guid.NewGuid(), original);
        Assert.True(ownership.ShouldRestore(!original));
        ownership.Observe(ownership.Context, !original);
        ownership.Observe(Guid.NewGuid(), original);
        ownership.Observe(Guid.NewGuid(), !original);
        Assert.False(ownership.ShouldRestore(!original));
    }

    [Fact]
    public void TemporaryMute_OwnNotificationsAndVolumeOnlyChangesRetainRestoration()
    {
        var ownership = new MicrophoneMuteOwnership(false);
        Assert.True(ApplyOwnership(ownership, false, true, enforce: false));
        ownership.Observe(ownership.Context, false); // A delayed notification from this lease.
        ownership.Observe(Guid.NewGuid(), true); // Volume changed, mute did not.
        Assert.True(ownership.ShouldRestore(true));
        Assert.False(ownership.ShouldRestore(false));
    }

    [Fact]
    public void PersistentPushToTalk_EnforcesMuteAndRetainsItsOriginalState()
    {
        var ownership = new MicrophoneMuteOwnership(false);
        Assert.True(ApplyOwnership(ownership, false, true, enforce: true));
        ownership.Observe(Guid.NewGuid(), false);
        Assert.True(ApplyOwnership(ownership, false, true, enforce: true));
        Assert.True(ownership.ShouldRestore(true));
        Assert.False(ownership.Original);
    }

    [Fact]
    public void TemporaryMutePromotion_AdoptsANewerExternalChoiceBeforeEnforcing()
    {
        var ownership = new MicrophoneMuteOwnership(true);
        Assert.True(ApplyOwnership(ownership, true, true, enforce: false));
        ownership.Observe(Guid.NewGuid(), false);
        Assert.False(ownership.ShouldRestore(true));
        Assert.True(ApplyOwnership(ownership, false, true, enforce: true));
        Assert.False(ownership.Original);
        Assert.True(ownership.ShouldRestore(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushToTalk_PartialOpenRemutesOnlyOwnedEndpointsAndPreservesRestoration(bool manualChange)
    {
        using var logger = Logger.CreateInMemoryForTests("hold-failure.log");
        using var first = new MuteEndpoint("first", initiallyMuted: true);
        using var failing = new MuteEndpoint("failing");
        using var untouched = new MuteEndpoint("untouched");
        EndpointMicrophoneMuteLease.Endpoint[] endpoints = [first, failing, untouched];
        Assert.True(EndpointMicrophoneMuteLease.ApplyToEndpoints(endpoints, muted: true,
            enforce: true, rollbackOnFailure: false, logger));
        failing.BeforeWrite = () =>
        {
            if (manualChange)
            {
                first.SetExternalMute(true);
                first.SetExternalMute(false);
            }
            throw new InvalidOperationException("Endpoint rejected the unmute.");
        };

        Assert.False(EndpointMicrophoneMuteLease.ApplyToEndpoints(endpoints, muted: false,
            enforce: true, rollbackOnFailure: true, logger));

        Assert.Equal(!manualChange, first.Mute);
        bool[] expectedWrites = manualChange ? [false] : [false, true];
        Assert.Equal(expectedWrites, first.Writes);
        Assert.Equal(!manualChange, first.Ownership.ShouldRestore(first.Mute));
        Assert.True(failing.Mute);
        Assert.True(failing.Ownership.ShouldRestore(failing.Mute));
        Assert.False(failing.Ownership.Original);
        Assert.Equal([true], untouched.Writes);
        if (manualChange)
        {
            Assert.True(EndpointMicrophoneMuteLease.ApplyToEndpoints([first], muted: true,
                enforce: true, rollbackOnFailure: false, logger));
            Assert.False(first.Ownership.Original);
        }
    }

    private static bool ApplyOwnership(MicrophoneMuteOwnership ownership, bool current, bool muted, bool enforce)
    {
        if (!ownership.TryPrepareWrite(current, enforce)) return false;
        ownership.ConfirmApplied(muted);
        return true;
    }

    private sealed class MuteEndpoint(string id, bool initiallyMuted = false)
        : EndpointMicrophoneMuteLease.Endpoint(id, new MicrophoneMuteOwnership(initiallyMuted))
    {
        private bool _muted = initiallyMuted;
        public List<bool> Writes { get; } = [];
        public Action? BeforeWrite { get; set; }
        public override bool Mute
        {
            get => _muted;
            set
            {
                BeforeWrite?.Invoke();
                Writes.Add(value);
                _muted = value;
                Ownership.Observe(Ownership.Context, value);
            }
        }

        public void SetExternalMute(bool muted)
        {
            _muted = muted;
            Ownership.Observe(Guid.NewGuid(), muted);
        }

        public override void Dispose() { }
    }

    private sealed class Input { public volatile bool Held = true; }

    [Fact]
    public async Task UpdateSuspension_IsAtomicWithHoldAdmissionAndLeavesMuteUntouched()
    {
        var lease = new Lease(true);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        using (var suspension = service.TrySuspendIdleForUpdate())
        {
            Assert.NotNull(suspension);
            service.Begin(true, () => true);
            service.SetPushToTalkEnabled(true);
            Assert.False(service.IsControlling);
            Assert.Empty(lease.Applied);
            Assert.Equal(0, lease.Restores);
        }
        var input = new Input();
        service.Begin(true, () => input.Held);
        Assert.Null(service.TrySuspendIdleForUpdate());
        await UntilAsync(() => !lease.Applied.IsEmpty);
        input.Held = false;
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(lease.Muted);
    }

    [Fact]
    public Task UpdateRestart_DefersThroughoutPushToTalkOwnershipWithoutRestoringLiveMicrophone() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var lease = new Lease(false);
        var input = new Input();
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        await using var app = new UpdatePreparationFixture(service);
        app.Harness.ViewModel.Theme = AppTheme.Light;
        CancellationToken token = TestContext.Current.CancellationToken;
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => lease.Muted);
        Assert.Equal("microphone-owned", await app.Host.PrepareForUpdateAsync(token));
        service.Begin(false, () => input.Held);
        await UntilAsync(() => !lease.Muted);
        Assert.Equal("microphone-owned", await app.Host.PrepareForUpdateAsync(token));
        input.Held = false;
        await UntilAsync(() => lease.Muted);
        Assert.Equal("microphone-owned", await app.Host.PrepareForUpdateAsync(token));
        Assert.Equal(AppTheme.Dark, app.Harness.SettingsService.LoadSettings().App.Theme);
        Assert.Equal(0, lease.Restores);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await UntilAsync(() => !service.IsControlling);
        Assert.Null(await app.Host.PrepareForUpdateAsync(token));
        Assert.Equal(AppTheme.Light, app.Harness.SettingsService.LoadSettings().App.Theme);
    });

    [Fact]
    public Task UpdateRestart_DefersHoldToMuteAndAllowsRestartOnlyAfterNormalRelease() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var lease = new Lease(false);
        var input = new Input();
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        await using var app = new UpdatePreparationFixture(service);
        app.Harness.ViewModel.Theme = AppTheme.Light;
        CancellationToken token = TestContext.Current.CancellationToken;
        service.Begin(true, () => input.Held);
        await UntilAsync(() => lease.Muted);
        Assert.Equal("microphone-owned", await app.Host.PrepareForUpdateAsync(token));
        Assert.False(app.Host.IsUpdateAdmissionBlocked);
        Assert.Equal(AppTheme.Dark, app.Harness.SettingsService.LoadSettings().App.Theme);
        Assert.Equal(0, lease.Restores);
        Assert.True(lease.Muted);
        input.Held = false;
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await UntilAsync(() => !service.IsControlling);
        Assert.Null(await app.Host.PrepareForUpdateAsync(token));
        Assert.False(lease.Muted);
    });

    [Fact]
    public Task UpdateRestart_RejectsFailedSaveAndRechecksMicrophoneAfterDraining() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { },
            () => throw new InvalidOperationException("Update preparation must not open the microphone."));
        await using var app = new UpdatePreparationFixture(service);
        var vm = app.Harness.ViewModel;
        vm.Theme = AppTheme.Light;
        CancellationToken token = TestContext.Current.CancellationToken;
        string settingsPath = app.Harness.SettingsService.GetSettingsPath();
        app.Harness.SettingsService.OverrideWriteTargetsForTests(app.Workspace.PrimaryDir, app.Workspace.FallbackDir);
        try
        {
            Assert.Equal("unsaved-changes", await app.Host.PrepareForUpdateAsync(token));
            Assert.False(app.Host.IsUpdateAdmissionBlocked);
            Assert.True(vm.HasPendingUpdateEdits);
        }
        finally
        {
            app.Harness.SettingsService.OverrideWriteTargetsForTests(settingsPath, System.IO.Path.Combine(app.Workspace.FallbackDir, "settings.json"));
        }

        Assert.Null(await app.Host.PrepareForUpdateAsync(token));
        app.Host.CancelUpdatePreparation();
        Assert.Equal(AppTheme.Light, app.Harness.SettingsService.LoadSettings().App.Theme);

        var saveGate = TestPrivateAccess.GetField<SemaphoreSlim>(vm, "_settingsWriteSemaphore");
        await saveGate.WaitAsync(token);
        Task<string?> preparation = app.Host.PrepareForUpdateAsync(token);
        string? result;
        try
        {
            Assert.False(preparation.IsCompleted);
            Assert.True(app.Host.IsUpdateAdmissionBlocked);
            service.Begin(true, () => true);
            Assert.False(service.IsControlling);
            // Simulate an enabled mode appearing in the saved settings while preparation is draining.
            vm.SettingsPushToTalkHotkeyDraft = "Ctrl + Alt + F9";
            vm.SettingsPushToTalkEnabledDraft = true;
            vm.CurrentSettings!.Hotkeys.Mute.PushToTalk = vm.SettingsPushToTalkHotkeyDraft;
            vm.CurrentSettings!.Hotkeys.Mute.PushToTalkEnabled = true;
            Assert.False(vm.HasPendingUpdateEdits);
        }
        finally
        {
            saveGate.Release();
            result = await preparation;
        }
        Assert.Equal("microphone-owned", result);
        Assert.False(app.Host.IsUpdateAdmissionBlocked);
        Assert.Equal(AppTheme.Light, app.Harness.SettingsService.LoadSettings().App.Theme);
    });

    private sealed class UpdatePreparationFixture : IAsyncDisposable
    {
        private readonly AppMainWindowManager _manager;
        private readonly AppTrayIconService _tray;
        private readonly AppRuntimeStartupResumeCoordinator _resume;
        internal TestSettingsWorkspace Workspace { get; } = new(nameof(UpdatePreparationFixture));
        internal AppViewModelHarnessBuilder.AppViewModelInteractionHarness Harness { get; }
        internal AppRuntimeHost Host { get; }

        internal UpdatePreparationFixture(MicrophoneHoldService microphone)
        {
            TestExecutionGuards.EnsureSharedWpfApplication();
            Harness = AppViewModelHarnessBuilder.CreateInteractionHarness(Workspace, Dispatcher.CurrentDispatcher);
            var settings = new Settings { App = new() { Theme = AppTheme.Dark, AutoSaveEnabled = true } };
            settings.DeviceSwitching.Output.HotkeysEnabled = false;
            settings.DeviceSwitching.Input.HotkeysEnabled = false;
            Harness.SetCachedSettings(settings);
            Harness.SettingsService.SaveSettings(settings);
            _manager = new(Application.Current, Harness.Messages);
            _tray = new(Application.Current, Harness.Messages);
            _resume = new(Logger.Instance, Harness.ViewModel, default,
                _ => throw new InvalidOperationException("Unexpected resume work."), _ => Task.CompletedTask, shutdown: null);

            // Attach the live services used by update preparation without bringing up the desktop shell.
            Host = (AppRuntimeHost)RuntimeHelpers.GetUninitializedObject(typeof(AppRuntimeHost));
            TestPrivateAccess.SetField(Host, "_application", Application.Current);
            TestPrivateAccess.SetField(Host, "_appVm", Harness.ViewModel);
            TestPrivateAccess.SetField(Host, "_windowManager", _manager);
            TestPrivateAccess.SetField(Host, "_trayService", _tray);
            TestPrivateAccess.SetField(Host, "_startupResumeCoordinator", _resume);
            TestPrivateAccess.SetField(Host, "_microphoneHold", microphone);
            TestPrivateAccess.SetField(Host, "_updateDisabledWindows", new List<Window>());
        }

        public async ValueTask DisposeAsync()
        {
            Host.CancelUpdatePreparation();
            _resume.Dispose();
            _tray.Dispose();
            await _manager.DisposeAsync();
            Harness.Dispose();
            Workspace.Dispose();
        }
    }

    [Fact]
    public async Task PushToTalk_MissingHookReleaseRemutesFromIndependentRelease()
    {
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None)
        {
            OnPressed = held => service.Begin(false, held),
        };
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        state.Press();
        state.CapturePressCallback()();
        await UntilAsync(() => !lease.Muted);
        state.ReleasePhysical(0);
        await UntilAsync(() => lease.Muted);
        Assert.True(state.WasPhysicallyReleased);
        Assert.False(state.IsHeld());
    }

    [Fact]
    public async Task EnablePushToTalkDuringHoldToMute_TransfersLeaseWithoutUnmuting()
    {
        var lease = new Lease(false);
        int captures = 0;
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => { captures++; return lease; });
        service.Begin(true, () => true);
        await UntilAsync(() => lease.Applied.Count == 1);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => lease.Applied.Count >= 2);
        Assert.All(lease.Applied, Assert.True);
        Assert.Equal(1, captures);
        Assert.Equal(0, lease.Restores);
        Assert.False(lease.Disposed.Task.IsCompleted);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
    }
    private sealed class Lease(bool initial) : IMicrophoneMuteLease
    {
        private readonly bool _initial = initial;
        public readonly ConcurrentQueue<bool> Applied = new();
        public volatile bool Muted = initial;
        public volatile bool ThrowOnApply;
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Restores;
        public int Refreshes;
        public Action? BeforeRestore;
        public Action? OnApply;
        public bool Apply(bool muted, bool enforce = false, bool rollbackOnFailure = false)
        {
            Muted = muted;
            Applied.Enqueue(muted);
            OnApply?.Invoke();
            if (ThrowOnApply) throw new InvalidOperationException("simulated endpoint removal");
            return true;
        }
        public bool Restore() { BeforeRestore?.Invoke(); Muted = _initial; Interlocked.Increment(ref Restores); return true; }
        public bool? ReadMuteState() => Muted;
        public bool RefreshResult { get; set; } = true;
        public bool Refresh() { Interlocked.Increment(ref Refreshes); return RefreshResult; }
        public void Dispose() => Disposed.TrySetResult();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HoldToMute_RestoresOriginalStateOnRelease(bool original)
    {
        var input = new Input();
        var lease = new Lease(original);
        var feedback = new ConcurrentQueue<MicrophoneHoldFeedback>();
        await using var service = new MicrophoneHoldService(Logger.Instance, feedback.Enqueue, () => lease);
        Assert.False(service.IsControlling);
        service.Begin(true, () => input.Held);
        Assert.True(service.IsControlling);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        Assert.True(lease.Muted);
        input.Held = false;
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(original, lease.Muted);
        Assert.Equal(1, lease.Restores);
        await UntilAsync(() => !service.IsControlling);
        await UntilAsync(() => feedback.Any(item => item.Title == "Hold-to-mute released"));
        Assert.Contains(feedback, item => item.Title == "Hold-to-mute" && item.Muted == true);
        var released = Assert.Single(feedback, item => item.Title == "Hold-to-mute released");
        Assert.Equal(original, released.Muted);
        Assert.Equal(original ? "Microphone muted" : "Microphone live", released.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushToTalk_MutesBeforeFirstPressAndAfterReleaseUntilDisabled(bool original)
    {
        var input = new Input();
        var lease = new Lease(original);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        Assert.True(lease.Muted);
        service.Begin(false, () => input.Held);
        await UntilAsync(() => !lease.Muted);
        input.Held = false;
        await UntilAsync(() => lease.Muted);
        Assert.False(lease.Disposed.Task.IsCompleted);
        Assert.Equal(0, lease.Restores);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(original, lease.Muted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task OverlappingHoldToMute_TakesPriorityOverPushToTalk()
    {
        var talk = new Input();
        var mute = new Input();
        var lease = new Lease(false);
        int captures = 0;
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => { captures++; return lease; });
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Begin(false, () => talk.Held);
        await UntilAsync(() => !lease.Muted);
        service.Begin(true, () => mute.Held);
        await UntilAsync(() => lease.Muted);
        mute.Held = false;
        await UntilAsync(() => !lease.Muted);
        talk.Held = false;
        await UntilAsync(() => lease.Muted);
        Assert.Equal(1, captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelHold_RestoresUnlessSupersededAndDoesNotRearmWhileHeld(bool superseded)
    {
        var input = new Input();
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        Assert.False(service.IsControlling);
        service.Begin(true, () => input.Held);
        Assert.True(service.IsControlling);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Cancel(abandonRestoration: superseded);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        service.Begin(true, () => input.Held);
        Assert.Single(lease.Applied);
        Assert.Equal(superseded ? 0 : 1, lease.Restores);
    }

    [Fact]
    public async Task CancelPushToTalk_RemutesAndRetainsPreModeState()
    {
        var input = new Input();
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Begin(false, () => input.Held);
        await UntilAsync(() => !lease.Muted);
        service.Cancel(abandonRestoration: true);
        await UntilAsync(() => lease.Muted);
        service.Begin(false, () => input.Held);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
    }

    [Fact]
    public async Task DeviceRefresh_CancelsSpeakingWithoutRestoringUnchangedEndpoints()
    {
        var input = new Input();
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Begin(false, () => input.Held);
        await UntilAsync(() => !lease.Muted);
        service.RefreshEndpoints();
        await UntilAsync(() => lease.Refreshes == 1 && lease.Muted);
        Assert.Equal(0, lease.Restores);
        Assert.False(lease.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task DeviceStateHotplug_WhenCaptureEndpointsUnchanged_DoesNotInterruptSpeakingOrHoldToMute()
    {
        var input = new Input();
        var lease = new Lease(false) { RefreshResult = false };
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Begin(false, () => input.Held);
        await UntilAsync(() => !lease.Muted);
        int appliedBefore = lease.Applied.Count;

        service.RefreshEndpointsIfChanged();
        await UntilAsync(() => lease.Refreshes == 1);

        Assert.False(lease.Muted);
        Assert.Equal(appliedBefore, lease.Applied.Count);
        Assert.Equal(0, lease.Restores);

        lease.RefreshResult = true;
        service.RefreshEndpointsIfChanged();
        await UntilAsync(() => lease.Refreshes == 2 && lease.Muted);
    }

    [Fact]
    public async Task NotifyInputReleased_WakesHoldLoopImmediatelyWithoutWaitingForPollTimeout()
    {
        var input = new Input();
        var lease = new Lease(false);
        int pollWaitCount = 0;
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease, async (signal, delayMs) =>
        {
            if (delayMs == 16)
            {
                Interlocked.Increment(ref pollWaitCount);
                return await signal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            return await signal.WaitAsync(delayMs, TestContext.Current.CancellationToken);
        });
        service.Begin(true, () => input.Held);
        await UntilAsync(() => lease.Muted && Volatile.Read(ref pollWaitCount) >= 1);

        input.Held = false;
        service.NotifyInputReleased();
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task ShutdownWhileSpeaking_RestoresAndDisposesModeLease()
    {
        var lease = new Lease(true);
        var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        service.Begin(false, () => true);
        await UntilAsync(() => !lease.Muted);
        await service.DisposeAsync();
        Assert.True(lease.Muted);
        Assert.True(lease.Disposed.Task.IsCompleted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task SessionEndingRestoresMicrophoneEvenWhenSettingsOutliveThePreparationBudget()
    {
        var lease = new Lease(true);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        service.Begin(false, () => true);
        await UntilAsync(() => !lease.Muted);
        var settings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            AudioPilot.Tests.Helpers.TestExecutionGuards.RunSta(() =>
                Assert.False(AudioPilot.Coordinators.AppSessionEndingCoordinator.Prepare(false,
                    System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    () => Task.WhenAll(service.DisposeAsync().AsTask(), settings.Task),
                    TimeSpan.FromMilliseconds(250), ex => Assert.Fail(ex.ToString()))));
        }
        finally { settings.TrySetResult(); }
        await service.DisposeAsync();
        Assert.True(lease.Muted);
        Assert.True(lease.Disposed.Task.IsCompleted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task EndpointAndFeedbackFailures_ReleaseTemporaryHoldLease()
    {
        var lease = new Lease(false) { ThrowOnApply = true };
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => throw new InvalidOperationException("feedback"), () => lease);
        service.Begin(true, () => true);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task DisabledPushToTalkOrReleasedHold_DoesNotOpenMicrophone()
    {
        int captures = 0;
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => { captures++; return new Lease(true); });
        service.Begin(false, () => true);
        service.Begin(true, () => false);
        Assert.Equal(0, captures);
    }

    [Fact]
    public async Task PushToTalk_TransientFailureRetainsOriginalStateAndRetriesMuted()
    {
        var lease = new Lease(false);
        int captures = 0;
        var feedback = new ConcurrentQueue<MicrophoneHoldFeedback>();
        await using var service = new MicrophoneHoldService(Logger.Instance, feedback.Enqueue, () => { captures++; return lease; });
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        lease.OnApply = () => { lease.OnApply = null; throw new InvalidOperationException("temporary failure"); };
        service.Begin(false, () => true);
        await UntilAsync(() => feedback.Any(item => item.Title == "Microphone control unavailable"));
        await UntilAsync(() => lease.Muted);
        Assert.True(Assert.Single(feedback, item => item.Title == "Microphone control unavailable").MuteUnverified);
        Assert.Equal(1, captures);
        Assert.False(lease.Disposed.Task.IsCompleted);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
    }

    [Fact]
    public async Task PushToTalk_DisableDuringFailureIsNotLost()
    {
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        lease.OnApply = () =>
        {
            lease.OnApply = null;
            service.SetPushToTalkEnabled(false);
            throw new InvalidOperationException("failure during disable");
        };
        service.SetPushToTalkEnabled(true);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushToTalk_DisablingHonorsNewerExplicitMutePreference(bool preference)
    {
        var lease = new Lease(!preference);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        service.SetPushToTalkEnabled(true);
        await UntilAsync(() => !lease.Applied.IsEmpty);
        service.Cancel(abandonRestoration: true, mutePreference: preference);
        service.SetPushToTalkEnabled(false);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(preference, lease.Muted);
        Assert.Equal(0, lease.Restores);
    }

    [Fact]
    public async Task PushToTalk_PersistentFailureStopsRetryingAndStillRestoresOnShutdown()
    {
        var lease = new Lease(false) { ThrowOnApply = true };
        var retryDelays = new List<int>();
        var settled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease, (signal, delay) =>
        {
            if (delay != Timeout.Infinite && retryDelays.Count < 3)
            {
                retryDelays.Add(delay);
                return Task.FromResult(false);
            }

            settled.TrySetResult(delay);
            return signal.WaitAsync(Timeout.Infinite);
        });
        try
        {
            service.SetPushToTalkEnabled(true);
            Assert.Equal(Timeout.Infinite, await settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal([100, 200, 400], retryDelays);
            Assert.Equal(4, lease.Applied.Count);
            Assert.False(lease.Disposed.Task.IsCompleted);
        }
        finally { await service.DisposeAsync(); }
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
    }

    [Fact]
    public async Task PushToTalk_RetriesMuteWhenTheEndpointChangesAsTheRetryBudgetRunsOut()
    {
        var lease = new Lease(false);
        var muted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new MicrophoneHoldService(Logger.Instance, feedback =>
        {
            if (feedback.Muted == true) muted.TrySetResult();
        }, () => lease);
        lease.OnApply = () =>
        {
            if (lease.Applied.Count > 4) return;
            if (lease.Applied.Count == 4) service.OnEndpointChanged();
            throw new InvalidOperationException("Endpoint unavailable during wake.");
        };

        service.SetPushToTalkEnabled(true);
        await muted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(5, lease.Applied.Count);
    }

    [Fact]
    public async Task HoldFailure_DoesNotRestoreOverANewerExplicitMuteChoice()
    {
        var lease = new Lease(false);
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        lease.OnApply = () =>
        {
            service.Cancel(abandonRestoration: true, mutePreference: true);
            throw new InvalidOperationException("failure after user muted");
        };
        service.Begin(true, () => true);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(lease.Muted);
        Assert.Equal(0, lease.Restores);
    }

    [Fact]
    public async Task RepressDuringRestoration_StartsAfterTheOriginalLeaseIsReleased()
    {
        var firstInput = new Input();
        var secondInput = new Input();
        var first = new Lease(false);
        var second = new Lease(false);
        int captures = 0;
        await using var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => Interlocked.Increment(ref captures) == 1 ? first : second);
        first.BeforeRestore = () => service.Begin(true, () => secondInput.Held);
        service.Begin(true, () => firstInput.Held);
        await UntilAsync(() => !first.Applied.IsEmpty);
        firstInput.Held = false;
        await UntilAsync(() => !second.Applied.IsEmpty);
        Assert.True(first.Disposed.Task.IsCompleted);
        Assert.False(first.Muted);
        secondInput.Held = false;
        await second.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(second.Muted);
        Assert.Equal(2, captures);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
