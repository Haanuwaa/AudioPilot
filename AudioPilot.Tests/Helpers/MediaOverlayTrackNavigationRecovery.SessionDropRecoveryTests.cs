using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed partial class MediaOverlayTrackNavigationRecoveryTests
{

    [Fact]

    public void ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop_ReturnsTrue_WhenSessionReappearsWithoutMetadata()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            90);
        var reappearedWithoutMetadata = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "browser",
            0);

        bool shouldExtend = MediaOverlayTrackNavigationRecoveryPolicy.ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop(
            baseline,
            reappearedWithoutMetadata,
            sawSessionDrop: true);

        Assert.True(shouldExtend);
    }

    [Fact]
    public void ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop_ReturnsFalse_WhenFallbackIsStillMissing()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            90);

        bool shouldExtend = MediaOverlayTrackNavigationRecoveryPolicy.ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop(
            baseline,
            MediaOverlaySessionSnapshot.Empty,
            sawSessionDrop: true);

        Assert.False(shouldExtend);
    }

    [Fact]
    public void ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop_ReturnsTrue_WhenSameTrackReturnsAtStartAfterDrop()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            0);
        var sameTrackAtStart = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            0);

        bool shouldExtend = MediaOverlayTrackNavigationRecoveryPolicy.ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop(
            baseline,
            sameTrackAtStart,
            sawSessionDrop: true);

        Assert.True(shouldExtend);
    }

    [Fact]
    public void IsSameTrackAtStartAfterSessionDrop_ReturnsTrue_WhenSameTrackReappearsAtStart()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            0);
        var fallback = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            0);

        bool sameTrackAtStart = MediaOverlayTrackNavigationRecoveryPolicy.IsSameTrackAtStartAfterSessionDrop(
            baseline,
            fallback,
            sawSessionDrop: true);

        Assert.True(sameTrackAtStart);
    }

    [Fact]
    public void IsSameTrackAtStartAfterSessionDrop_ReturnsTrue_WhenSameTrackReappearsWithinNearStartWindow()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            2);
        var fallback = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "browser",
            2);

        bool sameTrackAtStart = MediaOverlayTrackNavigationRecoveryPolicy.IsSameTrackAtStartAfterSessionDrop(
            baseline,
            fallback,
            sawSessionDrop: true);

        Assert.True(sameTrackAtStart);
    }

    [Fact]
    public void ShouldTreatBrowserSessionDropAsPending_ReturnsTrue_WhenActiveRivalEvidenceExists()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);
        var fallback = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);
        BrowserSameSourceCommandSummary sameSourceSummary = new(
            ConflictObserved: true,
            ConflictActive: true,
            DistinctCandidateCount: 2,
            ActiveRivalCount: 1,
            HasPendingCandidateEvidence: true,
            HasPendingNonWinnerRivalEvidence: true,
            RivalReasonClasses: "far-position");

        bool pending = MediaOverlayTrackNavigationRecoveryPolicy.ShouldTreatBrowserSessionDropAsPending(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true,
            sameSourceSummary);

        Assert.True(pending);
    }

    [Fact]
    public void ShouldTreatBrowserSessionDropAsPending_ReturnsFalse_WhenOnlyRecentSignalExists()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);
        var fallback = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            "Album",
            "brave",
            23);

        bool pending = MediaOverlayTrackNavigationRecoveryPolicy.ShouldTreatBrowserSessionDropAsPending(
            baseline,
            fallback,
            sawSessionDrop: true,
            hasRecentSignalForSource: true);

        Assert.False(pending);
    }

    [Fact]
    public void ShouldContinueSessionDropRecovery_ReturnsTrue_BeforeDeadlineAfterMinimumAttempts()
    {
        bool shouldContinue = MediaOverlayTrackNavigationRecoveryPolicy.ShouldContinueSessionDropRecovery(
            DateTimeOffset.UtcNow.AddSeconds(1),
            attempts: 5,
            minimumAttempts: 5,
            nowUtc: DateTimeOffset.UtcNow);

        Assert.True(shouldContinue);
    }

    [Fact]
    public void ShouldContinueSessionDropRecovery_ReturnsFalse_AfterDeadlineOnceMinimumAttemptsCompleted()
    {
        bool shouldContinue = MediaOverlayTrackNavigationRecoveryPolicy.ShouldContinueSessionDropRecovery(
            DateTimeOffset.UtcNow.AddSeconds(-1),
            attempts: 5,
            minimumAttempts: 5,
            nowUtc: DateTimeOffset.UtcNow);

        Assert.False(shouldContinue);
    }

}

