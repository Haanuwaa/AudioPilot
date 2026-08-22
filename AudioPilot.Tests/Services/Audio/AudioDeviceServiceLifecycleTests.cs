using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

[Collection("BackgroundTaskHelperDelayIsolation")]
public sealed class AudioDeviceServiceLifecycleTests
{
    [Fact]
    public async Task DrainBackgroundTasksAsync_ReturnsTrue_WhenTasksAlreadyCompleted()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceServiceLifecycleTests), "lifecycle-completed.log");
        var tasks = new[] { Task.CompletedTask, Task.CompletedTask };

        bool result = await AudioDeviceServiceLifecycle.DrainBackgroundTasksAsync(tasks, loggerScope.Logger);

        Assert.True(result);
    }

    [Fact]
    public async Task DrainBackgroundTasksAsync_ReturnsFalse_WhenTaskTimesOutPastGrace()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceServiceLifecycleTests), "lifecycle-timeout.log");
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool result = await AudioDeviceServiceLifecycle.DrainBackgroundTasksAsync([never.Task], loggerScope.Logger);

        Assert.False(result);
    }

    [Fact]
    public async Task DrainBackgroundTasksAsync_LogsGraceCompletion_WhenTaskCompletesAfterInitialTimeout()
    {
        using var loggerScope = TestLoggerScope.CreateInMemory("lifecycle-grace.log", LogLevel.Trace);
        var taskSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialDelayObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graceDelayObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialDelayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graceDelayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayCallCount = 0;

        BackgroundTaskHelper.DelayAsyncForTests = waitMs =>
        {
            int callNumber = Interlocked.Increment(ref delayCallCount);
            if (callNumber == 1)
            {
                initialDelayObserved.TrySetResult(true);
                return initialDelayGate.Task;
            }

            graceDelayObserved.TrySetResult(true);
            return graceDelayGate.Task;
        };

        try
        {
            Task<bool> drainTask = AudioDeviceServiceLifecycle.DrainBackgroundTasksAsync([taskSource.Task], loggerScope.Logger);
            await initialDelayObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            initialDelayGate.TrySetResult(true);
            await graceDelayObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            taskSource.TrySetResult(true);

            bool result = await drainTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            string logText = loggerScope.DisposeAndReadLogText();

            Assert.True(result);
            Assert.Contains(AppConstants.Audio.LogEvents.Lifecycle.DisposeCompleteAfterGrace, logText, StringComparison.Ordinal);
        }
        finally
        {
            taskSource.TrySetResult(true);
            initialDelayGate.TrySetResult(true);
            graceDelayGate.TrySetResult(true);
            BackgroundTaskHelper.DelayAsyncForTests = Task.Delay;
        }
    }

    [Fact]
    public async Task DisposeAsync_AwaitsSessionMonitoringDrain()
    {
        var drainGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new AudioDeviceService(
            new FakeInputListenPropertyWriter(),
            sessionMonitoringDrainOverride: () => drainGate.Task);

        Task disposeTask = service.DisposeAsync().AsTask();
        await service.WaitForDisposeStartedForTestsAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await TestExecutionGuards.AssertDoesNotCompleteWithinAsync(
            service.WaitForDisposeCleanupBarrierForTestsAsync(),
            TimeSpan.FromMilliseconds(150),
            "DisposeAsync should remain blocked until session monitoring drain completes.");

        drainGate.TrySetResult(true);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}

