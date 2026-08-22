using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class SharedStaDispatcherHostTests
{
    [Fact]
    public void Run_UsesDedicatedStaThread()
    {
        int executingThreadId = 0;
        ApartmentState apartmentState = ApartmentState.Unknown;

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            executingThreadId = Environment.CurrentManagedThreadId;
            apartmentState = Thread.CurrentThread.GetApartmentState();
        });

        Assert.NotEqual(0, executingThreadId);
        Assert.Equal(ApartmentState.STA, apartmentState);
    }

    [Fact]
    public async Task RunAsync_PropagatesExceptions()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestExecutionGuards.RunOnSharedStaAsync(() =>
            {
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task RunAsync_TimesOutPredictably()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                TestExecutionGuards.RunOnSharedStaAsync(async () =>
                {
                    await release.Task;
                    finished.TrySetResult();
                }, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RunAsync_ReusesSameDispatcherAcrossCompletedCalls()
    {
        int firstThreadId = 0;
        bool firstCallObserved = false;
        bool secondCallObserved = false;

        await TestExecutionGuards.RunOnSharedStaAsync(() =>
        {
            firstThreadId = Environment.CurrentManagedThreadId;
            firstCallObserved = Dispatcher.CurrentDispatcher.CheckAccess();
            return Task.CompletedTask;
        });

        await TestExecutionGuards.RunOnSharedStaAsync(() =>
        {
            secondCallObserved = Dispatcher.CurrentDispatcher.CheckAccess();
            Assert.Equal(firstThreadId, Environment.CurrentManagedThreadId);
            return Task.CompletedTask;
        });

        Assert.True(firstCallObserved);
        Assert.True(secondCallObserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295L)]
    public async Task RunAsync_RejectsInvalidTimeoutBeforeQueueingWork(long milliseconds)
    {
        bool invoked = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TestExecutionGuards.RunOnSharedStaAsync(() =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(milliseconds)));
        await TestExecutionGuards.RunOnSharedStaAsync(() => Task.CompletedTask);
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_ReportsUnexpectedWorkCancellation()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestExecutionGuards.RunOnSharedStaAsync(() => Task.FromCanceled(new CancellationToken(true))));
        Assert.IsType<TaskCanceledException>(failure.InnerException);
    }
}
