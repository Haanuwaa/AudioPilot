using System.Collections.Concurrent;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

[Collection("CoreAudioWorkerIsolation")]
public sealed class MicrophoneHoldServiceTests
{
    [Fact]
    public void TemporaryMute_ExternalUnmuteThenRemutePreventsRestoringOverNewChoice()
    {
        var ownership = new MicrophoneMuteOwnership(false);
        Assert.True(ownership.TryApply(false, true, enforce: false));
        ownership.Observe(ownership.Context, true);
        ownership.Observe(Guid.NewGuid(), false);
        ownership.Observe(Guid.NewGuid(), true);
        Assert.False(ownership.ShouldRestore(true));
        Assert.False(ownership.TryApply(true, true, enforce: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryMute_DelayedBaselineNotificationDoesNotCancelRestoration(bool original)
    {
        var ownership = new MicrophoneMuteOwnership(original);
        Assert.True(ownership.TryApply(original, !original, enforce: false));
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
        Assert.True(ownership.TryApply(false, true, enforce: false));
        ownership.Observe(ownership.Context, false); // A delayed notification from this lease.
        ownership.Observe(Guid.NewGuid(), true); // Volume changed, mute did not.
        Assert.True(ownership.ShouldRestore(true));
        Assert.False(ownership.ShouldRestore(false));
    }

    [Fact]
    public void PersistentPushToTalk_EnforcesMuteAndRetainsItsOriginalState()
    {
        var ownership = new MicrophoneMuteOwnership(false);
        Assert.True(ownership.TryApply(false, true, enforce: true));
        ownership.Observe(Guid.NewGuid(), false);
        Assert.True(ownership.TryApply(false, true, enforce: true));
        Assert.True(ownership.ShouldRestore(true));
        Assert.False(ownership.Original);
    }

    private sealed class Input { public volatile bool Held = true; }
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
        public bool Apply(bool muted, bool enforce = false)
        {
            Muted = muted;
            Applied.Enqueue(muted);
            OnApply?.Invoke();
            if (ThrowOnApply) throw new InvalidOperationException("simulated endpoint removal");
            return true;
        }
        public bool Restore() { BeforeRestore?.Invoke(); Muted = _initial; Interlocked.Increment(ref Restores); return true; }
        public bool? ReadMuteState() => Muted;
        public void Refresh() => Interlocked.Increment(ref Refreshes);
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
        var service = new MicrophoneHoldService(Logger.Instance, _ => { }, () => lease);
        try
        {
            service.SetPushToTalkEnabled(true);
            await UntilAsync(() => lease.Applied.Count == 4);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Equal(4, lease.Applied.Count);
            Assert.False(lease.Disposed.Task.IsCompleted);
        }
        finally { await service.DisposeAsync(); }
        Assert.False(lease.Muted);
        Assert.Equal(1, lease.Restores);
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
