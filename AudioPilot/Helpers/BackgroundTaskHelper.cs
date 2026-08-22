using System.Collections.Concurrent;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    /// <summary>
    /// Describes how a bounded background-task drain completed during shutdown or disposal.
    /// </summary>
    public enum BackgroundTaskDrainStatus
    {
        CompletedWithinInitial,
        CompletedWithinGrace,
        TimedOut,
    }

    /// <summary>
    /// Captures the final outcome of a bounded drain attempt together with the number of tasks still pending at each
    /// timeout boundary.
    /// </summary>
    public readonly record struct BackgroundTaskDrainResult(
        BackgroundTaskDrainStatus Status,
        int PendingAfterInitial,
        int PendingAfterGrace,
        int FaultedCount,
        int CanceledCount);

    /// <summary>
    /// Provides logging callbacks for the major milestones of a bounded background-task drain.
    /// </summary>
    public readonly record struct BackgroundTaskDrainLoggingCallbacks(
        Action<int, int, int> LogInitialTimeout,
        Action<int, int> LogCompletedWithinGrace,
        Action<int, int, int> LogForcedTimeout);

    /// <summary>
    /// Shared helpers for queuing, draining, and disposing background work tracked by a task dictionary.
    /// </summary>
    public static class BackgroundTaskHelper
    {
        internal static Func<int, Task> DelayAsyncForTests { get; set; } = Task.Delay;

        /// <summary>
        /// Returns a point-in-time snapshot of the currently tracked background tasks.
        /// </summary>
        public static Task[] SnapshotPendingTasks(ConcurrentDictionary<int, Task> backgroundTasks)
        {
            return [.. backgroundTasks.Values];
        }

        /// <summary>
        /// Cancels the shared background-work token source and returns the tasks that were still tracked at the time of
        /// cancellation.
        /// </summary>
        public static Task[] CancelAndSnapshotPendingTasks(CancellationTokenSource backgroundWorkCts, ConcurrentDictionary<int, Task> backgroundTasks)
        {
            backgroundWorkCts.Cancel();
            return SnapshotPendingTasks(backgroundTasks);
        }

        /// <summary>
        /// Disposes the shared cancellation source and clears the tracked task dictionary after shutdown cleanup.
        /// </summary>
        public static void DisposeResources(CancellationTokenSource backgroundWorkCts, ConcurrentDictionary<int, Task> backgroundTasks)
        {
            backgroundWorkCts.Dispose();
            backgroundTasks.Clear();
        }

        public static BackgroundTaskDrainLoggingCallbacks CreateWarningDrainLoggingCallbacks(
            ILogger logger,
            string category,
            Func<int, int, int, string> buildInitialTimeoutMessage,
            Func<int, int, string> buildCompletedWithinGraceMessage,
            Func<int, int, int, string> buildForcedTimeoutMessage)
        {
            return new BackgroundTaskDrainLoggingCallbacks(
                LogInitialTimeout: (pendingAfterInitial, faultedCount, canceledCount) =>
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.Warning(category, () => buildInitialTimeoutMessage(pendingAfterInitial, faultedCount, canceledCount));
                    }
                },
                LogCompletedWithinGrace: (faultedCount, canceledCount) =>
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.Warning(category, () => buildCompletedWithinGraceMessage(faultedCount, canceledCount));
                    }
                },
                LogForcedTimeout: (pendingAfterGrace, faultedCount, canceledCount) =>
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.Warning(category, () => buildForcedTimeoutMessage(pendingAfterGrace, faultedCount, canceledCount));
                    }
                });
        }

        public static bool TryQueue(
            ConcurrentDictionary<int, Task> backgroundTasks,
            ref int backgroundTaskId,
            CancellationTokenSource backgroundWorkCts,
            Func<bool> isShuttingDown,
            Func<CancellationToken, Task> operation,
            Action<Exception> onOperationFailed,
            Action? onTaskCompleted = null)
        {
            if (isShuttingDown())
            {
                return false;
            }

            int taskId = Interlocked.Increment(ref backgroundTaskId);
            CancellationToken shutdownToken;
            try
            {
                shutdownToken = backgroundWorkCts.Token;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (shutdownToken.IsCancellationRequested)
            {
                return false;
            }

            void RemoveTrackedTask()
            {
                if (backgroundTasks.TryRemove(taskId, out Task? _))
                {
                    onTaskCompleted?.Invoke();
                }
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await operation(shutdownToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    onOperationFailed(ex);
                }
                finally
                {
                    RemoveTrackedTask();
                }
            });

            backgroundTasks[taskId] = task;
            if (task.IsCompleted)
            {
                RemoveTrackedTask();
            }
            else
            {
                _ = task.ContinueWith(
                    _ => RemoveTrackedTask(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return true;
        }

        public static bool TryQueueWithPolicy(
            ConcurrentDictionary<int, Task> backgroundTasks,
            ref int backgroundTaskId,
            CancellationTokenSource backgroundWorkCts,
            Func<bool> isShuttingDown,
            Func<CancellationToken, Task> operation,
            string operationName,
            Action<string, Exception> onOperationFailed,
            int? maxPendingTasks = null,
            Action<string>? onQueueSaturated = null,
            Action? onTaskCompleted = null)
        {
            if (isShuttingDown())
            {
                return false;
            }

            if (maxPendingTasks is int maxEntries && backgroundTasks.Count >= maxEntries)
            {
                onQueueSaturated?.Invoke(operationName);
                return false;
            }

            return TryQueue(
                backgroundTasks,
                ref backgroundTaskId,
                backgroundWorkCts,
                isShuttingDown,
                operation,
                ex => onOperationFailed(operationName, ex),
                onTaskCompleted);
        }

        private static (int FaultedCount, int CanceledCount) CountCompletedTaskOutcomes(Task[] tasks)
        {
            int faultedCount = 0;
            int canceledCount = 0;

            for (int index = 0; index < tasks.Length; index++)
            {
                Task task = tasks[index];
                if (task.IsFaulted)
                {
                    faultedCount++;
                }
                else if (task.IsCanceled)
                {
                    canceledCount++;
                }
            }

            return (faultedCount, canceledCount);
        }

        public static async Task<BackgroundTaskDrainResult> DrainWithGraceAsync(
            Task[] pendingTasks,
            int initialWaitMs,
            int graceWaitMs)
        {
            if (pendingTasks.Length == 0)
            {
                return new BackgroundTaskDrainResult(BackgroundTaskDrainStatus.CompletedWithinInitial, 0, 0, 0, 0);
            }

            Task allTasks = Task.WhenAll(pendingTasks);
            Task timeoutTask = DelayAsyncForTests(initialWaitMs);
            Task completedTask = await Task.WhenAny(allTasks, timeoutTask);

            if (completedTask == allTasks)
            {
                try
                {
                    await allTasks;
                }
                catch
                {
                }

                (int faultedCount, int canceledCount) = CountCompletedTaskOutcomes(pendingTasks);

                return new BackgroundTaskDrainResult(BackgroundTaskDrainStatus.CompletedWithinInitial, 0, 0, faultedCount, canceledCount);
            }

            int pendingAfterInitial = pendingTasks.Count(task => !task.IsCompleted);

            Task graceTimeoutTask = DelayAsyncForTests(graceWaitMs);
            completedTask = await Task.WhenAny(allTasks, graceTimeoutTask);
            if (completedTask == allTasks)
            {
                try
                {
                    await allTasks;
                }
                catch
                {
                }

                (int faultedCount, int canceledCount) = CountCompletedTaskOutcomes(pendingTasks);

                return new BackgroundTaskDrainResult(BackgroundTaskDrainStatus.CompletedWithinGrace, pendingAfterInitial, 0, faultedCount, canceledCount);
            }

            int pendingAfterGrace = pendingTasks.Count(task => !task.IsCompleted);
            (int finalFaultedCount, int finalCanceledCount) = CountCompletedTaskOutcomes(pendingTasks);
            return new BackgroundTaskDrainResult(
                BackgroundTaskDrainStatus.TimedOut,
                pendingAfterInitial,
                pendingAfterGrace,
                finalFaultedCount,
                finalCanceledCount);
        }

        /// <summary>
        /// Waits for the supplied background tasks to finish within an initial timeout and a final grace period,
        /// logging the stage transitions when work outlives the expected shutdown window.
        /// </summary>
        /// <remarks>
        /// This helper is intentionally tolerant of faulted or canceled tasks because it is used during cleanup and
        /// shutdown flows where the caller needs a best-effort drain result rather than exception propagation. The
        /// returned boolean communicates whether all work completed before the forced-timeout boundary.
        /// </remarks>
        public static async Task<bool> DrainWithGraceAndLoggingAsync(
            Task[] pendingTasks,
            int initialWaitMs,
            int graceWaitMs,
            BackgroundTaskDrainLoggingCallbacks loggingCallbacks)
        {
            if (pendingTasks.Length == 0)
            {
                return true;
            }

            BackgroundTaskDrainResult result = await DrainWithGraceAsync(pendingTasks, initialWaitMs, graceWaitMs);
            if (result.Status == BackgroundTaskDrainStatus.CompletedWithinInitial)
            {
                return true;
            }

            loggingCallbacks.LogInitialTimeout(result.PendingAfterInitial, result.FaultedCount, result.CanceledCount);

            if (result.Status == BackgroundTaskDrainStatus.CompletedWithinGrace)
            {
                loggingCallbacks.LogCompletedWithinGrace(result.FaultedCount, result.CanceledCount);
                return true;
            }

            loggingCallbacks.LogForcedTimeout(result.PendingAfterGrace, result.FaultedCount, result.CanceledCount);
            return false;
        }
    }
}
