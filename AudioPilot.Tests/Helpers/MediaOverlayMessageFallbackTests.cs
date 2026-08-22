using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayMessageFallbackTests
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
                recoveryDisposition ?? TrackNavigationRecoveryDisposition.Unchanged));
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToUnchanged_WhenAmbiguousSnapshotMatchesBaseline()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);
        var ambiguousSnapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);

        MediaOverlayResult result = FormatTrackMessage(MediaOverlayCommand.NextTrack, baseline, ambiguousSnapshot);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track unchanged", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToUnchanged_WhenTimelineResetsButDispositionIsUnchanged()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);
        var staleResetSnapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 0);

        MediaOverlayResult result = FormatTrackMessage(MediaOverlayCommand.NextTrack, baseline, staleResetSnapshot);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track unchanged", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToMetadataLoading_WhenDispositionIsMetadataPending()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);
        var staleResetSnapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 0);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            staleResetSnapshot,
            recoveryDisposition: TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.MetadataPending));

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track metadata loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToUnchanged_WhenSnapshotHasNoMetadataButDispositionIsUnchanged()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);

        MediaOverlayResult result = FormatTrackMessage(MediaOverlayCommand.NextTrack, baseline, MediaOverlaySessionSnapshot.Empty);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track unchanged", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToLoading_WhenSessionDroppedAndStillMissing()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            MediaOverlaySessionSnapshot.Empty,
            recoveryDisposition: TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Missing),
            sawSessionDrop: true);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToMetadataLoading_WhenSessionReturnedWithoutMetadata()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);
        var reappearedWithoutMetadata = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: null,
            Artist: null,
            AlbumTitle: null,
            SourceAppUserModelId: "browser",
            PositionSeconds: 0);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            reappearedWithoutMetadata,
            recoveryDisposition: TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.MetadataPending),
            sawSessionDrop: true);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track metadata loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackFallsBackToLoading_WhenTrackChangeRemainsPendingAfterSessionDrop()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "brave",
            PositionSeconds: 0);
        var sameTrackAtStart = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "brave",
            PositionSeconds: 0);

        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            baseline,
            sameTrackAtStart,
            recoveryDisposition: TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Loading),
            sawSessionDrop: true);

        Assert.True(result.ShowOverlay);
        Assert.False(result.UseTrackFormatting);
        Assert.Equal("Next track loading", result.Message);
    }

    [Fact]
    public void BuildOverlayMessage_NextTrackHidden_WhenNoMediaSessionContextExists()
    {
        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.NextTrack,
            MediaOverlaySessionSnapshot.Empty,
            MediaOverlaySessionSnapshot.Empty);

        Assert.False(result.ShowOverlay);
    }

    [Fact]
    public void BuildOverlayMessage_PlayPauseHidden_WhenNoMediaSessionContextExists()
    {
        MediaOverlayResult result = FormatTrackMessage(
            MediaOverlayCommand.PlayPause,
            MediaOverlaySessionSnapshot.Empty,
            MediaOverlaySessionSnapshot.Empty);

        Assert.False(result.ShowOverlay);
    }
}
