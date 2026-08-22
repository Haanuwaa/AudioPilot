using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayAlternateEvidenceTests
{
    [Fact]
    public void ShouldIgnoreAlternateCandidateFromPreferredSource_ReturnsTrue_ForSiblingBrowserTab()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "chrome",
            42);

        MediaOverlaySessionSnapshot siblingTab = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "chrome",
            10);

        bool ignored = MediaOverlayEngine.ShouldIgnoreAlternateCandidateFromPreferredSource(
            siblingTab,
            baseline,
            preferredSourceForCommand: "chrome");

        Assert.True(ignored);
    }

    [Fact]
    public void ShouldIgnoreAlternateCandidateFromPreferredSource_ReturnsFalse_ForDifferentSource()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "chrome",
            42);

        MediaOverlaySessionSnapshot spotifyCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            1);

        bool ignored = MediaOverlayEngine.ShouldIgnoreAlternateCandidateFromPreferredSource(
            spotifyCandidate,
            baseline,
            preferredSourceForCommand: "chrome");

        Assert.False(ignored);
    }

    [Fact]
    public void ComputeAlternateEvidenceScore_Increases_WithTransitionSignals()
    {
        int weak = MediaOverlayEngine.ComputeAlternateEvidenceScore(
            changedVsPreferred: false,
            changedVsBaseline: false,
            changedVsPre: true,
            sourceDiffersFromBaseline: false,
            sourceMatchesPreferred: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            postPositionSeconds: null);

        int strong = MediaOverlayEngine.ComputeAlternateEvidenceScore(
            changedVsPreferred: true,
            changedVsBaseline: true,
            changedVsPre: true,
            sourceDiffersFromBaseline: true,
            sourceMatchesPreferred: true,
            timelineTransitionObserved: true,
            positionMovedBackwardFromPre: true,
            postPositionSeconds: 2);

        Assert.True(strong > weak);
    }

    [Fact]
    public void ComputeAlternateEvidenceScore_RewardsEarlyTrackPosition()
    {
        int withoutEarlyPosition = MediaOverlayEngine.ComputeAlternateEvidenceScore(
            changedVsPreferred: true,
            changedVsBaseline: true,
            changedVsPre: false,
            sourceDiffersFromBaseline: false,
            sourceMatchesPreferred: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            postPositionSeconds: 15);

        int withEarlyPosition = MediaOverlayEngine.ComputeAlternateEvidenceScore(
            changedVsPreferred: true,
            changedVsBaseline: true,
            changedVsPre: false,
            sourceDiffersFromBaseline: false,
            sourceMatchesPreferred: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            postPositionSeconds: 1);

        Assert.True(withEarlyPosition > withoutEarlyPosition);
    }

    [Fact]
    public void DescribeAlternateCandidateDiagnostics_IncludesEvidenceFlags()
    {
        string diagnostics = MediaOverlayEngine.DescribeAlternateCandidateDiagnostics(
            changedVsPreferred: true,
            changedVsBaseline: false,
            changedVsPre: true,
            sourceDiffersFromBaseline: true,
            sourceMatchesPreferred: false,
            timelineTransitionObserved: true,
            positionMovedBackwardFromPre: false,
            postPositionSeconds: 2,
            evidenceScore: 7,
            qualityScore: 13);

        Assert.Contains("evidenceScore=7", diagnostics, StringComparison.Ordinal);
        Assert.Contains("qualityScore=13", diagnostics, StringComparison.Ordinal);
        Assert.Contains("changedVsPreferred=True", diagnostics, StringComparison.Ordinal);
        Assert.Contains("nearStart=True", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void IsStrongAlternateEvidence_RequiresHighThreshold()
    {
        Assert.False(MediaOverlayEngine.IsStrongAlternateEvidence(
            evidenceScore: 4,
            nearStart: true,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false));
        Assert.False(MediaOverlayEngine.IsStrongAlternateEvidence(
            evidenceScore: 6,
            nearStart: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false));
        Assert.True(MediaOverlayEngine.IsStrongAlternateEvidence(
            evidenceScore: 5,
            nearStart: true,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false));
    }

    [Fact]
    public void ShouldAdoptModerateAlternateEvidence_ReturnsFalse_WhenBelowModerateThreshold()
    {
        bool adopt = MediaOverlayEngine.ShouldAdoptModerateAlternateEvidence(
            evidenceScore: 3,
            nearStart: true,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            baselineNotActivelyPlaying: true,
            preferredHasTimelineTransition: true,
            forceAlternateAfterStreak: true);

        Assert.False(adopt);
    }

    [Fact]
    public void ShouldAdoptModerateAlternateEvidence_ReturnsTrue_WhenModerateAndForcedBySignals()
    {
        bool adopt = MediaOverlayEngine.ShouldAdoptModerateAlternateEvidence(
            evidenceScore: 4,
            nearStart: true,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: true);

        Assert.True(adopt);
    }

    [Fact]
    public void ShouldAdoptModerateAlternateEvidence_ReturnsFalse_WhenNoTransitionSignalExists()
    {
        bool adopt = MediaOverlayEngine.ShouldAdoptModerateAlternateEvidence(
            evidenceScore: 4,
            nearStart: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false,
            baselineNotActivelyPlaying: true,
            preferredHasTimelineTransition: true,
            forceAlternateAfterStreak: true);

        Assert.False(adopt);
    }

    [Fact]
    public void HasSignalCorroborationForPreexistingCrossSourceAlternate_RequiresRecentSignalAndTransitionShape()
    {
        Assert.False(MediaOverlayEngine.HasSignalCorroborationForPreexistingCrossSourceAlternate(
            hasRecentSignalForSource: false,
            nearStart: true,
            timelineTransitionObserved: true,
            positionMovedBackwardFromPre: true));
        Assert.False(MediaOverlayEngine.HasSignalCorroborationForPreexistingCrossSourceAlternate(
            hasRecentSignalForSource: true,
            nearStart: false,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false));
        Assert.True(MediaOverlayEngine.HasSignalCorroborationForPreexistingCrossSourceAlternate(
            hasRecentSignalForSource: true,
            nearStart: true,
            timelineTransitionObserved: false,
            positionMovedBackwardFromPre: false));
    }
}
