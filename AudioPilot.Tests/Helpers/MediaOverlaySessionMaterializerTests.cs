using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlaySessionMaterializerTests
{
    [Fact]
    public async Task SourceFilter_SkipsUnrelatedMetadataAndIsolatesSourceReadFailures()
    {
        var reads = new List<string>();
        var failures = new List<Exception>();
        IReadOnlyList<MediaOverlaySessionSnapshot> result = await MediaOverlaySessionMaterializer.MaterializeAsync(
            ["unrelated", "broken", "target"],
            (source, _) =>
            {
                reads.Add(source);
                return Task.FromResult(new MediaOverlaySessionSnapshot(null, "Target track", null, null, source, null));
            },
            failures.Add,
            TestContext.Current.CancellationToken,
            source => source == "broken" ? throw new InvalidOperationException("Session source unavailable") : source == "target");

        Assert.Equal(["target"], reads);
        Assert.Equal("target", Assert.Single(result).SourceAppUserModelId);
        Assert.Single(failures);
    }

    [Fact]
    public async Task SourceFilter_CancellationDuringFinalExclusionDoesNotReturnPartialCapture()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaOverlaySessionMaterializer.MaterializeAsync(
            ["unrelated"],
            (_, _) => throw new InvalidOperationException("Excluded metadata must not be read"),
            _ => { }, cancellation.Token,
            _ => { cancellation.Cancel(); return false; }));
    }

    [Fact]
    public async Task MaterializeAsync_CanceledDuringFinalSnapshot_DoesNotReturnPartialCapture()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new List<Exception>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaOverlaySessionMaterializer.MaterializeAsync(
            ["session"],
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track", null, null, "browser", 0));
            },
            failures.Add,
            cancellation.Token));

        Assert.Empty(failures);
    }

    [Fact]
    public async Task MaterializeAsync_WhenOneSessionThrows_ContinuesWithLaterSessions()
    {
        var failures = new List<Exception>();

        IReadOnlyList<MediaOverlaySessionSnapshot> snapshots = await MediaOverlaySessionMaterializer.MaterializeAsync(
            ["stale", "valid"],
            static (session, _) => session == "stale"
                ? Task.FromException<MediaOverlaySessionSnapshot>(new InvalidOperationException("stale session"))
                : Task.FromResult(new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Valid title",
                    "Valid artist",
                    null,
                    "valid-source",
                    4)),
            failures.Add,
            TestContext.Current.CancellationToken);

        MediaOverlaySessionSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal("Valid title", snapshot.Title);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task MaterializeAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaOverlaySessionMaterializer.MaterializeAsync(
                ["session"],
                static (_, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
                static _ => { },
                cancellation.Token));
    }

    [Fact]
    public async Task MaterializeAsync_WhenSessionReturnsEmpty_SkipsIt()
    {
        IReadOnlyList<MediaOverlaySessionSnapshot> snapshots = await MediaOverlaySessionMaterializer.MaterializeAsync(
            ["empty"],
            static (_, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            static _ => { },
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshots);
    }
}
