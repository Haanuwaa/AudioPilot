using System.Text.RegularExpressions;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Microsoft.Win32;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppRuntimeStartupResumeCoordinatorTests
{
    [Fact]
    public async Task HandlePowerModeChanged_LogsCorrelatedResumeAndPassesOpIdToRecoveryHandler()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "resume-coordinator.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_LogsCorrelatedResumeAndPassesOpIdToRecoveryHandler));

        string receivedOpId = await recoveryHandler.WaitForInvocationAsync();

        string logText = loggerScope.DisposeAndReadLogText();

        Match opIdMatch = MyRegex().Match(logText);
        Assert.True(opIdMatch.Success, $"Expected resume opId in log.\nLog text:\n{logText}");
        string loggedOpId = opIdMatch.Groups[1].Value;

        Assert.Equal(loggedOpId, receivedOpId);
        Assert.Contains($"power-resume-detected | opId={loggedOpId}", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlePowerModeChanged_LogsCorrelatedFailure_WhenRecoveryThrows()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppRuntimeStartupResumeCoordinatorTests), "resume-coordinator-fail.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler
        {
            ExceptionToThrow = new InvalidOperationException("boom"),
        };
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_LogsCorrelatedFailure_WhenRecoveryThrows));

        string receivedOpId = await recoveryHandler.WaitForInvocationAsync();
        await recoveryHandler.WaitForCompletionAsync();

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            $"power-resume-detected | opId={receivedOpId}",
            $"power-resume-recovery-failed | opId={receivedOpId}");
        loggerScope.Logger.Dispose();

        Assert.Contains($"power-resume-detected | opId={receivedOpId}", logText, StringComparison.Ordinal);
        Assert.Contains($"power-resume-recovery-failed | opId={receivedOpId}", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "resume-coordinator-duplicate.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow));
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_IgnoresDuplicateResumeSignalsWithinCooldownWindow));

        await recoveryHandler.WaitForCompletionAsync();

        Assert.Equal(1, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppRuntimeStartupResumeCoordinatorTests), "resume-coordinator-skip.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler
        {
            BlockUntilReleased = true,
        };
        var coordinator = CreateCoordinator(loggerScope.Logger, recoveryHandler);

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress));
        string opId = await recoveryHandler.WaitForInvocationAsync();

        await recoveryHandler.WaitForBlockEntryAsync();
        TestPrivateAccess.SetField(coordinator, "_lastResumeSignalUtc", DateTime.UtcNow.AddSeconds(-2));
        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_SkipsDuplicateResumeSignals_WhenRecoveryIsAlreadyInProgress));

        recoveryHandler.Release();
        await recoveryHandler.WaitForCompletionAsync();

        Assert.Equal(1, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandlePowerModeChanged_AfterDispose_DoesNotRunQueuedRecovery()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "resume-coordinator-disposed.log", LogLevel.Info);

        var recoveryHandler = new FakeResumeRecoveryHandler();
        Func<Task>? queuedRecovery = null;
        var queuedRecoveryCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppRuntimeStartupResumeCoordinator(
            loggerScope.Logger,
            recoveryHandler,
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.CompletedTask,
                CaptureInitialHotplugSnapshot: static () => { }),
            queueResumeRecoveryWork: work =>
            {
                queuedRecovery = work;
                queuedRecoveryCaptured.TrySetResult(true);
                return Task.CompletedTask;
            },
            showStartupError: _ => Task.CompletedTask,
            shutdown: () => { });

        coordinator.HandlePowerModeChanged(new PowerModeChangedEventArgs(PowerModes.Resume), nameof(HandlePowerModeChanged_AfterDispose_DoesNotRunQueuedRecovery));
        await queuedRecoveryCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        coordinator.Dispose();
        Assert.NotNull(queuedRecovery);

        await queuedRecovery!();

        Assert.Equal(0, recoveryHandler.InvocationCount);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_InitializesStartup_WithNoSettingsFlagAndCapturesSnapshot()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-load.log", LogLevel.Info);

        bool? receivedNoSettingsFlag = null;
        int captureSnapshotCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => false,
                InitializeStartupAsync: noSettings =>
                {
                    receivedNoSettingsFlag = noSettings;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++));

        AppRuntimeStartupInitializationOutcome outcome =
            await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_InitializesStartup_WithNoSettingsFlagAndCapturesSnapshot));

        Assert.Equal(AppRuntimeStartupInitializationOutcome.Succeeded, outcome);
        Assert.True(receivedNoSettingsFlag);
        Assert.Equal(1, captureSnapshotCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-duplicate.log", LogLevel.Info);

        int initializeCalls = 0;
        int captureSnapshotCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: _ =>
                {
                    initializeCalls++;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++));

        await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent));
        await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_IgnoresDuplicateLoadedEvent));

        Assert.Equal(1, initializeCalls);
        Assert.Equal(1, captureSnapshotCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_ContinuesStartup_WhenNotificationRegistrationFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-notification-warning.log", LogLevel.Info);

        int initializeCalls = 0;
        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        int showErrorCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => throw new InvalidOperationException("boom"),
                SettingsFileExists: static () => true,
                InitializeStartupAsync: _ =>
                {
                    initializeCalls++;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            _ =>
            {
                showErrorCalls++;
                return Task.CompletedTask;
            },
            () => shutdownCalls++);

        await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_ContinuesStartup_WhenNotificationRegistrationFails));

        Assert.Equal(1, initializeCalls);
        Assert.Equal(1, captureSnapshotCalls);
        Assert.Equal(0, shutdownCalls);
        Assert.Equal(0, showErrorCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_WhenSettingsProbeFails_ShowsErrorAndShutsDown()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-settings-probe-fail.log", LogLevel.Info);

        int initializeCalls = 0;
        int shutdownCalls = 0;
        string? errorMessage = null;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => throw new IOException("settings unavailable"),
                InitializeStartupAsync: _ =>
                {
                    initializeCalls++;
                    return Task.CompletedTask;
                },
                CaptureInitialHotplugSnapshot: static () => { }),
            message =>
            {
                errorMessage = message;
                return Task.CompletedTask;
            },
            () => shutdownCalls++);

        AppRuntimeStartupInitializationOutcome outcome =
            await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_WhenSettingsProbeFails_ShowsErrorAndShutsDown));

        Assert.Equal(AppRuntimeStartupInitializationOutcome.Fatal, outcome);
        Assert.Equal(0, initializeCalls);
        Assert.Equal(1, shutdownCalls);
        Assert.NotNull(errorMessage);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_WhenStartupCanceled_SkipsSnapshotAndShutdown()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-cancel.log", LogLevel.Info);

        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        int showErrorCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.FromCanceled(new CancellationToken(canceled: true)),
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            _ =>
            {
                showErrorCalls++;
                return Task.CompletedTask;
            },
            () => shutdownCalls++);

        AppRuntimeStartupInitializationOutcome outcome =
            await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_WhenStartupCanceled_SkipsSnapshotAndShutdown));

        Assert.Equal(AppRuntimeStartupInitializationOutcome.Cancelled, outcome);
        Assert.Equal(0, captureSnapshotCalls);
        Assert.Equal(0, shutdownCalls);
        Assert.Equal(0, showErrorCalls);
    }

    [Fact]
    public async Task HandleWindowLoadedAsync_WhenStartupFails_ShowsErrorAndShutsDown()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-fail.log", LogLevel.Info);

        int captureSnapshotCalls = 0;
        int shutdownCalls = 0;
        string? errorMessage = null;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.FromException(new InvalidOperationException("boom")),
                CaptureInitialHotplugSnapshot: () => captureSnapshotCalls++),
            message =>
            {
                errorMessage = message;
                return Task.CompletedTask;
            },
            () => shutdownCalls++);

        AppRuntimeStartupInitializationOutcome outcome =
            await coordinator.InitializeAsync(nameof(HandleWindowLoadedAsync_WhenStartupFails_ShowsErrorAndShutsDown));

        Assert.Equal(AppRuntimeStartupInitializationOutcome.Fatal, outcome);
        Assert.Equal(0, captureSnapshotCalls);
        Assert.Equal(1, shutdownCalls);
        Assert.NotNull(errorMessage);
        Assert.Contains("Failed to initialize application services", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ConcurrentCallersShareOneInitializationAndOutcome()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-single-flight.log", LogLevel.Info);

        var initializationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int initializeCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: async _ =>
                {
                    initializeCalls++;
                    initializationStarted.TrySetResult(true);
                    await releaseInitialization.Task;
                },
                CaptureInitialHotplugSnapshot: static () => { }));

        Task<AppRuntimeStartupInitializationOutcome> first = coordinator.InitializeAsync(nameof(InitializeAsync_ConcurrentCallersShareOneInitializationAndOutcome));
        await initializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Task<AppRuntimeStartupInitializationOutcome> second = coordinator.InitializeAsync(nameof(InitializeAsync_ConcurrentCallersShareOneInitializationAndOutcome));

        Assert.Same(first, second);
        releaseInitialization.TrySetResult(true);

        AppRuntimeStartupInitializationOutcome[] outcomes = await Task.WhenAll(first, second);
        Assert.All(outcomes, outcome => Assert.Equal(AppRuntimeStartupInitializationOutcome.Succeeded, outcome));
        Assert.Equal(1, initializeCalls);
    }

    [Fact]
    public async Task InitializeAsync_WhenFailureDialogThrows_StillRequestsShutdownAndReturnsFatal()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRuntimeStartupResumeCoordinatorTests), "startup-dialog-fail.log", LogLevel.Info);

        int shutdownCalls = 0;
        var coordinator = CreateCoordinator(
            loggerScope.Logger,
            new FakeResumeRecoveryHandler(),
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.FromException(new InvalidOperationException("startup failed")),
                CaptureInitialHotplugSnapshot: static () => { }),
            static _ => Task.FromException(new InvalidOperationException("dialog failed")),
            () => shutdownCalls++);

        AppRuntimeStartupInitializationOutcome outcome =
            await coordinator.InitializeAsync(nameof(InitializeAsync_WhenFailureDialogThrows_StillRequestsShutdownAndReturnsFatal));

        Assert.Equal(AppRuntimeStartupInitializationOutcome.Fatal, outcome);
        Assert.Equal(1, shutdownCalls);
    }

    private static AppRuntimeStartupResumeCoordinator CreateCoordinator(Logger logger, IResumeRecoveryHandler recoveryHandler)
    {
        return CreateCoordinator(
            logger,
            recoveryHandler,
            new AppRuntimeStartupResumeDependencies(
                RegisterNotificationClient: static () => { },
                SettingsFileExists: static () => true,
                InitializeStartupAsync: static _ => Task.CompletedTask,
                CaptureInitialHotplugSnapshot: static () => { }));
    }

    private static AppRuntimeStartupResumeCoordinator CreateCoordinator(
        Logger logger,
        IResumeRecoveryHandler recoveryHandler,
        AppRuntimeStartupResumeDependencies dependencies,
        Func<string, Task>? showStartupError = null,
        Action? shutdown = null)
    {
        return new AppRuntimeStartupResumeCoordinator(
            logger,
            recoveryHandler,
            dependencies,
            static work => Task.Run(work),
            showStartupError ?? (_ => Task.CompletedTask),
            shutdown ?? (() => { }));
    }

    [GeneratedRegex(@"opId=(resume:[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
