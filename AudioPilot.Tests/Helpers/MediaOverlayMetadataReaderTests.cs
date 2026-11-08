namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayMetadataReaderTests
{
    [Fact]
    public async Task Timeout_DoesNotResubmitPendingRead_AndDiscardsItsLateResult()
    {
        var session = new object();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var reader = new MediaOverlayMetadataReader<object, string>(
            _ => ++calls == 1 ? pending.Task : Task.FromResult("fresh"), TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(session, TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadAsync(session, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Equal("fresh", await reader.ReadAsync(new object(), TestContext.Current.CancellationToken));
        pending.SetResult("stale");
        Assert.Equal("fresh", await reader.ReadAsync(session, TestContext.Current.CancellationToken));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Cancellation_StopsWaitingForUncooperativeProvider_AndAllowsReplacementSession()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new object();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new MediaOverlayMetadataReader<object, string>(
            candidate => ReferenceEquals(candidate, session) ? pending.Task : Task.FromResult("replacement"), TimeSpan.FromSeconds(30));
        Task<string?> request = reader.ReadAsync(session, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal("replacement", await reader.ReadAsync(new object(), TestContext.Current.CancellationToken));
        pending.SetException(new InvalidOperationException("late provider failure"));
    }

    [Fact]
    public async Task FailedRead_CanBeRetried_AndPreCanceledCallDoesNotRead()
    {
        int calls = 0;
        var session = new object();
        var reader = new MediaOverlayMetadataReader<object, string>(
            _ => ++calls == 1 ? Task.FromException<string>(new InvalidOperationException()) : Task.FromResult("recovered"), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(session, new CancellationToken(true)));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(session, TestContext.Current.CancellationToken));
        Assert.Equal("recovered", await reader.ReadAsync(session, TestContext.Current.CancellationToken));
    }
}
