using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPreferredSourceDecisionTests
{
    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_ReturnsReject_ForPausedSiblingWhileReferenceIsPlaying()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            18);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.Reject, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PausedSibling, decision.Reason);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_ReturnsPending_ForAmbiguousNearStartPlayingTrackChange()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            1);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            0);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, decision.Reason);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_ReturnsPending_ForFarPlayingTrackChangeWithoutTransition()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            173);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            326);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, decision.Reason);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_ReturnsPending_ForBrowserFarPositionTimelineTransition()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Next Video",
            "Creator",
            null,
            "chrome",
            40);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, decision.Reason);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_ReturnsPending_ForBrowserNearStartTimelineTransition()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Next Video",
            "Creator",
            null,
            "chrome",
            0);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, decision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, decision.Reason);
    }

    [Fact]
    public void DescribePreferredSourceCandidateDiagnostics_IncludesVerdictAndReason()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            1);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            0);

        string diagnostics = MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(
            baseline,
            candidate,
            candidateScore: null);

        Assert.Contains("verdict=PendingCorroboration", diagnostics, StringComparison.Ordinal);
        Assert.Contains("reason=AmbiguousSameSourceNearStart", diagnostics, StringComparison.Ordinal);
        Assert.Contains("browserLikeSource=True", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribePreferredSourceCandidateDiagnostics_IncludesBlockedBrowserReason_ForFarPositionSibling()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Tab",
            "Other Artist",
            null,
            "brave",
            60);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(baseline, candidate);
        string diagnostics = MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(
            baseline,
            candidate,
            candidateScore: null,
            initialDecision: decision,
            finalDecision: decision);

        Assert.Contains("blockedBrowserSiblingReason=FarPositionDelta", diagnostics, StringComparison.Ordinal);
        Assert.Contains("convergenceEligibilityReason=far-position", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluatePreferredSourceCandidateDecision_UsesAmbiguousNearStartWindowBoundary()
    {
        int boundary = RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds;
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "chrome",
            boundary);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "chrome",
            boundary - 1);

        PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);

        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, decision.Reason);
    }
}
