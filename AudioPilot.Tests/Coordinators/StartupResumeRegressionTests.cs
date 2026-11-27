using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using Microsoft.Win32;

namespace AudioPilot.Tests.Coordinators;

public sealed class StartupResumeRegressionTests
{
    [Fact]
    public async Task AutomaticWakeAndLaterInteractiveWake_ShareOneRecoveryAndReleaseUnlock()
    {
        var resume = new AudioRoutine { Name = "Resume", MasterVolumePercent = 40, Triggers = [new() { Kind = RoutineTriggerKind.SystemResume }] };
        var unlock = new AudioRoutine { Name = "Unlock", MasterVolumePercent = 40, Triggers = [new() { Kind = RoutineTriggerKind.SessionUnlock }] };
        List<string> ran = [];
        using var systemEvents = new RoutineSystemEventCoordinator(() => [resume, unlock], _ => [], (routine, _) =>
        {
            ran.Add(routine.Name);
            return Task.CompletedTask;
        }, Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        var handler = new FakeResumeRecoveryHandler();
        using var coordinator = CreateWakeCoordinator(handler, systemEvents);
        await coordinator.InitializeAsync("startup");
        int endpointRefreshes = 0;

        coordinator.HandlePowerModeChanged(SystemPowerNotifications.GetPowerMode(4)!.Value, "suspend");
        coordinator.HandlePowerModeChanged(SystemPowerNotifications.GetPowerMode(18)!.Value, "automatic", onResume: () => endpointRefreshes++);
        await coordinator.DrainResumeAsync().WaitAsync(TestContext.Current.CancellationToken);
        await systemEvents.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);
        coordinator.HandlePowerModeChanged(SystemPowerNotifications.GetPowerMode(7)!.Value, "interactive", onResume: () => endpointRefreshes++);
        systemEvents.Unlock();
        await systemEvents.DrainAsync().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(1, endpointRefreshes);
        Assert.Equal(["Resume", "Unlock"], ran);
    }

