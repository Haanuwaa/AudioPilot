using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class TestPrivateAccessTests
{
    [Fact]
    public void RunTaskOnDispatcher_PumpsQueuedCompletion()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => completion.SetResult());
            TestPrivateAccess.RunTaskOnDispatcher(completion.Task, TimeSpan.FromSeconds(2));
            Assert.True(completion.Task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void RunTaskOnDispatcher_PropagatesQueuedFailure()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("queued failure");
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => completion.SetException(failure));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                TestPrivateAccess.RunTaskOnDispatcher(completion.Task, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
        });
    }

    [Fact]
    public void RunTaskOnDispatcher_TimesOutWithoutStrandingTheDispatcher()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestExecutionGuards.RunIsolatedSta(() =>
            {
                Assert.Throws<TimeoutException>(() =>
                    TestPrivateAccess.RunTaskOnDispatcher(completion.Task, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
                var next = Dispatcher.CurrentDispatcher.InvokeAsync(() => true);
                TestPrivateAccess.RunTaskOnDispatcher(next.Task, TimeSpan.FromSeconds(2));
                Assert.True(next.Task.Result);
            }, TimeSpan.FromSeconds(3));
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    [Fact]
    public void RunTaskOnDispatcher_StopsOnCancellation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        try
        {
            TestExecutionGuards.RunIsolatedSta(() =>
            {
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => cancellation.Cancel());
                var failure = Assert.Throws<OperationCanceledException>(() =>
                    TestPrivateAccess.RunTaskOnDispatcher(completion.Task, TimeSpan.FromSeconds(2), cancellation.Token));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            });
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295L)]
    public void RunTaskOnDispatcher_RejectsInvalidTimeoutEvenForCompletedWork(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestPrivateAccess.RunTaskOnDispatcher(Task.CompletedTask, TimeSpan.FromMilliseconds(milliseconds), TestContext.Current.CancellationToken));
    }
}
