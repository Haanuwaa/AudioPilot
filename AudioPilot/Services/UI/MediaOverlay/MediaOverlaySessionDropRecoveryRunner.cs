using AudioPilot.Logging;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal readonly record struct MediaOverlaySessionDropRecoveryResult(
        SessionSnapshot Snapshot,
        int PollAttempts,
        bool EndedByDeadline,
        bool UsedExtendedTrackLoadRecovery,
        int ElapsedMs);

    internal sealed class MediaOverlaySessionDropRecoveryRunner(
        Func<int, string?, DateTimeOffset, long, CancellationToken, Task<bool>> delayWithEventAssistIfWithinBudgetAsync,
        Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> captureCurrentSnapshotAsync,
        Func<MediaOverlayCommand, SessionSnapshot, SessionSnapshot, bool> isConfirmedTrackNavigationTransition,
        Func<string?, SessionSnapshot, long, bool> shouldAbortBrowserConflictRecoveryEarly,
        MediaOverlayTimingProfile timingProfile,
        Func<DateTimeOffset>? utcNow = null)
    {
        private readonly Func<int, string?, DateTimeOffset, long, CancellationToken, Task<bool>> _delayWithEventAssistIfWithinBudgetAsync = delayWithEventAssistIfWithinBudgetAsync;
        private readonly Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> _captureCurrentSnapshotAsync = captureCurrentSnapshotAsync;
        private readonly Func<MediaOverlayCommand, SessionSnapshot, SessionSnapshot, bool> _isConfirmedTrackNavigationTransition = isConfirmedTrackNavigationTransition;
        private readonly Func<string?, SessionSnapshot, long, bool> _shouldAbortBrowserConflictRecoveryEarly = shouldAbortBrowserConflictRecoveryEarly;
        private readonly MediaOverlayTimingProfile _timingProfile = timingProfile;
        private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);

        private readonly record struct SessionDropPollingResult(
            SessionSnapshot Snapshot,
            int Attempts,
            bool EndedByDeadline);

        private readonly record struct TrackLoadRecoveryResult(
            SessionSnapshot Snapshot,
            bool Attempted,
            bool EndedByDeadline);

        internal async Task<MediaOverlaySessionDropRecoveryResult> ResolveAsync(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop,
            string? preferredSourceAppUserModelId,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            string operationName,
            CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            SessionDropPollingResult polling = new(SessionSnapshot.Empty, 0, false);
            bool usedExtendedTrackLoadRecovery = false;
            bool endedByDeadline = false;

            if (ShouldPollAfterObservedSessionDrop(baseline, fallback, sawSessionDrop))
            {
                polling = await TryRecoverAfterSessionDropAsync(command, preferredSourceAppUserModelId, baseline, commandSequence, deadlineUtc, cancellationToken);
                endedByDeadline = polling.EndedByDeadline;
                if (!MediaOverlayEngine.IsSessionMissing(polling.Snapshot))
                {
                    fallback = polling.Snapshot;
                }
            }

            if (MediaOverlayTrackNavigationRecoveryPolicy.ShouldUseExtendedTrackLoadRecoveryAfterSessionDrop(baseline, fallback, true))
            {
                TrackLoadRecoveryResult sessionDropTrackLoadRecovery = await TryRecoverAfterTrackLoadDetailedAsync(
                    command,
                    preferredSourceAppUserModelId,
                    baseline,
                    true,
                    _timingProfile.SessionDropTrackLoadRecoveryInitialDelayMs,
                    _timingProfile.SessionDropTrackLoadRecoveryRetryDelayMs,
                    _timingProfile.SessionDropTrackLoadRecoveryAttempts,
                    operationName,
                    commandSequence,
                    deadlineUtc,
                    cancellationToken);
                usedExtendedTrackLoadRecovery = sessionDropTrackLoadRecovery.Attempted;
                endedByDeadline |= sessionDropTrackLoadRecovery.EndedByDeadline;
                if (!MediaOverlayEngine.IsSessionMissing(sessionDropTrackLoadRecovery.Snapshot))
                {
                    fallback = sessionDropTrackLoadRecovery.Snapshot;
                }
            }

            stopwatch.Stop();
            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                $"Session-drop recovery completed for {command}. attempts={polling.Attempts} endedByDeadline={endedByDeadline} usedExtendedTrackLoad={usedExtendedTrackLoadRecovery} elapsedMs={stopwatch.ElapsedMilliseconds} snapshot={MediaOverlayEngine.FormatSnapshot(fallback)}",
                operationName);

            return new MediaOverlaySessionDropRecoveryResult(
                fallback,
                polling.Attempts,
                endedByDeadline,
                usedExtendedTrackLoadRecovery,
                (int)stopwatch.ElapsedMilliseconds);
        }

        private async Task<SessionDropPollingResult> TryRecoverAfterSessionDropAsync(
            MediaOverlayCommand command,
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            bool delayed = await _delayWithEventAssistIfWithinBudgetAsync(
                _timingProfile.SessionDropRecoveryInitialDelayMs,
                preferredSourceAppUserModelId,
                deadlineUtc,
                commandSequence,
                cancellationToken);
            if (!delayed)
            {
                return new SessionDropPollingResult(SessionSnapshot.Empty, 0, true);
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot lastNonMissing = SessionSnapshot.Empty;
            int attempts = 0;
            bool endedByDeadline = false;
            while (MediaOverlayTrackNavigationRecoveryPolicy.ShouldContinueSessionDropRecovery(
                deadlineUtc,
                attempts,
                _timingProfile.SessionDropRecoveryAttempts,
                _utcNow()))
            {
                if (attempts > 0)
                {
                    delayed = await _delayWithEventAssistIfWithinBudgetAsync(
                        _timingProfile.SessionDropRecoveryRetryDelayMs,
                        preferredSourceAppUserModelId,
                        deadlineUtc,
                        commandSequence,
                        cancellationToken);
                    if (!delayed)
                    {
                        endedByDeadline = true;
                        break;
                    }
                }

                latest = await _captureCurrentSnapshotAsync(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    baseline,
                    true,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(latest))
                {
                    lastNonMissing = latest;

                    if (_isConfirmedTrackNavigationTransition(command, baseline, latest))
                    {
                        return new SessionDropPollingResult(latest, attempts + 1, false);
                    }
                }

                if (_shouldAbortBrowserConflictRecoveryEarly(preferredSourceAppUserModelId, baseline, commandSequence))
                {
                    break;
                }

                attempts++;
            }

            if (!endedByDeadline
                && attempts >= _timingProfile.SessionDropRecoveryAttempts
                && _utcNow() >= deadlineUtc)
            {
                endedByDeadline = true;
            }

            SessionSnapshot resolved = !MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest;
            return new SessionDropPollingResult(resolved, attempts, endedByDeadline);
        }

        private async Task<TrackLoadRecoveryResult> TryRecoverAfterTrackLoadDetailedAsync(
            MediaOverlayCommand command,
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            bool allowSingleCandidateMetadataChangeFallback,
            int initialDelayMs,
            int retryDelayMs,
            int attempts,
            string operationName,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            bool delayed = await _delayWithEventAssistIfWithinBudgetAsync(initialDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
            if (!delayed)
            {
                return new TrackLoadRecoveryResult(SessionSnapshot.Empty, Attempted: false, EndedByDeadline: true);
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot lastNonMissing = SessionSnapshot.Empty;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    delayed = await _delayWithEventAssistIfWithinBudgetAsync(retryDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
                    if (!delayed)
                    {
                        return new TrackLoadRecoveryResult(!MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest, Attempted: true, EndedByDeadline: true);
                    }
                }

                latest = await _captureCurrentSnapshotAsync(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    baseline,
                    allowSingleCandidateMetadataChangeFallback,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(latest))
                {
                    lastNonMissing = latest;

                    if (_isConfirmedTrackNavigationTransition(command, baseline, latest))
                    {
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            $"Recovered track metadata after post-command load wait. baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(latest)}",
                            operationName);
                        return new TrackLoadRecoveryResult(latest, Attempted: true, EndedByDeadline: false);
                    }
                }
            }

            return new TrackLoadRecoveryResult(!MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest, Attempted: true, EndedByDeadline: false);
        }

        private static bool ShouldPollAfterObservedSessionDrop(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop)
        {
            if (!sawSessionDrop)
            {
                return false;
            }

            if (MediaOverlayEngine.IsSessionMissing(fallback))
            {
                return true;
            }

            return MediaOverlayEngine.HasTrackData(baseline)
                && MediaOverlayEngine.HasTrackData(fallback)
                && MediaOverlayEngine.IsSameTrack(baseline, fallback)
                && string.Equals(baseline.SourceAppUserModelId, fallback.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
