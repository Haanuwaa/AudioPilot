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
        await Assert.ThrowsAsync<TimeoutException>(() =>
            TestExecutionGuards.RunOnSharedStaAsync(
                async () => await Task.Delay(TimeSpan.FromSeconds(5)),
                TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task RunAsync_ProcessesQueuedCallsSequentiallyWithoutLeakingState()
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
}
