using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlaySessionMaterializerTests
{
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
