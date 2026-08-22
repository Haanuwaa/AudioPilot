using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayBrowserConflictPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void EvaluatePreferredSourceCandidateDecision_KeepsAmbiguousNearStartBrowserSwapPending(
        long baselinePosition,
        long candidatePosition)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            baselinePosition);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            candidatePosition);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, decision.Reason);
        Assert.True(MediaOverlayPreferredSourceCandidateEvaluator.IsPendingCandidate(baseline, candidate));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(10, 5)]
    public void EvaluatePreferredSourceCandidateDecision_KeepsBrowserNearStartSwapPending_WithinBrowserWindow(
        long baselinePosition,
        long candidatePosition)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            baselinePosition);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            candidatePosition);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, decision.Reason);
        Assert.True(MediaOverlayPreferredSourceCandidateEvaluator.IsPendingCandidate(baseline, candidate));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    public void EvaluatePreferredSourceCandidateDecision_RejectsPausedSibling_WhenBaselineIsPlaying(
        long baselinePosition,
        long candidatePosition)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            baselinePosition);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track B",
            "Artist B",
            null,
            "brave",
            candidatePosition);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.Reject, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PausedSibling, decision.Reason);
        Assert.True(MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, candidate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EvaluatePreferredSourceCandidateDecision_RejectsPausedNearStartBrowserSibling_WhenReferenceIsAlsoPaused(
        long candidatePosition)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track B",
            "Artist B",
            null,
            "brave",
            candidatePosition);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.Reject, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PausedSibling, decision.Reason);
    }

    [Theory]
    [InlineData(52, 40)]
    [InlineData(357, 59)]
    public void EvaluatePreferredSourceCandidateDecision_KeepsBrowserFarPositionTimelineTransitionPending(
        long baselinePosition,
        long candidatePosition)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            baselinePosition);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            candidatePosition);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, decision.Reason);
    }

    [Theory]
    [InlineData(84, 126, true)]
    [InlineData(0, 110, false)]
    public void RecentSignalUpgrade_OnlyPromotesPendingCandidates_WithinGuardrail(
        long baselinePosition,
        long candidatePosition,
        bool shouldUpgrade)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            baselinePosition);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            candidatePosition);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(
            baseline,
            candidate);

        Assert.Equal(shouldUpgrade, accepted);
    }

    [Fact]
    public void RecentSignalUpgrade_DoesNotPromoteBrowserFarPositionDeltaCandidate()
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

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(
            baseline,
            candidate);

        Assert.False(accepted);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_KeepsBrowserPlayingWithoutMetadataPending()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            12);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            18);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PlayingWithoutMetadata, decision.Reason);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_AllowsPausedNearStartForNonBrowser_WhenReferenceIsPaused()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track A",
            "Artist A",
            null,
            "spotify",
            0);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track B",
            "Artist B",
            null,
            "spotify",
            1);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.Accept, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PausedNearStartWhileReferencePaused, decision.Reason);
    }

    [Fact]
    public void RecentSignalUpgrade_DoesNotOverrideDirectReject_ForPausedSibling()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            10);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track B",
            "Artist B",
            null,
            "spotify",
            0);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(
            baseline,
            candidate);

        Assert.False(accepted);
        Assert.True(MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, candidate));
    }
}
