using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class SharedStaDispatcherHostTests
{
    [Fact]
    public async Task QueuedWorkTimesOutWithoutRunningOrReleasingTheCurrentOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool expiredInvoked = false;
        var clock = new ControlledTimeProvider();
        Task owner = SharedStaDispatcherHost.RunAsync(async () => { entered.TrySetResult(); await release.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Task queued = SharedStaDispatcherHost.RunAsync(
                () => { expiredInvoked = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), clock);
            clock.Expire();
            await Assert.ThrowsAsync<TimeoutException>(() => queued);
            Assert.False(owner.IsCompleted);
            Assert.False(expiredInvoked);
        }
        finally { release.TrySetResult(); await owner; }
        await SharedStaDispatcherHost.RunAsync(() => Task.CompletedTask);
        Assert.False(expiredInvoked);
    }

    [Fact]
    public async Task IndependentAsyncTests_DoNotInterleaveWhileAwaiting()
    {
        var entered = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondInvoked = false;
        Task first = SharedStaDispatcherHost.RunAsync(async () =>
        {
            entered.SetResult(Dispatcher.CurrentDispatcher);
            await release.Task;
        });
        Task? second = null;
        try
        {
            Dispatcher dispatcher = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            second = SharedStaDispatcherHost.RunAsync(() => { secondInvoked = true; return Task.CompletedTask; });
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, TestContext.Current.CancellationToken).Task
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(secondInvoked);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second != null) await second;
        }
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task TimedOutTest_RetainsDispatcherOwnershipUntilItsWorkFinishes()
    {
        var entered = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ControlledTimeProvider();
        Task first = SharedStaDispatcherHost.RunAsync(async () =>
        {
            entered.SetResult(Dispatcher.CurrentDispatcher);
            await release.Task;
        }, TimeSpan.FromSeconds(1), clock);
        bool secondInvoked = false;
        Task? second = null;
        try
        {
            Dispatcher dispatcher = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Expire();
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            second = SharedStaDispatcherHost.RunAsync(() => { secondInvoked = true; return Task.CompletedTask; });
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, TestContext.Current.CancellationToken).Task
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(secondInvoked);
        }
        finally
        {
            release.TrySetResult();
            if (second != null) await second;
        }
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task NestedDispatcherCalls_ReuseTheirCurrentOwnership()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SharedStaDispatcherHost.Run(() => Assert.True(dispatcher.CheckAccess()));
            await SharedStaDispatcherHost.RunAsync(() =>
            {
                Assert.True(dispatcher.CheckAccess());
                return Task.CompletedTask;
            });
        });
    }

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
        var clock = new ControlledTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task work = SharedStaDispatcherHost.RunAsync(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
            }, TimeSpan.FromSeconds(1), clock);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Expire();
            await Assert.ThrowsAsync<TimeoutException>(() => work);
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
    private sealed class ControlledTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public void Expire() => _callback?.Invoke(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new ControlledTimer(() => _callback = null);
        }
        private sealed class ControlledTimer(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => dispose();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

}
