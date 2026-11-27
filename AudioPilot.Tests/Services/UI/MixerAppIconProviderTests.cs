using System.Windows.Media;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class MixerAppIconProviderTests
{
    [Fact]
    public async Task SaturationDoesNotCacheARejectedRequestAndAllowsRetryAfterDraining()
    {
        using var release = new ManualResetEventSlim();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var provider = new MixerAppIconProvider((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothStarted.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException();
            return null;
        });
        var first = provider.GetAsync(1, "first", null);
        var second = provider.GetAsync(2, "second", null);
        Assert.NotNull(first); Assert.NotNull(second);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var queued = Enumerable.Range(0, 32).Select(i => provider.GetAsync((uint)(i + 3), $"queued-{i}", null)).ToArray();
            Assert.All(queued, Assert.NotNull);
            Assert.Null(provider.GetAsync(100, "retry", null));
            release.Set();
            await Task.WhenAll(queued.OfType<Task<ImageSource?>>().Append(first).Append(second))
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var retry = provider.GetAsync(100, "retry", null);
            Assert.NotNull(retry);
            await retry.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(35, calls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Reopening_KeepsNativeReadsBoundedAndCancelsWaitingWorkersOnClose()
    {
        using var release = new ManualResetEventSlim();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, released = 0;
        using var original = new MixerAppIconProvider((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothStarted.TrySetResult();
            if (release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) Interlocked.Increment(ref released);
            return null;
        });
        _ = original.GetAsync(1, "first", null);
        _ = original.GetAsync(2, "second", null);
        MixerAppIconProvider? reopened = null;
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            original.Dispose();
            reopened = new((_, _) => { Interlocked.Increment(ref calls); return null; });
            var first = Assert.IsType<Task<ImageSource?>>(reopened.GetAsync(3, "third", null), exactMatch: false);
            var second = Assert.IsType<Task<ImageSource?>>(reopened.GetAsync(4, "fourth", null), exactMatch: false);
            await TestExecutionGuards.WaitUntilAsync(() => reopened.WaitingReaderCount == 2,
                "Reopened icon workers did not reach the native-reader gate.", TimeSpan.FromSeconds(5));
            reopened.Dispose();
            // Closing must settle workers waiting for slots while old native I/O is
            // still blocked, rather than waiting for an unrelated window to finish.
            await reopened.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(await first); Assert.Null(await second);
            Assert.Equal(2, calls);
            Assert.False(original.Completion.IsCompleted);
        }
        finally
        {
            reopened?.Dispose();
            release.Set();
            await original.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, released);
    }

    [Fact]
    public async Task Closing_SettlesQueuedRequestsWithoutWaitingForTwoBlockedNativeReaders()
    {
        using var release = new ManualResetEventSlim();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, released = 0;
        using var provider = new MixerAppIconProvider((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothStarted.TrySetResult();
            if (release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) Interlocked.Increment(ref released);
            return null;
        });
        var first = Assert.IsType<Task<ImageSource?>>(provider.GetAsync(1, "first", null), exactMatch: false);
        var second = Assert.IsType<Task<ImageSource?>>(provider.GetAsync(2, "second", null), exactMatch: false);
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(first, provider.GetAsync(1, "first", null));
            var waiting = Enumerable.Range(3, 64).Select(index => provider.GetAsync((uint)index, $"session-{index}", null)).ToArray();
            Assert.Equal(32, waiting.Count(task => task != null));
            provider.Dispose();
            Assert.False(provider.Completion.IsCompleted);
            Assert.Null(await first); Assert.Null(await second);
            Assert.All(await Task.WhenAll(waiting.OfType<Task<ImageSource?>>()), Assert.Null);
            Assert.Equal(2, calls);
            var afterClose = provider.GetAsync(100, "after-close", null);
            Assert.NotNull(afterClose);
            Assert.Null(await afterClose);
        }
        finally { release.Set(); }
        await provider.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, released);
    }
}
