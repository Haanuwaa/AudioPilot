using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayCommandSnapshotCacheTests
{
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
