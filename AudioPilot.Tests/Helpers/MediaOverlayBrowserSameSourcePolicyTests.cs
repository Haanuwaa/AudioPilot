using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayBrowserSameSourcePolicyTests
{
    [Fact]
    public void IsBrowserLikeSource_ReturnsTrue_ForKnownBrowserAppIds()
    {
        Assert.True(MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource("Brave"));
        Assert.True(MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource("Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"));
        Assert.True(MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource("msedge"));
        Assert.True(MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource("firefox"));
    }

    [Fact]
    public void ShouldIgnoreRecentSignal_ReturnsTrue_ForPendingBrowserSameSourceCandidate()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Tab",
            "Other Artist",
            null,
            "brave",
            40);
        PreferredSourceCandidateResolution resolution = MediaOverlayPreferredSourceCandidateEvaluator.ResolveCandidateResolution(
            baseline,
            candidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true);

        bool ignored = MediaOverlayBrowserSameSourcePolicy.ShouldIgnoreRecentSignal(
            hasRecentSignalForSource: true,
            baseline,
            candidate,
            resolution);

        Assert.True(ignored);
    }

    [Fact]
    public void CanPendingCandidateConverge_ReturnsTrue_ForAmbiguousNearStart()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanPendingCandidateConverge(
            PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart));

        Assert.True(canConverge);
    }

    [Fact]
    public void CanPendingCandidateConverge_ReturnsTrue_ForPlayingWithoutMetadata()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanPendingCandidateConverge(
            PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.PlayingWithoutMetadata));

        Assert.True(canConverge);
    }

    [Fact]
    public void CanPendingCandidateConverge_ReturnsFalse_ForFarPositionDelta()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanPendingCandidateConverge(
            PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.FarPositionDelta));

        Assert.False(canConverge);
    }

    [Fact]
    public void CanFarPositionCandidateConvergeByPositionCorrection_ReturnsTrue_ForFarPositionDelta()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanFarPositionCandidateConvergeByPositionCorrection(
            PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.FarPositionDelta));

        Assert.True(canConverge);
    }

    [Fact]
    public void GetPendingConvergenceEligibilityReason_ReturnsAmbiguousNearStart_ForRecoverableBrowserPendingReason()
    {
        string reason = MediaOverlayBrowserSameSourcePolicy.GetPendingConvergenceEligibilityReason(
            PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart));

        Assert.Equal("ambiguous-near-start", reason);
    }

    [Fact]
    public void BuildPendingCandidateFingerprint_UsesConfiguredPositionBucketSize()
    {
        MediaOverlaySessionSnapshot first = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist",
            null,
            "brave",
            AppConstants.MediaOverlay.BrowserPendingConvergencePositionBucketSeconds - 1);
        MediaOverlaySessionSnapshot second = first with
        {
            PositionSeconds = AppConstants.MediaOverlay.BrowserPendingConvergencePositionBucketSeconds,
        };

        string firstFingerprint = MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateFingerprint(first);
        string secondFingerprint = MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateFingerprint(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void CanStableObservationCorroborate_ReturnsFalse_ForAmbiguousNearStartOutsideStrictWindow()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanStableObservationCorroborate(
            BrowserPendingCandidateReasonClass.AmbiguousNearStart,
            previousPositionSeconds: 2,
            currentPositionSeconds: 6);

        Assert.False(canConverge);
    }

    [Fact]
    public void CanStableObservationCorroborate_ReturnsTrue_ForAmbiguousNearStartInsideStrictWindow()
    {
        bool canConverge = MediaOverlayBrowserSameSourcePolicy.CanStableObservationCorroborate(
            BrowserPendingCandidateReasonClass.AmbiguousNearStart,
            previousPositionSeconds: 0,
            currentPositionSeconds: 1);

        Assert.True(canConverge);
    }

    [Fact]
    public void BrowserSameSourceConflictLedger_ActivatesConflict_WhenDistinctSameSourceCandidatesAppear()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.PausedSibling,
                "brave|wrong-tab|paused|0",
                "brave|wrong-tab|artist|album",
                0,
                false),
            nowUtc);
        BrowserSameSourceLedgerObservation observation = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(50));

        Assert.True(observation.ConflictActive);
        Assert.Equal(2, observation.DistinctCandidateCount);
        Assert.Contains("paused-sibling", observation.RivalReasonClasses, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserSameSourceConflictLedger_SingleSeenRivalDoesNotBlockImmediatePostConflictRecorroboration()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.PausedSibling,
                "brave|wrong-tab|paused|0",
                "brave|wrong-tab|artist|album",
                0,
                false),
            nowUtc);
        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(50));

        BrowserSameSourceLedgerObservation tooSoonObservation = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(150));
        BrowserSameSourceLedgerObservation quietWindowObservation = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(AppConstants.MediaOverlay.TrackLoadRecoveryRetryDelayMs + 100));

        Assert.True(tooSoonObservation.PostConflictRecorroborated);
        Assert.True(quietWindowObservation.PostConflictRecorroborated);
        Assert.Equal(BrowserSameSourcePromotionKind.PostConflictRecorroboration, quietWindowObservation.PromotionKind);
    }

    [Fact]
    public void BrowserSameSourceConflictLedger_ReinforcedRivalHistoryIsReported_WhenCandidateRecorroborates()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.PausedSibling,
                "brave|wrong-tab|paused|0",
                "brave|wrong-tab|artist|album",
                0,
                false),
            nowUtc);
        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.PausedSibling,
                "brave|wrong-tab|paused|0",
                "brave|wrong-tab|artist|album",
                0,
                false),
            nowUtc.AddMilliseconds(100));

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(AppConstants.MediaOverlay.TrackLoadRecoveryRetryDelayMs + 50));
        BrowserSameSourceLedgerObservation observation = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(AppConstants.MediaOverlay.TrackLoadRecoveryRetryDelayMs + 100));

        Assert.True(observation.PostConflictRecorroborated);
        Assert.Equal(0, observation.ActiveRivalCount);
        Assert.Equal(1, observation.ReinforcedRivalCount);
        Assert.Equal(0, observation.StaleRivalCount);
    }

    [Fact]
    public void BrowserSameSourceConflictLedger_StaleRivalCanBeIgnored_WhenLaterWinnerRecorroborates()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.PausedSibling,
                "brave|wrong-tab|paused|0",
                "brave|wrong-tab|artist|album",
                0,
                false),
            nowUtc);
        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(50));
        BrowserSameSourceLedgerObservation observation = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                "brave|track-b|artist-b|0",
                "brave|track-b|artist-b|album",
                0,
                false),
            nowUtc.AddMilliseconds(AppConstants.MediaOverlay.TrackLoadRecoveryRetryDelayMs + 125));

        Assert.True(observation.PostConflictRecorroborated);
        Assert.True(observation.StaleRivalIgnored);
        Assert.Equal(0, observation.ActiveRivalCount);
        Assert.Equal(0, observation.ReinforcedRivalCount);
        Assert.Equal(1, observation.StaleRivalCount);
    }

    [Fact]
    public void BrowserSameSourceConflictLedger_SingleCandidateSummary_DoesNotReportConflictActivity()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        _ = tracker.Observe(
            "brave",
            1,
            new BrowserSameSourceEvidence(
                BrowserPendingCandidateReasonClass.FarPosition,
                "brave|wrong-tab|38",
                "brave|wrong-tab|artist|album",
                38,
                false),
            nowUtc);

        BrowserSameSourceCommandSummary summary = tracker.GetSummary("brave", 1);

        Assert.False(summary.ConflictObserved);
        Assert.False(summary.ConflictActive);
        Assert.Equal(1, summary.DistinctCandidateCount);
        Assert.Equal(0, summary.ActiveRivalCount);
        Assert.Equal(0, summary.ReinforcedRivalCount);
        Assert.Equal(0, summary.StaleRivalCount);
        Assert.Equal("<none>", summary.RivalReasonClasses);
        Assert.True(summary.HasBlockedRivalEvidence);
        Assert.True(summary.HasPendingCandidateEvidence);
        Assert.False(summary.HasPendingNonWinnerRivalEvidence);
    }
}
