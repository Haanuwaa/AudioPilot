using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class RoutineSystemEventCoordinatorTests
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("recheck")]
    [InlineData("device")]
    public async Task TransientDiscoveryFailuresRecoverWithoutConsumingOrDuplicatingTheWake(string phase)
    {
        int snapshots = 0, failures = 0, actions = 0;
        var routine = Routine(RoutineTriggerKind.SessionUnlock, RoutineTriggerKind.SystemResume);
        routine.OutputDeviceId = "out";
        using var coordinator = new RoutineSystemEventCoordinator(() =>
        {
            snapshots++;
            if (failures < 2 && (phase == "snapshot" || phase == "recheck" && snapshots > 1))
            { failures++; throw new IOException("Temporary settings discovery failure."); }
            return [routine];
        }, _ =>
        {
            if (phase == "device" && failures++ < 2) throw new IOException("Audio is recovering.");
            return [new() { Id = "out" }];
        }, (_, _) => { actions++; return Task.CompletedTask; }, Logger.Instance, TestContext.Current.CancellationToken,
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        coordinator.Suspend(); coordinator.BeginResume(1); coordinator.Unlock(); coordinator.CompleteRecovery(1);
        await coordinator.DrainAsync();
        coordinator.Unlock();
        await coordinator.DrainAsync();
        Assert.Equal(1, actions);
        Assert.True(snapshots >= 2);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 21)]
    public async Task PersistentDiscoveryFailureHasABoundedRetryBudgetAndDoesNotExecute(bool devicesFail, int maximumReads)
    {
        int reads = 0, actions = 0;
        var routine = Routine(RoutineTriggerKind.SessionUnlock);
        routine.OutputDeviceId = "out";
        using var coordinator = new RoutineSystemEventCoordinator(() =>
        {
            if (!devicesFail) { reads++; throw new IOException("Settings unavailable."); }
            return [routine];
        }, _ => { reads++; throw new IOException("Devices unavailable."); },
            (_, _) => { actions++; return Task.CompletedTask; }, Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        coordinator.Unlock();
        await coordinator.DrainAsync();
        Assert.InRange(reads, 2, maximumReads);
        Assert.Equal(0, actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotRetryDoesNotSurvivePauseOrShutdown(bool shutdown)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routine = Routine(RoutineTriggerKind.SessionUnlock);
        int reads = 0, actions = 0;
        using var coordinator = new RoutineSystemEventCoordinator(() =>
        {
            if (++reads == 1) throw new IOException("Temporary snapshot failure.");
            return [routine];
        }, _ => [], (_, _) => { actions++; return Task.CompletedTask; }, Logger.Instance, lifetime.Token,
            async (ms, token) => { if (ms == 500) { retrying.SetResult(); await release.Task.WaitAsync(token); } });
        coordinator.Unlock();
        try
        {
            await retrying.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (shutdown) lifetime.Cancel();
            else { coordinator.SetAutomaticPaused(true); coordinator.SetAutomaticPaused(false); }
        }
        finally { release.TrySetResult(); }
        await coordinator.DrainAsync();
        Assert.Equal(1, reads);
        Assert.Equal(0, actions);
    }

    [Fact]
    public async Task PauseDiscardsPendingWakeEvents_AndLaterWakeStillWorks()
    {
        int calls = 0;
        var routine = Routine(RoutineTriggerKind.SystemResume);
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        coordinator.Suspend();
        coordinator.BeginResume(1);
        coordinator.SetAutomaticPaused(true);
        coordinator.CompleteRecovery(1);
        coordinator.SetAutomaticPaused(false);
        await coordinator.DrainAsync();
        Assert.Equal(0, calls);
        coordinator.Suspend();
        coordinator.BeginResume(2);
        coordinator.CompleteRecovery(2);
        await coordinator.DrainAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LockDuringRecovery_DiscardsPendingUnlockAndResume()
    {
        int calls = 0;
        var routine = Routine(RoutineTriggerKind.SessionUnlock, RoutineTriggerKind.SystemResume);
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        coordinator.Suspend(); coordinator.BeginResume(1); coordinator.Unlock(); coordinator.LockSession();
        coordinator.CompleteRecovery(1); await coordinator.DrainAsync();
        Assert.Equal(0, calls);
        coordinator.Unlock(); await coordinator.DrainAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewResumeCycle_IgnoresOldRecoveryCompletion()
    {
        int calls = 0;
        var routine = Routine(RoutineTriggerKind.SystemResume);
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        coordinator.Suspend(); coordinator.BeginResume(1);
        coordinator.Suspend(); coordinator.BeginResume(2);
        coordinator.CompleteRecovery(1);
        await coordinator.DrainAsync();
        Assert.Equal(0, calls);
        coordinator.CompleteRecovery(2);
        await coordinator.DrainAsync();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeCycle_CoalescesUnlockAndResumeAfterRecovery(bool unlockFirst)
    {
        var routine = Routine(RoutineTriggerKind.SessionUnlock, RoutineTriggerKind.SystemResume);
        int calls = 0;
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, TestContext.Current.CancellationToken, (_, token) => Task.CompletedTask);
        coordinator.Suspend();
        if (unlockFirst) coordinator.Unlock();
        coordinator.BeginResume(1);
        if (!unlockFirst) coordinator.Unlock();
        await coordinator.DrainAsync();
        Assert.Equal(0, calls);
        coordinator.CompleteRecovery(1);
        await coordinator.DrainAsync();
        Assert.Equal(1, calls);
        coordinator.BeginResume(1); coordinator.CompleteRecovery(1); coordinator.Unlock();
        await coordinator.DrainAsync();
        Assert.Equal(1, calls);
        coordinator.Suspend(); coordinator.BeginResume(2); coordinator.CompleteRecovery(2);
        await coordinator.DrainAsync();
        coordinator.Unlock();
        await coordinator.DrainAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unlock_ExecutesAgainOnlyAfterAnotherLock()
    {
        int calls = 0;
        var routine = Routine(RoutineTriggerKind.SessionUnlock);
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        coordinator.Unlock(); await coordinator.DrainAsync();
        coordinator.Unlock(); await coordinator.DrainAsync();
        Assert.Equal(1, calls);
        coordinator.LockSession(); coordinator.Unlock(); await coordinator.DrainAsync();
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("edited")]
    [InlineData("disabled")]
    [InlineData("shutdown")]
    [InlineData("suspend")]
    public async Task WaitingForTargets_RechecksConfigurationAndCancellation(string outcome)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool present = false;
        int calls = 0;
        var routine = Routine(RoutineTriggerKind.SessionUnlock);
        routine.CommunicationsInput = new() { Id = "old", StableId = "stable", Playback = false };
        using var coordinator = new RoutineSystemEventCoordinator(() => [routine], playback =>
            !playback && present ? [new() { Id = "new", StableId = "stable" }] : [], (_, _) => { calls++; return Task.CompletedTask; },
            Logger.Instance, shutdown.Token, async (milliseconds, token) =>
            {
                if (milliseconds != 500) return;
                waiting.TrySetResult();
                await release.Task.WaitAsync(token);
            });
        coordinator.Unlock();
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        switch (outcome)
        {
            case "edited": routine.InputMuteAction = RoutineMuteAction.Mute; break;
            case "disabled": routine.Enabled = false; break;
            case "shutdown": shutdown.Cancel(); break;
            case "suspend": coordinator.Suspend(); break;
        }
        present = true;
        release.SetResult();
        await coordinator.DrainAsync();
        Assert.Equal(outcome == "ready" ? 1 : 0, calls);
    }

    [Fact]
    public async Task ReadinessIsBounded_AndOneFailureDoesNotBlockOtherRoutines()
    {
        var first = Routine(RoutineTriggerKind.SystemResume);
        first.OutputDeviceId = "missing";
        var second = Routine(RoutineTriggerKind.SystemResume);
        int polls = 0;
        int calls = 0;
        using var coordinator = new RoutineSystemEventCoordinator(() => [first, second], _ => [], (routine, _) =>
        {
            calls++;
            if (routine.Id == first.Id) throw new InvalidOperationException("Disconnected");
            return Task.CompletedTask;
        }, Logger.Instance, TestContext.Current.CancellationToken, (ms, _) => { if (ms == 500) polls++; return Task.CompletedTask; });
        coordinator.BeginResume(1); coordinator.CompleteRecovery(1);
        await coordinator.DrainAsync();
        Assert.Equal(20, polls);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(RoutineTriggerKind.SessionUnlock, "Windows unlock")]
    [InlineData(RoutineTriggerKind.SystemResume, "System resume")]
    public void Editor_PreservesSystemEventAndDoesNotOfferStatefulRestoration(RoutineTriggerKind kind, string label)
    {
        var routine = Routine(kind);
        using var editor = new RoutineEditorViewModel([], [], routine, preloadNetworks: false);
        Assert.Equal(label, editor.SelectedTriggerMode);
        Assert.True(editor.IsSystemEventTriggerSelected);
        Assert.False(editor.IsStatefulTriggerSelected);
        Assert.Null(editor.Validate());
        Assert.Equal(kind, Assert.Single(editor.BuildRoutine().Triggers).Kind);
        Assert.Contains(label, routine.TriggerSummary);
    }

    private static AudioRoutine Routine(params RoutineTriggerKind[] kinds) => new()
    {
        Name = "System event",
        MasterVolumePercent = 50,
        Triggers = [.. kinds.Select(kind => new RoutineTrigger { Kind = kind })],
    };
}
