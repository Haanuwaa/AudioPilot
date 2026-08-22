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
    public void TrySelectBestAlternateCandidateForAdoption_PicksHighestEvidenceThenQuality()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 4, QualityScore: 15, NearStart: true, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 6, QualityScore: 10, NearStart: false, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: true, HasRecentSignalForSource: false),
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 5, QualityScore: 20, NearStart: true, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.True(adopted);
        Assert.Equal(2, selectedIndex);
        Assert.Equal(5, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_RejectsLowConfidenceAlternates()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 3, QualityScore: 20, NearStart: true, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 2, QualityScore: 30, NearStart: true, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.False(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(3, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_AcceptsModerateWhenBaselineNotPlaying()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 4, QualityScore: 8, NearStart: true, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 4, QualityScore: 7, NearStart: false, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: false, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: true,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.True(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(4, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_RejectsFarPlayingAlternateWithoutTransitionSignal()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 6, QualityScore: 13, NearStart: false, TimelineTransitionObserved: false, PositionMovedBackwardFromPre: false, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: true, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: true,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.False(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(6, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_RejectsPreexistingCrossSourceAlternate_WhenBaselineStillPlaying()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 11, QualityScore: 15, NearStart: true, TimelineTransitionObserved: true, PositionMovedBackwardFromPre: true, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: true, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.False(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(11, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_StillRejectsPreexistingCrossSourceAlternate_WhenForcedAfterStreak()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 9, QualityScore: 15, NearStart: true, TimelineTransitionObserved: true, PositionMovedBackwardFromPre: true, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: true, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: true,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.False(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(9, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_AcceptsNewCrossSourceAlternate_WhenStrongTransitionExists()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 11, QualityScore: 15, NearStart: true, TimelineTransitionObserved: true, PositionMovedBackwardFromPre: true, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: false, HasRecentSignalForSource: false),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.True(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(11, selectedEvidenceScore);
    }

    [Fact]
    public void TrySelectBestAlternateCandidateForAdoption_AcceptsPreexistingCrossSourceAlternate_WhenExactSourceRecentlySignaled()
    {
        List<MediaOverlayAlternateCandidateScore> candidates =
        [
            new MediaOverlayAlternateCandidateScore(EvidenceScore: 11, QualityScore: 15, NearStart: true, TimelineTransitionObserved: true, PositionMovedBackwardFromPre: true, SourceDiffersFromBaseline: true, SourceWasPresentPreCommand: true, HasRecentSignalForSource: true),
        ];

        bool adopted = MediaOverlayEngine.TrySelectBestAlternateCandidateForAdoption(
            candidates,
            baselineNotActivelyPlaying: false,
            preferredHasTimelineTransition: false,
            forceAlternateAfterStreak: false,
            out int selectedIndex,
            out int selectedEvidenceScore);

        Assert.True(adopted);
        Assert.Equal(0, selectedIndex);
        Assert.Equal(11, selectedEvidenceScore);
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
