using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceBackgroundWorkHelperTests
{
    [Fact]
    public void TryQueue_ReturnsFalse_WhenDisposed()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceBackgroundWorkHelperTests), "background-helper-disposed.log");
        var helper = new AudioDeviceBackgroundWorkHelper(loggerScope.Logger, () => true);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        int backgroundTaskId = 0;

        bool queued = helper.TryQueue(
            backgroundTasks,
            ref backgroundTaskId,
            backgroundWorkCts,
            static _ => Task.CompletedTask,
            "test-op");

        Assert.False(queued);
        Assert.Empty(backgroundTasks);
    }

    [Fact]
    public void TryQueue_ReturnsFalse_WhenQueueIsSaturated()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceBackgroundWorkHelperTests), "background-helper-saturated.log", LogLevel.Warning);
        var helper = new AudioDeviceBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        for (int i = 0; i < AppConstants.Limits.MaxConcurrentBackgroundTasks; i++)
        {
            backgroundTasks[i] = Task.CompletedTask;
        }

        int backgroundTaskId = 0;
        bool queued = helper.TryQueue(
            backgroundTasks,
            ref backgroundTaskId,
            backgroundWorkCts,
            static _ => Task.CompletedTask,
            "test-op");

        Assert.False(queued);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("background-operation-drop | operation=test-op reason=queue-saturated disposed=False", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryQueue_LogsStructuredFailure_WhenOperationThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceBackgroundWorkHelperTests), "background-helper-failed.log", LogLevel.Warning);
        var helper = new AudioDeviceBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        int backgroundTaskId = 0;

        bool queued = helper.TryQueue(
            backgroundTasks,
            ref backgroundTaskId,
            backgroundWorkCts,
            static _ => Task.FromException(new InvalidOperationException("boom")),
            "failing-op");

        Assert.True(queued);

        if (backgroundTasks.TryGetValue(backgroundTaskId, out Task? task))
        {
            await task;
        }

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("background-operation-failed | operation=failing-op reason=exception disposed=False", logText, StringComparison.Ordinal);
    }

}
