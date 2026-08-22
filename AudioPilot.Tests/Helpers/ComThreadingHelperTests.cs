using System.Diagnostics;
using System.Reflection;

namespace AudioPilot.Tests.Helpers;

[Collection("CoreAudioWorkerIsolation")]
public sealed class ComThreadingHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreAudioComExecutor_SaturatedQueueRejectsWithoutBlockingCaller(bool returnsValue)
    {
        object executor = CreatePrivateExecutor();
        MethodInfo enqueue = GetPrivateExecutorMethod("Enqueue", [typeof(Action), typeof(CancellationToken)]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new List<Task>();
        bool overflowExecuted = false;
        try
        {
            accepted.Add((Task)enqueue.Invoke(executor, [(Action)(() =>
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }), CancellationToken.None])!);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            int capacity = (int)executor.GetType().GetField("MaxPendingWorkItems", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            for (int index = 0; index < capacity; index++)
                accepted.Add((Task)enqueue.Invoke(executor, [(Action)(static () => { }), CancellationToken.None])!);

            var stopwatch = Stopwatch.StartNew();
            Task rejected;
            if (returnsValue)
            {
                MethodInfo genericEnqueue = executor.GetType().GetMethods().Single(method => method.Name == "Enqueue" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(int));
                rejected = (Task)genericEnqueue.Invoke(executor, [(Func<int>)(() => { overflowExecuted = true; return 1; }), CancellationToken.None])!;
            }
            else
            {
                rejected = (Task)enqueue.Invoke(executor, [(Action)(() => overflowExecuted = true), CancellationToken.None])!;
            }
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(375), $"Queue admission blocked for {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);
            Assert.Contains("queue is full", exception.Message, StringComparison.Ordinal);
            Assert.False(overflowExecuted);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            finally
            {
                ((IDisposable)executor).Dispose();
            }
        }
    }

    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_OnNextInvocation()
    {
        int first = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 41, TestContext.Current.CancellationToken);
        Assert.Equal(41, first);

        await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync();

        await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync();

        int afterRestart = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => 42, TestContext.Current.CancellationToken);

        Assert.Equal(42, afterRestart);
    }

    [Fact]
    public async Task ForceCoreAudioWorkerFailure_RestartsWorker_AcrossRepeatedFailures()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await ComThreadingHelper.ForceCoreAudioWorkerFailureForTestsAsync();
            await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync();

            int value = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => iteration + 100, TestContext.Current.CancellationToken);
            Assert.Equal(iteration + 100, value);
        }
    }

    [Fact]
    public async Task CoreAudioComExecutor_Invoke_UnblocksWhenDisposeStartsDuringHungWorkItem()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        object executor = CreatePrivateExecutor();
        MethodInfo invokeMethod = GetPrivateExecutorMethod("Invoke", [typeof(Action), typeof(CancellationToken)]);
        MethodInfo disposeMethod = GetPrivateExecutorMethod(nameof(IDisposable.Dispose), Type.EmptyTypes);

        using var blockerStarted = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);

        Task invokeTask = Task.Run(() =>
        {
            try
            {
                invokeMethod.Invoke(executor,
                [
                    (Action)(() =>
                    {
                        blockerStarted.Set();
                        Assert.True(releaseBlocker.Wait(TimeSpan.FromSeconds(5), testCancellationToken));
                    }),
                    testCancellationToken,
                ]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }, testCancellationToken);

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5), testCancellationToken));

        disposeMethod.Invoke(executor, []);

        ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await invokeTask.WaitAsync(TimeSpan.FromSeconds(3), testCancellationToken));
        Assert.Contains("CoreAudioComExecutor", exception.ObjectName, StringComparison.Ordinal);

        releaseBlocker.Set();
    }

    private static object CreatePrivateExecutor()
    {
        Type? executorType = typeof(ComThreadingHelper)
            .GetNestedType("CoreAudioComExecutor", BindingFlags.NonPublic);

        Assert.NotNull(executorType);

        object? executor = Activator.CreateInstance(
            executorType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["AudioPilot.CoreAudioCOM.Test"],
            culture: null);

        Assert.NotNull(executor);
        return executor;
    }

    private static MethodInfo GetPrivateExecutorMethod(string name, Type[] parameterTypes)
    {
        Type executorType = typeof(ComThreadingHelper)
            .GetNestedType("CoreAudioComExecutor", BindingFlags.NonPublic)!;

        MethodInfo? method = executorType.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        Assert.NotNull(method);
        return method;
    }

}