    [Fact]
    public async Task NewSuspendCancelsActiveWake_AndOnlyNewRecoveryReleasesRoutines()
    {
        var routine = new AudioRoutine { Name = "Resume", MasterVolumePercent = 40, Triggers = [new() { Kind = RoutineTriggerKind.SystemResume }] };
        int routineRuns = 0;
        using var systemEvents = new RoutineSystemEventCoordinator(() => [routine], _ => [], (_, _) =>
        {
            routineRuns++;
            return Task.CompletedTask;
        }, Logger.Instance, TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        var firstAudio = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        int hotkeyRegistrations = 0;
        int deviceRefreshes = 0;
        var handler = new ResumeHandler((opId, token) => AppResumeRecoveryCoordinator.ExecuteAsync(opId, new(
            async () =>
            {
                if (++attempts == 1)
                {
                    firstAudio.SetResult(token);
                    await releaseFirst.Task;
                }
                else
                {
                    nextAudio.SetResult();
                    await releaseNext.Task;
                }
                return true;
            },
            _ =>
            {
                hotkeyRegistrations++;
                return Task.FromResult((new ResumeHotkeyRegistrationResult(true, true, true, true, true, true, true, true, true), 1));
            },
            () => { deviceRefreshes++; return Task.CompletedTask; }), Logger.Instance, "test", token));
        using var coordinator = CreateWakeCoordinator(handler, systemEvents);
        await coordinator.InitializeAsync("startup");
        CancellationToken testToken = TestContext.Current.CancellationToken;
        var deadline = TimeSpan.FromSeconds(5);
        coordinator.HandlePowerModeChanged(PowerModes.Resume, "first");
        try
        {
            CancellationToken first = await firstAudio.Task.WaitAsync(deadline, testToken);
            coordinator.HandlePowerModeChanged(PowerModes.Suspend, "suspend");
            Assert.True(first.IsCancellationRequested);
            coordinator.HandlePowerModeChanged(PowerModes.Resume, "second");
            releaseFirst.SetResult();
            await nextAudio.Task.WaitAsync(deadline, testToken);
            Assert.Equal(0, routineRuns);
            Assert.Equal(0, hotkeyRegistrations);
            Assert.Equal(0, deviceRefreshes);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseNext.TrySetResult();
            await coordinator.DrainResumeAsync().WaitAsync(deadline, testToken);
        }

        await systemEvents.DrainAsync().WaitAsync(deadline, testToken);
        Assert.Equal(1, hotkeyRegistrations);
        Assert.Equal(1, deviceRefreshes);
        Assert.Equal(1, routineRuns);
    }

    private static AppRuntimeStartupResumeCoordinator CreateWakeCoordinator(
        IResumeRecoveryHandler handler, RoutineSystemEventCoordinator systemEvents) =>
        new(Logger.Instance, handler, new(() => { }, () => true, _ => Task.CompletedTask, () => { }),
            work => Task.Run(work), _ => Task.CompletedTask, shutdown: null, systemEvents: systemEvents);

    private sealed class ResumeHandler(Func<string, CancellationToken, Task> recover) : IResumeRecoveryHandler
    {
        public Task RecoverAfterSystemResumeAsync(string? resumeOpId, CancellationToken cancellationToken) =>
            recover(resumeOpId!, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeWaitsForSuccessfulStartup(bool signalBeforeInitialization)
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new FakeResumeRecoveryHandler();
        Func<Task>? queued = null;
        using var coordinator = new AppRuntimeStartupResumeCoordinator(Logger.Instance, recovery,
            new AppRuntimeStartupResumeDependencies(() => { }, () => true, _ => startup.Task, () => { }),
            work => { queued = work; return Task.CompletedTask; }, _ => Task.CompletedTask, () => { });
        Task<AppRuntimeStartupInitializationOutcome>? initialization = signalBeforeInitialization
            ? null : coordinator.InitializeAsync(nameof(ResumeWaitsForSuccessfulStartup));
        coordinator.HandlePowerModeChanged(PowerModes.Resume, "test-resume");
        Assert.NotNull(queued);
        Task recoveryWork = queued();
        try
        {
            Assert.Equal(0, recovery.InvocationCount);
        }
        finally
        {
            initialization ??= coordinator.InitializeAsync(nameof(ResumeWaitsForSuccessfulStartup));
            startup.TrySetResult();
            await initialization;
            await recoveryWork.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, recovery.InvocationCount);
    }

    [Fact]
    public async Task DisposalDuringStartupSkipsSnapshotAndReportsCancellation()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshots = 0;
        using var coordinator = new AppRuntimeStartupResumeCoordinator(Logger.Instance, new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(() => { }, () => true, _ => startup.Task, () => snapshots++),
            work => work(), _ => Task.CompletedTask, () => { });
        Task<AppRuntimeStartupInitializationOutcome> initialization = coordinator.InitializeAsync("test-startup");
        coordinator.Dispose();
        startup.SetResult();
        Assert.Equal(AppRuntimeStartupInitializationOutcome.Cancelled, await initialization);
        Assert.Equal(0, snapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledStartupDoesNotRunQueuedRecovery(bool cancelled)
    {
        var recovery = new FakeResumeRecoveryHandler();
        using var coordinator = new AppRuntimeStartupResumeCoordinator(Logger.Instance, recovery,
            new AppRuntimeStartupResumeDependencies(() => { }, () => true,
                _ => Task.FromException(cancelled ? new OperationCanceledException() : new InvalidOperationException("startup failed")), () => { }),
            work => work(), _ => Task.CompletedTask, () => { });
        AppRuntimeStartupInitializationOutcome outcome = await coordinator.InitializeAsync("test-startup");
        coordinator.HandlePowerModeChanged(PowerModes.Resume, "test-resume");
        Assert.Equal(cancelled ? AppRuntimeStartupInitializationOutcome.Cancelled : AppRuntimeStartupInitializationOutcome.Fatal, outcome);
        Assert.Equal(0, recovery.InvocationCount);
    }

    [Fact]
    public async Task DisposalReleasesRecoveryWaitingForStartup()
    {
        var recovery = new FakeResumeRecoveryHandler();
        Task? recoveryWork = null;
        using var coordinator = new AppRuntimeStartupResumeCoordinator(Logger.Instance, recovery,
            new AppRuntimeStartupResumeDependencies(() => { }, () => true, _ => Task.CompletedTask, () => { }),
            work => recoveryWork = work(), _ => Task.CompletedTask, () => { });
        coordinator.HandlePowerModeChanged(PowerModes.Resume, "test-resume");
        Assert.NotNull(recoveryWork);
        Assert.False(recoveryWork.IsCompleted);
        coordinator.Dispose();
        await recoveryWork.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, recovery.InvocationCount);
    }
}
