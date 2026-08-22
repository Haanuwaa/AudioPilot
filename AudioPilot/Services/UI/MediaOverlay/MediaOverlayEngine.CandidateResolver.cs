using AudioPilot.Logging;
using Windows.Media.Control;
using AlternateCandidateScore = AudioPilot.Services.UI.MediaOverlay.MediaOverlayAlternateCandidateScore;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        internal static int ComputeAlternateEvidenceScore(
            bool changedVsPreferred,
            bool changedVsBaseline,
            bool changedVsPre,
            bool sourceDiffersFromBaseline,
            bool sourceMatchesPreferred,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre,
            long? postPositionSeconds)
        {
            int evidenceScore = 0;
            if (changedVsPreferred)
            {
                evidenceScore += 3;
            }

            if (changedVsBaseline)
            {
                evidenceScore += 2;
            }

            if (changedVsPre)
            {
                evidenceScore += 2;
            }

            if (sourceDiffersFromBaseline)
            {
                evidenceScore += 1;
            }

            if (timelineTransitionObserved)
            {
                evidenceScore += 1;
            }

            if (positionMovedBackwardFromPre)
            {
                evidenceScore += 1;
            }

            if (sourceMatchesPreferred)
            {
                evidenceScore += 1;
            }

            if (postPositionSeconds.HasValue && postPositionSeconds.Value <= 3)
            {
                evidenceScore += 1;
            }

            return evidenceScore;
        }

        internal static bool HasStrongAlternateTransitionSignal(
            bool nearStart,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre)
        {
            return nearStart || timelineTransitionObserved || positionMovedBackwardFromPre;
        }

        internal static bool RequiresExistingCrossSourceCorroboration(
            bool sourceDiffersFromBaseline,
            bool sourceWasPresentPreCommand)
        {
            return sourceDiffersFromBaseline && sourceWasPresentPreCommand;
        }

        internal static bool HasSignalCorroborationForPreexistingCrossSourceAlternate(
            bool hasRecentSignalForSource,
            bool nearStart,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre)
        {
            return hasRecentSignalForSource
                && HasStrongAlternateTransitionSignal(
                    nearStart,
                    timelineTransitionObserved,
                    positionMovedBackwardFromPre);
        }

        internal static bool IsStrongAlternateEvidence(
            int evidenceScore,
            bool nearStart,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre)
        {
            return evidenceScore >= 5
                && HasStrongAlternateTransitionSignal(
                    nearStart,
                    timelineTransitionObserved,
                    positionMovedBackwardFromPre);
        }

        internal static bool ShouldAdoptModerateAlternateEvidence(
            int evidenceScore,
            bool nearStart,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre,
            bool baselineNotActivelyPlaying,
            bool preferredHasTimelineTransition,
            bool forceAlternateAfterStreak)
        {
            if (evidenceScore < 4)
            {
                return false;
            }

            if (!HasStrongAlternateTransitionSignal(
                    nearStart,
                    timelineTransitionObserved,
                    positionMovedBackwardFromPre))
            {
                return false;
            }

            return baselineNotActivelyPlaying
                || preferredHasTimelineTransition
                || forceAlternateAfterStreak;
        }

        internal static bool TrySelectBestAlternateCandidateForAdoption(
            IReadOnlyList<AlternateCandidateScore> candidates,
            bool baselineNotActivelyPlaying,
            bool preferredHasTimelineTransition,
            bool forceAlternateAfterStreak,
            out int selectedIndex,
            out int selectedEvidenceScore)
        {
            selectedIndex = -1;
            selectedEvidenceScore = int.MinValue;
            int bestQualityScore = int.MinValue;
            int bestAdoptableIndex = -1;
            int bestAdoptableEvidenceScore = int.MinValue;
            int bestAdoptableQualityScore = int.MinValue;

            for (int index = 0; index < candidates.Count; index++)
            {
                AlternateCandidateScore candidate = candidates[index];
                if (candidate.EvidenceScore > selectedEvidenceScore
                    || (candidate.EvidenceScore == selectedEvidenceScore && candidate.QualityScore > bestQualityScore))
                {
                    selectedEvidenceScore = candidate.EvidenceScore;
                    bestQualityScore = candidate.QualityScore;
                    selectedIndex = index;
                }

                bool adoptable = IsStrongAlternateEvidence(
                        candidate.EvidenceScore,
                        candidate.NearStart,
                        candidate.TimelineTransitionObserved,
                        candidate.PositionMovedBackwardFromPre)
                    || ShouldAdoptModerateAlternateEvidence(
                        candidate.EvidenceScore,
                        candidate.NearStart,
                        candidate.TimelineTransitionObserved,
                        candidate.PositionMovedBackwardFromPre,
                        baselineNotActivelyPlaying,
                        preferredHasTimelineTransition,
                        forceAlternateAfterStreak);
                if (adoptable
                    && RequiresExistingCrossSourceCorroboration(
                        candidate.SourceDiffersFromBaseline,
                        candidate.SourceWasPresentPreCommand)
                    && !baselineNotActivelyPlaying
                    && !preferredHasTimelineTransition
                    && !HasSignalCorroborationForPreexistingCrossSourceAlternate(
                        candidate.HasRecentSignalForSource,
                        candidate.NearStart,
                        candidate.TimelineTransitionObserved,
                        candidate.PositionMovedBackwardFromPre))
                {
                    adoptable = false;
                }
                if (!adoptable)
                {
                    continue;
                }

                if (candidate.EvidenceScore > bestAdoptableEvidenceScore
                    || (candidate.EvidenceScore == bestAdoptableEvidenceScore && candidate.QualityScore > bestAdoptableQualityScore))
                {
                    bestAdoptableEvidenceScore = candidate.EvidenceScore;
                    bestAdoptableQualityScore = candidate.QualityScore;
                    bestAdoptableIndex = index;
                }
            }

            if (selectedIndex < 0)
            {
                return false;
            }

            if (bestAdoptableIndex < 0)
            {
                return false;
            }

            selectedIndex = bestAdoptableIndex;
            selectedEvidenceScore = bestAdoptableEvidenceScore;
            return true;
        }

        internal static bool ShouldIgnoreAlternateCandidateFromPreferredSource(
            SessionSnapshot candidate,
            SessionSnapshot baseline,
            string? preferredSourceForCommand)
        {
            string? candidateSource = candidate.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(candidateSource))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(preferredSourceForCommand)
                && string.Equals(candidateSource, preferredSourceForCommand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(baseline.SourceAppUserModelId)
                && string.Equals(candidateSource, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<SessionSnapshot> TryFindChangedAlternateSnapshotAsync(
            SessionSnapshot baseline,
            SessionSnapshot preferredFallback,
            Dictionary<string, SessionSnapshot> preCommandSnapshots,
            bool forceAlternateAfterStreak,
            string? preferredSourceForCommand,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            List<SessionSnapshot> postCommandSnapshots = await TryGetSessionSnapshotsAsync(commandSequence, cancellationToken);
            SessionSnapshot selected = SessionSnapshot.Empty;
            AlternateCandidateScore selectedScore = default;
            int selectedEvidenceScore = int.MinValue;
            int bestQualityScore = int.MinValue;
            int bestAdoptableEvidenceScore = int.MinValue;
            int bestAdoptableQualityScore = int.MinValue;
            int candidateCount = 0;
            bool selectedSet = false;

            bool baselineNotActivelyPlaying = baseline.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool preferredHasTimelineTransition = HasTimelineTransition(baseline, preferredFallback);
            foreach (SessionSnapshot post in postCommandSnapshots)
            {
                if (IsSessionMissing(post) || !HasTrackData(post))
                {
                    continue;
                }

                if (post.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    continue;
                }

                if (ShouldIgnoreAlternateCandidateFromPreferredSource(post, baseline, preferredSourceForCommand))
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        $"Ignoring alternate candidate from preferred-source family source={LogPrivacy.Id(post.SourceAppUserModelId)} preferredSource={LogPrivacy.Id(preferredSourceForCommand)} baselineSource={LogPrivacy.Id(baseline.SourceAppUserModelId)} snapshot={FormatSnapshot(post)}",
                        nameof(TryFindChangedAlternateSnapshotAsync));
                    continue;
                }

                string? source = post.SourceAppUserModelId;
                SessionSnapshot pre = SessionSnapshot.Empty;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    preCommandSnapshots.TryGetValue(source, out pre);
                }

                bool hasPrior = !IsSessionMissing(pre);

                bool changedVsPre = !hasPrior
                    || !IsSameTrack(pre, post)
                    || HasTimelineTransition(pre, post)
                    || pre.PlaybackStatus != post.PlaybackStatus;
                bool changedVsPreferred = !IsSameTrack(preferredFallback, post)
                    || HasTimelineTransition(preferredFallback, post)
                    || preferredFallback.PlaybackStatus != post.PlaybackStatus;
                bool changedVsBaseline = !IsSameTrack(baseline, post)
                    || HasTimelineTransition(baseline, post)
                    || baseline.PlaybackStatus != post.PlaybackStatus;
                bool sourceDiffersFromBaseline = !string.IsNullOrWhiteSpace(source)
                    && !string.Equals(source, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
                bool sourceMatchesPreferred = !string.IsNullOrWhiteSpace(source)
                    && !string.IsNullOrWhiteSpace(preferredSourceForCommand)
                    && string.Equals(source, preferredSourceForCommand, StringComparison.OrdinalIgnoreCase);
                bool timelineTransitionFromBaseline = HasTimelineTransition(baseline, post);
                bool timelineTransitionFromPre = HasTimelineTransition(pre, post);

                if (!changedVsPre && !changedVsPreferred && !changedVsBaseline)
                {
                    continue;
                }

                bool positionMovedBackwardFromPre = hasPrior
                    && pre.PositionSeconds.HasValue
                    && post.PositionSeconds.HasValue
                    && post.PositionSeconds.Value < pre.PositionSeconds.Value;
                bool hasRecentSignalForSource = HasRecentSignalForSource(source);

                int evidenceScore = ComputeAlternateEvidenceScore(
                    changedVsPreferred,
                    changedVsBaseline,
                    changedVsPre,
                    sourceDiffersFromBaseline,
                    sourceMatchesPreferred,
                    timelineTransitionObserved: timelineTransitionFromBaseline || timelineTransitionFromPre,
                    positionMovedBackwardFromPre,
                    post.PositionSeconds);

                int qualityScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(post);
                bool nearStart = post.PositionSeconds.HasValue && post.PositionSeconds.Value <= 3;
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"Evaluated alternate candidate source={LogPrivacy.Id(source)} {DescribeAlternateCandidateDiagnostics(changedVsPreferred, changedVsBaseline, changedVsPre, sourceDiffersFromBaseline, sourceMatchesPreferred, timelineTransitionFromBaseline || timelineTransitionFromPre, positionMovedBackwardFromPre, post.PositionSeconds, evidenceScore, qualityScore)} snapshot={FormatSnapshot(post)}",
                    nameof(TryFindChangedAlternateSnapshotAsync));
                candidateCount++;
                AlternateCandidateScore candidateScore = new(
                    evidenceScore,
                    qualityScore,
                    nearStart,
                    timelineTransitionFromBaseline || timelineTransitionFromPre,
                    positionMovedBackwardFromPre,
                    sourceDiffersFromBaseline,
                    hasPrior,
                    hasRecentSignalForSource);

                if (evidenceScore > selectedEvidenceScore
                    || (evidenceScore == selectedEvidenceScore && qualityScore > bestQualityScore))
                {
                    selectedEvidenceScore = evidenceScore;
                    bestQualityScore = qualityScore;
                }

                bool adoptable = IsStrongAlternateEvidence(
                        candidateScore.EvidenceScore,
                        candidateScore.NearStart,
                        candidateScore.TimelineTransitionObserved,
                        candidateScore.PositionMovedBackwardFromPre)
                    || ShouldAdoptModerateAlternateEvidence(
                        candidateScore.EvidenceScore,
                        candidateScore.NearStart,
                        candidateScore.TimelineTransitionObserved,
                        candidateScore.PositionMovedBackwardFromPre,
                        baselineNotActivelyPlaying,
                        preferredHasTimelineTransition,
                        forceAlternateAfterStreak);
                if (adoptable
                    && RequiresExistingCrossSourceCorroboration(
                        candidateScore.SourceDiffersFromBaseline,
                        candidateScore.SourceWasPresentPreCommand)
                    && !baselineNotActivelyPlaying
                    && !preferredHasTimelineTransition
                    && !HasSignalCorroborationForPreexistingCrossSourceAlternate(
                        candidateScore.HasRecentSignalForSource,
                        candidateScore.NearStart,
                        candidateScore.TimelineTransitionObserved,
                        candidateScore.PositionMovedBackwardFromPre))
                {
                    adoptable = false;
                }

                if (!adoptable)
                {
                    continue;
                }

                if (!selectedSet
                    || candidateScore.EvidenceScore > bestAdoptableEvidenceScore
                    || (candidateScore.EvidenceScore == bestAdoptableEvidenceScore && candidateScore.QualityScore > bestAdoptableQualityScore))
                {
                    selected = post;
                    selectedScore = candidateScore;
                    bestAdoptableEvidenceScore = candidateScore.EvidenceScore;
                    bestAdoptableQualityScore = candidateScore.QualityScore;
                    selectedSet = true;
                }
            }

            if (!selectedSet)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"No alternate candidate adopted candidateCount={candidateCount} baselineNotActivelyPlaying={baselineNotActivelyPlaying} preferredHasTimelineTransition={preferredHasTimelineTransition} forceAlternateAfterStreak={forceAlternateAfterStreak} selectedEvidenceScore={selectedEvidenceScore}",
                    nameof(TryFindChangedAlternateSnapshotAsync));
                return SessionSnapshot.Empty;
            }

            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                () => $"Selected alternate candidate source={LogPrivacy.Id(selected.SourceAppUserModelId)} evidenceScore={selectedScore.EvidenceScore} qualityScore={selectedScore.QualityScore} baselineNotActivelyPlaying={baselineNotActivelyPlaying} preferredHasTimelineTransition={preferredHasTimelineTransition} forceAlternateAfterStreak={forceAlternateAfterStreak} snapshot={FormatSnapshot(selected)}",
                nameof(TryFindChangedAlternateSnapshotAsync));

            return selected;
        }

        internal static string DescribeAlternateCandidateDiagnostics(
            bool changedVsPreferred,
            bool changedVsBaseline,
            bool changedVsPre,
            bool sourceDiffersFromBaseline,
            bool sourceMatchesPreferred,
            bool timelineTransitionObserved,
            bool positionMovedBackwardFromPre,
            long? postPositionSeconds,
            int evidenceScore,
            int qualityScore)
        {
            bool nearStart = postPositionSeconds.HasValue && postPositionSeconds.Value <= 3;
            return $"evidenceScore={evidenceScore} qualityScore={qualityScore} changedVsPreferred={changedVsPreferred} changedVsBaseline={changedVsBaseline} changedVsPre={changedVsPre} sourceDiffersFromBaseline={sourceDiffersFromBaseline} sourceMatchesPreferred={sourceMatchesPreferred} timelineTransitionObserved={timelineTransitionObserved} positionMovedBackwardFromPre={positionMovedBackwardFromPre} nearStart={nearStart}";
        }
    }
}
