using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppDebouncedBackgroundWorkCoordinatorTests
{
    [Fact]
    public void BeginDebounce_CancelsAndDisposesPreviousDebounceSource_AndReturnsNewSource()
    {
        using var previous = new CancellationTokenSource();

        CancellationTokenSource next = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(_ => previous);

        try
        {
            Assert.True(previous.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = previous.Token.WaitHandle);
            Assert.False(next.IsCancellationRequested);
        }
        finally
        {
            next.Dispose();
        }
    }

    [Fact]
    public void BeginDebounce_ByRef_ReportsWhetherItReplacedAPreviousDebounce()
    {
        CancellationTokenSource? current = null;

        CancellationTokenSource first = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref current, out bool replacedFirst);
        CancellationTokenSource second = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref current, out bool replacedSecond);

        try
        {
            Assert.False(replacedFirst);
            Assert.True(replacedSecond);
            Assert.True(first.IsCancellationRequested);
            Assert.Same(second, current);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void CancelAndDetach_CancelsCurrentDebounceAndClearsState()
    {
        CancellationTokenSource? current = new();

        CancellationTokenSource? detached = AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref current);

        Assert.NotNull(detached);
        Assert.True(detached.IsCancellationRequested);
        Assert.Null(current);
        detached.Dispose();
    }

    [Fact]
    public void CancelAndDispose_CancelsDisposesAndClearsState()
    {
        CancellationTokenSource? current = new();

        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref current);

        Assert.Null(current);
    }

    [Fact]
    public void ReleaseOwned_ClearsCurrentOnlyWhenItStillOwnsTheState()
    {
        CancellationTokenSource? current = new();
        CancellationTokenSource owned = current;

        AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref current, owned);

        Assert.Null(current);
        Assert.Throws<ObjectDisposedException>(() => _ = owned.Token.WaitHandle);
    }

    [Fact]
    public void ReleaseOwned_DoesNotClearNewerDebounceState()
    {
        using var owned = new CancellationTokenSource();
        CancellationTokenSource? current = new();
        CancellationTokenSource replacement = current;

        AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref current, owned);

        Assert.Same(replacement, current);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesOwnedDebounce_WhenWorkCompletes()
    {
        using var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;
        CancellationToken observedToken = CancellationToken.None;

        await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
            ownedDebounceCts,
            current => released = current,
            token =>
            {
                observedToken = token;
                return Task.CompletedTask;
            },
            shutdownCts.Token);

        Assert.Same(ownedDebounceCts, released);
        Assert.False(observedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ExecuteAsync_SwallowsOperationCanceledException_AndReleasesOwnedDebounce()
    {
        using var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;
        shutdownCts.Cancel();

        await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
            ownedDebounceCts,
            current => released = current,
            token =>
            {
                Assert.True(token.IsCancellationRequested);
                throw new OperationCanceledException(token);
            },
            shutdownCts.Token);

        Assert.Same(ownedDebounceCts, released);
    }

    [Fact]
    public async Task ExecuteDelayedAsync_WaitsBeforeRunningWorkAndReleasesOwnedDebounce()
    {
        using var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;
        bool workInvoked = false;

        await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
            ownedDebounceCts,
            current => released = current,
            delayMs: 20,
            _ =>
            {
                workInvoked = true;
                return Task.CompletedTask;
            },
            shutdownCts.Token);

        Assert.True(workInvoked);
        Assert.Same(ownedDebounceCts, released);
    }

    [Fact]
    public async Task ExecuteDelayedAsync_SwallowsCanceledDelayAndReleasesOwnedDebounce()
    {
        using var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;
        bool workInvoked = false;

        ownedDebounceCts.Cancel();

        await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
            ownedDebounceCts,
            current => released = current,
            delayMs: 20,
            _ =>
            {
                workInvoked = true;
                return Task.CompletedTask;
            },
            shutdownCts.Token);

        Assert.False(workInvoked);
        Assert.Same(ownedDebounceCts, released);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesOwnedDebounce_WhenWorkFails()
    {
        using var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                ownedDebounceCts,
                current => released = current,
                _ => throw new InvalidOperationException("boom"),
                shutdownCts.Token));

        Assert.Equal("boom", exception.Message);
        Assert.Same(ownedDebounceCts, released);
    }

    [Fact]
    public async Task ExecuteAsync_SwallowsDisposedSupersededDebounce_AndReleasesOwnedDebounce()
    {
        var ownedDebounceCts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        CancellationTokenSource? released = null;
        bool workInvoked = false;

        ownedDebounceCts.Cancel();
        ownedDebounceCts.Dispose();

        await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
            ownedDebounceCts,
            current => released = current,
            _ =>
            {
                workInvoked = true;
                return Task.CompletedTask;
            },
            shutdownCts.Token);

        Assert.False(workInvoked);
        Assert.Same(ownedDebounceCts, released);
    }

    [Fact]
    public async Task RapidReplacement_SkipsSupersededWorkAndRunsOnlyLatestRequest()
    {
        CancellationTokenSource? current = null;
        CancellationTokenSource first = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref current);
        CancellationTokenSource second = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref current);
        int firstInvocations = 0;
        int secondInvocations = 0;

        await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
            first,
            owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref current, owned),
            _ =>
            {
                firstInvocations++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
            second,
            owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref current, owned),
            _ =>
            {
                secondInvocations++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, firstInvocations);
        Assert.Equal(1, secondInvocations);
        Assert.Null(current);
    }
}
