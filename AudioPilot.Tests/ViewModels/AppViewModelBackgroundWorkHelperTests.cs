using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelBackgroundWorkHelperTests
{
    [Fact]
    public async Task TryQueue_QueuesBackgroundOperation_WhenNotCleaningUp()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper.log");
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        int calls = 0;

        bool queued = helper.TryQueue(
            backgroundTasks,
            backgroundWorkCts,
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            "test-op");

        Assert.True(queued);
        Task completion = Task.WhenAll([.. backgroundTasks.Values]);
        await completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryQueue_DoesNotLeaveCompletedTasksTracked_WhenOperationCompletesSynchronously()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper-sync.log");
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();

        int operationCount = AppConstants.Limits.MaxConcurrentBackgroundTasks
            + AppConstants.Limits.MaxDeferredBackgroundOperations;
        for (int index = 0; index < operationCount; index++)
        {
            bool queued = helper.TryQueue(
                backgroundTasks,
                backgroundWorkCts,
                static _ => Task.CompletedTask,
                $"sync-op-{index}");

            Assert.True(queued);
        }

        await TestExecutionGuards.WaitUntilAsync(
            () => backgroundTasks.IsEmpty,
            "Completed background tasks remained tracked past the allotted timeout.");
    }

    [Fact]
    public async Task TryQueue_BoundsActiveWork_DefersInOrder_AndCoalescesLatestAtOverflow()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper-backpressure.log", LogLevel.Warning);
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false, maxActiveTasks: 1, maxDeferredOperations: 2);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new ConcurrentQueue<int>();

        Assert.True(helper.TryQueue(
            backgroundTasks,
            backgroundWorkCts,
            async cancellationToken =>
            {
                executionOrder.Enqueue(1);
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            "blocking"));

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(helper.TryQueue(backgroundTasks, backgroundWorkCts, _ => RecordExecutionAsync(executionOrder, 2), "refresh"));
        Assert.True(helper.TryQueue(backgroundTasks, backgroundWorkCts, _ => RecordExecutionAsync(executionOrder, 3), "coalesced-refresh"));
        Assert.False(helper.TryQueue(backgroundTasks, backgroundWorkCts, _ => RecordExecutionAsync(executionOrder, 5), "unrelated-overflow"));
        Assert.True(helper.TryQueue(backgroundTasks, backgroundWorkCts, _ => RecordExecutionAsync(executionOrder, 4), "coalesced-refresh"));
        Assert.Equal(2, helper.DeferredOperationCountForTests);

        releaseFirst.TrySetResult();

        await TestExecutionGuards.WaitUntilAsync(
            () => backgroundTasks.IsEmpty && helper.DeferredOperationCountForTests == 0,
            "Deferred AppViewModel work did not drain after active work completed.");

        Assert.Equal([1, 2, 4], [.. executionOrder]);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("background-queue-saturated | action=defer maxActive=1", logText, StringComparison.Ordinal);
        Assert.Contains("background-deferred-queue-saturated | operation=unrelated-overflow action=drop-newest maxDeferred=2", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryQueue_EnforcesActiveLimitDuringConcurrentEventStorm()
    {
        const int operationCount = 64;
        const int maxActiveTasks = 4;
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper-concurrent.log", LogLevel.Warning);
        var helper = new AppViewModelBackgroundWorkHelper(
            loggerScope.Logger,
            () => false,
            maxActiveTasks,
            maxDeferredOperations: operationCount - maxActiveTasks);
        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        using var backgroundWorkCts = new CancellationTokenSource();
        var releaseOperations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeOperations = 0;
        int peakActiveOperations = 0;

        Task<bool>[] queueAttempts = [.. Enumerable.Range(0, operationCount)
            .Select(index => Task.Run(() => helper.TryQueue(
                backgroundTasks,
                backgroundWorkCts,
                async cancellationToken =>
                {
                    int currentActive = Interlocked.Increment(ref activeOperations);
                    UpdateMaximum(ref peakActiveOperations, currentActive);
                    try
                    {
                        await releaseOperations.Task.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeOperations);
                    }
                },
                $"storm-{index}")))];

        bool[] queueResults = await Task.WhenAll(queueAttempts).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.All(queueResults, Assert.True);
        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref activeOperations) == maxActiveTasks,
            "The expected number of active storm operations did not start.");
        Assert.True(backgroundTasks.Count <= maxActiveTasks);
        Assert.Equal(operationCount - maxActiveTasks, helper.DeferredOperationCountForTests);

        releaseOperations.TrySetResult();

        await TestExecutionGuards.WaitUntilAsync(
            () => backgroundTasks.IsEmpty && helper.DeferredOperationCountForTests == 0,
            "Concurrent deferred AppViewModel work did not fully drain.");
        Assert.Equal(maxActiveTasks, peakActiveOperations);
    }

    [Fact]
    public void ClearDeferredOperations_DropsQueuedClosuresDuringCleanup()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelBackgroundWorkHelperTests), "appvm-background-helper-clear.log");
        var helper = new AppViewModelBackgroundWorkHelper(loggerScope.Logger, () => false, maxActiveTasks: 1, maxDeferredOperations: 2);
        var backgroundTasks = new ConcurrentDictionary<int, Task>
        {
            [1] = new TaskCompletionSource().Task,
        };
        using var backgroundWorkCts = new CancellationTokenSource();

        Assert.True(helper.TryQueue(backgroundTasks, backgroundWorkCts, static _ => Task.CompletedTask, "deferred"));
        Assert.Equal(1, helper.DeferredOperationCountForTests);

        helper.ClearDeferredOperations();

        Assert.Equal(0, helper.DeferredOperationCountForTests);
    }

    private static Task RecordExecutionAsync(ConcurrentQueue<int> executionOrder, int value)
    {
        executionOrder.Enqueue(value);
        return Task.CompletedTask;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
