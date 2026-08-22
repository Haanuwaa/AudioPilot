using System.Collections.Concurrent;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class BackgroundTaskHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryQueue_WhenFailureReporterThrows_ContainsFailureAndRemovesTrackedTask(bool completionFails)
    {
        using var shutdown = new CancellationTokenSource();
        var tasks = new ConcurrentDictionary<int, Task>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("work failed");
        Exception? reportedFailure = null;
        int id = 0;
        int completions = 0;

        Assert.True(BackgroundTaskHelper.TryQueue(
            tasks,
            ref id,
            shutdown,
            static () => false,
            async _ =>
            {
                await release.Task;
                if (!completionFails)
                {
                    throw failure;
                }
            },
            exception =>
            {
                reportedFailure = exception;
                throw new InvalidOperationException("reporter failed");
            },
            () =>
            {
                Interlocked.Increment(ref completions);
                if (completionFails)
                {
                    throw failure;
                }
            }));

        Task queued = Assert.Single(tasks).Value;
        release.SetResult();
        await queued.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Same(failure, reportedFailure);
        Assert.Equal(1, completions);
        Assert.Empty(tasks);
    }
}
