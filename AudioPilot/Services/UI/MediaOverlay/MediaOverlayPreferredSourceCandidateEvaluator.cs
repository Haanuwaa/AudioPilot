using AudioPilot.Constants;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlayPreferredSourceCandidateEvaluator
    {
        internal static PreferredSourceCandidateResolution ResolveCandidateResolution(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            bool allowSingleCandidateMetadataChangeFallback,
            bool hasRecentSignalForSource,
            BrowserSameSourceLedgerObservation browserPendingObservation = default)
        {
            PreferredSourceCandidateDecision initialDecision = EvaluateCandidateDecision(
                preferredReferenceSnapshot,
                candidate);
            return ResolveCandidateResolution(
                initialDecision,
                preferredReferenceSnapshot,
                candidate,
                allowSingleCandidateMetadataChangeFallback,
                hasRecentSignalForSource,
                browserPendingObservation);
        }

        internal static PreferredSourceCandidateResolution ResolveCandidateResolution(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            bool allowSingleCandidateMetadataChangeFallback,
            bool hasRecentSignalForSource,
            BrowserSameSourceLedgerObservation browserPendingObservation = default)
        {
            PreferredSourceCandidateDecision finalDecision = initialDecision;
            bool browserLikeSameSource = MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate);

            if (initialDecision.IsPending)
            {
                if (browserLikeSameSource)
                {
                    if (browserPendingObservation.FarPositionCorrectionCorroborated
                        && MediaOverlayBrowserSameSourcePolicy.CanFarPositionCandidateConvergeByPositionCorrection(initialDecision))
                    {
                        finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.BrowserConvergenceCorroborated);
                    }
                    else if (browserPendingObservation.PostConflictRecorroborated
                        && MediaOverlayBrowserSameSourcePolicy.CanPendingCandidateConvergeAfterConflict(initialDecision))
                    {
                        finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.BrowserConvergenceCorroborated);
                    }
                    else if (browserPendingObservation.StableObservationCorroborated
                        && (MediaOverlayBrowserSameSourcePolicy.CanPendingCandidateConverge(initialDecision)
                            || ShouldAcceptStableFarPositionBrowserCandidate(
                                initialDecision,
                                preferredReferenceSnapshot,
                                candidate,
                                browserPendingObservation)))
                    {
                        finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.BrowserConvergenceCorroborated);
                    }
                    else if (ShouldAcceptCloseDeltaBrowserConflictCorrectionCandidate(
                        initialDecision,
                        preferredReferenceSnapshot,
                        candidate,
                        browserPendingObservation))
                    {
                        finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.BrowserConvergenceCorroborated);
                    }
                }
                else if (allowSingleCandidateMetadataChangeFallback
                    && ShouldAcceptSingleCandidateMetadataChangeFallback(initialDecision, preferredReferenceSnapshot, candidate))
                {
                    finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.MetadataFallback);
                }
                else if (hasRecentSignalForSource
                    && ShouldAcceptPendingCorroborationCandidateByRecentSignal(initialDecision, preferredReferenceSnapshot, candidate))
                {
                    finalDecision = PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.RecentSignalCorroborated);
                }
            }

            return new PreferredSourceCandidateResolution(initialDecision, finalDecision);
        }

        internal static PreferredSourceCandidateDecision EvaluateCandidateDecision(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            if (preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate))
            {
                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.NoReferenceContext);
            }

            if (string.IsNullOrWhiteSpace(reference.SourceAppUserModelId)
                || !string.Equals(reference.SourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.MissingOrDifferentSource);
            }

            bool browserLikeSameSource = MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(candidate.SourceAppUserModelId);

            if (MediaOverlayEngine.IsSameTrack(reference, candidate))
            {
                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.SameTrack);
            }

            if (candidate.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                if (browserLikeSameSource)
                {
                    return MediaOverlayBrowserSameSourcePolicy.EvaluateBrowserNonPlayingDecision();
                }

                return MediaOverlayBrowserSameSourcePolicy.EvaluateGenericNonPlayingDecision(reference, candidate);
            }

            if (MediaOverlayEngine.HasTimelineTransition(reference, candidate))
            {
                if (browserLikeSameSource)
                {
                    return candidate.PositionSeconds.HasValue
                        && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds
                        ? PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart)
                        : PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.FarPositionDelta);
                }

                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.TimelineTransition);
            }

            if (!MediaOverlayEngine.HasTrackData(candidate))
            {
                return MediaOverlayBrowserSameSourcePolicy.EvaluateTrackDataAvailabilityDecision(browserLikeSameSource);
            }

            if (IsAmbiguousNearStartPlayingTrackChange(reference, candidate))
            {
                return PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart);
            }

            if (candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds)
            {
                if (browserLikeSameSource)
                {
                    return PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.AmbiguousSameSourceNearStart);
                }

                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.PlayingNearStart);
            }

            return PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.FarPositionDelta);
        }

        internal static bool ShouldRejectCandidate(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            return !EvaluateCandidateDecision(preferredReferenceSnapshot, candidate).IsAccepted;
        }

        internal static bool IsPendingCandidate(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            return EvaluateCandidateDecision(preferredReferenceSnapshot, candidate).IsPending;
        }

        internal static bool ShouldAcceptPendingCandidateByRecentSignal(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            PreferredSourceCandidateDecision initialDecision = EvaluateCandidateDecision(preferredReferenceSnapshot, candidate);
            return ShouldAcceptPendingCorroborationCandidateByRecentSignal(initialDecision, preferredReferenceSnapshot, candidate);
        }

        internal static bool ShouldAcceptSingleCandidateMetadataChangeFallback(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            if (preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(reference.SourceAppUserModelId)
                || !string.Equals(reference.SourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(candidate.SourceAppUserModelId))
            {
                return false;
            }

            if (candidate.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return false;
            }

            if (!MediaOverlayEngine.HasTrackData(candidate)
                || MediaOverlayEngine.IsSameTrack(reference, candidate)
                || IsAmbiguousNearStartPlayingTrackChange(reference, candidate))
            {
                return false;
            }

            if (candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds)
            {
                return true;
            }

            if (reference.PositionSeconds.HasValue && candidate.PositionSeconds.HasValue)
            {
                long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
                return delta <= RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds;
            }

            return false;
        }

        internal static string DescribeCandidateDiagnostics(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            int? candidateScore,
            PreferredSourceCandidateDecision? initialDecision = null,
            PreferredSourceCandidateDecision? finalDecision = null,
            BrowserSameSourceLedgerObservation browserPendingObservation = default)
        {
            SessionSnapshot reference = preferredReferenceSnapshot ?? SessionSnapshot.Empty;
            bool hasReference = !MediaOverlayEngine.IsSessionMissing(reference);
            bool sameSource = hasReference
                && string.Equals(reference.SourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
            bool sameTrack = hasReference && MediaOverlayEngine.IsSameTrack(reference, candidate);
            bool timelineTransition = hasReference && MediaOverlayEngine.HasTimelineTransition(reference, candidate);
            bool browserLikeSource = hasReference
                && sameSource
                && MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(candidate.SourceAppUserModelId);
            long? positionDelta = hasReference && reference.PositionSeconds.HasValue && candidate.PositionSeconds.HasValue
                ? Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value)
                : null;
            bool withinStartWindow = candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds;
            PreferredSourceCandidateDecision effectiveInitialDecision = initialDecision ?? finalDecision ?? EvaluateCandidateDecision(preferredReferenceSnapshot, candidate);
            PreferredSourceCandidateDecision effectiveFinalDecision = finalDecision ?? effectiveInitialDecision;
            string blockedBrowserSiblingReason = browserLikeSource && !effectiveFinalDecision.IsAccepted
                ? effectiveFinalDecision.Reason.ToString()
                : "<none>";
            string convergenceEligibilityReason = browserLikeSource
                ? MediaOverlayBrowserSameSourcePolicy.GetPendingConvergenceEligibilityReason(effectiveInitialDecision)
                : "<none>";
            string convergenceSuccessReason = browserLikeSource
                && effectiveFinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                ? effectiveInitialDecision.Reason.ToString()
                : "<none>";
            string conflictObserved = browserPendingObservation.ConflictObserved ? "True" : "False";
            string conflictActive = browserPendingObservation.ConflictActive ? "True" : "False";
            string promotionMode = browserPendingObservation.PromotionKind != BrowserSameSourcePromotionKind.None
                ? browserPendingObservation.PromotionKind.ToString()
                : "<none>";

            return $"diagnostics verdict={effectiveFinalDecision.Verdict} reason={effectiveFinalDecision.Reason} sameSource={sameSource} sameTrack={sameTrack} browserLikeSource={browserLikeSource} hasTrackData={MediaOverlayEngine.HasTrackData(candidate)} timelineTransition={timelineTransition} withinStartWindow={withinStartWindow} playbackStatus={candidate.PlaybackStatus} positionDeltaSec={(positionDelta?.ToString() ?? "<null>")} score={(candidateScore?.ToString() ?? "<null>")} blockedBrowserSiblingReason={blockedBrowserSiblingReason} convergenceEligibilityReason={convergenceEligibilityReason} convergenceSuccessReason={convergenceSuccessReason} sameSourceConflictObserved={conflictObserved} sameSourceConflictActive={conflictActive} distinctSameSourceCandidates={browserPendingObservation.DistinctCandidateCount} activeRivals={browserPendingObservation.ActiveRivalCount} reinforcedRivals={browserPendingObservation.ReinforcedRivalCount} staleRivals={browserPendingObservation.StaleRivalCount} rivalReasonClasses={browserPendingObservation.RivalReasonClasses} staleRivalIgnored={browserPendingObservation.StaleRivalIgnored} promotionMode={promotionMode}";
        }

        internal static int ComputeSnapshotSelectionScore(
            bool isPlaying,
            bool hasTrackData,
            bool hasAlbumTitle,
            long? positionSeconds)
        {
            int score = 0;
            if (isPlaying)
            {
                score += 8;
            }

            if (hasTrackData)
            {
                score += 4;
            }

            if (hasAlbumTitle)
            {
                score += 1;
            }

            if (positionSeconds.HasValue)
            {
                score += 1;
                if (positionSeconds.Value <= 3)
                {
                    score += 1;
                }
            }

            return score;
        }

        internal static int ComputeSnapshotSelectionScore(SessionSnapshot snapshot)
        {
            bool isPlaying = snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool hasTrackData = MediaOverlayEngine.HasTrackData(snapshot);
            bool hasAlbumTitle = !string.IsNullOrWhiteSpace(snapshot.AlbumTitle);
            return ComputeSnapshotSelectionScore(isPlaying, hasTrackData, hasAlbumTitle, snapshot.PositionSeconds);
        }

        internal static int ComputePreferredSourceSnapshotScore(
            SessionSnapshot candidate,
            SessionSnapshot? preferredReferenceSnapshot)
        {
            int score = ComputeSnapshotSelectionScore(candidate);
            if (preferredReferenceSnapshot is not SessionSnapshot reference || MediaOverlayEngine.IsSessionMissing(reference))
            {
                return score;
            }

            if (MediaOverlayEngine.IsSameTrack(reference, candidate))
            {
                score += 16;
            }

            if (MediaOverlayEngine.HasTimelineTransition(reference, candidate))
            {
                score += 10;
            }

            if (reference.PositionSeconds.HasValue && candidate.PositionSeconds.HasValue)
            {
                long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
                if (delta <= 3)
                {
                    score += 8;
                }
                else if (delta <= 8)
                {
                    score += 4;
                }

                if (candidate.PositionSeconds.Value < reference.PositionSeconds.Value)
                {
                    score += 5;
                }
            }

            if (candidate.PlaybackStatus == reference.PlaybackStatus)
            {
                score += 2;
            }

            if (!MediaOverlayEngine.HasTrackData(candidate) && candidate.PositionSeconds.HasValue)
            {
                score += 2;
            }

            return score;
        }

        internal static int ComputeTransientSameSourceFingerprintScore(
            SessionSnapshot candidate,
            SessionSnapshot? preferredReferenceSnapshot)
        {
            if (preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate))
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(reference.SourceAppUserModelId)
                || !string.Equals(reference.SourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int score = 0;
            if (MediaOverlayEngine.IsSameTrack(reference, candidate))
            {
                score += 16;
            }

            if (candidate.PlaybackStatus == reference.PlaybackStatus)
            {
                score += 6;
            }

            if (candidate.PositionSeconds.HasValue && reference.PositionSeconds.HasValue)
            {
                long delta = Math.Abs(candidate.PositionSeconds.Value - reference.PositionSeconds.Value);
                score += Math.Max(0, 8 - (int)Math.Min(delta, 8));

                if (candidate.PositionSeconds.Value < reference.PositionSeconds.Value)
                {
                    score += 2;
                }
            }

            if (candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds)
            {
                score += 2;
            }

            if (!MediaOverlayEngine.HasTrackData(candidate) && candidate.PositionSeconds.HasValue)
            {
                score += 1;
            }

            return score;
        }

        private static bool IsAmbiguousNearStartPlayingTrackChange(SessionSnapshot reference, SessionSnapshot candidate)
        {
            if (candidate.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                || !MediaOverlayEngine.HasTrackData(candidate)
                || MediaOverlayEngine.HasTimelineTransition(reference, candidate)
                || !reference.PositionSeconds.HasValue
                || !candidate.PositionSeconds.HasValue)
            {
                return false;
            }

            long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
            int ambiguousWindow = MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(reference, candidate)
                ? RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds
                : RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds;
            return reference.PositionSeconds.Value <= ambiguousWindow
                && candidate.PositionSeconds.Value <= ambiguousWindow
                && delta <= ambiguousWindow;
        }

        private static bool ShouldAcceptSingleCandidateMetadataChangeFallback(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            if (!initialDecision.IsPending
                || preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate))
            {
                return false;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate))
            {
                return false;
            }

            if (candidate.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                || !MediaOverlayEngine.HasTrackData(candidate)
                || MediaOverlayEngine.IsSameTrack(reference, candidate)
                || IsAmbiguousNearStartPlayingTrackChange(reference, candidate))
            {
                return false;
            }

            if (candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds)
            {
                return true;
            }

            if (reference.PositionSeconds.HasValue && candidate.PositionSeconds.HasValue)
            {
                long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
                return delta <= RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds;
            }

            return false;
        }

        private static bool ShouldAcceptPendingCorroborationCandidateByRecentSignal(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            if (!initialDecision.IsPending
                || preferredReferenceSnapshot is not SessionSnapshot reference
                || !reference.PositionSeconds.HasValue
                || !candidate.PositionSeconds.HasValue)
            {
                return false;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate))
            {
                return false;
            }

            long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
            return delta <= AppConstants.MediaOverlay.PendingEventRecentSignalMaxPositionDeltaSeconds;
        }

        private static bool ShouldAcceptStableFarPositionBrowserCandidate(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            BrowserSameSourceLedgerObservation browserPendingObservation)
        {
            if (initialDecision.Reason != PreferredSourceCandidateReason.FarPositionDelta
                || preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate)
                || !browserPendingObservation.StableObservationCorroborated
                || browserPendingObservation.ConflictObserved
                || browserPendingObservation.ConflictActive
                || browserPendingObservation.DistinctCandidateCount != 1
                || browserPendingObservation.ActiveRivalCount != 0
                || browserPendingObservation.ReinforcedRivalCount != 0
                || browserPendingObservation.StaleRivalCount != 0
                || !reference.PositionSeconds.HasValue
                || !candidate.PositionSeconds.HasValue)
            {
                return false;
            }

            long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
            return delta <= RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds;
        }

        private static bool ShouldAcceptCloseDeltaBrowserConflictCorrectionCandidate(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            BrowserSameSourceLedgerObservation browserPendingObservation)
        {
            if (initialDecision.Reason != PreferredSourceCandidateReason.FarPositionDelta
                || preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate)
                || !browserPendingObservation.ConflictObserved
                || browserPendingObservation.DistinctCandidateCount < 2
                || !CanAcceptCloseDeltaBrowserConflictCorrection(browserPendingObservation)
                || !reference.PositionSeconds.HasValue
                || !candidate.PositionSeconds.HasValue)
            {
                return false;
            }

            if (candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds)
            {
                return false;
            }

            long delta = Math.Abs(reference.PositionSeconds.Value - candidate.PositionSeconds.Value);
            return delta <= RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds;
        }

        private static bool CanAcceptCloseDeltaBrowserConflictCorrection(BrowserSameSourceLedgerObservation browserPendingObservation)
        {
            if (string.IsNullOrWhiteSpace(browserPendingObservation.RivalReasonClasses)
                || string.Equals(browserPendingObservation.RivalReasonClasses, "<none>", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] rivalReasonClasses = browserPendingObservation.RivalReasonClasses
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool containsOnlySupportedCorrectionRivals = rivalReasonClasses.All(reasonClass =>
                string.Equals(reasonClass, "far-position", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reasonClass, "paused-sibling", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reasonClass, "ambiguous-near-start", StringComparison.OrdinalIgnoreCase));
            if (!containsOnlySupportedCorrectionRivals)
            {
                return false;
            }

            bool onlyFarPositionRivals = rivalReasonClasses.All(reasonClass =>
                string.Equals(reasonClass, "far-position", StringComparison.OrdinalIgnoreCase));
            if (onlyFarPositionRivals)
            {
                return browserPendingObservation.ActiveRivalCount > 0
                    || browserPendingObservation.ReinforcedRivalCount > 0
                    || browserPendingObservation.StaleRivalCount > 0;
            }

            return browserPendingObservation.ActiveRivalCount == 0
                && browserPendingObservation.ReinforcedRivalCount == 0
                && browserPendingObservation.StaleRivalCount > 0;
        }
    }
}
