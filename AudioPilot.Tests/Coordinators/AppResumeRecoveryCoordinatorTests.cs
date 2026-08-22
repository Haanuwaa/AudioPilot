using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppResumeRecoveryCoordinatorTests
{
    [Fact]
    public void ResolveOperationId_ReturnsProvidedOperationId_WhenPresent()
    {
        string operationId = AppResumeRecoveryCoordinator.ResolveOperationId("resume:test");

        Assert.Equal("resume:test", operationId);
    }

    [Fact]
    public void ResolveOperationId_GeneratesPrefixedOperationId_WhenMissing()
    {
        string operationId = AppResumeRecoveryCoordinator.ResolveOperationId(null);

        Assert.StartsWith("resume:", operationId, StringComparison.Ordinal);
        Assert.Equal("resume:".Length + 32, operationId.Length);
    }

    [Fact]
    public void ShouldRetryHotkeyRegistration_OnlyOnFirstAttemptWithFailures()
    {
        var success = new AppViewModel.ResumeHotkeyRegistrationResult(
            true, true, true, true, true, true, true, true, true);

        var failed = new AppViewModel.ResumeHotkeyRegistrationResult(
            false, true, true, false, true, true, true, true, true);

        Assert.False(AppResumeRecoveryCoordinator.ShouldRetryHotkeyRegistration(success, attempt: 1));
        Assert.True(AppResumeRecoveryCoordinator.ShouldRetryHotkeyRegistration(failed, attempt: 1));
        Assert.False(AppResumeRecoveryCoordinator.ShouldRetryHotkeyRegistration(failed, attempt: 2));
    }

    [Fact]
    public async Task RegisterHotkeysAsync_RetriesOnce_WhenInitialAttemptFails()
    {
        int attempts = 0;

        (AppViewModel.ResumeHotkeyRegistrationResult Result, int Attempts) = await AppResumeRecoveryCoordinator.RegisterHotkeysAsync(
            () =>
            {
                attempts++;
                return Task.FromResult(new AppViewModel.ResumeHotkeyRegistrationResult(
                    ToggleAppVisibilityRegistered: attempts > 1,
                    MediaHotkeysRegistered: true,
                    MuteHotkeysRegistered: true,
                    ListenToInputRegistered: true,
                    VolumeStepHotkeysRegistered: true,
                    OutputSwitchRegistered: true,
                    InputSwitchRegistered: true,
                    OutputReverseSwitchRegistered: true,
                    InputReverseSwitchRegistered: true));
            },
            retryDelayMs: 0,
            Logger.Instance,
            resumeOpId: "resume:test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal(2, Attempts);
        Assert.True(Result.AllSucceeded);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_WhenCanceledDuringRetryDelay_PropagatesCancellation()
    {
        int attempts = 0;
        using var cancellationTokenSource = new CancellationTokenSource();

        Exception? exception = await Record.ExceptionAsync(() => AppResumeRecoveryCoordinator.RegisterHotkeysAsync(
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    cancellationTokenSource.Cancel();
                }

                return Task.FromResult(new AppViewModel.ResumeHotkeyRegistrationResult(
                    ToggleAppVisibilityRegistered: false,
                    MediaHotkeysRegistered: true,
                    MuteHotkeysRegistered: true,
                    ListenToInputRegistered: true,
                    VolumeStepHotkeysRegistered: true,
                    OutputSwitchRegistered: true,
                    InputSwitchRegistered: true,
                    OutputReverseSwitchRegistered: true,
                    InputReverseSwitchRegistered: true));
            },
            retryDelayMs: 500,
            Logger.Instance,
            resumeOpId: "resume:test-cancel",
            cancellationToken: cancellationTokenSource.Token));

        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccess_WhenRecoveryPipelineSucceeds()
    {
        int recoverCalls = 0;
        int hotkeyCalls = 0;
        int refreshCalls = 0;

        ResumeRecoveryExecutionResult result = await AppResumeRecoveryCoordinator.ExecuteAsync(
            "resume:test",
            new ResumeRecoveryExecutionDependencies(
                RecoverAudioAsync: () =>
                {
                    recoverCalls++;
                    return Task.CompletedTask;
                },
                RegisterHotkeysAsync: _ =>
                {
                    hotkeyCalls++;
                    return Task.FromResult((
                        new AppViewModel.ResumeHotkeyRegistrationResult(true, true, true, true, true, true, true, true, true),
                        Attempts: 1));
                },
                RefreshDevicesAsync: () =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                }),
            Logger.Instance,
            failureMethodName: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.HotkeyAttempts);
        Assert.Equal(0, result.HotkeyFailedCount);
        Assert.Equal(1, recoverCalls);
        Assert.Equal(1, hotkeyCalls);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenRecoveryStepThrows()
    {
        int hotkeyCalls = 0;
        int refreshCalls = 0;

        ResumeRecoveryExecutionResult result = await AppResumeRecoveryCoordinator.ExecuteAsync(
            "resume:test",
            new ResumeRecoveryExecutionDependencies(
                RecoverAudioAsync: () => throw new InvalidOperationException("boom"),
                RegisterHotkeysAsync: _ =>
                {
                    hotkeyCalls++;
                    return Task.FromResult((
                        new AppViewModel.ResumeHotkeyRegistrationResult(true, true, true, true, true, true, true, true, true),
                        Attempts: 1));
                },
                RefreshDevicesAsync: () =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                }),
            Logger.Instance,
            failureMethodName: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.HotkeyAttempts);
        Assert.Equal(0, result.HotkeyFailedCount);
        Assert.Equal(0, hotkeyCalls);
        Assert.Equal(0, refreshCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsPartialFailure_WhenHotkeysStillFailAfterRetry()
    {
        int refreshCalls = 0;

        ResumeRecoveryExecutionResult result = await AppResumeRecoveryCoordinator.ExecuteAsync(
            "resume:partial",
            new ResumeRecoveryExecutionDependencies(
                RecoverAudioAsync: () => Task.CompletedTask,
                RegisterHotkeysAsync: _ => Task.FromResult((
                    new AppViewModel.ResumeHotkeyRegistrationResult(
                        true, true, true, true, true, true, true, true, true,
                        RoutineHotkeysRegistered: false),
                    Attempts: 2)),
                RefreshDevicesAsync: () =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                }),
            Logger.Instance,
            failureMethodName: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.HotkeyAttempts);
        Assert.Equal(1, result.HotkeyFailedCount);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceledDuringHotkeyRetryDelay_LogsSkipAndStopsPipeline()
    {
        int recoverCalls = 0;
        int refreshCalls = 0;
        using var cancellationTokenSource = new CancellationTokenSource();

        ResumeRecoveryExecutionResult result = await AppResumeRecoveryCoordinator.ExecuteAsync(
            "resume:test-cancel",
            new ResumeRecoveryExecutionDependencies(
                RecoverAudioAsync: () =>
                {
                    recoverCalls++;
                    return Task.CompletedTask;
                },
                RegisterHotkeysAsync: async _ =>
                {
                    cancellationTokenSource.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationTokenSource.Token);
                    return (
                        new AppViewModel.ResumeHotkeyRegistrationResult(true, true, true, true, true, true, true, true, true),
                        Attempts: 1);
                },
                RefreshDevicesAsync: () =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                }),
            Logger.Instance,
            failureMethodName: "test",
            cancellationToken: cancellationTokenSource.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(1, recoverCalls);
        Assert.Equal(0, refreshCalls);
        Assert.Equal(0, result.HotkeyAttempts);
    }
}
