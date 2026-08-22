using AudioPilot.Constants;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlayTrackNavigationRecoveryPolicy
    {
        internal static bool IsUsableTrackNavigationCandidate(SessionSnapshot baseline, SessionSnapshot latest)
        {
            if (MediaOverlayEngine.IsSessionMissing(latest)
                || MediaOverlayEngine.IsSameTrack(baseline, latest))
            {
                return false;
            }

            if (baseline.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && latest.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return false;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(baseline, latest))
            {
                PreferredSourceCandidateDecision decision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
                    baseline,
                    latest);
                return decision.Verdict switch
                {
                    PreferredSourceCandidateVerdict.Reject => false,
                    PreferredSourceCandidateVerdict.PendingCorroboration => MediaOverlayBrowserSameSourcePolicy.IsRecoverablePendingReason(decision.Reason),
                    _ => true,
                };
            }

            if (!MediaOverlayEngine.HasTrackData(latest))
            {
                return false;
            }

            return true;
        }

        internal static bool ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop)
        {
            return sawSessionDrop
                && MediaOverlayEngine.HasTrackData(baseline)
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && (!MediaOverlayEngine.HasTrackData(fallback)
                    || IsSameTrackAtStartAfterSessionDrop(baseline, fallback, sawSessionDrop));
        }

        internal static bool IsSameTrackAtStartAfterSessionDrop(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop)
        {
            if (!sawSessionDrop
                || MediaOverlayEngine.IsSessionMissing(baseline)
                || MediaOverlayEngine.IsSessionMissing(fallback)
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayEngine.HasTrackData(fallback)
                || !MediaOverlayEngine.IsSameTrack(baseline, fallback))
            {
                return false;
            }

            return baseline.PositionSeconds.HasValue
                && fallback.PositionSeconds.HasValue
                && baseline.PositionSeconds.Value <= AppConstants.MediaOverlay.PostDropSameTrackNearStartWindowSeconds
                && fallback.PositionSeconds.Value <= AppConstants.MediaOverlay.PostDropSameTrackNearStartWindowSeconds;
        }

        internal static bool ShouldTreatBrowserSessionDropAsPending(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop,
            bool hasRecentSignalForSource,
            BrowserSameSourceCommandSummary sameSourceCommandSummary = default)
        {
            if (!sawSessionDrop
                || MediaOverlayEngine.IsSessionMissing(baseline)
                || MediaOverlayEngine.IsSessionMissing(fallback)
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayEngine.HasTrackData(fallback)
                || !MediaOverlayEngine.IsSameTrack(baseline, fallback)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(baseline.SourceAppUserModelId))
            {
                return false;
            }

            return HasActivePendingTrackChangeEvidence(sameSourceCommandSummary)
                || (sameSourceCommandSummary.HasPendingCandidateEvidence
                    && hasRecentSignalForSource
                    && baseline.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && fallback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }

        internal static TrackNavigationRecoveryDisposition ClassifyFinalRecoveryDisposition(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop,
            bool hasRecentSignalForSource,
            BrowserSameSourceCommandSummary sameSourceCommandSummary = default)
        {
            bool sessionDropStillMissing = sawSessionDrop && MediaOverlayEngine.IsSessionMissing(fallback);
            if (sessionDropStillMissing)
            {
                if (sameSourceCommandSummary.HasBlockedRivalEvidence
                    || sameSourceCommandSummary.HasPendingCandidateEvidence
                    || sameSourceCommandSummary.ActiveRivalCount > 0
                    || sameSourceCommandSummary.ReinforcedRivalCount > 0)
                {
                    return TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Loading);
                }

                return TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Missing);
            }

            bool metadataPendingAfterSessionDrop = sawSessionDrop
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && !MediaOverlayEngine.HasTrackData(fallback);
            if (metadataPendingAfterSessionDrop)
            {
                return TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.MetadataPending);
            }

            bool pendingTrackChangeAfterSessionDrop = sawSessionDrop
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && !metadataPendingAfterSessionDrop
                && ((MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(baseline.SourceAppUserModelId)
                        && IsSameTrackAtStartAfterSessionDrop(baseline, fallback, sawSessionDrop))
                    || ShouldTreatBrowserSessionDropAsPending(
                        baseline,
                        fallback,
                        sawSessionDrop,
                        hasRecentSignalForSource,
                        sameSourceCommandSummary));
            if (pendingTrackChangeAfterSessionDrop)
            {
                return TrackNavigationRecoveryDisposition.Loading(TrackNavigationFallbackClassification.Loading);
            }

            if (sameSourceCommandSummary.ConflictObserved
                && sameSourceCommandSummary.StaleRivalCount > 0
                && sameSourceCommandSummary.ActiveRivalCount == 0
                && sameSourceCommandSummary.ReinforcedRivalCount == 0)
            {
                return TrackNavigationRecoveryDisposition.Unchanged;
            }

            return TrackNavigationRecoveryDisposition.Unchanged;
        }

        private static bool HasActivePendingTrackChangeEvidence(BrowserSameSourceCommandSummary sameSourceCommandSummary)
        {
            return sameSourceCommandSummary.ActiveRivalCount > 0
                || sameSourceCommandSummary.ReinforcedRivalCount > 0
                || sameSourceCommandSummary.HasBlockedRivalEvidence;
        }

        internal static string DescribeRecoveryOutcome(TrackNavigationRecoveryOutcome outcome)
        {
            return outcome switch
            {
                TrackNavigationRecoveryOutcome.Changed => "changed",
                TrackNavigationRecoveryOutcome.Loading => "loading",
                _ => "unchanged",
            };
        }

        internal static string DescribeFallbackClassification(TrackNavigationFallbackClassification fallbackClassification)
        {
            return fallbackClassification switch
            {
                TrackNavigationFallbackClassification.Confirmed => "changed",
                TrackNavigationFallbackClassification.Missing => "hidden",
                TrackNavigationFallbackClassification.MetadataPending => "metadata-pending",
                TrackNavigationFallbackClassification.Loading => "loading",
                _ => "unchanged",
            };
        }

        internal static bool ShouldContinueSessionDropRecovery(
            DateTimeOffset deadlineUtc,
            int attempts,
            int minimumAttempts,
            DateTimeOffset nowUtc)
        {
            return attempts < minimumAttempts || nowUtc < deadlineUtc;
        }
    }
}
