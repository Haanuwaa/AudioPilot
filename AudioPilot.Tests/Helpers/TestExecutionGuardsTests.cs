using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class TestExecutionGuardsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunIsolatedSta_ShutsDownDispatcherOnItsOwnerThreadEvenWhenActionFails(bool actionFails)
    {
        Dispatcher? dispatcher = null;
        bool shutdownOnOwnerThread = false;
        void Run() => TestExecutionGuards.RunIsolatedSta(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.ShutdownFinished += (_, _) => shutdownOnOwnerThread = dispatcher.CheckAccess();
            if (actionFails)
                throw new InvalidOperationException("test action failed");
        });

        if (actionFails)
            Assert.Equal("test action failed", Assert.Throws<InvalidOperationException>(Run).Message);
        else
            Run();

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.True(shutdownOnOwnerThread);
    }

    [Fact]
    public async Task RunIsolatedSta_LateCompletionAfterTimeoutDoesNotCrashTheTestHost()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Throws<TimeoutException>(() => TestExecutionGuards.RunIsolatedSta(() =>
            {
                workerStarted.TrySetResult(Thread.CurrentThread);
                release.Task.GetAwaiter().GetResult();
            }, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            release.TrySetResult();
        }

        Thread worker = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
    }
}
