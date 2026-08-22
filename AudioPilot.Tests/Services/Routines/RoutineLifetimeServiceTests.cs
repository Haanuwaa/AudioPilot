using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineLifetimeServiceTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndingTrigger_ReleasesTheSeparateActionTarget_WithoutOverridingOtherOwners(bool anotherOwner)
    {
        var lifetime = new RoutineLifetimeService();
        var routine = CreateRoutine("one");
        routine.TargetAppPath = "target.exe";
        routine.SwitchOutputPerApp = true;
        routine.OutputDeviceId = "out";
        routine.InputDeviceId = "in";
        var lease = lifetime.RegisterLease(routine, 84, true, true, true, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.Register(routine, 42, null, routingLease: lease, cancellationToken: TestContext.Current.CancellationToken);
        if (anotherOwner)
        {
            var other = routine.Clone();
            other.Id = "other";
            other.InputDeviceId = "";
            lifetime.RegisterLease(other, 84, true, false, true, cancellationToken: TestContext.Current.CancellationToken);
        }
        await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(static _ => true)), (session, restore) =>
        {
            var (Lease, ResetOutput, ResetInput) = lifetime.ReleaseSessionRouting(session, restore)!.Value;
            Assert.Equal(84, Lease.RootProcessId);
            Assert.Equal(!anotherOwner, ResetOutput);
            Assert.True(ResetInput);
            return Task.CompletedTask;
        });
        Assert.Equal(anotherOwner ? 1 : 0, lifetime.LeaseCount);
    }

    [Fact]
    public async Task EndingOldTrigger_DoesNotReleaseRoutingForReusedProcessId()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = CreateRoutine("one");
        routine.TargetAppPath = "target.exe";
        routine.OutputDeviceId = "out";
        var lease = lifetime.RegisterLease(routine, 84, true, false, true, new(84, "target.exe", StartTimeUtcTicks: 1), cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.Register(routine, 42, null, routingLease: lease, cancellationToken: TestContext.Current.CancellationToken);
        var replacement = lifetime.RegisterLease(routine, 84, true, false, true, new(84, "target.exe", StartTimeUtcTicks: 2), cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(static _ => true)), (session, restore) =>
        {
            Assert.Null(lifetime.ReleaseSessionRouting(session, restore));
            return Task.CompletedTask;
        });
        Assert.Equal(replacement.Generation, Assert.Single(lifetime.GetLeases()).Generation);
    }

    [Fact]
    public void NonApplicationRoutingLease_SurvivesRefreshButNotTargetEdits()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = new AudioRoutine { Id = "scheduled", Enabled = true, TriggerKind = RoutineTriggerKind.Scheduled, SwitchOutputPerApp = true, TargetAppPath = @"C:\Apps\target.exe", OutputDeviceId = "out" };
        lifetime.RegisterLease(routine, 84, true, false, true, cancellationToken: TestContext.Current.CancellationToken);
        lifetime.SynchronizeLeases([routine]);
        Assert.Single(lifetime.GetLeases());
        routine.TargetAppPath = "other.exe";
        lifetime.SynchronizeLeases([routine]);
        Assert.Empty(lifetime.GetLeases());
    }

    [Fact]
    public async Task ManualRun_IsIndependentOfAutomaticTriggerDeactivation()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = new AudioRoutine { Id = "steam", TriggerKind = RoutineTriggerKind.SteamBigPicture };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> manual = lifetime.RunActivationAsync(routine, 0, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, isManual: true, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            lifetime.CancelTrigger(RoutineTriggerKind.SteamBigPicture);
        }
        finally { release.TrySetResult(); }
        Assert.True(await manual);
        Assert.Empty(lifetime.Sessions);
    }

    [Fact]
    public async Task ManualRun_WaitsForAutomaticActivationWithoutCancellingItOrReplacingItsRestoreState()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutineStatefulSession? original = null;
        Task<bool> automatic = lifetime.RunActivationAsync(routine, 42, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            original = lifetime.Register(routine, 42, new("original", "Speakers", "", ""), cancellationToken: token);
            return true;
        }, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<bool> manual = lifetime.RunActivationAsync(routine, 42, _ =>
        {
            Assert.Same(original, Assert.Single(lifetime.Sessions));
            return Task.FromResult(true);
        }, cancellationToken: TestContext.Current.CancellationToken, isManual: true);
        Assert.False(manual.IsCompleted);
        release.TrySetResult();
        Assert.True(await automatic);
        Assert.True(await manual);
        bool restored = false;
        await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(static _ => true)), (session, restore) =>
        {
            Assert.Same(original, session);
            restored = restore;
            return Task.CompletedTask;
        });
        Assert.True(restored);
        Assert.Empty(lifetime.Sessions);
    }

    [Fact]
    public void CancelledActivationCannotCommitARoutingLease()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        routine.TargetAppPath = @"C:\Apps\app.exe";
        routine.OutputDeviceId = "speakers";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            lifetime.RegisterLease(routine, 42, false, false, false, cancellationToken: cancellation.Token));
        Assert.Equal(0, lifetime.LeaseCount);
        Assert.Equal((0, 0), lifetime.PendingLeaseCounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StableDeviceIdentityChangeInvalidatesTheActiveRoutine(bool output)
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        lifetime.Register(routine, 42, null, cancellationToken: TestContext.Current.CancellationToken);
        AudioRoutine changed = routine.Clone();
        if (output) changed.OutputDeviceStableId = "new-stable-id";
        else changed.InputDeviceStableId = "new-stable-id";
        Assert.Single(lifetime.Synchronize([changed], []));
    }

    [Fact]
    public void ProcessSubscriptions_AreNotDuplicatedAndDetachAtShutdown()
    {
        using var monitor = new FakeProcessLifecycleMonitor();
        var lifetime = new RoutineLifetimeService();
        int starts = 0;
        int stops = 0;
        lifetime.AttachProcessMonitor(monitor, _ => starts++, _ => stops++);
        lifetime.AttachProcessMonitor(monitor, _ => starts++, _ => stops++);
        monitor.FireProcessStarted(42);
        monitor.FireProcessStopped(42);
        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        lifetime.Stop();
        monitor.FireProcessStarted(42);
        monitor.FireProcessStopped(42);
        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        Assert.Equal(0, monitor.DisposeCallCount);
    }

    [Fact]
    public void ReleasingObsoleteClaim_DoesNotReleaseReplacementAfterSettingsChange()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        var process = new RoutineProcessSnapshot(42, "app.exe");
        Assert.True(lifetime.TryClaim(routine, process, out var old, out _));
        AudioRoutine changed = routine.Clone();
        changed.OutputDeviceId = "new-target";
        lifetime.Synchronize([changed], []);
        Assert.True(lifetime.TryClaim(changed, process, out var replacement, out _));
        lifetime.ReleaseClaim(old!);
        Assert.False(lifetime.TryClaim(changed, process, out _, out _));
        lifetime.ReleaseClaim(replacement!);
        Assert.True(lifetime.TryClaim(changed, process, out _, out _));
    }

    [Fact]
    public void ExpandedTriggerClaim_UsesRoutineLeaseAndRemovesStaleProcessLease()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        routine.TargetAppPath = @"C:\Apps\app.exe";
        routine.OutputDeviceId = "speakers";
        routine.TriggerAppPath = routine.TargetAppPath;
        AudioRoutine trigger = Assert.Single(routine.ExpandAutomaticTriggers());
        var process = new RoutineProcessSnapshot(42, routine.TargetAppPath);
        lifetime.RegisterLease(trigger, 42, true, false, false, process, TestContext.Current.CancellationToken);
        Assert.False(lifetime.TryClaim(trigger, process, out _, out string reason));
        Assert.Equal("existing-active-lease", reason);
        Assert.Equal(1, lifetime.LeaseCount);

        var replacement = new RoutineProcessSnapshot(42, @"C:\Other\app.exe");
        Assert.True(lifetime.TryClaim(trigger, replacement, out var claim, out _));
        Assert.Equal(0, lifetime.LeaseCount);
        Assert.False(lifetime.TryClaim(trigger, replacement, out _, out _));
        lifetime.ReleaseClaim(claim!);
        Assert.True(lifetime.TryClaim(trigger, replacement, out _, out _));
        lifetime.Stop();
    }

    [Fact]
    public void ReplacedLease_RejectsOldCompletionEvenWhenTargetsAreIdentical()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        routine.TargetAppPath = @"C:\Apps\app.exe";
        routine.OutputDeviceId = "speakers";
        RoutineAppOutputLease old = lifetime.RegisterLease(routine, 42, false, false, false, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        RoutineAppOutputLease current = lifetime.RegisterLease(routine, 42, false, false, false, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.MarkLeaseProcessApplied(old, 42, true);
        lifetime.MarkLeaseOverlayShown(old);
        Assert.Null(lifetime.RemoveLease(old.LeaseKey, old.Generation));
        RoutineAppOutputLease remaining = Assert.Single(lifetime.GetLeases());
        Assert.Equal(current.Generation, remaining.Generation);
        Assert.Empty(remaining.AppliedOutputProcessIds);
        Assert.False(remaining.CompletionOverlayShown);
        Assert.Equal(1, lifetime.PendingLeaseCounts.Output);
    }

    [Fact]
    public async Task BatchDeactivation_RestoresOnlyItsNewestSession()
    {
        var lifetime = new RoutineLifetimeService();
        lifetime.Register(CreateRoutine("one"), 42, null, cancellationToken: TestContext.Current.CancellationToken);
        lifetime.Register(CreateRoutine("two"), 43, null, cancellationToken: TestContext.Current.CancellationToken);
        var restored = new List<string>();
        foreach (var pending in lifetime.CaptureDeactivations(static _ => true))
            await lifetime.DeactivateAsync(pending, (session, restore) =>
            {
                if (restore) restored.Add(session.RoutineId);
                return Task.CompletedTask;
            });
        Assert.Equal("two", Assert.Single(restored));
        Assert.Empty(lifetime.Sessions);
    }

    [Fact]
    public async Task Deactivation_DoesNotRemoveReplacementWithSameKey()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        lifetime.Register(routine, 42, null, cancellationToken: TestContext.Current.CancellationToken);
        RoutineLifetimeService.Deactivation old = Assert.Single(lifetime.CaptureDeactivations(static _ => true));
        RoutineStatefulSession replacement = lifetime.Register(routine, 42, null, cancellationToken: TestContext.Current.CancellationToken);
        await lifetime.DeactivateAsync(old, static (_, _) => throw new InvalidOperationException("Stale deactivation ran."));
        Assert.Same(replacement, Assert.Single(lifetime.Sessions));
    }

    [Fact]
    public async Task Deactivation_DoesNotRestoreAcrossNewerActivation()
    {
        var lifetime = new RoutineLifetimeService();
        lifetime.Register(CreateRoutine("one"), 42, null, cancellationToken: TestContext.Current.CancellationToken);
        RoutineLifetimeService.Deactivation old = Assert.Single(lifetime.CaptureDeactivations(static _ => true));
        lifetime.Register(CreateRoutine("two"), 43, null, cancellationToken: TestContext.Current.CancellationToken);
        bool? restored = null;
        await lifetime.DeactivateAsync(old, (_, restore) => { restored = restore; return Task.CompletedTask; });
        Assert.False(restored);
        Assert.Equal("two", Assert.Single(lifetime.Sessions).RoutineId);
    }

    [Fact]
    public async Task Activation_WaitsForRestorationToFinish()
    {
        var lifetime = new RoutineLifetimeService();
        lifetime.Register(CreateRoutine("one"), 42, null, cancellationToken: TestContext.Current.CancellationToken);
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task deactivation = lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(static _ => true)), async (_, restore) =>
        {
            Assert.True(restore);
            restoring.SetResult();
            await release.Task;
        });
        await restoring.Task;
        bool ran = false;
        Task<bool> activation = lifetime.RunActivationAsync(CreateRoutine("two"), 43,
            _ => { ran = true; return Task.FromResult(true); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(ran);
        release.SetResult();
        await deactivation;
        Assert.True(await activation);
    }

    [Theory]
    [InlineData("exit", false)]
    [InlineData("focus", false)]
    [InlineData("settings", false)]
    [InlineData("shutdown", false)]
    [InlineData("exit", true)]
    [InlineData("settings", true)]
    [InlineData("shutdown", true)]
    public async Task PendingActivation_IsCancelledWhenItsLifetimeEnds(string reason, bool isManual)
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> activation = lifetime.RunActivationAsync(routine, 42, async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            lifetime.Register(routine, 42, null, cancellationToken: TestContext.Current.CancellationToken);
            return true;
        }, isManual: isManual, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task;
        switch (reason)
        {
            case "exit": lifetime.CancelProcess(42); break;
            case "focus": lifetime.CancelActivation(routine, 42); break;
            case "settings": lifetime.Synchronize([], []); break;
            case "shutdown": lifetime.Stop(); break;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        await lifetime.DrainAsync();
        Assert.Empty(lifetime.Sessions);
    }

    [Fact]
    public async Task ReplacementActivation_RemainsCancellableAfterOlderActivationFinishes()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine("one");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = lifetime.RunActivationAsync(routine, 42, async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task;
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> replacement = lifetime.RunActivationAsync(routine, 42, async token =>
        {
            replacementEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await replacementEntered.Task;
        lifetime.CancelProcess(42);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
    }

    [Fact]
    public void CaptureEnded_DetectsReusedProcessIdEvenForSameExecutable()
    {
        var lifetime = new RoutineLifetimeService();
        var original = new RoutineProcessSnapshot(42, "app.exe", StartTimeUtcTicks: 100);
        lifetime.Register(CreateRoutine("one"), 42, null, original, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(lifetime.CaptureEnded([original]));
        Assert.Single(lifetime.CaptureEnded([original with { StartTimeUtcTicks = 200 }]));
    }

    [Fact]
    public async Task Routing_RechecksOwnershipAfterWaitingForActivation()
    {
        var lifetime = new RoutineLifetimeService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> activation = lifetime.RunActivationAsync(CreateRoutine("one"), 42, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task;
        bool current = true;
        Task<bool> routing = lifetime.ApplyRoutingAsync(() => current, static () => throw new InvalidOperationException("Expired routing ran."));
        current = false;
        release.SetResult();
        await activation;
        Assert.False(await routing);
    }

    private static AudioRoutine CreateRoutine(string id) => new()
    {
        Id = id,
        Enabled = true,
        TriggerKind = RoutineTriggerKind.Application,
        TriggerAppPath = "app.exe",
        RestorePreviousAudioOnDeactivate = true,
    };
}
