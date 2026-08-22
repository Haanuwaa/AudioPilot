using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineMultiInstanceRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentLaunches_RouteBothTrees_RetryOnlyPendingWork_AndRestoreAfterLastTrigger(bool reverseExitOrder)
    {
        using var logger = new TestLoggerScope(nameof(RoutineMultiInstanceRoutingTests), "instances.log");
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine();
        RoutineProcessSnapshot[] processes = [new(100, routine.TargetAppPath, 0, StartTimeUtcTicks: 1),
            new(200, routine.TargetAppPath, 0, StartTimeUtcTicks: 2), new(201, routine.TargetAppPath, 200, StartTimeUtcTicks: 3)];
        var initialLease = lifetime.RegisterLease(routine, 100, true, true, true, processes[0], TestContext.Current.CancellationToken)!.Value.Lease;
        var first = lifetime.Register(routine, 100, new("old-output", "Speakers", "old-input", "Mic", 45, false, 60, true),
            processes[0], initialLease, TestContext.Current.CancellationToken);
        foreach (RoutineProcessSnapshot process in processes.Skip(1))
        {
            var matches = RoutineApplicationRouting.EvaluateRoutineAppStartMatchesForProcess([routine], process);
            var plan = Assert.Single(AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(matches, process, lifetime.GetLeases(), processes));
            Assert.Equal(process.ProcessId == 200 ? RoutineAppStartMatchExecutionAction.Execute : RoutineAppStartMatchExecutionAction.SkipExistingActiveLease, plan.Action);
            Assert.True(lifetime.TryJoinActiveRoutine(plan.Match.Routine, process.ProcessId, process, true,
                plan.Action == RoutineAppStartMatchExecutionAction.Execute, TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, lifetime.LeaseCount);
        Assert.Equal(3, lifetime.Sessions.Count);
        Assert.All(lifetime.Sessions, session =>
        {
            Assert.Equal(first.ActivationSequence, session.ActivationSequence);
            Assert.Equal(first.RestoreSnapshot, session.RestoreSnapshot);
        });
        List<(uint Pid, bool Output)> writes = [];
        bool microphoneReady = false;
        async Task Refresh() => await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
            lifetime.GetLeases(), processes, [], RoutineApplicationRouting.CollectRoutineAppOutputCandidateProcessIds,
            (_, pid) => { writes.Add((pid, true)); return Task.FromResult(true); },
            (_, pid) => { writes.Add((pid, false)); return Task.FromResult(microphoneReady); },
            (_, _, _) => Task.CompletedTask, lifetime.MarkLeaseProcessApplied, lifetime.MarkLeaseOverlayShown,
            _ => { }, logger.Logger, TestContext.Current.CancellationToken);
        await Refresh();
        Assert.Equal(new[] { (200u, true), (200u, false), (201u, true), (201u, false) }, writes);
        writes.Clear();
        microphoneReady = true;
        await Refresh();
        Assert.Equal(new[] { (200u, false), (201u, false) }, writes);
        writes.Clear();
        await Refresh();
        Assert.Empty(writes);
        Assert.Equal((0, 0), lifetime.PendingLeaseCounts);

        var sessions = lifetime.CaptureDeactivations(static _ => true);
        if (reverseExitOrder) sessions = [.. sessions.Reverse()];
        int ended = 0;
        foreach (var deactivation in sessions)
            await lifetime.DeactivateAsync(deactivation, (session, restore) =>
            {
                bool last = ++ended == sessions.Count;
                Assert.Equal(last, restore);
                var plans = lifetime.ReleaseSessionRouting(session, restore);
                if (!last) Assert.Empty(plans);
                else
                {
                    Assert.Equal([100, 200], plans.Select(static plan => plan.Lease.RootProcessId).Order());
                    Assert.All(plans, plan => { Assert.True(plan.ResetOutput); Assert.True(plan.ResetInput); });
                }
                return Task.CompletedTask;
            });
        Assert.Empty(lifetime.Sessions);
        Assert.Empty(lifetime.GetLeases());
    }

    [Fact]
    public async Task JoiningAnotherInstance_PreservesNewerOwners_AndDoesNotAdoptUnrelatedManualLease()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine();
        var lease = lifetime.RegisterLease(routine, 100, true, true, true, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.Register(routine, 100, null, routingLease: lease, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(lifetime.TryJoinActiveRoutine(routine, 200, new(200, routine.TargetAppPath), true, true, TestContext.Current.CancellationToken));
        lifetime.RegisterLease(routine, 300, true, true, true, cancellationToken: TestContext.Current.CancellationToken);
        AudioRoutine newer = routine.Clone();
        newer.Id = "newer";
        newer.InputDeviceId = "";
        lifetime.RegisterLease(newer, 200, true, false, true, cancellationToken: TestContext.Current.CancellationToken);
        List<(int Root, bool Output, bool Input)> resets = [];
        foreach (var deactivation in lifetime.CaptureDeactivations(static _ => true))
            await lifetime.DeactivateAsync(deactivation, (session, restore) =>
            {
                resets.AddRange(lifetime.ReleaseSessionRouting(session, restore).Select(static plan => (plan.Lease.RootProcessId, plan.ResetOutput, plan.ResetInput)));
                return Task.CompletedTask;
            });
        Assert.Equal(new[] { (100, true, true), (200, false, true) }, resets);
        Assert.Equal(2, lifetime.LeaseCount);
        Assert.Contains(lifetime.GetLeases(), item => item.RootProcessId == 300);
        Assert.Contains(lifetime.GetLeases(), item => item.RoutineId == "newer");
    }

    [Fact]
    public async Task ReusedProcessId_ReplacesSessionAndLease_WithoutAllowingOldDeactivationToReleaseThem()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine();
        var original = new RoutineProcessSnapshot(100, routine.TargetAppPath, 0, StartTimeUtcTicks: 1);
        var replacement = original with { StartTimeUtcTicks = 2 };
        var lease = lifetime.RegisterLease(routine, 100, true, true, true, original, TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.Register(routine, 100, null, original, lease, TestContext.Current.CancellationToken);
        var oldDeactivation = Assert.Single(lifetime.CaptureDeactivations(static _ => true));
        Assert.True(lifetime.TryJoinActiveRoutine(routine, 100, replacement, true, true, TestContext.Current.CancellationToken));
        await lifetime.DeactivateAsync(oldDeactivation, (_, _) => throw new InvalidOperationException("Old session was released."));
        Assert.Equal(replacement, Assert.Single(lifetime.Sessions).ProcessIdentity);
        Assert.Equal(replacement, Assert.Single(lifetime.GetLeases()).ProcessIdentity);
        Assert.Empty(Assert.Single(lifetime.GetLeases()).AppliedOutputProcessIds);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => lifetime.TryJoinActiveRoutine(routine, 200, new(200, routine.TargetAppPath), true, true, cancellation.Token));
        Assert.Single(lifetime.GetLeases());
        lifetime.Stop();
    }

    [Fact]
    public void DifferentActionTarget_DoesNotRouteTheTriggerApplicationWhenAnotherInstanceLaunches()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine routine = CreateRoutine();
        routine.TargetAppPath = @"C:\Apps\Other.exe";
        var lease = lifetime.RegisterLease(routine, 300, true, true, true, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        lifetime.Register(routine, 100, null, routingLease: lease, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(lifetime.TryJoinActiveRoutine(routine, 200, new(200, routine.TriggerAppPath), true, true, TestContext.Current.CancellationToken));
        Assert.Equal(300, Assert.Single(lifetime.GetLeases()).RootProcessId);
        Assert.Equal(2, lifetime.Sessions.Count);
        lifetime.Stop();
    }

    private static AudioRoutine CreateRoutine() => new()
    {
        Id = "app",
        TriggerKind = RoutineTriggerKind.Application,
        TriggerAppPath = @"C:\Apps\Player.exe",
        RouteToApplication = true,
        TargetAppPath = @"C:\Apps\Player.exe",
        OutputDeviceId = "speakers",
        InputDeviceId = "mic",
        MasterVolumePercent = 70,
        MicVolumePercent = 80,
        InputMuteAction = RoutineMuteAction.Unmute,
        RestorePreviousAudioOnDeactivate = true,
    };
}
