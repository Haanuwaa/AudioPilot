using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceResumeRecoveryHelperTests
{
    [Fact]
    public async Task RunBestEffortRecoveryAsync_RegistersNotifications_WhenNotRegistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-register.log");
        bool isRegistered = false;
        int registerCalls = 0;
        int updateCalls = 0;

        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => isRegistered,
            () =>
            {
                registerCalls++;
                isRegistered = true;
            },
            () => updateCalls++);

        await helper.RunBestEffortRecoveryAsync(CancellationToken.None);

        Assert.Equal(1, registerCalls);
        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public async Task RunBestEffortRecoveryAsync_SkipsRegistration_WhenAlreadyRegistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-skip-register.log");
        int registerCalls = 0;
        int updateCalls = 0;

        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => true,
            () => registerCalls++,
            () => updateCalls++);

        await helper.RunBestEffortRecoveryAsync(CancellationToken.None);

        Assert.Equal(0, registerCalls);
        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public void TryQueueBestEffortRecovery_ReturnsFalse_WhenQueueIsSaturated()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-saturated.log", LogLevel.Warning);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        for (int i = 0; i < AppConstants.Limits.MaxConcurrentBackgroundTasks; i++)
        {
            backgroundTasks[i] = Task.CompletedTask;
        }

        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => false,
            static () => { },
            static () => { });
        int backgroundTaskId = 0;

        bool queued = helper.TryQueueBestEffortRecovery(backgroundTasks, ref backgroundTaskId, new CancellationTokenSource());

        Assert.False(queued);
    }

    [Fact]
    public void TryQueueBestEffortRecovery_ReturnsFalse_WhenDisposed()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-disposed.log", LogLevel.Warning);
        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => true,
            () => false,
            static () => { },
            static () => { });
        int backgroundTaskId = 0;

        bool queued = helper.TryQueueBestEffortRecovery(new ConcurrentDictionary<int, Task>(), ref backgroundTaskId, new CancellationTokenSource());

        Assert.False(queued);
    }

    [Fact]
    public async Task RunBestEffortRecoveryAsync_ReturnsImmediately_WhenShutdownRequested()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-cancelled.log");
        int registerCalls = 0;
        int updateCalls = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => false,
            () => registerCalls++,
            () => updateCalls++);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await helper.RunBestEffortRecoveryAsync(cts.Token);

        Assert.Equal(0, registerCalls);
        Assert.Equal(0, updateCalls);
    }

    [Fact]
    public async Task RunBestEffortRecoveryAsync_ContinuesMonitorUpdate_WhenRegistrationThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-register-throws.log");
        int updateCalls = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => false,
            () => throw new InvalidOperationException("boom"),
            () => updateCalls++);

        await helper.RunBestEffortRecoveryAsync(CancellationToken.None);

        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public async Task RunBestEffortRecoveryAsync_SwallowsMonitorUpdateFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceResumeRecoveryHelperTests), "resume-helper-monitor-throws.log");
        int registerCalls = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(
            loggerScope.Logger,
            () => false,
            () => false,
            () => registerCalls++,
            () => throw new InvalidOperationException("boom"));

        await helper.RunBestEffortRecoveryAsync(CancellationToken.None);

        Assert.Equal(1, registerCalls);
    }
}
