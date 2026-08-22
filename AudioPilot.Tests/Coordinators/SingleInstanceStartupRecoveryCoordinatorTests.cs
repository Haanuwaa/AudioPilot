using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed class SingleInstanceStartupRecoveryCoordinatorTests
{
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
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(true, @"C:\Apps\AudioPilot.exe"),
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
