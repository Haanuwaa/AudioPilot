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

    [Fact]
    public async Task Resolve_WhenRetrySucceedsByAcquiring_ContinuesStartup()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired));

        Assert.True(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Resolve_WhenRetryFindsHealthyExistingInstance_ExitsCleanly()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("healthy-existing-instance", result.FailureReason);
    }

    [Fact]
    public async Task Resolve_WhenRetryGetsFailedActivationResponse_ReportsThatFailure()
    {
        List<string> errors = [];
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance),
            errors);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(
                SingleInstanceAcquireDisposition.ExistingHealthyInstance,
                ResponseExitCode: 3,
                ResponseErrorCode: "activation-failed"));

        Assert.False(result.ContinueStartup);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("activation-failed", result.FailureReason);
        Assert.Equal(
            "The running AudioPilot instance could not show its main window. Try opening it from the tray menu, or exit it and start AudioPilot again.",
            Assert.Single(errors));
    }

    [Fact]
    public async Task Resolve_WhenRetryStillUnresponsive_ShowsError()
    {
        List<string> errors = [];
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Retry,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance),
            errors);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(
                SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
                SingleInstanceSignalFailureKind.ConnectionFailed));

        Assert.False(result.ContinueStartup);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("retry-unresponsive", result.FailureReason);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Resolve_WhenTerminateSucceedsAndReacquires_ContinuesStartup()
    {
        var processRecoveryHelper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(true, @"C:\Apps\AudioPilot.exe", SingleInstanceScope.Current),
            tryCloseMainWindow: static _ => true,
            waitForExit: static (_, _) => true);

        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
            processRecoveryHelper);

        Queue<SingleInstanceAcquireResult> acquireResults = new(
        [
            new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance),
            new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired),
        ]);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(() => acquireResults.Dequeue());

        Assert.True(result.ContinueStartup);
    }

    [Fact]
    public async Task Resolve_WhenTerminateChoiceFindsHealthyInstanceOnRecheck_DoesNotTerminateIt()
    {
        int enumerationCount = 0;
        var processRecoveryHelper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: () =>
            {
                enumerationCount++;
                return [];
            });

        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
            processRecoveryHelper);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingHealthyInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("healthy-existing-instance", result.FailureReason);
        Assert.Equal(0, enumerationCount);
    }

    [Fact]
    public async Task Resolve_WhenTerminateFails_ShowsError()
    {
        List<string> errors = [];
        var processRecoveryHelper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () => [],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe");

        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
            processRecoveryHelper,
            errors);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("no-matching-process", result.FailureReason);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Resolve_WhenCancelled_ExitsWithoutContinuing()
    {
        var coordinator = CreateCoordinator(
            SingleInstanceRecoveryPromptResult.Cancel,
            new SingleInstanceProcessRecoveryHelper(Logger.Instance));

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance));

        Assert.False(result.ContinueStartup);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cancelled", result.FailureReason);
    }

    [Fact]
    public async Task Resolve_DefaultPrompt_UsesNamedRecoveryActionsAndDestructiveTermination()
    {
        var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.Retry };
        var coordinator = new SingleInstanceStartupRecoveryCoordinator(
            new SingleInstanceProcessRecoveryHelper(Logger.Instance),
            Logger.Instance,
            dialogs);

        SingleInstanceStartupRecoveryResult result = await coordinator.ResolveAsync(
            static () => new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired));

        Assert.True(result.ContinueStartup);
        AppDialogRequest request = Assert.Single(dialogs.Requests);
        Assert.Equal(["_Retry", "_Terminate and continue", "E_xit"], request.Actions.Select(static action => action.Label));
        Assert.Equal(AppDialogActionStyle.Destructive, request.Actions[1].Style);
        Assert.Equal(AppDialogResult.Cancelled, request.SafeCloseResult);
    }

    private static SingleInstanceStartupRecoveryCoordinator CreateCoordinator(
        SingleInstanceRecoveryPromptResult promptResult,
        SingleInstanceProcessRecoveryHelper processRecoveryHelper,
        List<string>? shownErrors = null)
    {
        return new SingleInstanceStartupRecoveryCoordinator(
            processRecoveryHelper,
            Logger.Instance,
            new RecordingAppDialogService(),
            promptForRecovery: () => Task.FromResult(promptResult),
            showError: (message, _) =>
            {
                shownErrors?.Add(message);
                return Task.FromResult(AppDialogResult.Acknowledged);
            });
    }
}
