using System.Reflection;

namespace AudioPilot.Tests.Helpers;

[Collection("CoreAudioWorkerIsolation")]
public sealed class ComThreadingHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunOnCoreAudioThreadAsync_CancelledNestedWorkDoesNotExecute(bool returnsValue)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool executed = false;
        Task nested = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            return returnsValue
                ? ComThreadingHelper.RunOnCoreAudioThreadAsync(() => { executed = true; return 42; }, cancelled.Token)
                : ComThreadingHelper.RunOnCoreAudioThreadAsync(() => { executed = true; }, cancelled.Token);
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nested);
        Assert.False(executed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreAudioComExecutor_SaturatedQueueRejectsWithoutBlockingCaller(bool returnsValue)
    {
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new List<Task>();
        bool overflowExecuted = false;
        try
        {
            accepted.Add(executor.Enqueue(() =>
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }, CancellationToken.None));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            int capacity = executor.CaptureHealth().QueueCapacity;
            for (int index = 0; index < capacity; index++)
                accepted.Add(executor.Enqueue(static () => { }, CancellationToken.None));

            Task rejected = Task.Run(async () =>
            {
                if (returnsValue)
                {
                    await executor.Enqueue(() => { overflowExecuted = true; return 1; }, CancellationToken.None);
                }
                else
                {
                    await executor.Enqueue(() => { overflowExecuted = true; }, CancellationToken.None);
                }
            }, TestContext.Current.CancellationToken);
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rejected.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Contains("queue is full", exception.Message, StringComparison.Ordinal);
            Assert.False(release.Task.IsCompleted);
            Assert.False(overflowExecuted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CoreAudioComExecutor_WorkerExitSignal_WaitsForRetirement()
    {
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = executor.Enqueue(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }, TestContext.Current.CancellationToken);
        Task? exited = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            exited = executor.EnqueueWorkerFailureForTests();
            Assert.False(exited.IsCompleted);
            Task<bool> earlyPublication = exited.ContinueWith(
                _ => TestPrivateAccess.GetField<Thread?>(executor, "_worker") != null,
                CancellationToken.None, TaskContinuationOptions.None, new CompletionObservationScheduler());
            release.TrySetResult();
            bool signaledBeforeRetirement = await earlyPublication.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(signaledBeforeRetirement, "Worker exit was reported while queue admission still saw the retiring worker.");
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(blocker, exited ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await executor.Enqueue(static () => { }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_OnNextInvocation()
    {
        int first = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 41, TestContext.Current.CancellationToken);
        Assert.Equal(41, first);

        await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        int afterRestart = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 42, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(42, afterRestart);
    }

    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_AcrossRepeatedFailures()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            int value = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => iteration + 100, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(iteration + 100, value);
        }
    }

    [Fact]
    public async Task CoreAudioComExecutor_Invoke_UnblocksWhenDisposeStartsDuringHungWorkItem()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task invokeTask = Task.Run(() => executor.Invoke(() =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }, testCancellationToken), testCancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
            executor.Dispose();
            ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => invokeTask.WaitAsync(TimeSpan.FromSeconds(3), testCancellationToken));
            Assert.Contains("CoreAudioComExecutor", exception.ObjectName, StringComparison.Ordinal);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCancellation_CompletesAndReleasesPayloadBeforeBlockedWorkReturns(bool returnsValue)
    {
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Cancellation");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocked = executor.Enqueue(() => { started.TrySetResult(); release.Task.GetAwaiter().GetResult(); }, CancellationToken.None);
        bool executed = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            (Task queued, WeakReference payload) = QueuePayload(executor, returnsValue, () => executed = true, cancellation.Token);
            cancellation.Cancel();
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(blocked.IsCompleted);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(payload.IsAlive);
        }
        finally { release.TrySetResult(); await blocked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        await executor.Enqueue(static () => { }, CancellationToken.None);
        Assert.False(executed);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Task Task, WeakReference Payload) QueuePayload(ComThreadingHelper.CoreAudioComExecutor executor, bool returnsValue, Action executed, CancellationToken token)
    {
        var payload = new byte[1024 * 1024];
        Task queued = returnsValue
            ? executor.Enqueue(() => { executed(); return payload.Length; }, token)
            : executor.Enqueue(() => { executed(); GC.KeepAlive(payload); }, token);
        return (queued, new WeakReference(payload));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAfterExecutionStarts_PreservesActualResult(bool returnsValue, bool fails)
    {
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.InFlight");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int Operation()
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (fails) throw new InvalidOperationException("Started operation failed");
            return 42;
        }
        Task result = returnsValue ? executor.Enqueue(Operation, cancellation.Token) : executor.Enqueue(() => { Operation(); }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            Assert.False(result.IsCompleted);
            release.TrySetResult();
            if (fails) await Assert.ThrowsAsync<InvalidOperationException>(() => result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            else
            {
                await result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                if (returnsValue) Assert.Equal(42, await (Task<int>)result);
            }
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationRacingQueueAdmissionOrExecution_NeverMisreportsAnExecutedOperationAsCanceled()
    {
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Races");
        for (int iteration = 0; iteration < 500; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            int calls = 0;
            Task cancel = Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken);
            Task operation = iteration % 2 == 0
                ? executor.Enqueue(() => Interlocked.Increment(ref calls), cancellation.Token)
                : executor.Enqueue(() => { Interlocked.Increment(ref calls); }, cancellation.Token);
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
            catch (OperationCanceledException) { Assert.True(operation.IsCanceled); }
            await cancel.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await executor.Enqueue(static () => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(operation.IsCanceled ? 0 : 1, Volatile.Read(ref calls));
        }
    }

    [Fact]
    public async Task HealthCapture_ReportsBlockedWorkWithoutJoiningItAndUsesMonotonicElapsedTime()
    {
        using var clock = new ManualTimeProvider();
        using var executor = new ComThreadingHelper.CoreAudioComExecutor("AudioPilot.CoreAudioCOM.Health", clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = executor.Enqueue(() => { started.TrySetResult(); release.Task.GetAwaiter().GetResult(); }, CancellationToken.None);
        Task? queued = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            queued = executor.Enqueue(static () => { }, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(6));
            var health = await Task.Run(executor.CaptureHealth, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(health.Initialized && health.Running && health.Busy);
            Assert.Equal(1, health.QueuedEntries);
            Assert.Equal(6000, health.ActiveOperationElapsedMs);
            Assert.Null(health.LastCompletedUtc);
            Assert.False(active.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (queued != null) await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await TestExecutionGuards.WaitUntilAsync(() => !executor.CaptureHealth().Busy, "Worker did not become idle.", TimeSpan.FromSeconds(5));
        var idle = executor.CaptureHealth();
        Assert.Equal(0, idle.QueuedEntries);
        Assert.Null(idle.ActiveOperationElapsedMs);
        Assert.Equal(clock.GetUtcNow(), idle.LastCompletedUtc);
        executor.Dispose();
        Assert.True(executor.CaptureHealth().ShuttingDown);
    }

    [Fact]
    public void HealthCapture_DoesNotInitializeTheSharedWorker()
    {
        var lazy = (Lazy<ComThreadingHelper.CoreAudioComExecutor>)typeof(ComThreadingHelper)
            .GetField("s_coreAudioExecutor", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        bool created = lazy.IsValueCreated;
        Assert.Equal(created, ComThreadingHelper.CaptureRuntimeHealth().Initialized);
        Assert.Equal(created, lazy.IsValueCreated);
    }

    /// <summary>Observes publication on the completing thread before a delayed continuation can hide an early signal.</summary>
    private sealed class CompletionObservationScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];
        protected override void QueueTask(Task task) => TryExecuteTask(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}

