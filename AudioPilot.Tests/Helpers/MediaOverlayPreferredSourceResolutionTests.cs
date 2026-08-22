using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPreferredSourceResolutionTests
{
    private static PreferredSourceCandidateResolution ResolveBrowserCandidate(
        MediaOverlayBrowserSameSourceConflictLedger tracker,
        string preferredSourceAppUserModelId,
        MediaOverlaySessionSnapshot? baseline,
        MediaOverlaySessionSnapshot candidate,
        bool allowSingleCandidateMetadataChangeFallback,
        bool hasRecentSignalForSource,
        long commandSequence)
    {
        PreferredSourceCandidateDecision initialDecision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
            baseline,
            candidate);
        bool browserSameSourceCandidate = MediaOverlayBrowserSameSourcePolicy.TryCreateSameSourceCandidateEvidence(
            initialDecision,
            baseline,
            candidate,
            hasRecentSignalForSource,
            out BrowserSameSourceEvidence pendingEvidence);
        BrowserSameSourceLedgerObservation browserPendingObservation = browserSameSourceCandidate
            ? tracker.Observe(preferredSourceAppUserModelId, commandSequence, pendingEvidence)
            : default;

        PreferredSourceCandidateResolution resolution = MediaOverlayPreferredSourceCandidateEvaluator.ResolveCandidateResolution(
            initialDecision,
            baseline,
            candidate,
            allowSingleCandidateMetadataChangeFallback,
            hasRecentSignalForSource,
            browserPendingObservation);

        if (!MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(baseline, candidate)
            || resolution.IsAccepted
            || !browserSameSourceCandidate)
        {
            tracker.Clear(preferredSourceAppUserModelId, commandSequence);
        }

        return resolution;
    }

    [Fact]
    public void ResolveCandidateResolution_DoesNotUpgradeBrowserPendingCandidateByRecentSignal()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
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

        PreferredSourceCandidateResolution resolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            candidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, resolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, resolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_DoesNotPromoteBrowserFarPositionCandidateAfterTwoStableSamples()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Tab",
            "Other Artist",
            null,
            "brave",
            40);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 41 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesBrowserFarPositionCandidate_WhenPositionCorrectsBackwardForSameTrack()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            1);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Out of Touch",
            "Hall & Oates",
            null,
            "brave",
            351);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 0 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_DoesNotPromoteBrowserPendingCandidateAcrossDifferentPositionBuckets()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Tab",
            "Other Artist",
            null,
            "brave",
            40);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 47 };

        _ = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesSingleBrowserFarPositionCandidate_AfterStableRepeatedCloseDeltaSamples()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Tambourine Lasagna",
            "Party In Backyard",
            null,
            "brave",
            336);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Track Alpha",
            "SyntheticChannelAlpha",
            null,
            "brave",
            343);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 344 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_KeepsSingleBrowserNearStartTimelineCandidatePending_WithoutCommittedConvergence()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track",
            "SyntheticBaselineArtist",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 2 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesBrowserPlayingWithoutMetadataAfterTwoStableSamples()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            12);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            18);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 19 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.PlayingWithoutMetadata, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesAmbiguousNearStartBrowserCandidateAfterTwoStableSamples()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 0 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesAmbiguousNearStartBrowserCandidate_WhenSinglePausedSiblingRivalGoesStale()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot pausedSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Wrong Stream",
            "Other Artist",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 0 };

        PreferredSourceCandidateResolution rejectedRivalResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            pausedSibling,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.Reject, rejectedRivalResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesCloseDeltaBrowserCandidate_WhenOnlyFarPositionSiblingRivalExists()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Beta",
            "SyntheticArtistBeta",
            null,
            "brave",
            513);
        MediaOverlaySessionSnapshot farPositionSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            1065);
        MediaOverlaySessionSnapshot correctedCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Candidate Track",
            "SyntheticChannelGamma",
            null,
            "brave",
            521);

        PreferredSourceCandidateResolution rivalResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            farPositionSibling,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution correctedResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            correctedCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, rivalResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, rivalResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, correctedResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, correctedResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesCloseDeltaBrowserCandidate_WhenPausedSiblingRivalGoesStale()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Gamma",
            "SyntheticChannelDelta",
            null,
            "brave",
            600);
        MediaOverlaySessionSnapshot pausedSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Synthetic Paused Stream",
            "SyntheticPausedHost",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Corrected Track",
            "SyntheticChannelEpsilon",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 601 };

        PreferredSourceCandidateResolution pausedSiblingResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            pausedSibling,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.Reject, pausedSiblingResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.FarPositionDelta, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_PromotesCloseDeltaBrowserCandidate_WhenAmbiguousNearStartRivalGoesStale()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Delta",
            "SyntheticChannelZeta",
            null,
            "brave",
            667);
        MediaOverlaySessionSnapshot ambiguousNearStartRival = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Corrected Track Delta",
            "SyntheticChannelEta",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 673 };

        PreferredSourceCandidateResolution rivalResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            ambiguousNearStartRival,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: false,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, rivalResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, rivalResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.Accept, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.BrowserConvergenceCorroborated, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void ResolveCandidateResolution_DoesNotPromoteAmbiguousNearStartBrowserCandidate_WhenCorroboratingSamplesDriftPastStrictWindow()
    {
        MediaOverlayBrowserSameSourceConflictLedger tracker = new();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot firstCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Stream",
            "Other Artist",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot secondCandidate = firstCandidate with { PositionSeconds = 6 };

        PreferredSourceCandidateResolution firstResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            firstCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);
        PreferredSourceCandidateResolution secondResolution = ResolveBrowserCandidate(
            tracker,
            "brave",
            baseline,
            secondCandidate,
            allowSingleCandidateMetadataChangeFallback: true,
            hasRecentSignalForSource: true,
            commandSequence: 1);

        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, firstResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, firstResolution.FinalDecision.Reason);
        Assert.Equal(PreferredSourceCandidateVerdict.PendingCorroboration, secondResolution.FinalDecision.Verdict);
        Assert.Equal(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart, secondResolution.FinalDecision.Reason);
    }

    [Fact]
    public void IsPendingEventCorroborationCandidate_ReturnsTrue_ForSameSourceChangedPlayingTrackWithoutTimelineReset()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            115);

        bool pending = MediaOverlayPreferredSourceCandidateEvaluator.IsPendingCandidate(baseline, candidate);

        Assert.True(pending);
    }

    [Fact]
    public void IsPendingEventCorroborationCandidate_ReturnsFalse_WhenTimelineResetAlreadyCorroboratesChange()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            1);

        bool pending = MediaOverlayPreferredSourceCandidateEvaluator.IsPendingCandidate(baseline, candidate);

        Assert.False(pending);
    }

    [Fact]
    public void ShouldAcceptPendingCorroborationCandidateByRecentSignal_ReturnsTrue_WhenPositionDeltaIsWithinGuardrail()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            126);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptPendingCorroborationCandidateByRecentSignal_ReturnsTrue_AtBoundary()
    {
        int boundarySeconds = AppConstants.MediaOverlay.PendingEventRecentSignalMaxPositionDeltaSeconds;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            84 + boundarySeconds);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptPendingCorroborationCandidateByRecentSignal_ReturnsFalse_WhenPositionDeltaIsTooLarge()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Rap God (Instrumental)",
            "Preexisting Artist A - Topic",
            null,
            "Brave",
            0);
        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "Brave",
            110);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptPendingCandidateByRecentSignal(baseline, candidate);

        Assert.False(accepted);
    }

    [Fact]
    public void ComputeSnapshotSelectionScore_PrefersPlayingWithMetadata()
    {
        int pausedNoData = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(
            isPlaying: false,
            hasTrackData: false,
            hasAlbumTitle: false,
            positionSeconds: null);

        int playingWithData = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(
            isPlaying: true,
            hasTrackData: true,
            hasAlbumTitle: false,
            positionSeconds: 2);

        Assert.True(playingWithData > pausedNoData);
    }

    [Fact]
    public void ComputeSnapshotSelectionScore_RewardsAlbumAndPosition()
    {
        int baseline = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(
            isPlaying: true,
            hasTrackData: true,
            hasAlbumTitle: false,
            positionSeconds: null);

        int richer = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(
            isPlaying: true,
            hasTrackData: true,
            hasAlbumTitle: true,
            positionSeconds: 1);

        Assert.True(richer > baseline);
    }

    [Fact]
    public void ComputePreferredSourceSnapshotScore_PrefersBaselineLikeSession_OverDifferentTab()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "chrome",
            42);

        MediaOverlaySessionSnapshot metadataPendingSameTab = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "chrome",
            1);

        MediaOverlaySessionSnapshot otherPlayingTab = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Completely Different Track",
            "Other Artist",
            null,
            "chrome",
            140);

        int metadataPendingScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputePreferredSourceSnapshotScore(metadataPendingSameTab, baseline);
        int otherTabScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputePreferredSourceSnapshotScore(otherPlayingTab, baseline);

        Assert.True(metadataPendingScore > otherTabScore);
    }

    [Fact]
    public void ComputeTransientSameSourceFingerprintScore_PrefersExactReferencePosition_WhenMetadataIsIdentical()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            100);

        MediaOverlaySessionSnapshot exactMatch = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            100);

        MediaOverlaySessionSnapshot nearbySibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            101);

        int exactScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(exactMatch, baseline);
        int siblingScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(nearbySibling, baseline);

        Assert.True(exactScore > siblingScore);
    }

    [Fact]
    public void ComputeTransientSameSourceFingerprintScore_PrefersEarlierRestartCandidate_WhenMetadataIsIdentical()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            60);

        MediaOverlaySessionSnapshot earlierCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            58);

        MediaOverlaySessionSnapshot laterCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Episode",
            "Host",
            null,
            "chrome",
            62);

        int earlierScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(earlierCandidate, baseline);
        int laterScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(laterCandidate, baseline);

        Assert.True(earlierScore > laterScore);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForPausedSiblingTabWithoutTransition()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);

        MediaOverlaySessionSnapshot pausedSiblingTab = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            18);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, pausedSiblingTab);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForBrowserEarlyPositionCandidateUntilCorroborated()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);

        MediaOverlaySessionSnapshot earlyCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "chrome",
            1);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, earlyCandidate);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForBrowserTimelineTransitionUntilCorroborated()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            52);

        MediaOverlaySessionSnapshot actualNextVideo = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Next Video",
            "Creator",
            null,
            "chrome",
            0);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, actualNextVideo);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForPlayingSiblingTabNearStart_WhenReferenceAlsoNearStart()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            1);

        MediaOverlaySessionSnapshot siblingTab = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            0);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, siblingTab);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForBrowserNearStartChangedTrack_WhenReferenceRemainsWithinBrowserWindow()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            3);

        MediaOverlaySessionSnapshot nextTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Next Video",
            "Next Creator",
            null,
            "chrome",
            0);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, nextTrack);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_SingleVisibleCandidatePausedSibling_IsRejected()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            75);

        MediaOverlaySessionSnapshot onlyVisibleCandidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Paused Other Tab",
            "Other Creator",
            null,
            "chrome",
            24);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, onlyVisibleCandidate);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_ForPausedBrowserNextVideoNearStart()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Current Video",
            "Creator",
            null,
            "chrome",
            108);

        MediaOverlaySessionSnapshot pausedNextVideo = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Next Video",
            "Creator",
            null,
            "chrome",
            8);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, pausedNextVideo);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldRejectPreferredSourceCandidate_ReturnsTrue_AtPausedCandidateStartWindowBoundary()
    {
        int boundarySeconds = RuntimeTuningConfig.MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            173);

        MediaOverlaySessionSnapshot pausedNextVideoAtBoundary = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Next Video",
            "Creator",
            null,
            "chrome",
            boundarySeconds);

        bool rejected = MediaOverlayPreferredSourceCandidateEvaluator.ShouldRejectCandidate(baseline, pausedNextVideoAtBoundary);

        Assert.True(rejected);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsTrue_ForNonBrowserPlayingChangedTrackAfterDrop()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "spotify",
            173);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Loaded Next Video",
            "Next Creator",
            null,
            "spotify",
            173);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsFalse_ForPausedSiblingTab()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            173);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Paused Other Tab",
            "Other Creator",
            null,
            "chrome",
            76);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsFalse_ForFarAwayPlayingSiblingTab()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            173);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Playing Tab",
            "Other Creator",
            null,
            "chrome",
            326);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsTrue_ForNonBrowserNearStartPlayingChangedTrack()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "spotify",
            173);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Loaded Next Video",
            "Next Creator",
            null,
            "spotify",
            8);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsFalse_ForPlayingSiblingTabNearStart_WhenReferenceAlsoNearStart()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "chrome",
            1);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Other Video",
            "Other Creator",
            null,
            "chrome",
            0);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.False(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsTrue_ForNonBrowserPlayingChangedTrackAtStart_WhenReferenceIsPastFirstSecond()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "spotify",
            3);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Loaded Next Video",
            "Next Creator",
            null,
            "spotify",
            0);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void ShouldAcceptSingleCandidateMetadataChangeFallback_ReturnsTrue_ForNonBrowserAtPositionDeltaBoundary()
    {
        int boundarySeconds = RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Current Video",
            "Creator",
            null,
            "spotify",
            173);

        MediaOverlaySessionSnapshot candidate = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Loaded Next Video",
            "Next Creator",
            null,
            "spotify",
            173 + boundarySeconds);

        bool accepted = MediaOverlayPreferredSourceCandidateEvaluator.ShouldAcceptSingleCandidateMetadataChangeFallback(baseline, candidate);

        Assert.True(accepted);
    }

    [Fact]
    public void IsTimelineTransition_ReturnsTrue_ForLargeBackwardJump()
    {
        bool transitioned = MediaOverlayEngine.IsTimelineTransition(
            baselinePositionSeconds: 42,
            latestPositionSeconds: 10,
            jumpThresholdSeconds: 6,
            resetFromSeconds: 4,
            resetToSeconds: 1);

        Assert.True(transitioned);
    }

    [Fact]
    public void IsTimelineTransition_ReturnsTrue_ForSmallRestartPattern()
    {
        bool transitioned = MediaOverlayEngine.IsTimelineTransition(
            baselinePositionSeconds: 5,
            latestPositionSeconds: 1,
            jumpThresholdSeconds: 6,
            resetFromSeconds: 4,
            resetToSeconds: 1);

        Assert.True(transitioned);
    }

    [Fact]
    public void IsTimelineTransition_ReturnsTrue_AtConfiguredResetBoundary()
    {
        bool transitioned = MediaOverlayEngine.IsTimelineTransition(
            baselinePositionSeconds: AppConstants.MediaOverlay.TimelineResetFromSeconds,
            latestPositionSeconds: 0,
            jumpThresholdSeconds: AppConstants.MediaOverlay.TimelineJumpThresholdSeconds,
            resetFromSeconds: AppConstants.MediaOverlay.TimelineResetFromSeconds,
            resetToSeconds: AppConstants.MediaOverlay.TimelineResetToSeconds);

        Assert.True(transitioned);
    }

    [Fact]
    public void IsTimelineTransition_ReturnsFalse_ForMinorBackwardSeek()
    {
        bool transitioned = MediaOverlayEngine.IsTimelineTransition(
            baselinePositionSeconds: 3,
            latestPositionSeconds: 1,
            jumpThresholdSeconds: 6,
            resetFromSeconds: 4,
            resetToSeconds: 1);

        Assert.False(transitioned);
    }

    [Fact]
    public void IsTimelineTransition_ReturnsFalse_ForPausedToPlayingRestartPatternAtTwoSeconds()
    {
        bool transitioned = MediaOverlayEngine.IsTimelineTransition(
            baselinePositionSeconds: 2,
            latestPositionSeconds: 0,
            jumpThresholdSeconds: 6,
            resetFromSeconds: 4,
            resetToSeconds: 1);

        Assert.False(transitioned);
    }

    [Fact]
    public void DescribePreferredSourceCandidateDiagnostics_IncludesSelectionSignals()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "chrome",
            42);
        var candidate = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "chrome",
            1);

        string diagnostics = MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(
            baseline,
            candidate,
            candidateScore: 12);

        Assert.Contains("sameSource=True", diagnostics, StringComparison.Ordinal);
        Assert.Contains("withinStartWindow=True", diagnostics, StringComparison.Ordinal);
        Assert.Contains("score=12", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribePreferredSourceCandidateDiagnostics_IncludesConvergenceSuccessReason_ForBrowserConvergence()
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
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        PreferredSourceCandidateDecision initialDecision = PreferredSourceCandidateDecision.Pending(
            PreferredSourceCandidateReason.AmbiguousSameSourceNearStart);
        PreferredSourceCandidateDecision finalDecision = PreferredSourceCandidateDecision.Accept(
            PreferredSourceCandidateReason.BrowserConvergenceCorroborated);

        string diagnostics = MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(
            baseline,
            candidate,
            candidateScore: 12,
            initialDecision: initialDecision,
            finalDecision: finalDecision);

        Assert.Contains("convergenceEligibilityReason=ambiguous-near-start", diagnostics, StringComparison.Ordinal);
        Assert.Contains("convergenceSuccessReason=AmbiguousSameSourceNearStart", diagnostics, StringComparison.Ordinal);
    }
}
