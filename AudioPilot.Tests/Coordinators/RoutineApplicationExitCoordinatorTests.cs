using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class RoutineApplicationExitCoordinatorTests
{
    private const string Target = @"C:\Apps\Player.exe";
    private static AudioRoutine Routine() => new()
    {
        Id = "exit",
        Name = "After player",
        MasterVolumePercent = 30,
        TriggerKind = RoutineTriggerKind.Application,
        ApplicationTriggerMode = ApplicationTriggerMode.AppExit,
        TriggerAppPath = Target
    };

    [Fact]
    public void LastInstanceExit_FiresOnceAndCanFireAgainAfterRestart()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        Assert.True(coordinator.NeedsBaseline);
        coordinator.Seed([new(1, Target), new(2, Target)], coordinator.Generation);
        Assert.False(coordinator.NeedsBaseline);
        Assert.Empty(coordinator.ObserveStop(1, [new(2, Target)]).Routines);
        var pending = Assert.Single(coordinator.ObserveStop(2, []).Routines);
        Assert.True(coordinator.IsCurrent(pending));
        Assert.True(coordinator.TryClaim(pending));
        Assert.False(coordinator.TryClaim(Assert.Single(coordinator.ObserveStop(2, []).Routines)));
        coordinator.Complete(pending);
        Assert.Empty(coordinator.ObserveStop(2, []).Routines);
        var started = coordinator.BeginStart(3);
        coordinator.CompleteStart(started, new(3, Target));
        var next = Assert.Single(coordinator.ObserveStop(3, []).Routines);
        Assert.NotSame(pending, next);
        coordinator.Complete(pending);
        Assert.True(coordinator.IsCurrent(next));
    }

    [Fact]
    public async Task ConcurrentStopWorkers_ClaimAnExitOnlyOnce()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        var pending = Assert.Single(coordinator.ObserveStop(1, []).Routines);
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => coordinator.TryClaim(pending), TestContext.Current.CancellationToken)));
        Assert.Single(claims, static claimed => claimed);
    }

    [Fact]
    public void InitialAbsenceUnrelatedStopsAndUncertainDiscovery_DoNotInventExits()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        coordinator.Seed([], coordinator.Generation);
        Assert.Empty(coordinator.ObserveStop(5, []).Routines);
        var started = coordinator.BeginStart(1);
        coordinator.CompleteStart(started, new(1, Target));
        Assert.False(coordinator.ShouldObserveStop(5));
        var (Routines, Retry) = coordinator.ObserveStop(1, [new(2, "", ProcessName: "Player", IdentityUnavailable: true)]);
        Assert.True(Retry);
        Assert.Empty(Routines);
        Assert.Single(coordinator.ObserveStop(1, []).Routines);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("configuration")]
    [InlineData("pause")]
    [InlineData("removed")]
    public void ObsoletePendingExit_IsRejected(string reason)
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        var routine = Routine();
        coordinator.Configure([routine]);
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        var pending = Assert.Single(coordinator.ObserveStop(1, []).Routines);
        if (reason == "restart") coordinator.CompleteStart(coordinator.BeginStart(2), new(2, Target));
        else if (reason == "configuration") { var edited = routine.Clone(); edited.MasterVolumePercent = 60; coordinator.Configure([edited]); }
        else if (reason == "removed") coordinator.Configure([]);
        else coordinator.Reset();
        Assert.False(coordinator.IsCurrent(pending));
        Assert.False(coordinator.TryClaim(pending));
    }

    [Fact]
    public async Task Reset_CompletesPendingStartWaitsAndRejectsAnOldBaseline()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        long generation = coordinator.Generation;
        var started = coordinator.BeginStart(1);
        Task wait = coordinator.PendingStart(1);
        Assert.False(wait.IsCompleted);
        coordinator.Reset();
        await wait.WaitAsync(TestContext.Current.CancellationToken);
        coordinator.CompleteStart(started, new(1, Target));
        coordinator.Seed([new(1, Target)], generation);
        coordinator.Seed([], coordinator.Generation);
        Assert.Empty(coordinator.ObserveStop(1, []).Routines);
    }

    [Fact]
    public void OldStopObservation_CannotApplyToTheNewBaseline()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        long generation = coordinator.Generation;
        coordinator.Reset();
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        Assert.Empty(coordinator.ObserveStop(1, [], generation).Routines);
        Assert.False(coordinator.ForgetUnverifiedStop(1, generation, coordinator.ObservationVersion));
        Assert.Single(coordinator.ObserveStop(1, [], coordinator.Generation).Routines);
    }

    [Fact]
    public void UnverifiedStops_CanBeDiscardedWithoutRetainingDeadProcessIds()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        coordinator.ObserveStop(1, [new(2, "", IdentityUnavailable: true)]);
        Assert.True(coordinator.ForgetUnverifiedStop(1, coordinator.Generation, coordinator.ObservationVersion));
        Assert.False(coordinator.ShouldObserveStop(1));
        Assert.Empty(coordinator.ObserveStop(1, []).Routines);
        coordinator.CompleteStart(coordinator.BeginStart(3), new(3, Target));
        Assert.Single(coordinator.ObserveStop(3, []).Routines);
    }

    [Fact]
    public void StartDuringDiscovery_RejectsAnOlderEmptySnapshot()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        long observation = coordinator.ObservationVersion;
        coordinator.CompleteStart(coordinator.BeginStart(2), new(2, Target));
        var (Routines, Retry) = coordinator.ObserveStop(1, [], coordinator.Generation, observation);
        Assert.True(Retry);
        Assert.Empty(Routines);
        Assert.Empty(coordinator.ObserveStop(1, [new(2, Target)], coordinator.Generation, coordinator.ObservationVersion).Routines);
        Assert.Single(coordinator.ObserveStop(2, [], coordinator.Generation, coordinator.ObservationVersion).Routines);
    }

    [Fact]
    public void UnresolvedNewStart_IsCheckedWithoutDelayingUnrelatedResolvedStarts()
    {
        var coordinator = new RoutineApplicationExitCoordinator();
        coordinator.Configure([Routine()]);
        coordinator.Seed([new(1, Target)], coordinator.Generation);
        long observation = coordinator.ObservationVersion;
        var unrelated = coordinator.BeginStart(2);
        Assert.True(coordinator.ObserveStop(1, [], coordinator.Generation, observation).Retry);
        coordinator.CompleteStart(unrelated, new(2, @"C:\Apps\Other.exe"));
        Assert.False(coordinator.ObserveStop(1, [], coordinator.Generation, observation).Retry);
        Assert.Single(coordinator.ObserveStop(1, [], coordinator.Generation, observation).Routines);
    }

    [Fact]
    public void ExitMode_RoundTripsWithoutBecomingAStatefulTrigger()
    {
        var routine = Routine();
        using var editor = new RoutineEditorViewModel([], [], routine);
        Assert.Equal("When application exits", editor.SelectedApplicationTriggerMode);
        Assert.False(editor.IsStatefulTriggerSelected);
        var saved = editor.BuildRoutine();
        Assert.Equal(ApplicationTriggerMode.AppExit, saved.ApplicationTriggerMode);
        Assert.False(saved.IsStatefulTrigger);
        Assert.False(Assert.Single(saved.Triggers).IsStateful);
        Assert.Contains("exit", saved.TriggerSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(saved.ValidateTriggers());
    }
}
