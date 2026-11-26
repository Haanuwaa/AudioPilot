using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed partial class MediaOverlayTrackNavigationRecoveryTests
{

    [Fact]

    public void IsUsableTrackNavigationCandidate_ReturnsFalse_ForPausedChangedTrack_WhenBaselineWasPlaying()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Track",
            "Artist",
            null,
            "chrome",
            7);

        MediaOverlaySessionSnapshot pausedSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Paused Other Tab",
            "Other Artist",
            null,
            "chrome",
            0);

        bool usable = MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, pausedSibling);

        Assert.False(usable);
    }

    [Fact]
    public void IsUsableTrackNavigationCandidate_ReturnsFalse_ForPausedBrowserChangedTrack_WhenBaselineWasPaused()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Current Track",
            "Artist",
            null,
            "chrome",
            108);

        MediaOverlaySessionSnapshot pausedNextTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Next Track",
            "Artist",
            null,
            "chrome",
            0);

        bool usable = MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, pausedNextTrack);

        Assert.False(usable);
    }

    [Fact]
    public void IsUsableTrackNavigationCandidate_ReturnsFalse_ForBrowserFarPositionSibling()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Intended Track",
            "Artist",
            null,
            "brave",
            0);

        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Tab",
            "Other Artist",
            null,
            "brave",
            1175);

        bool usable = MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, wrongSibling);

        Assert.False(usable);
    }

    [Fact]
    public void IsUsableTrackNavigationCandidate_ReturnsTrue_ForBrowserAmbiguousNearStartPendingReason()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);

        MediaOverlaySessionSnapshot nextTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);

        bool usable = MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, nextTrack);

        Assert.True(usable);
    }

    [Fact]
    public void IsUsableTrackNavigationCandidate_ReturnsTrue_ForBrowserPlayingWithoutMetadataPendingReason()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            9);

        MediaOverlaySessionSnapshot metadataPending = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            10);

        bool usable = MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, metadataPending);

        Assert.True(usable);
    }

}

