using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.TestDoubles;
using Microsoft.Win32;

namespace AudioPilot.Tests.Coordinators;

public sealed class StartupResumeRegressionTests
{
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
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), "test-resume");
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
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), "test-resume");
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
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), "test-resume");
        Assert.NotNull(recoveryWork);
        Assert.False(recoveryWork.IsCompleted);
        coordinator.Dispose();
        await recoveryWork.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, recovery.InvocationCount);
    }
}
