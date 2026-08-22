using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayPreferredSourceResolver(
        Func<string?, bool> hasRecentSignalForSource,
        MediaOverlayPreferredSourceTraceLimiter traceLimiter,
        Action<MediaOverlayPreferredSourceObservation>? observationSink,
        Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task<SessionSnapshot>> tryBuildSnapshotAsync)
    {
        private readonly MediaOverlayBrowserSameSourceConflictLedger _browserSameSourceConflictLedger = new();
        private readonly Func<string?, bool> _hasRecentSignalForSource = hasRecentSignalForSource;
        private readonly Action<MediaOverlayPreferredSourceObservation>? _observationSink = observationSink;
        private readonly MediaOverlayPreferredSourceTraceLimiter _traceLimiter = traceLimiter;
        private readonly Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task<SessionSnapshot>> _tryBuildSnapshotAsync = tryBuildSnapshotAsync;

        internal int CommandStateCountForTests => _browserSameSourceConflictLedger.EntryCount;

        public async Task<SessionSnapshot> TryResolvePreferredSourceSnapshotAsync(
            GlobalSystemMediaTransportControlsSessionManager manager,
            string preferredSourceAppUserModelId,
            SessionSnapshot? preferredReferenceSnapshot,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
            IReadOnlyList<SessionSnapshot> materializedSnapshots = await MediaOverlaySessionMaterializer.MaterializeAsync(
                sessions,
                _tryBuildSnapshotAsync,
                ex => Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to materialize one preferred-source media session. {ex.GetType().Name}",
                    nameof(TryResolvePreferredSourceSnapshotAsync)),
                cancellationToken);
            List<SessionSnapshot> matchingSnapshots = [.. materializedSnapshots
                .Where(snapshot => string.Equals(
                    snapshot.SourceAppUserModelId,
                    preferredSourceAppUserModelId,
                    StringComparison.OrdinalIgnoreCase))];

            return ResolvePreferredSourceSnapshots(
                preferredSourceAppUserModelId,
                preferredReferenceSnapshot,
                allowSingleCandidateMetadataChangeFallback,
                commandSequence,
                matchingSnapshots);
        }

        internal SessionSnapshot ResolvePreferredSourceSnapshots(
            string preferredSourceAppUserModelId,
            SessionSnapshot? preferredReferenceSnapshot,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            IReadOnlyList<SessionSnapshot> candidateSnapshots)
        {
            if (candidateSnapshots.Count == 0)
            {
                _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                PreferredSourceMissingSourceLogDecision decision = _traceLimiter.EvaluateMissingSource(
                    preferredSourceAppUserModelId,
                    now,
                    TimeSpan.FromMilliseconds(RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs));
                if (decision.ShouldEmit)
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        () =>
                        {
                            string message = $"Preferred-source resolution found no matching sessions for source={LogPrivacy.Id(preferredSourceAppUserModelId)}.";
                            return decision.SuppressedRepeats > 0
                                ? string.Concat(message, $" suppressedRepeats={decision.SuppressedRepeats}")
                                : message;
                        },
                        nameof(TryResolvePreferredSourceSnapshotAsync));
                }

                return SessionSnapshot.Empty;
            }

            if (candidateSnapshots.Count == 1)
            {
                SessionSnapshot singleSnapshot = candidateSnapshots[0];
                PreferredSourceCandidateResolution resolution = ResolveSingleCandidateResolution(
                    preferredSourceAppUserModelId,
                    preferredReferenceSnapshot,
                    singleSnapshot,
                    allowSingleCandidateMetadataChangeFallback,
                    commandSequence,
                    _hasRecentSignalForSource(singleSnapshot.SourceAppUserModelId),
                    clearBrowserStateOnAccept: false,
                    out BrowserSameSourceLedgerObservation browserPendingObservation);

                LogPreferredSourceSingleCandidate(
                    preferredSourceAppUserModelId,
                    preferredReferenceSnapshot,
                    singleSnapshot,
                    resolution,
                    _hasRecentSignalForSource(singleSnapshot.SourceAppUserModelId),
                    browserPendingObservation);
                RecordPreferredSourceObservation(
                    resolution,
                    preferredReferenceSnapshot,
                    singleSnapshot,
                    browserPendingObservation);
                if (!resolution.IsAccepted)
                {
                    return SessionSnapshot.Empty;
                }

                _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
                return singleSnapshot;
            }

            SessionSnapshot bestSnapshot = SessionSnapshot.Empty;
            int bestScore = int.MinValue;
            bool sawBrowserSameSourceEvidence = false;
            List<PreferredSourceCandidateContext> candidateContexts = [];

            for (int index = 0; index < candidateSnapshots.Count; index++)
            {
                SessionSnapshot candidate = candidateSnapshots[index];
                bool hasRecentSignalForSource = _hasRecentSignalForSource(candidate.SourceAppUserModelId);
                PreferredSourceCandidateDecision initialDecision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
                    preferredReferenceSnapshot,
                    candidate);
                bool hasBrowserSameSourceEvidence = MediaOverlayBrowserSameSourcePolicy.TryCreateSameSourceCandidateEvidence(
                    initialDecision,
                    preferredReferenceSnapshot,
                    candidate,
                    hasRecentSignalForSource,
                    out BrowserSameSourceEvidence browserEvidence);
                sawBrowserSameSourceEvidence |= hasBrowserSameSourceEvidence;
                if (hasBrowserSameSourceEvidence)
                {
                    _browserSameSourceConflictLedger.RecordObservation(
                        preferredSourceAppUserModelId,
                        commandSequence,
                        browserEvidence);
                }

                candidateContexts.Add(new PreferredSourceCandidateContext(
                    candidate,
                    CandidateScore: MediaOverlayPreferredSourceCandidateEvaluator.ComputePreferredSourceSnapshotScore(candidate, preferredReferenceSnapshot),
                    hasRecentSignalForSource,
                    initialDecision,
                    hasBrowserSameSourceEvidence,
                    browserEvidence));
            }

            for (int index = 0; index < candidateContexts.Count; index++)
            {
                PreferredSourceCandidateContext candidateContext = candidateContexts[index];
                SessionSnapshot candidate = candidateContext.Candidate;
                BrowserSameSourceLedgerObservation browserObservation = candidateContext.HasBrowserSameSourceEvidence
                    ? _browserSameSourceConflictLedger.EvaluateCandidate(
                        preferredSourceAppUserModelId,
                        commandSequence,
                        candidateContext.BrowserEvidence)
                    : default;
                PreferredSourceCandidateResolution resolution = MediaOverlayPreferredSourceCandidateEvaluator.ResolveCandidateResolution(
                    candidateContext.InitialDecision,
                    preferredReferenceSnapshot,
                    candidate,
                    allowSingleCandidateMetadataChangeFallback: false,
                    candidateContext.HasRecentSignalForSource,
                    browserObservation);
                int candidateScore = candidateContext.CandidateScore;

                if (!resolution.IsAccepted)
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        () => $"Preferred-source candidate source={LogPrivacy.Id(preferredSourceAppUserModelId)} index={index} finalVerdict={resolution.FinalDecision.Verdict} finalReason={resolution.FinalDecision.Reason} initialVerdict={resolution.InitialDecision.Verdict} initialReason={resolution.InitialDecision.Reason}{BuildBrowserRecentSignalIgnoredSuffix(candidateContext.HasRecentSignalForSource, preferredReferenceSnapshot, candidate, resolution)} {MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(preferredReferenceSnapshot, candidate, candidateScore: null, initialDecision: resolution.InitialDecision, finalDecision: resolution.FinalDecision, browserObservation)} snapshot={MediaOverlayEngine.FormatSnapshot(candidate)}",
                        nameof(TryResolvePreferredSourceSnapshotAsync));
                    RecordPreferredSourceObservation(resolution, preferredReferenceSnapshot, candidate, browserObservation);
                    continue;
                }

                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"Preferred-source candidate source={LogPrivacy.Id(preferredSourceAppUserModelId)} index={index} finalVerdict={resolution.FinalDecision.Verdict} finalReason={resolution.FinalDecision.Reason} initialVerdict={resolution.InitialDecision.Verdict} initialReason={resolution.InitialDecision.Reason}{BuildBrowserRecentSignalIgnoredSuffix(candidateContext.HasRecentSignalForSource, preferredReferenceSnapshot, candidate, resolution)} {MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(preferredReferenceSnapshot, candidate, candidateScore, resolution.InitialDecision, resolution.FinalDecision, browserObservation)} snapshot={MediaOverlayEngine.FormatSnapshot(candidate)}",
                    nameof(TryResolvePreferredSourceSnapshotAsync));
                RecordPreferredSourceObservation(resolution, preferredReferenceSnapshot, candidate, browserObservation);
                if (candidateScore > bestScore)
                {
                    bestSnapshot = candidate;
                    bestScore = candidateScore;
                    continue;
                }

                if (candidateScore == bestScore
                    && ShouldReplaceSnapshotForSharedSource(
                        bestSnapshot,
                        candidate,
                        preferredReferenceSnapshot,
                        out bool usedLexicographicFallback)
                    && LogPreferredSourceTieBreak(
                        preferredSourceAppUserModelId,
                        bestSnapshot,
                        candidate,
                        preferredReferenceSnapshot,
                        usedLexicographicFallback))
                {
                    bestSnapshot = candidate;
                }
            }

            if (MediaOverlayEngine.IsSessionMissing(bestSnapshot)
                && !sawBrowserSameSourceEvidence)
            {
                _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
            }
            else if (!MediaOverlayEngine.IsSessionMissing(bestSnapshot))
            {
                _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
            }

            Logger.Instance?.Trace(
                "MediaOverlayHelper",
                () => $"Preferred-source resolution completed source={LogPrivacy.Id(preferredSourceAppUserModelId)} matchingCount={candidateSnapshots.Count} selected={MediaOverlayEngine.FormatSnapshot(bestSnapshot)} bestScore={bestScore}",
                nameof(TryResolvePreferredSourceSnapshotAsync));

            return bestSnapshot;
        }

        private PreferredSourceCandidateResolution ResolveSingleCandidateResolution(
            string preferredSourceAppUserModelId,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            bool hasRecentSignalForSource,
            bool clearBrowserStateOnAccept,
            out BrowserSameSourceLedgerObservation browserPendingObservation)
        {
            PreferredSourceCandidateDecision initialDecision = MediaOverlayPreferredSourceCandidateEvaluator.EvaluateCandidateDecision(
                preferredReferenceSnapshot,
                candidate);
            bool browserSameSourceCandidate = MediaOverlayBrowserSameSourcePolicy.TryCreateSameSourceCandidateEvidence(
                initialDecision,
                preferredReferenceSnapshot,
                candidate,
                hasRecentSignalForSource,
                out BrowserSameSourceEvidence sameSourceEvidence);
            if (browserSameSourceCandidate)
            {
                _browserSameSourceConflictLedger.RecordObservation(preferredSourceAppUserModelId, commandSequence, sameSourceEvidence);
            }
            browserPendingObservation = browserSameSourceCandidate
                ? _browserSameSourceConflictLedger.EvaluateCandidate(preferredSourceAppUserModelId, commandSequence, sameSourceEvidence)
                : default;
            PreferredSourceCandidateResolution resolution = MediaOverlayPreferredSourceCandidateEvaluator.ResolveCandidateResolution(
                initialDecision,
                preferredReferenceSnapshot,
                candidate,
                allowSingleCandidateMetadataChangeFallback,
                hasRecentSignalForSource,
                browserPendingObservation);

            if (!browserSameSourceCandidate || (clearBrowserStateOnAccept && resolution.IsAccepted))
            {
                _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
            }

            return resolution;
        }

        private static string BuildBrowserRecentSignalIgnoredSuffix(
            bool hasRecentSignalForSource,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            PreferredSourceCandidateResolution resolution)
        {
            return MediaOverlayBrowserSameSourcePolicy.ShouldIgnoreRecentSignal(
                hasRecentSignalForSource,
                preferredReferenceSnapshot,
                candidate,
                resolution)
                ? " recentSignalIgnoredForBrowser=True"
                : string.Empty;
        }

        private static bool ShouldReplaceSnapshotForSharedSource(
            SessionSnapshot existing,
            SessionSnapshot candidate,
            SessionSnapshot? preferredReferenceSnapshot,
            out bool usedLexicographicFallback)
        {
            usedLexicographicFallback = false;
            int existingScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(existing);
            int candidateScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(candidate);
            if (candidateScore != existingScore)
            {
                return candidateScore > existingScore;
            }

            int existingFingerprintScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(existing, preferredReferenceSnapshot);
            int candidateFingerprintScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(candidate, preferredReferenceSnapshot);
            if (candidateFingerprintScore != existingFingerprintScore)
            {
                return candidateFingerprintScore > existingFingerprintScore;
            }

            usedLexicographicFallback = true;
            string existingKey = BuildSnapshotTieBreaker(existing);
            string candidateKey = BuildSnapshotTieBreaker(candidate);
            return string.CompareOrdinal(candidateKey, existingKey) > 0;
        }

        private void LogPreferredSourceSingleCandidate(
            string preferredSourceAppUserModelId,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot singleSnapshot,
            PreferredSourceCandidateResolution resolution,
            bool hasRecentSignalForSource,
            BrowserSameSourceLedgerObservation browserPendingObservation)
        {
            string signature = BuildPreferredSourceSingleCandidateSignature(resolution, singleSnapshot);
            PreferredSourceSingleCandidateLogDecision decision = _traceLimiter.Evaluate(
                preferredSourceAppUserModelId,
                signature,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs));

            if (!decision.ShouldEmit)
            {
                return;
            }

            Logger.Instance?.Trace(
                "MediaOverlayHelper",
                () =>
                {
                    string message = $"Preferred-source single candidate source={LogPrivacy.Id(preferredSourceAppUserModelId)} finalVerdict={resolution.FinalDecision.Verdict} finalReason={resolution.FinalDecision.Reason} initialVerdict={resolution.InitialDecision.Verdict} initialReason={resolution.InitialDecision.Reason}{BuildBrowserRecentSignalIgnoredSuffix(hasRecentSignalForSource, preferredReferenceSnapshot, singleSnapshot, resolution)} {MediaOverlayPreferredSourceCandidateEvaluator.DescribeCandidateDiagnostics(preferredReferenceSnapshot, singleSnapshot, candidateScore: null, initialDecision: resolution.InitialDecision, finalDecision: resolution.FinalDecision, browserPendingObservation)} snapshot={MediaOverlayEngine.FormatSnapshot(singleSnapshot)}";
                    return decision.SuppressedRepeats > 0
                        ? string.Concat(message, $" suppressedRepeats={decision.SuppressedRepeats}")
                        : message;
                },
                nameof(TryResolvePreferredSourceSnapshotAsync));
        }
        private static string BuildPreferredSourceSingleCandidateSignature(
            PreferredSourceCandidateResolution resolution,
            SessionSnapshot snapshot)
        {
            return string.Concat(
                resolution.InitialDecision.Verdict,
                "|",
                resolution.InitialDecision.Reason,
                "|",
                resolution.FinalDecision.Verdict,
                "|",
                resolution.FinalDecision.Reason,
                "|",
                snapshot.SourceAppUserModelId ?? string.Empty,
                "|",
                snapshot.Title ?? string.Empty,
                "|",
                snapshot.Artist ?? string.Empty,
                "|",
                snapshot.AlbumTitle ?? string.Empty,
                "|",
                snapshot.PositionSeconds?.ToString() ?? string.Empty,
                "|",
                snapshot.PlaybackStatus?.ToString() ?? string.Empty);
        }

        private static bool LogPreferredSourceTieBreak(
            string preferredSourceAppUserModelId,
            SessionSnapshot existing,
            SessionSnapshot candidate,
            SessionSnapshot? preferredReferenceSnapshot,
            bool usedLexicographicFallback)
        {
            if (!usedLexicographicFallback)
            {
                return true;
            }

            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                () => $"Preferred-source candidate tie resolved by lexicographic fallback source={LogPrivacy.Id(preferredSourceAppUserModelId)} existingFingerprintScore={MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(existing, preferredReferenceSnapshot)} candidateFingerprintScore={MediaOverlayPreferredSourceCandidateEvaluator.ComputeTransientSameSourceFingerprintScore(candidate, preferredReferenceSnapshot)} existing={MediaOverlayEngine.FormatSnapshot(existing)} candidate={MediaOverlayEngine.FormatSnapshot(candidate)}",
                nameof(TryResolvePreferredSourceSnapshotAsync));
            return true;
        }

        private static string BuildSnapshotTieBreaker(SessionSnapshot snapshot)
        {
            string title = snapshot.Title ?? string.Empty;
            string artist = snapshot.Artist ?? string.Empty;
            string album = snapshot.AlbumTitle ?? string.Empty;
            string source = snapshot.SourceAppUserModelId ?? string.Empty;
            string position = snapshot.PositionSeconds?.ToString() ?? string.Empty;
            string status = snapshot.PlaybackStatus?.ToString() ?? string.Empty;
            return string.Concat(source, "|", title, "|", artist, "|", album, "|", position, "|", status);
        }

        private static string? CleanValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private void RecordPreferredSourceObservation(
            PreferredSourceCandidateResolution resolution,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            BrowserSameSourceLedgerObservation browserPendingObservation = default)
        {
            if (_observationSink == null
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate))
            {
                return;
            }

            _observationSink(new MediaOverlayPreferredSourceObservation(
                BrowserCandidateBlocked: !resolution.IsAccepted,
                BrowserBlockedReason: !resolution.IsAccepted ? resolution.FinalDecision.Reason.ToString() : null,
                BrowserCandidateConverged: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated,
                BrowserConvergenceReason: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                    ? resolution.InitialDecision.Reason.ToString()
                    : null,
                BrowserSourceAppUserModelId: candidate.SourceAppUserModelId,
                BrowserTrackFingerprint: MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateTrackFingerprint(candidate),
                BrowserSameSourceConflictObserved: browserPendingObservation.ConflictObserved,
                BrowserSameSourceConflictActive: browserPendingObservation.ConflictActive,
                BrowserSameSourceDistinctCandidateCount: browserPendingObservation.DistinctCandidateCount,
                BrowserSameSourceActiveRivalCount: browserPendingObservation.ActiveRivalCount,
                BrowserSameSourceReinforcedRivalCount: browserPendingObservation.ReinforcedRivalCount,
                BrowserSameSourceStaleRivalCount: browserPendingObservation.StaleRivalCount,
                BrowserConvergedAfterConflict: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                    && browserPendingObservation.ConflictActive,
                BrowserConvergedAfterStaleRival: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                    && browserPendingObservation.StaleRivalIgnored,
                BrowserFarPositionCorrectionWin: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                    && browserPendingObservation.PromotionKind == BrowserSameSourcePromotionKind.FarPositionCorrection,
                BrowserRivalReasonClasses: browserPendingObservation.RivalReasonClasses,
                BrowserPromotionMode: resolution.FinalDecision.Reason == PreferredSourceCandidateReason.BrowserConvergenceCorroborated
                    ? browserPendingObservation.PromotionKind.ToString()
                    : null));
        }

        internal BrowserSameSourceCommandSummary GetBrowserSameSourceCommandSummary(
            string? preferredSourceAppUserModelId,
            long commandSequence)
        {
            return string.IsNullOrWhiteSpace(preferredSourceAppUserModelId)
                ? default
                : _browserSameSourceConflictLedger.GetSummary(preferredSourceAppUserModelId, commandSequence);
        }

        internal void ClearCommandState(string? preferredSourceAppUserModelId, long commandSequence)
        {
            if (string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
            {
                return;
            }

            _browserSameSourceConflictLedger.Clear(preferredSourceAppUserModelId, commandSequence);
        }

        private readonly record struct PreferredSourceCandidateContext(
            SessionSnapshot Candidate,
            int CandidateScore,
            bool HasRecentSignalForSource,
            PreferredSourceCandidateDecision InitialDecision,
            bool HasBrowserSameSourceEvidence,
            BrowserSameSourceEvidence BrowserEvidence);
    }
}
