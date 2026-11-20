using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayPlayPauseResolver(
        Func<int, string?, DateTimeOffset, long, CancellationToken, Task<MediaOverlayDelayAssistResult>> delayWithEventAssistIfWithinBudgetAsync,
        Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> captureCurrentSnapshotAsync,
        MediaOverlayTimingProfile timingProfile,
        Func<DateTimeOffset> utcNow,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        private readonly Func<int, string?, DateTimeOffset, long, CancellationToken, Task<MediaOverlayDelayAssistResult>> _delayWithEventAssistIfWithinBudgetAsync = delayWithEventAssistIfWithinBudgetAsync;
        private readonly Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> _captureCurrentSnapshotAsync = captureCurrentSnapshotAsync;
        private readonly MediaOverlayTimingProfile _timingProfile = timingProfile;
        private readonly Func<DateTimeOffset> _utcNow = utcNow;
        private readonly Func<int, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;

        /// <summary>
        /// Events accelerate confirmed feedback, but can precede updated playback metadata. If they exhaust
        /// the probes early, bounded timed samples detect later updates without waiting for the entire settling
        /// window or allowing an event storm to drive continuous polling.
        /// </summary>
        public async Task<MediaOverlayPlayPauseResolutionResult> ResolveSnapshotAsync(
            SessionSnapshot baseline,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            string? stickySource,
            string? preferredSourceAppUserModelId,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            bool usedEventAssist = false;
            DateTimeOffset settleDeadlineUtc = _utcNow().AddMilliseconds(
                _timingProfile.PlayPauseSettleInitialDelayMs
                + Math.Max(0, _timingProfile.PlayPauseSettleAttempts - 1) * _timingProfile.PlayPauseSettleRetryDelayMs);
            if (settleDeadlineUtc > deadlineUtc)
            {
                settleDeadlineUtc = deadlineUtc;
            }

            MediaOverlayDelayAssistResult delayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                _timingProfile.PlayPauseSettleInitialDelayMs,
                preferredSourceAppUserModelId,
                deadlineUtc,
                commandSequence,
                cancellationToken);
            usedEventAssist |= delayResult.ObservedEvent;
            if (!delayResult.CompletedWithinBudget)
            {
                return new MediaOverlayPlayPauseResolutionResult(
                    new PlayPauseSnapshotResolution(baseline, SessionSnapshot.Empty),
                    new MediaOverlayPlayPauseDiagnostics(
                        FinalPath: "timed-out-before-settle",
                        Outcome: MediaOverlayEngine.IsSessionMissing(baseline) ? "no-session" : "unconfirmed",
                        UsedEventAssist: usedEventAssist,
                        UsedChangedBySourceSnapshots: false,
                        UsedImmediateCurrentEvidence: false,
                        ReusedBaselineMetadata: false));
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot best = SessionSnapshot.Empty;
            int timedSamplesRemaining = Math.Max(1, _timingProfile.PlayPauseSettleAttempts);
            for (int attempt = 0; attempt < _timingProfile.PlayPauseSettleAttempts || timedSamplesRemaining > 0; attempt++)
            {
                if (attempt >= _timingProfile.PlayPauseSettleAttempts)
                {
                    timedSamplesRemaining--;
                    int remainingMs = (int)Math.Ceiling((settleDeadlineUtc - _utcNow()).TotalMilliseconds);
                    if (!usedEventAssist || remainingMs <= 0)
                    {
                        break;
                    }

                    int sampleDelayMs = Math.Min(remainingMs, Math.Max(1, _timingProfile.PlayPauseSettleRetryDelayMs));
                    await _delayAsync(sampleDelayMs, cancellationToken);
                    delayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                        0,
                        preferredSourceAppUserModelId,
                        deadlineUtc,
                        commandSequence,
                        cancellationToken);
                    if (!delayResult.CompletedWithinBudget)
                    {
                        break;
                    }
                }
                else if (attempt > 0)
                {
                    delayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                        _timingProfile.PlayPauseSettleRetryDelayMs,
                        preferredSourceAppUserModelId,
                        deadlineUtc,
                        commandSequence,
                        cancellationToken);
                    usedEventAssist |= delayResult.ObservedEvent;
                    if (!delayResult.CompletedWithinBudget)
                    {
                        break;
                    }
                }

                SessionSnapshot latestBySourceSnapshot = SessionSnapshot.Empty;
                if (preCommandSnapshots.Count > 0)
                {
                    Dictionary<string, SessionSnapshot> latestBySource = await TryCaptureSnapshotsBySourceAsync(
                        preCommandSnapshots.Keys,
                        commandSequence,
                        cancellationToken);
                    if (TryResolveChangedPlayPauseSnapshot(
                        preCommandSnapshots,
                        latestBySource,
                        baseline.SourceAppUserModelId,
                        stickySource,
                        out PlayPauseSnapshotResolution resolvedBySource))
                    {
                        return new MediaOverlayPlayPauseResolutionResult(
                            resolvedBySource,
                            new MediaOverlayPlayPauseDiagnostics(
                                FinalPath: "changed-by-source-snapshots",
                                Outcome: "changed",
                                UsedEventAssist: usedEventAssist,
                                UsedChangedBySourceSnapshots: true,
                                UsedImmediateCurrentEvidence: false,
                                ReusedBaselineMetadata: false));
                    }

                    if (!string.IsNullOrWhiteSpace(preferredSourceAppUserModelId)
                        && latestBySource.TryGetValue(preferredSourceAppUserModelId, out SessionSnapshot preferredLatestBySource))
                    {
                        latestBySourceSnapshot = preferredLatestBySource;
                    }
                }

                latest = !MediaOverlayEngine.IsSessionMissing(latestBySourceSnapshot)
                    ? latestBySourceSnapshot
                    : await _captureCurrentSnapshotAsync(
                        preferredSourceAppUserModelId,
                        commandSequence,
                        baseline,
                        true,
                        cancellationToken);
                if (MediaOverlayEngine.IsSessionMissing(latest))
                {
                    continue;
                }

                if (MediaOverlayMessageFormatter.IsSnapshotEvidenceForPlayPause(latest, baseline))
                {
                    return new MediaOverlayPlayPauseResolutionResult(
                        new PlayPauseSnapshotResolution(baseline, latest),
                        new MediaOverlayPlayPauseDiagnostics(
                            FinalPath: "delayed-current-snapshot",
                            Outcome: "changed",
                            UsedEventAssist: usedEventAssist,
                            UsedChangedBySourceSnapshots: false,
                            UsedImmediateCurrentEvidence: true,
                            ReusedBaselineMetadata: false));
                }

                if (best.PlaybackStatus == null && latest.PlaybackStatus != null)
                {
                    best = latest;
                }
                else if (MediaOverlayEngine.IsSessionMissing(best) && !MediaOverlayEngine.IsSessionMissing(latest))
                {
                    best = latest;
                }
            }

            SessionSnapshot resolved = !MediaOverlayEngine.IsSessionMissing(best) ? best : latest;
            string outcome = MediaOverlayEngine.IsSessionMissing(resolved) && MediaOverlayEngine.IsSessionMissing(baseline)
                ? "no-session"
                : "unconfirmed";
            return new MediaOverlayPlayPauseResolutionResult(
                new PlayPauseSnapshotResolution(baseline, resolved),
                new MediaOverlayPlayPauseDiagnostics(
                    FinalPath: "settle-window-exhausted",
                    Outcome: outcome,
                    UsedEventAssist: usedEventAssist,
                    UsedChangedBySourceSnapshots: false,
                    UsedImmediateCurrentEvidence: false,
                    ReusedBaselineMetadata: false));
        }

        internal static bool TryResolveChangedPlayPauseSnapshot(
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            IReadOnlyDictionary<string, SessionSnapshot> postCommandSnapshots,
            string? baselineSource,
            string? stickySource,
            out PlayPauseSnapshotResolution resolution)
        {
            resolution = PlayPauseSnapshotResolution.Empty;

            string? selectedSource = null;
            SessionSnapshot selectedBaseline = SessionSnapshot.Empty;
            SessionSnapshot selectedSnapshot = SessionSnapshot.Empty;
            int bestScore = int.MinValue;

            foreach ((string source, SessionSnapshot preSnapshot) in preCommandSnapshots)
            {
                if (!postCommandSnapshots.TryGetValue(source, out SessionSnapshot postSnapshot))
                {
                    continue;
                }

                if (!HasPlayPauseStatusTransition(preSnapshot, postSnapshot))
                {
                    continue;
                }

                int score = 0;
                if (!string.IsNullOrWhiteSpace(baselineSource)
                    && string.Equals(source, baselineSource, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                }

                if (!string.IsNullOrWhiteSpace(stickySource)
                    && string.Equals(source, stickySource, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }

                if (MediaOverlayEngine.HasTrackData(postSnapshot))
                {
                    score += 6;
                }

                if (MediaOverlayEngine.HasTrackData(preSnapshot) && MediaOverlayEngine.IsSameTrack(preSnapshot, postSnapshot))
                {
                    score += 2;
                }

                if (postSnapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    score += 1;
                }

                if (score > bestScore
                    || (score == bestScore
                        && ShouldPreferPlayPauseSnapshot(postSnapshot, selectedSnapshot)))
                {
                    bestScore = score;
                    selectedSource = source;
                    selectedBaseline = preSnapshot;
                    selectedSnapshot = postSnapshot;
                }
            }

            if (string.IsNullOrWhiteSpace(selectedSource))
            {
                return false;
            }

            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                $"Resolved play/pause source from changed session snapshots source={LogPrivacy.Id(selectedSource)} baseline={MediaOverlayEngine.FormatSnapshot(selectedBaseline)} latest={MediaOverlayEngine.FormatSnapshot(selectedSnapshot)} score={bestScore}",
                nameof(TryResolveChangedPlayPauseSnapshot));

            resolution = new PlayPauseSnapshotResolution(selectedBaseline, selectedSnapshot);
            return true;
        }

        private async Task<Dictionary<string, SessionSnapshot>> TryCaptureSnapshotsBySourceAsync(
            IEnumerable<string> candidateSources,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            var snapshots = new Dictionary<string, SessionSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (string source in candidateSources)
            {
                SessionSnapshot snapshot = await _captureCurrentSnapshotAsync(
                    source,
                    commandSequence,
                    null,
                    true,
                    cancellationToken);
                if (MediaOverlayEngine.IsSessionMissing(snapshot) || string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId))
                {
                    continue;
                }

                snapshots[snapshot.SourceAppUserModelId] = snapshot;
            }

            return snapshots;
        }

        private static bool HasPlayPauseStatusTransition(SessionSnapshot baseline, SessionSnapshot latest)
        {
            return baseline.PlaybackStatus.HasValue
                && latest.PlaybackStatus.HasValue
                && baseline.PlaybackStatus.Value != latest.PlaybackStatus.Value
                && (baseline.PlaybackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    || baseline.PlaybackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                && (latest.PlaybackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    || latest.PlaybackStatus.Value == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        }

        private static bool ShouldPreferPlayPauseSnapshot(SessionSnapshot candidate, SessionSnapshot selected)
        {
            if (MediaOverlayEngine.IsSessionMissing(selected))
            {
                return true;
            }

            int candidateScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(candidate);
            int selectedScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(selected);
            if (candidateScore != selectedScore)
            {
                return candidateScore > selectedScore;
            }

            return string.CompareOrdinal(
                candidate.SourceAppUserModelId ?? string.Empty,
                selected.SourceAppUserModelId ?? string.Empty) > 0;
        }
    }
}
