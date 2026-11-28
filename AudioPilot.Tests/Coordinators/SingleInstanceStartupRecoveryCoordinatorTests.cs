using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed class SingleInstanceStartupRecoveryCoordinatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadlessHandoff_RetriesConnectionTeardownThenAcquires(bool confirmedHeadless)
    {
        using var clock = new ManualTimeProvider();
        int attempts = 0;
        var busy = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance,
            ResponseExitCode: 3, ResponseErrorCode: SingleInstanceHelper.HeadlessBusyErrorCode);
        Task<SingleInstanceAcquireResult> pending = SingleInstanceStartupRecoveryCoordinator.WaitForStartupHandoffAsync(confirmedHeadless ? busy
            : new(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance, SingleInstanceSignalFailureKind.ConnectionFailed), () =>
        {
            return ++attempts == 1
                ? new(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance, SingleInstanceSignalFailureKind.ConnectionFailed)
                : new(SingleInstanceAcquireDisposition.Acquired);
        }, clock, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceListenerRetryDelayMs));
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceListenerRetryDelayMs));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Acquired);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadlessHandoff_DoesNotReplayUncertainRequests(bool outcomeUnknown)
    {
        using var clock = new ManualTimeProvider();
        int attempts = 0;
        var busy = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance,
            ResponseExitCode: 3, ResponseErrorCode: SingleInstanceHelper.HeadlessBusyErrorCode);
        var failed = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
            outcomeUnknown ? SingleInstanceSignalFailureKind.OutcomeUnknown : SingleInstanceSignalFailureKind.InvalidResponse);
        Task<SingleInstanceAcquireResult> pending = SingleInstanceStartupRecoveryCoordinator.WaitForStartupHandoffAsync(busy, () =>
        {
            attempts++;
            return failed;
        }, clock, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceListenerRetryDelayMs));
        Assert.Equal(failed, await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadlessHandoff_StopsAtDeadlineOrCancellation(bool cancel)
    {
        using var clock = new ManualTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int attempts = 0;
        var busy = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance,
            ResponseExitCode: 3, ResponseErrorCode: SingleInstanceHelper.HeadlessBusyErrorCode);
        Task<SingleInstanceAcquireResult> pending = SingleInstanceStartupRecoveryCoordinator.WaitForStartupHandoffAsync(busy, () =>
        {
            attempts++;
            return busy;
        }, clock, cancellation.Token);
        await clock.WaitForTimerAsync();
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        else
        {
            clock.Advance(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceHeadlessHandoffTimeoutMs));
            Assert.Equal(busy, await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        if (cancel) Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(3, "activation-failed")]
    public async Task HeadlessHandoff_DoesNotRetryGuiReplies(int exitCode, string? errorCode)
    {
        var result = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance,
            ResponseExitCode: exitCode, ResponseErrorCode: errorCode);
        Assert.Equal(result, await SingleInstanceStartupRecoveryCoordinator.WaitForStartupHandoffAsync(result,
            () => throw new InvalidOperationException("GUI requests must not be replayed."), cancellationToken: TestContext.Current.CancellationToken));
    }

    private static readonly SingleInstanceAcquireResult Unreachable = new(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance);
    private static readonly SingleInstanceAcquireResult ActivationExpired = new(
        SingleInstanceAcquireDisposition.ExistingHealthyInstance, ResponseExitCode: 4, ResponseErrorCode: "activation-expired");
    private static readonly SingleInstanceOwnerProbe VerifiedHung = new(SingleInstanceOwnerState.Unresponsive, new(42, 100));

    [Theory]
    [InlineData((int)SingleInstanceOwnerState.Unknown)]
    [InlineData((int)SingleInstanceOwnerState.Starting)]
    [InlineData((int)SingleInstanceOwnerState.Stopping)]
    [InlineData((int)SingleInstanceOwnerState.Headless)]
    [InlineData((int)SingleInstanceOwnerState.Unresponsive)]
    public async Task RecoveryWithoutVerifiedOwnerOnlyOffersRetryAndExit(int ownerState)
    {
        var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.TerminateExisting };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => throw new InvalidOperationException());
        var result = await coordinator.ResolveAsync(Unreachable, () => throw new InvalidOperationException(),
            () => Unreachable,
            _ => Task.FromResult(new SingleInstanceOwnerProbe((SingleInstanceOwnerState)ownerState)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cancelled", result.FailureReason);
        Assert.Equal([AppDialogResult.Retry, AppDialogResult.Cancelled], Assert.Single(dialogs.Requests).Actions.Select(action => action.Result));
    }

    [Fact]
    public async Task MutexAcquisitionFailureOffersRetryWithoutProbingOrTerminating()
    {
        var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.Retry };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs);
        var failure = new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.AcquisitionFailed, SingleInstanceSignalFailureKind.AcquisitionFailed);
        var result = await coordinator.ResolveAsync(failure, () => new(SingleInstanceAcquireDisposition.Acquired),
            () => failure,
            _ => throw new InvalidOperationException(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.ContinueStartup);
        var prompt = Assert.Single(dialogs.Requests);
        Assert.Contains("could not check", prompt.Message);
        Assert.Equal([AppDialogResult.Retry, AppDialogResult.Cancelled], prompt.Actions.Select(action => action.Result));
    }

    [Fact]
    public async Task ResponsiveDispatcherAfterActivationTimeoutOffersRetryAndExit()
    {
        var dialogs = new RecordingAppDialogService();
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs);
        Assert.True(SingleInstanceStartupRecoveryCoordinator.NeedsRecovery(ActivationExpired));
        var result = await coordinator.ResolveAsync(ActivationExpired, () => throw new InvalidOperationException(),
            () => Unreachable,
            _ => Task.FromResult(new SingleInstanceOwnerProbe(SingleInstanceOwnerState.Responsive)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cancelled", result.FailureReason);
        var prompt = Assert.Single(dialogs.Requests);
        Assert.Contains("could not complete", prompt.Message);
        Assert.Equal([AppDialogResult.Retry, AppDialogResult.Cancelled], prompt.Actions.Select(action => action.Result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TerminationRequiresTheSameVerifiedOwnerAndAcceptsRaces(int scenario)
    {
        var target = new RecoveryTarget();
        var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.TerminateExisting };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        int probes = 0;
        var result = await coordinator.ResolveAsync(ActivationExpired,
            () => scenario == 2 ? new(SingleInstanceAcquireDisposition.ExistingHealthyInstance, ResponseExitCode: 0)
                : new(SingleInstanceAcquireDisposition.Acquired),
            () => Unreachable,
            _ =>
            {
                probes++;
                if (probes == 1) return Task.FromResult(VerifiedHung);
                if (scenario == 1) return Task.FromResult(VerifiedHung with { Identity = new(43, 100) });
                if (scenario == 3) target.HasExited = true;
                return Task.FromResult(VerifiedHung);
            }, cancellationToken: TestContext.Current.CancellationToken);

        var prompt = Assert.Single(dialogs.Requests);
        Assert.Equal(AppDialogActionStyle.Destructive, prompt.Actions[1].Style);
        Assert.Equal(AppDialogResult.Cancelled, prompt.SafeCloseResult);
        Assert.Equal(scenario is 0 or 2 ? 1 : 0, target.Terminations);
        Assert.True(target.Disposed);
        Assert.Equal(scenario != 2, result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task RecoveryStopsBeforeForceWhenGracefulShutdownHasStarted()
    {
        var target = new RecoveryTarget { CloseRequested = true };
        var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.TerminateExisting };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        int probes = 0;
        var result = await coordinator.ResolveAsync(Unreachable, () => new(SingleInstanceAcquireDisposition.Acquired),
            () => Unreachable,
            _ => Task.FromResult(++probes <= 2 ? VerifiedHung : new(SingleInstanceOwnerState.Stopping)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.ContinueStartup);
        Assert.Equal(1, target.CloseAttempts);
        Assert.Equal(0, target.Terminations);
    }

    [Fact]
    public async Task OwnerExitDuringProbeSkipsThePromptWithoutReplayingActivation()
    {
        var dialogs = new RecordingAppDialogService();
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs);
        bool probed = false;
        var result = await coordinator.ResolveAsync(Unreachable,
            () => throw new InvalidOperationException("Do not replay the original request."),
            () => { Assert.True(probed); return new(SingleInstanceAcquireDisposition.Acquired); },
            _ => { probed = true; return Task.FromResult(default(SingleInstanceOwnerProbe)); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.ContinueStartup);
        Assert.Empty(dialogs.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPromptClosesWhenOwnerExitsOrRecovers(bool recovers)
    {
        using var clock = new ManualTimeProvider();
        var never = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new RecordingAppDialogService { PendingConfirmation = never.Task };
        var target = new RecoveryTarget();
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        int ownershipChecks = 0;
        int probes = 0;
        Task<SingleInstanceStartupRecoveryResult> pending = coordinator.ResolveAsync(Unreachable,
            () => throw new InvalidOperationException("Do not replay activation or terminate the owner."),
            () => { ownershipChecks++; throw new InvalidOperationException("Automatic recovery must not acquire ownership."); },
            _ => Task.FromResult(++probes > 1 && recovers ? VerifiedHung with { State = SingleInstanceOwnerState.Responsive } : VerifiedHung),
            cancellationToken: TestContext.Current.CancellationToken, stalledOwner: VerifiedHung.Identity, timeProvider: clock);
        await clock.WaitForTimerAsync();
        Assert.False(pending.IsCompleted);
        if (!recovers) target.HasExited = true;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.ContinueStartup);
        Assert.Equal(recovers ? "owner-no-longer-stalled" : "owner-exited", result.FailureReason);
        Assert.Equal(0, ownershipChecks);
        Assert.Single(dialogs.Requests);
        Assert.Equal(0, target.Terminations);
        Assert.True(target.Disposed);
        Assert.False(never.Task.IsCompleted);
    }

    [Fact]
    public async Task CancellingRecoveryStopsPromptAndOwnershipObservation()
    {
        using var clock = new ManualTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dialogs = new RecordingAppDialogService { PendingConfirmation = new TaskCompletionSource<AppDialogResult>().Task };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs);
        int checks = 0;
        Task<SingleInstanceStartupRecoveryResult> pending = coordinator.ResolveAsync(Unreachable,
            () => throw new InvalidOperationException(), () => { checks++; return Unreachable; },
            _ => Task.FromResult(default(SingleInstanceOwnerProbe)), cancellationToken: cancellation.Token, timeProvider: clock);
        await clock.WaitForTimerAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checks);
    }

    [Theory]
    [InlineData((int)SingleInstanceOwnerState.Responsive, 42)]
    [InlineData((int)SingleInstanceOwnerState.Stopping, 42)]
    [InlineData((int)SingleInstanceOwnerState.Starting, 42)]
    [InlineData((int)SingleInstanceOwnerState.Unresponsive, 43)]
    public async Task WatchdogRecoveryCannotTargetARecoveredOrReplacementOwner(int state, int pid)
    {
        var dialogs = new RecordingAppDialogService();
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => throw new InvalidOperationException());
        var result = await coordinator.ResolveAsync(Unreachable, () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(),
            _ => Task.FromResult(new SingleInstanceOwnerProbe((SingleInstanceOwnerState)state, new(pid, 100))),
            cancellationToken: TestContext.Current.CancellationToken, stalledOwner: VerifiedHung.Identity);
        Assert.False(result.ContinueStartup);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task ManualRecoveryStillReacquiresWhenOwnerExitsWhilePromptIsOpen()
    {
        using var clock = new ManualTimeProvider();
        var dialogs = new RecordingAppDialogService { PendingConfirmation = new TaskCompletionSource<AppDialogResult>().Task };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs);
        int checks = 0;
        var pending = coordinator.ResolveAsync(Unreachable, () => throw new InvalidOperationException(),
            () => ++checks == 1 ? Unreachable : new(SingleInstanceAcquireDisposition.Acquired),
            _ => Task.FromResult(default(SingleInstanceOwnerProbe)), timeProvider: clock, cancellationToken: TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ContinueStartup);
        Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRecoveryAllowsExplicitRetryAndRestart(bool terminate)
    {
        var target = new RecoveryTarget();
        var dialogs = new RecordingAppDialogService { YesNoResponse = terminate ? AppDialogResult.TerminateExisting : AppDialogResult.Retry };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        int requests = 0;
        var result = await coordinator.ResolveAsync(Unreachable,
            () => { requests++; return new(SingleInstanceAcquireDisposition.Acquired); },
            () => throw new InvalidOperationException("Automatic ownership check"),
            _ => Task.FromResult(VerifiedHung), stalledOwner: VerifiedHung.Identity, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.ContinueStartup);
        Assert.Equal(1, requests);
        Assert.Equal(terminate ? 1 : 0, target.Terminations);
    }

    [Fact]
    public async Task AutomaticRecoveryWithExitedOwnerClosesBeforePromptWithoutAcquiring()
    {
        var dialogs = new RecordingAppDialogService();
        var target = new RecoveryTarget { HasExited = true };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        var result = await coordinator.ResolveAsync(Unreachable, () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), _ => Task.FromResult(VerifiedHung),
            stalledOwner: VerifiedHung.Identity, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.ContinueStartup);
        Assert.Equal("owner-exited", result.FailureReason);
        Assert.Empty(dialogs.Requests);
        Assert.True(target.Disposed);
    }

    [Fact]
    public async Task VerifiedProcessWithoutVerifiedPipeOwnerDoesNotOfferTermination()
    {
        var dialogs = new RecordingAppDialogService();
        var target = new RecoveryTarget();
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(Logger.Instance, dialogs, _ => target);
        var result = await coordinator.ResolveAsync(Unreachable, () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), _ => Task.FromResult(default(SingleInstanceOwnerProbe)),
            stalledOwner: VerifiedHung.Identity, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.ContinueStartup);
        Assert.Equal([AppDialogResult.Retry, AppDialogResult.Cancelled], Assert.Single(dialogs.Requests).Actions.Select(action => action.Result));
        Assert.Equal(0, target.Terminations);
    }

    private sealed class RecoveryTarget : ISingleInstanceRecoveryTarget
    {
        public bool HasExited { get; set; }
        public bool CloseRequested { get; init; }
        public bool Disposed { get; private set; }
        public int CloseAttempts { get; private set; }
        public int Terminations { get; private set; }
        public bool RequestClose() { CloseAttempts++; return CloseRequested; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(HasExited);
        public void Terminate() { Terminations++; HasExited = true; }
        public void Dispose() => Disposed = true;
    }
}
