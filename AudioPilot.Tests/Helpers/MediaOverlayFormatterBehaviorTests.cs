using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayFormatterBehaviorTests
{
    private static MediaOverlayResult FormatTrackMessage(
        MediaOverlayCommand command,
        MediaOverlaySessionSnapshot baseline,
        MediaOverlaySessionSnapshot snapshot,
        TrackNavigationRecoveryDisposition? recoveryDisposition = null,
        bool sawSessionDrop = false)
    {
        return MediaOverlayMessageFormatter.BuildOverlayMessage(
            command,
            baseline,
            new SnapshotCaptureResult(
                snapshot,
                sawSessionDrop,
                recoveryDisposition ?? TrackNavigationRecoveryDisposition.Changed));
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackReturnsTrack_WhenChangedTrackObserved()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "spotify",
            42);
        var latest = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Next Track",
            "Artist B",
            null,
            "spotify",
            0);

        MediaOverlayResult result = FormatTrackMessage(MediaOverlayCommand.NextTrack, baseline, latest);

        Assert.True(result.IsTrackMessage);
        Assert.Equal("Next track", result.Header);
        Assert.Equal("Next Track", result.Title);
        Assert.Equal("Artist B", result.Artist);
    }

    [Fact]
    public void BuildOverlayMessage_PreviousTrackReturnsTrack_WhenChangedTrackObserved()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "spotify",
            42);
        var latest = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Previous Track",
            "Artist Z",
            null,
            "spotify",
            0);

        MediaOverlayResult result = FormatTrackMessage(MediaOverlayCommand.PreviousTrack, baseline, latest);

        Assert.True(result.IsTrackMessage);
        Assert.Equal("Previous track", result.Header);
        Assert.Equal("Previous Track", result.Title);
        Assert.Equal("Artist Z", result.Artist);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackUsesLoadingMessage_WhenDispositionIsLoading()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "brave",
            42);
        var latest = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "brave",
            42);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            latest,
            TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Loading),
            sawSessionDrop: true);

        Assert.True(result.IsPlainMessage);
        Assert.Equal("Next track loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackUsesMetadataLoadingMessage_WhenDispositionIsMetadataPending()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "brave",
            42);
        var latest = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "brave",
            0);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            latest,
            TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.MetadataPending),
            sawSessionDrop: true);

        Assert.True(result.IsPlainMessage);
        Assert.Equal("Next track metadata loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackUsesUnchangedMessage_WhenDispositionIsUnchanged()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist A",
            null,
            "brave",
            42);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            TrackNavigationRecoveryDisposition.Unchanged);

        Assert.True(result.IsPlainMessage);
        Assert.Equal("Next track unchanged", result.Message);
    }
}
