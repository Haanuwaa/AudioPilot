using AudioPilot.Constants;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayBrowserPendingCorroborationStrategy(
        Func<string?, long, BrowserSameSourceCommandSummary> getBrowserSameSourceCommandSummary,
        MediaOverlayTimingProfile timingProfile)
    {
        private readonly Func<string?, long, BrowserSameSourceCommandSummary> _getBrowserSameSourceCommandSummary = getBrowserSameSourceCommandSummary;
        private readonly MediaOverlayTimingProfile _timingProfile = timingProfile;

        public (int InitialDelayMs, int RetryDelayMs) ResolveUnchangedRecoveryCadence(
            bool hasSingleSourceSamplingContext,
            string? preferredSourceAppUserModelId)
        {
            if (!hasSingleSourceSamplingContext
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(preferredSourceAppUserModelId))
            {
                return (_timingProfile.UnchangedRecoveryInitialDelayMs, _timingProfile.UnchangedRecoveryRetryDelayMs);
            }

            return (
                Math.Min(_timingProfile.UnchangedRecoveryInitialDelayMs, AppConstants.MediaOverlay.BrowserSingleSourceUnchangedRecoveryInitialDelayMs),
                Math.Min(_timingProfile.UnchangedRecoveryRetryDelayMs, AppConstants.MediaOverlay.BrowserSingleSourceUnchangedRecoveryRetryDelayMs));
        }

        public static int ResolveRetryDelayMs(int retryDelayMs, bool expediteLatePendingCorroborationRetry)
        {
            return expediteLatePendingCorroborationRetry
                ? Math.Min(retryDelayMs, AppConstants.MediaOverlay.BrowserSingleSourceLatePendingCorroborationRetryDelayMs)
                : retryDelayMs;
        }

        public bool ShouldExpediteLatePendingRetry(
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            long commandSequence)
        {
            string? browserSource = preferredSourceAppUserModelId ?? baseline.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(browserSource)
                || MediaOverlayEngine.IsSessionMissing(baseline)
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(browserSource))
            {
                return false;
            }

            BrowserSameSourceCommandSummary sameSourceCommandSummary = _getBrowserSameSourceCommandSummary(browserSource, commandSequence);
            return sameSourceCommandSummary.DistinctCandidateCount == 1
                && sameSourceCommandSummary.HasPendingCandidateEvidence
                && !sameSourceCommandSummary.HasPendingNonWinnerRivalEvidence
                && !sameSourceCommandSummary.HasBlockedRivalEvidence
                && !sameSourceCommandSummary.ConflictObserved
                && !sameSourceCommandSummary.ConflictActive
                && sameSourceCommandSummary.ActiveRivalCount == 0
                && sameSourceCommandSummary.ReinforcedRivalCount == 0
                && !sameSourceCommandSummary.WinnerElection.HasWinner;
        }

        public bool ShouldAbortConflictedBrowserRecoveryEarly(
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            long commandSequence)
        {
            string? browserSource = preferredSourceAppUserModelId ?? baseline.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(browserSource)
                || MediaOverlayEngine.IsSessionMissing(baseline)
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(browserSource))
            {
                return false;
            }

            BrowserSameSourceCommandSummary sameSourceCommandSummary = _getBrowserSameSourceCommandSummary(browserSource, commandSequence);
            return sameSourceCommandSummary.ConflictObserved
                && sameSourceCommandSummary.DistinctCandidateCount > 1
                && !sameSourceCommandSummary.WinnerElection.HasWinner
                && (sameSourceCommandSummary.ActiveRivalCount > 0
                    || sameSourceCommandSummary.ReinforcedRivalCount > 0
                    || sameSourceCommandSummary.StaleRivalCount > 0)
                && string.Equals(sameSourceCommandSummary.RivalReasonClasses, "far-position", StringComparison.OrdinalIgnoreCase);
        }
    }
}
