using AudioPilot.Constants;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayBrowserSameSourceConflictLedger
    {
        private const long ActiveRivalObservationWindow = 1;
        private const long ReinforcedRivalObservationWindow = 3;

        private readonly Lock _lock = new();
        private readonly Dictionary<string, BrowserSameSourceLedgerState> _ledgerBySource = new(StringComparer.OrdinalIgnoreCase);

        internal int EntryCount
        {
            get
            {
                lock (_lock)
                {
                    return _ledgerBySource.Count;
                }
            }
        }

        private sealed class BrowserSameSourceLedgerState(long commandSequence)
        {
            public long CommandSequence { get; } = commandSequence;

            public long ObservationOrder { get; set; }

            public Dictionary<string, BrowserSameSourceCandidateState> CandidatesByTrackFingerprint { get; } =
                new(StringComparer.Ordinal);
        }

        private readonly record struct BrowserSameSourceCandidateState(
            BrowserPendingCandidateReasonClass ReasonClass,
            string Fingerprint,
            string TrackFingerprint,
            long? FirstPositionSeconds,
            long? LastPositionSeconds,
            long FirstObservationOrder,
            long LastObservationOrder,
            int ObservationCount,
            int ConsecutiveReasonObservationCount,
            bool HasRelevantRecentSignal,
            bool EverBlocked,
            bool EverCorrected,
            DateTimeOffset FirstObservedUtc,
            DateTimeOffset LastObservedUtc);

        private readonly record struct LocalWinnerCandidate(
            BrowserSameSourceCandidateState Candidate,
            BrowserSameSourceWinnerElectionResult Election);

        internal BrowserSameSourceLedgerObservation Observe(
            string preferredSourceAppUserModelId,
            long commandSequence,
            BrowserSameSourceEvidence evidence,
            DateTimeOffset? observedAtUtc = null)
        {
            RecordObservation(preferredSourceAppUserModelId, commandSequence, evidence, observedAtUtc);
            return EvaluateCandidate(preferredSourceAppUserModelId, commandSequence, evidence);
        }

        internal void RecordObservation(
            string preferredSourceAppUserModelId,
            long commandSequence,
            BrowserSameSourceEvidence evidence,
            DateTimeOffset? observedAtUtc = null)
        {
            lock (_lock)
            {
                DateTimeOffset nowUtc = observedAtUtc ?? DateTimeOffset.UtcNow;
                BrowserSameSourceLedgerState ledger = GetOrCreateLedger(preferredSourceAppUserModelId, commandSequence);
                ledger.ObservationOrder++;

                BrowserSameSourceCandidateState? existingState = null;
                if (ledger.CandidatesByTrackFingerprint.TryGetValue(evidence.TrackFingerprint, out BrowserSameSourceCandidateState state))
                {
                    existingState = state;
                }

                bool farPositionCorrectionCorroborated = existingState.HasValue
                    && existingState.Value.ReasonClass == BrowserPendingCandidateReasonClass.FarPosition
                    && evidence.ReasonClass == BrowserPendingCandidateReasonClass.AmbiguousNearStart
                    && existingState.Value.LastPositionSeconds.HasValue
                    && evidence.PositionSeconds.HasValue
                    && existingState.Value.LastPositionSeconds.Value > evidence.PositionSeconds.Value
                    && existingState.Value.LastPositionSeconds.Value - evidence.PositionSeconds.Value >= AppConstants.MediaOverlay.TimelineJumpThresholdSeconds
                    && !MediaOverlayBrowserSameSourcePolicy.IsWithinStrictAmbiguousNearStartWindow(existingState.Value.LastPositionSeconds)
                    && MediaOverlayBrowserSameSourcePolicy.IsWithinStrictAmbiguousNearStartWindow(evidence.PositionSeconds)
                    && (existingState.Value.HasRelevantRecentSignal || evidence.HasRelevantRecentSignal);

                int consecutiveReasonObservationCount = existingState.HasValue
                    && existingState.Value.ReasonClass == evidence.ReasonClass
                    && string.Equals(existingState.Value.Fingerprint, evidence.Fingerprint, StringComparison.Ordinal)
                    ? existingState.Value.ConsecutiveReasonObservationCount + 1
                    : 1;

                BrowserSameSourceCandidateState updatedState = new(
                    evidence.ReasonClass,
                    evidence.Fingerprint,
                    evidence.TrackFingerprint,
                    existingState?.FirstPositionSeconds ?? evidence.PositionSeconds,
                    evidence.PositionSeconds,
                    existingState?.FirstObservationOrder ?? ledger.ObservationOrder,
                    ledger.ObservationOrder,
                    (existingState?.ObservationCount ?? 0) + 1,
                    consecutiveReasonObservationCount,
                    (existingState?.HasRelevantRecentSignal ?? false) || evidence.HasRelevantRecentSignal,
                    (existingState?.EverBlocked ?? false) || IsBlockedReasonClass(evidence.ReasonClass),
                    (existingState?.EverCorrected ?? false) || farPositionCorrectionCorroborated,
                    existingState?.FirstObservedUtc ?? nowUtc,
                    nowUtc);
                ledger.CandidatesByTrackFingerprint[evidence.TrackFingerprint] = updatedState;
            }
        }

        internal BrowserSameSourceLedgerObservation EvaluateCandidate(
            string preferredSourceAppUserModelId,
            long commandSequence,
            BrowserSameSourceEvidence evidence)
        {
            lock (_lock)
            {
                if (!_ledgerBySource.TryGetValue(preferredSourceAppUserModelId, out BrowserSameSourceLedgerState? ledger)
                    || ledger.CommandSequence != commandSequence
                    || !ledger.CandidatesByTrackFingerprint.TryGetValue(evidence.TrackFingerprint, out BrowserSameSourceCandidateState candidate))
                {
                    return default;
                }

                bool stableObservationEligible = candidate.ConsecutiveReasonObservationCount >= 2
                    && MediaOverlayBrowserSameSourcePolicy.CanStableObservationCorroborate(
                        candidate.ReasonClass,
                        candidate.FirstPositionSeconds,
                        candidate.LastPositionSeconds);
                if (candidate.ReasonClass == BrowserPendingCandidateReasonClass.AmbiguousNearStart
                    && !MediaOverlayBrowserSameSourcePolicy.IsWithinStrictAmbiguousNearStartWindow(candidate.LastPositionSeconds))
                {
                    stableObservationEligible = false;
                }

                BrowserSameSourceWinnerElectionResult winnerElection = BuildWinnerElection(
                    ledger,
                    candidate,
                    stableObservationEligible);

                return new BrowserSameSourceLedgerObservation(
                    candidate.ReasonClass,
                    StableObservationCorroborated: winnerElection.HasWinner
                        && winnerElection.WinnerIsCurrentCandidate
                        && winnerElection.PromotionKind == BrowserSameSourcePromotionKind.StableRepetition,
                    FarPositionCorrectionCorroborated: winnerElection.HasWinner
                        && winnerElection.WinnerIsCurrentCandidate
                        && winnerElection.PromotionKind == BrowserSameSourcePromotionKind.FarPositionCorrection,
                    PostConflictRecorroborated: winnerElection.HasWinner
                        && winnerElection.WinnerIsCurrentCandidate
                        && winnerElection.PromotionKind == BrowserSameSourcePromotionKind.PostConflictRecorroboration,
                    ConflictObserved: ledger.CandidatesByTrackFingerprint.Count >= 2,
                    ConflictActive: winnerElection.ActiveRivalCount > 0 || winnerElection.ReinforcedRivalCount > 0,
                    DistinctCandidateCount: ledger.CandidatesByTrackFingerprint.Count,
                    ActiveRivalCount: winnerElection.ActiveRivalCount,
                    ReinforcedRivalCount: winnerElection.ReinforcedRivalCount,
                    StaleRivalCount: winnerElection.StaleRivalCount,
                    RivalReasonClasses: winnerElection.RivalReasonClasses,
                    StaleRivalIgnored: winnerElection.StaleRivalIgnored,
                    WinnerElection: winnerElection);
            }
        }

        internal BrowserSameSourceCommandSummary GetSummary(
            string? preferredSourceAppUserModelId,
            long commandSequence)
        {
            if (string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
            {
                return default;
            }

            lock (_lock)
            {
                if (!_ledgerBySource.TryGetValue(preferredSourceAppUserModelId, out BrowserSameSourceLedgerState? ledger)
                    || ledger.CommandSequence != commandSequence
                    || ledger.CandidatesByTrackFingerprint.Count == 0)
                {
                    return default;
                }

                bool conflictObserved = ledger.CandidatesByTrackFingerprint.Count >= 2;
                BrowserSameSourceWinnerElectionResult winnerElection = SelectOverallWinner(ledger);
                string? winningTrackFingerprint = winnerElection.WinningTrackFingerprint;

                (int activeRivalCount, int reinforcedRivalCount, int staleRivalCount, _, string rivalReasonClasses) = conflictObserved
                    ? ComputeRivalCounters(ledger, currentTrackFingerprint: winningTrackFingerprint)
                    : (0, 0, 0, false, "<none>");
                bool hasBlockedRivalEvidence = ledger.CandidatesByTrackFingerprint.Values.Any(candidate => candidate.EverBlocked);
                bool hasPendingCandidateEvidence = ledger.CandidatesByTrackFingerprint.Values.Any(candidate =>
                    candidate.ReasonClass is BrowserPendingCandidateReasonClass.AmbiguousNearStart
                        or BrowserPendingCandidateReasonClass.MetadataPending
                        or BrowserPendingCandidateReasonClass.FarPosition);
                bool hasPendingNonWinnerRivalEvidence = conflictObserved
                    && ledger.CandidatesByTrackFingerprint.Values.Any(candidate =>
                        !string.Equals(candidate.TrackFingerprint, winningTrackFingerprint, StringComparison.Ordinal)
                        && (candidate.ReasonClass is BrowserPendingCandidateReasonClass.AmbiguousNearStart
                            or BrowserPendingCandidateReasonClass.MetadataPending
                            or BrowserPendingCandidateReasonClass.FarPosition));

                return new BrowserSameSourceCommandSummary(
                    ConflictObserved: conflictObserved,
                    ConflictActive: conflictObserved && (activeRivalCount > 0 || reinforcedRivalCount > 0),
                    DistinctCandidateCount: ledger.CandidatesByTrackFingerprint.Count,
                    ActiveRivalCount: activeRivalCount,
                    ReinforcedRivalCount: reinforcedRivalCount,
                    StaleRivalCount: staleRivalCount,
                    HasBlockedRivalEvidence: hasBlockedRivalEvidence,
                    HasPendingCandidateEvidence: hasPendingCandidateEvidence,
                    HasPendingNonWinnerRivalEvidence: hasPendingNonWinnerRivalEvidence,
                    RivalReasonClasses: rivalReasonClasses,
                    WinnerElection: winnerElection);
            }
        }

        internal void Clear(string preferredSourceAppUserModelId, long commandSequence)
        {
            lock (_lock)
            {
                if (_ledgerBySource.TryGetValue(preferredSourceAppUserModelId, out BrowserSameSourceLedgerState? ledger)
                    && ledger.CommandSequence == commandSequence)
                {
                    _ledgerBySource.Remove(preferredSourceAppUserModelId);
                }
            }
        }

        private BrowserSameSourceLedgerState GetOrCreateLedger(string preferredSourceAppUserModelId, long commandSequence)
        {
            if (_ledgerBySource.TryGetValue(preferredSourceAppUserModelId, out BrowserSameSourceLedgerState? ledger)
                && ledger.CommandSequence == commandSequence)
            {
                return ledger;
            }

            BrowserSameSourceLedgerState created = new(commandSequence);
            _ledgerBySource[preferredSourceAppUserModelId] = created;
            return created;
        }

        private static BrowserSameSourceWinnerElectionResult BuildWinnerElection(
            BrowserSameSourceLedgerState ledger,
            BrowserSameSourceCandidateState candidate,
            bool stableObservationEligible)
        {
            BrowserSameSourceWinnerElectionResult localElection = BuildLocalWinnerElection(
                ledger,
                candidate,
                stableObservationEligible);
            if (!localElection.HasWinner)
            {
                return localElection;
            }

            BrowserSameSourceWinnerElectionResult overallWinner = SelectOverallWinner(ledger);
            return overallWinner.WinnerIsCurrentCandidate
                && string.Equals(overallWinner.WinningTrackFingerprint, candidate.TrackFingerprint, StringComparison.Ordinal)
                ? overallWinner
                : localElection with
                {
                    HasWinner = false,
                    WinnerIsCurrentCandidate = false,
                    WinningTrackFingerprint = overallWinner.WinningTrackFingerprint,
                    WinningReasonClass = BrowserPendingCandidateReasonClass.None,
                    PromotionKind = BrowserSameSourcePromotionKind.None,
                };
        }

        private static BrowserSameSourceWinnerElectionResult BuildLocalWinnerElection(
            BrowserSameSourceLedgerState ledger,
            BrowserSameSourceCandidateState candidate,
            bool stableObservationEligible)
        {
            (int activeRivalCount, int reinforcedRivalCount, int staleRivalCount, bool staleRivalIgnored, string rivalReasonClasses) =
                ComputeRivalCounters(ledger, candidate.TrackFingerprint);

            bool conflictObserved = ledger.CandidatesByTrackFingerprint.Count >= 2;
            bool conflictActive = activeRivalCount > 0 || reinforcedRivalCount > 0;
            bool hasExplicitCorrection = candidate.EverCorrected
                && candidate.ReasonClass == BrowserPendingCandidateReasonClass.AmbiguousNearStart
                && MediaOverlayBrowserSameSourcePolicy.IsWithinStrictAmbiguousNearStartWindow(candidate.LastPositionSeconds);

            BrowserSameSourcePromotionKind promotionKind = BrowserSameSourcePromotionKind.None;
            bool hasWinner = false;

            if (hasExplicitCorrection)
            {
                promotionKind = BrowserSameSourcePromotionKind.FarPositionCorrection;
                hasWinner = true;
            }
            else if (stableObservationEligible)
            {
                if (conflictObserved
                    && candidate.ReasonClass == BrowserPendingCandidateReasonClass.AmbiguousNearStart
                    && activeRivalCount == 0)
                {
                    promotionKind = BrowserSameSourcePromotionKind.PostConflictRecorroboration;
                }
                else if (!conflictActive
                    && (!conflictObserved
                        || candidate.ReasonClass == BrowserPendingCandidateReasonClass.MetadataPending))
                {
                    promotionKind = BrowserSameSourcePromotionKind.StableRepetition;
                }

                hasWinner = promotionKind != BrowserSameSourcePromotionKind.None;
            }

            return new BrowserSameSourceWinnerElectionResult(
                HasWinner: hasWinner,
                WinnerIsCurrentCandidate: hasWinner,
                WinningTrackFingerprint: hasWinner ? candidate.TrackFingerprint : null,
                WinningReasonClass: hasWinner ? candidate.ReasonClass : BrowserPendingCandidateReasonClass.None,
                PromotionKind: promotionKind,
                ActiveRivalCount: activeRivalCount,
                ReinforcedRivalCount: reinforcedRivalCount,
                StaleRivalCount: staleRivalCount,
                RivalReasonClasses: rivalReasonClasses,
                StaleRivalIgnored: staleRivalIgnored && hasWinner);
        }

        private static BrowserSameSourceWinnerElectionResult SelectOverallWinner(
            BrowserSameSourceLedgerState ledger)
        {
            LocalWinnerCandidate? bestCandidate = null;
            foreach (BrowserSameSourceCandidateState candidate in ledger.CandidatesByTrackFingerprint.Values)
            {
                bool stableObservationEligible = candidate.ConsecutiveReasonObservationCount >= 2
                    && MediaOverlayBrowserSameSourcePolicy.CanStableObservationCorroborate(
                        candidate.ReasonClass,
                        candidate.FirstPositionSeconds,
                        candidate.LastPositionSeconds);
                if (candidate.ReasonClass == BrowserPendingCandidateReasonClass.AmbiguousNearStart
                    && !MediaOverlayBrowserSameSourcePolicy.IsWithinStrictAmbiguousNearStartWindow(candidate.LastPositionSeconds))
                {
                    stableObservationEligible = false;
                }

                BrowserSameSourceWinnerElectionResult localElection = BuildLocalWinnerElection(
                    ledger,
                    candidate,
                    stableObservationEligible);
                if (!localElection.HasWinner)
                {
                    continue;
                }

                LocalWinnerCandidate current = new(candidate, localElection);
                if (!bestCandidate.HasValue || CompareWinnerCandidates(current, bestCandidate.Value) > 0)
                {
                    bestCandidate = current;
                }
            }

            if (!bestCandidate.HasValue)
            {
                return default;
            }

            return bestCandidate.Value.Election with
            {
                HasWinner = true,
                WinnerIsCurrentCandidate = true,
                WinningTrackFingerprint = bestCandidate.Value.Candidate.TrackFingerprint,
                WinningReasonClass = bestCandidate.Value.Candidate.ReasonClass,
            };
        }

        private static int CompareWinnerCandidates(
            LocalWinnerCandidate left,
            LocalWinnerCandidate right)
        {
            int leftTier = GetPromotionTier(left.Election.PromotionKind);
            int rightTier = GetPromotionTier(right.Election.PromotionKind);
            if (leftTier != rightTier)
            {
                return leftTier.CompareTo(rightTier);
            }

            if (left.Candidate.ConsecutiveReasonObservationCount != right.Candidate.ConsecutiveReasonObservationCount)
            {
                return left.Candidate.ConsecutiveReasonObservationCount.CompareTo(right.Candidate.ConsecutiveReasonObservationCount);
            }

            if (left.Candidate.HasRelevantRecentSignal != right.Candidate.HasRelevantRecentSignal)
            {
                return left.Candidate.HasRelevantRecentSignal ? 1 : -1;
            }

            if (left.Candidate.LastObservationOrder != right.Candidate.LastObservationOrder)
            {
                return left.Candidate.LastObservationOrder.CompareTo(right.Candidate.LastObservationOrder);
            }

            return string.CompareOrdinal(left.Candidate.TrackFingerprint, right.Candidate.TrackFingerprint);
        }

        private static int GetPromotionTier(BrowserSameSourcePromotionKind promotionKind)
        {
            return promotionKind switch
            {
                BrowserSameSourcePromotionKind.FarPositionCorrection => 3,
                BrowserSameSourcePromotionKind.PostConflictRecorroboration => 2,
                BrowserSameSourcePromotionKind.StableRepetition => 1,
                _ => 0,
            };
        }

        private static (int ActiveRivalCount, int ReinforcedRivalCount, int StaleRivalCount, bool StaleRivalIgnored, string RivalReasonClasses) ComputeRivalCounters(
            BrowserSameSourceLedgerState ledger,
            string? currentTrackFingerprint)
        {
            int activeRivalCount = 0;
            int reinforcedRivalCount = 0;
            int staleRivalCount = 0;
            List<string> rivalReasonClasses = [];

            foreach ((string trackFingerprint, BrowserSameSourceCandidateState rival) in ledger.CandidatesByTrackFingerprint)
            {
                if (!string.IsNullOrEmpty(currentTrackFingerprint)
                    && string.Equals(trackFingerprint, currentTrackFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                BrowserSameSourceRivalFreshness freshness = ClassifyRivalFreshness(ledger, rival);
                switch (freshness)
                {
                    case BrowserSameSourceRivalFreshness.Active:
                        activeRivalCount++;
                        break;
                    case BrowserSameSourceRivalFreshness.Reinforced:
                        reinforcedRivalCount++;
                        break;
                    case BrowserSameSourceRivalFreshness.Stale:
                        staleRivalCount++;
                        break;
                }

                rivalReasonClasses.Add(MediaOverlayBrowserSameSourcePolicy.DescribeReasonClass(rival.ReasonClass));
            }

            string joinedReasonClasses = JoinReasonClasses(rivalReasonClasses);
            return (
                activeRivalCount,
                reinforcedRivalCount,
                staleRivalCount,
                staleRivalCount > 0 && activeRivalCount == 0 && reinforcedRivalCount == 0,
                joinedReasonClasses);
        }

        private static BrowserSameSourceRivalFreshness ClassifyRivalFreshness(
            BrowserSameSourceLedgerState ledger,
            BrowserSameSourceCandidateState candidate)
        {
            long observationLag = Math.Max(0, ledger.ObservationOrder - candidate.LastObservationOrder);
            if (observationLag <= ActiveRivalObservationWindow)
            {
                return BrowserSameSourceRivalFreshness.Active;
            }

            if (candidate.ObservationCount >= 2 && observationLag <= ReinforcedRivalObservationWindow)
            {
                return BrowserSameSourceRivalFreshness.Reinforced;
            }

            if (candidate.ObservationCount > 0)
            {
                return BrowserSameSourceRivalFreshness.Stale;
            }

            return BrowserSameSourceRivalFreshness.None;
        }

        private static bool IsBlockedReasonClass(BrowserPendingCandidateReasonClass reasonClass)
        {
            return reasonClass is BrowserPendingCandidateReasonClass.PausedSibling
                or BrowserPendingCandidateReasonClass.RejectedOther
                or BrowserPendingCandidateReasonClass.FarPosition;
        }

        private static string JoinReasonClasses(IEnumerable<string> reasonClasses)
        {
            string[] values =
            [
                .. reasonClasses
                .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "<none>", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal),
            ];
            return values.Length == 0 ? "<none>" : string.Join(",", values);
        }
    }
}
