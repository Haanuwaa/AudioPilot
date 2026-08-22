using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayCommandSnapshotCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetManagerAsync_CompletedFailure_IsEvictedAndRetried(bool canceled)
    {
        int calls = 0;
        var cache = new MediaOverlayCommandSnapshotCache(_ => ++calls == 1
            ? canceled
                ? Task.FromCanceled<GlobalSystemMediaTransportControlsSessionManager>(new CancellationToken(true))
                : Task.FromException<GlobalSystemMediaTransportControlsSessionManager>(new InvalidOperationException("Failed request"))
            : Task.FromResult<GlobalSystemMediaTransportControlsSessionManager>(null!));

        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetManagerAsync(1, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetManagerAsync(1, CancellationToken.None));
        }
        Assert.Equal(0, cache.ManagerEntryCountForTests);
        _ = await cache.GetManagerAsync(1, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSessionSnapshotsAsync_CompletedFailure_IsEvictedAndRetried(bool canceled)
    {
        var cache = new MediaOverlayCommandSnapshotCache();
        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> failed = canceled
            ? Task.FromCanceled<IReadOnlyList<MediaOverlaySessionSnapshot>>(new CancellationToken(true))
            : Task.FromException<IReadOnlyList<MediaOverlaySessionSnapshot>>(new InvalidOperationException("Failed snapshot"));

        if (canceled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetSessionSnapshotsAsync(1, () => failed));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetSessionSnapshotsAsync(1, () => failed));
        }
        Assert.Equal(0, cache.SnapshotEntryCountForTests);
        IReadOnlyList<MediaOverlaySessionSnapshot> recovered = [];
        Assert.Same(recovered, await cache.GetSessionSnapshotsAsync(1, () => Task.FromResult(recovered)));
    }

    [Fact]
    public async Task GetManagerAsync_OldFailure_DoesNotEvictReplacementOrItsSnapshots()
    {
        var pending = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var cache = new MediaOverlayCommandSnapshotCache(_ => ++calls == 1
            ? pending.Task
            : Task.FromResult<GlobalSystemMediaTransportControlsSessionManager>(null!));

        Task<GlobalSystemMediaTransportControlsSessionManager> old = cache.GetManagerAsync(1, CancellationToken.None);
        cache.Clear(1);
        Task<GlobalSystemMediaTransportControlsSessionManager> replacement = cache.GetManagerAsync(1, CancellationToken.None);
        _ = await cache.GetSessionSnapshotsAsync(1, () => Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>([]));
        pending.SetException(new InvalidOperationException("Old request failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);

        Assert.Same(replacement, cache.GetManagerAsync(1, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.SnapshotEntryCountForTests);
    }

    [Fact]
    public async Task GetSessionSnapshotsAsync_OldFailure_DoesNotEvictReplacement()
    {
        var cache = new MediaOverlayCommandSnapshotCache();
        var pending = new TaskCompletionSource<IReadOnlyList<MediaOverlaySessionSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> old = cache.GetSessionSnapshotsAsync(1, () => pending.Task);
        cache.InvalidateSnapshots(1);
        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> replacement = cache.GetSessionSnapshotsAsync(1,
            () => Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>([]));
        pending.SetException(new InvalidOperationException("Old snapshot failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);

        Assert.Same(replacement, cache.GetSessionSnapshotsAsync(1, () => throw new InvalidOperationException("Unexpected rebuild")));
        Assert.Equal(1, cache.SnapshotEntryCountForTests);
    }

    [Fact]
    public async Task GetManagerAsync_ReusesEntry_UntilCommandIsCleared()
    {
        int factoryInvocationCount = 0;
        var cache = new MediaOverlayCommandSnapshotCache(_ =>
        {
            factoryInvocationCount++;
            return Task.FromResult<GlobalSystemMediaTransportControlsSessionManager>(null!);
        });

        _ = await cache.GetManagerAsync(17, TestContext.Current.CancellationToken);
        _ = await cache.GetManagerAsync(17, TestContext.Current.CancellationToken);

        Assert.Equal(1, factoryInvocationCount);
        Assert.Equal(1, cache.ManagerEntryCountForTests);

        cache.Clear(17);

        Assert.Equal(0, cache.ManagerEntryCountForTests);
        _ = await cache.GetManagerAsync(17, TestContext.Current.CancellationToken);
        Assert.Equal(2, factoryInvocationCount);
    }

    [Fact]
    public async Task Clear_RemovesManagerAndMaterializedSnapshotEntries()
    {
        var cache = new MediaOverlayCommandSnapshotCache(_ =>
            Task.FromResult<GlobalSystemMediaTransportControlsSessionManager>(null!));

        _ = await cache.GetManagerAsync(23, TestContext.Current.CancellationToken);
        _ = await cache.GetSessionSnapshotsAsync(
            23,
            () => Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>([]));

        cache.Clear(23);

        Assert.Equal(0, cache.ManagerEntryCountForTests);
        Assert.Equal(0, cache.SnapshotEntryCountForTests);
    }

    [Fact]
    public async Task GetManagerAsync_WhenCanceled_RemovesFailedEntry()
    {
        var cache = new MediaOverlayCommandSnapshotCache(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null!;
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task request = cache.GetManagerAsync(29, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, cache.ManagerEntryCountForTests);
    }

    [Fact]
    public async Task GetSessionSnapshotsAsync_ReusesMaterializedSnapshots_PerCommandSequence()
    {
        MediaOverlayCommandSnapshotCache cache = new();
        MediaOverlaySessionSnapshot snapshot = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            "Album A",
            "spotify",
            12);
        int factoryInvocationCount = 0;

        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> Factory()
        {
            factoryInvocationCount++;
            return Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>([snapshot]);
        }

        IReadOnlyList<MediaOverlaySessionSnapshot> first = await cache.GetSessionSnapshotsAsync(1, Factory);
        IReadOnlyList<MediaOverlaySessionSnapshot> second = await cache.GetSessionSnapshotsAsync(1, Factory);

        Assert.Equal(1, factoryInvocationCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task InvalidateSnapshots_ForcesRebuild_ForSameCommandSequence()
    {
        MediaOverlayCommandSnapshotCache cache = new();
        int factoryInvocationCount = 0;

        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> Factory()
        {
            int invocationNumber = ++factoryInvocationCount;
            return Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>(
            [
                new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    $"Track {invocationNumber}",
                    "Artist",
                    "Album",
                    "spotify",
                    invocationNumber),
            ]);
        }

        IReadOnlyList<MediaOverlaySessionSnapshot> first = await cache.GetSessionSnapshotsAsync(7, Factory);
        cache.InvalidateSnapshots(7);
        IReadOnlyList<MediaOverlaySessionSnapshot> second = await cache.GetSessionSnapshotsAsync(7, Factory);

        Assert.Equal(2, factoryInvocationCount);
        Assert.NotSame(first, second);
        Assert.Equal("Track 1", first[0].Title);
        Assert.Equal("Track 2", second[0].Title);
    }

    [Fact]
    public async Task FailedSnapshotFactory_IsNotRetained_InCache()
    {
        MediaOverlayCommandSnapshotCache cache = new();
        int factoryInvocationCount = 0;

        Task<IReadOnlyList<MediaOverlaySessionSnapshot>> Factory()
        {
            factoryInvocationCount++;
            if (factoryInvocationCount == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult<IReadOnlyList<MediaOverlaySessionSnapshot>>(
            [
                new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Recovered",
                    "Artist",
                    "Album",
                    "spotify",
                    1),
            ]);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetSessionSnapshotsAsync(9, Factory));
        IReadOnlyList<MediaOverlaySessionSnapshot> recovered = await cache.GetSessionSnapshotsAsync(9, Factory);

        Assert.Equal(2, factoryInvocationCount);
        Assert.Equal("Recovered", Assert.Single(recovered).Title);
    }
}
