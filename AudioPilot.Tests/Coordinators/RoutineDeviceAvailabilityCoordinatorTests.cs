using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class RoutineDeviceAvailabilityCoordinatorTests
{
    [Theory]
    [InlineData(DeviceAvailabilityTransition.Connected, 1, 0)]
    [InlineData(DeviceAvailabilityTransition.Disconnected, 0, 1)]
    [InlineData(DeviceAvailabilityTransition.Both, 1, 1)]
    public void Availability_UsesTransitionsAndIgnoresRepeatedSnapshots(DeviceAvailabilityTransition transition, int connected, int disconnected)
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(transition);
        IReadOnlyList<CycleDevice> devices = [];
        IReadOnlyList<CycleDevice> Read(bool playback) { Assert.True(playback); return devices; }
        Assert.Empty(coordinator.Observe([routine], Read, false));
        devices = [new() { Id = "new-id", StableId = "stable", Name = "renamed" }];
        var connection = coordinator.Observe([routine], Read, true);
        Assert.Equal(connected, connection.Count);
        foreach (var trigger in connection) coordinator.Complete(trigger);
        Assert.Empty(coordinator.Observe([routine], Read, true));
        devices = [];
        var disconnection = coordinator.Observe([routine], Read, true);
        Assert.Equal(disconnected, disconnection.Count);
        foreach (var trigger in disconnection) coordinator.Complete(trigger);
        Assert.Empty(coordinator.Observe([routine], Read, true));
    }

    [Fact]
    public void Baselines_DoNotInventConnectionsOnStartupEditsOrFailedSnapshots()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Both);
        CycleDevice[] present = [new() { Id = "out", StableId = "stable", Name = "Speaker" }];
        Assert.Empty(coordinator.Observe([routine], _ => present, false));
        Assert.Empty(coordinator.Observe([routine], _ => throw new InvalidOperationException("Unavailable snapshot"), true));
        Assert.Empty(coordinator.Observe([routine], _ => present, true));
        routine.TriggerDevice = new() { Id = "other", Playback = false };
        Assert.Empty(coordinator.Observe([routine], _ => [], false));
        Assert.Single(coordinator.Observe([routine], playback => playback ? [] : [new() { Id = "other" }], true));
        coordinator.Observe([], _ => throw new InvalidOperationException("No watched devices"), false);
        Assert.Empty(coordinator.Observe([routine], _ => present, true));
    }

    [Fact]
    public void Identity_DoesNotMatchNamesOrAmbiguousStableIds()
    {
        var reference = new RoutineDeviceReference { Id = "original", Name = "Same name", StableId = "stable" };
        Assert.False(reference.GetAvailability([new() { Id = "other", Name = "Same name" }]));
        Assert.Null(reference.GetAvailability([new() { Id = "a", StableId = "stable" }, new() { Id = "b", StableId = "stable" }]));
        Assert.True(reference.GetAvailability([new() { Id = "original" }, new() { Id = "b", StableId = "stable" }]));
    }

    [Fact]
    public void Triggers_KeepIndependentPendingTransitions()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Connected);
        routine.Triggers = [.. routine.Triggers, new() { Id = "mic", Kind = RoutineTriggerKind.DeviceAvailability,
            Device = new() { Id = "mic", Playback = false } }];
        coordinator.Observe([routine], _ => [], false);
        int reads = 0;
        var result = coordinator.Observe([routine], playback => { reads++; return [new() { Id = playback ? "out" : "mic" }]; }, true);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, reads);
        foreach (var trigger in result) coordinator.Complete(trigger);
        Assert.Empty(coordinator.Observe([routine], playback => [new() { Id = playback ? "out" : "mic" }], true));
    }

    [Fact]
    public void InterruptedExecution_RemainsPendingUntilCompleted_WithoutClearingANewerTransition()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Both);
        coordinator.Observe([routine], _ => [], false);
        var connected = Assert.Single(coordinator.Observe([routine], _ => [new() { Id = "out" }], true));
        Assert.Same(connected, Assert.Single(coordinator.Observe([routine], _ => [new() { Id = "out" }], true)));
        var disconnected = Assert.Single(coordinator.Observe([routine], _ => [], true));
        coordinator.Complete(connected);
        Assert.Same(disconnected, Assert.Single(coordinator.Observe([routine], _ => [], true)));
        coordinator.Complete(disconnected);
        Assert.Empty(coordinator.Observe([routine], _ => [], true));
    }

    [Fact]
    public void OlderConfiguration_CannotRevertAnEditOrRunItsPendingActions()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var old = Routine(DeviceAvailabilityTransition.Connected);
        coordinator.Observe([old], _ => [], false);
        var pending = Assert.Single(coordinator.Observe([old], _ => [new() { Id = "out" }], true));
        var edited = old.Clone();
        edited.MasterVolumePercent = 60;
        coordinator.Observe([edited], _ => [new() { Id = "out" }], false);
        Assert.False(coordinator.IsCurrentConfiguration(pending));
        Assert.Empty(coordinator.Observe([old], _ => [], true));
        Assert.Empty(coordinator.Observe([edited], _ => [new() { Id = "out" }], true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectionFailure_DoesNotBlockHealthyTransitionsOrInventRecoveryEvents(bool failingPlayback)
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Connected);
        routine.Triggers = [.. routine.Triggers, new() { Id = "input", Kind = RoutineTriggerKind.DeviceAvailability, Device = new() { Id = "mic", Playback = false } }];
        coordinator.Observe([routine], _ => [], false);
        var healthy = Assert.Single(coordinator.Observe([routine], direction => direction == failingPlayback ? throw new IOException("Unavailable inventory")
            : [new() { Id = direction ? "out" : "mic" }], true));
        Assert.Equal(!failingPlayback, healthy.TriggerDevice!.Playback);
        coordinator.Complete(healthy);
        var recovered = Assert.Single(coordinator.Observe([routine], direction => [new() { Id = direction ? "out" : "mic" }], true));
        Assert.Equal(failingPlayback, recovered.TriggerDevice!.Playback);
    }

    [Fact]
    public async Task BlockedDiscovery_DoesNotHoldStateLockOrPublishAnObsoleteConfiguration()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Connected);
        coordinator.Observe([routine], _ => [], false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = Task.Run(() => coordinator.Observe([routine], _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return [new() { Id = "out" }];
        }, true), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var removed = Task.Run(() => coordinator.Observe([], _ => [], false), TestContext.Current.CancellationToken);
        bool removalCompleted = false;
        try { await removed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); removalCompleted = true; }
        finally { release.TrySetResult(); }
        Assert.Empty(await observation);
        Assert.True(removalCompleted);
    }

    [Fact]
    public async Task OlderDiscovery_CannotUndoANewerSnapshot()
    {
        var coordinator = new RoutineDeviceAvailabilityCoordinator(Logger.Instance);
        var routine = Routine(DeviceAvailabilityTransition.Both);
        coordinator.Observe([routine], _ => [], false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = Task.Run(() => coordinator.Observe([routine], _ =>
        {
            entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return [];
        }, true), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { foreach (var pending in coordinator.Observe([routine], _ => [new() { Id = "out" }], true)) coordinator.Complete(pending); }
        finally { release.TrySetResult(); }
        Assert.Empty(await older);
        Assert.Empty(coordinator.Observe([routine], _ => [new() { Id = "out" }], true));
    }

    private static AudioRoutine Routine(DeviceAvailabilityTransition transition) => new()
    {
        Id = "routine",
        Name = "Desk",
        MasterVolumePercent = 30,
        TriggerKind = RoutineTriggerKind.DeviceAvailability,
        DeviceTransition = transition,
        TriggerDevice = new() { Id = "out", StableId = "stable", Name = "Speaker" },
    };
}
