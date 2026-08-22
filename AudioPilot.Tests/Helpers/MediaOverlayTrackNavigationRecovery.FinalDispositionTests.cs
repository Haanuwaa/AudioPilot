using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed partial class MediaOverlayTrackNavigationRecoveryTests
{

    [Fact]

    public void ClassifyFinalRecoveryDisposition_ReturnsLoading_WhenBrowserSessionDropRemainsPending()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            0);
        MediaOverlaySessionSnapshot sameTrackFallback = baseline with { PositionSeconds = 0 };

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            sameTrackFallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Loading, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsLoadingMissing_WhenSessionDropStillMissing()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            MediaOverlaySessionSnapshot.Empty,
            sawSessionDrop: true,
            hasRecentSignalForSource: true);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Missing, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsLoadingMetadataPending_WhenSessionReturnsWithoutMetadata()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);
        MediaOverlaySessionSnapshot fallback = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            0);

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.MetadataPending, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsUnchanged_WhenNoPendingSessionDropEvidenceExists()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "spotify",
            90);
        MediaOverlaySessionSnapshot fallback = baseline;

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: false,
            hasRecentSignalForSource: false);

        Assert.Equal(TrackNavigationRecoveryOutcome.Unchanged, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Unchanged, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsUnchanged_WhenNonBrowserSessionDropEndsOnStableBaseline()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "spotify",
            0);
        MediaOverlaySessionSnapshot fallback = baseline;

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true);

        Assert.Equal(TrackNavigationRecoveryOutcome.Unchanged, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Unchanged, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsLoading_WhenBlockedSameSourceRivalEvidenceExists()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            0);
        MediaOverlaySessionSnapshot fallback = baseline;
        BrowserSameSourceCommandSummary sameSourceSummary = new(
            ConflictActive: false,
            DistinctCandidateCount: 1,
            HasBlockedRivalEvidence: true,
            HasPendingCandidateEvidence: false,
            RivalReasonClasses: "paused-sibling");

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: false,
            sameSourceSummary);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Loading, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsUnchanged_WhenBrowserOnlyStaleRivalsRemain()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);
        MediaOverlaySessionSnapshot fallback = baseline;
        BrowserSameSourceCommandSummary sameSourceSummary = new(
            ConflictObserved: true,
            ConflictActive: false,
            DistinctCandidateCount: 2,
            ActiveRivalCount: 0,
            ReinforcedRivalCount: 0,
            StaleRivalCount: 1,
            RivalReasonClasses: "far-position");

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: false,
            sameSourceSummary);

        Assert.Equal(TrackNavigationRecoveryOutcome.Unchanged, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Unchanged, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsLoading_WhenBrowserActiveRivalEvidenceStillExists()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            0);
        MediaOverlaySessionSnapshot fallback = baseline;
        BrowserSameSourceCommandSummary sameSourceSummary = new(
            ConflictObserved: true,
            ConflictActive: true,
            DistinctCandidateCount: 2,
            ActiveRivalCount: 1,
            HasPendingCandidateEvidence: true,
            HasPendingNonWinnerRivalEvidence: true,
            RivalReasonClasses: "ambiguous-near-start");

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true,
            sameSourceSummary);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Loading, disposition.FallbackClassification);
    }

    [Fact]
    public void ClassifyFinalRecoveryDisposition_ReturnsMissing_WhenSessionDropEndsMissingWithOnlyStaleBrowserConflict()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            0);
        BrowserSameSourceCommandSummary sameSourceSummary = new(
            ConflictObserved: true,
            ConflictActive: false,
            DistinctCandidateCount: 2,
            ActiveRivalCount: 0,
            ReinforcedRivalCount: 0,
            StaleRivalCount: 1,
            RivalReasonClasses: "far-position");

        TrackNavigationRecoveryDisposition disposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
            baseline,
            MediaOverlaySessionSnapshot.Empty,
            sawSessionDrop: true,
            hasRecentSignalForSource: false,
            sameSourceSummary);

        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, disposition.Outcome);
        Assert.Equal(TrackNavigationFallbackClassification.Missing, disposition.FallbackClassification);
    }
}

