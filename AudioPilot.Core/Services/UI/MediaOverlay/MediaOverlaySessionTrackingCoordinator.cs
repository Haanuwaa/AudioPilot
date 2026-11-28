using AudioPilot.Constants;
using AudioPilot.Logging;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlaySessionTrackingCoordinator(MediaOverlayStateStore state, Func<DateTimeOffset>? utcNow = null)
    {
        private readonly MediaOverlayStateStore _state = state;
        private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        private readonly Lock _trimLock = new();
        private int _commandsSinceLastTrim = AppConstants.MediaOverlay.StateTrimCommandCadence;
        private DateTimeOffset _lastTrimUtc = DateTimeOffset.MinValue;

        public long BeginCommand()
        {
            MaybeTrimState();
            return _state.GetNextCommandSequence();
        }

        public long BeginReadOnlySnapshot()
        {
            MaybeTrimState();
            return _state.GetNextReadOnlyCommandSequence();
        }

        public void ThrowIfSuperseded(long commandSequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_state.IsCommandSequenceCurrent(commandSequence))
            {
                throw new OperationCanceledException();
            }
        }

        public bool ShouldForceAlternateAfterStagnantUnchanged(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            out int unchangedStreak,
            out int stagnantPositionStreak)
        {
            unchangedStreak = 0;
            stagnantPositionStreak = 0;

            string? source = baseline.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            string key = $"{command}|{source}";
            bool unchanged = MediaOverlayEngine.IsSameTrack(baseline, fallback);
            bool stagnantPosition = unchanged
                && baseline.PositionSeconds.HasValue
                && fallback.PositionSeconds.HasValue
                && baseline.PositionSeconds.Value == fallback.PositionSeconds.Value;

            return _state.UpdateTrackStreak(key, unchanged, stagnantPosition, out unchangedStreak, out stagnantPositionStreak);
        }

        public void ResetStreakIfChanged(MediaOverlayCommand command, SessionSnapshot baseline, SessionSnapshot finalSnapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(finalSnapshot) || MediaOverlayEngine.IsSameTrack(baseline, finalSnapshot))
            {
                return;
            }

            string? source = baseline.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            string key = $"{command}|{source}";
            _state.ResetTrackStreak(key);
        }

        private void MaybeTrimState()
        {
            lock (_trimLock)
            {
                DateTimeOffset now = _utcNow();
                _commandsSinceLastTrim++;

                if (_commandsSinceLastTrim < RuntimeTuningConfig.MediaOverlayStateTrimCommandCadence
                    && (_lastTrimUtc != DateTimeOffset.MinValue)
                    && (now - _lastTrimUtc).TotalSeconds < RuntimeTuningConfig.MediaOverlayStateTrimIntervalSeconds)
                {
                    return;
                }

                _state.TrimStateIfNeeded();
                _commandsSinceLastTrim = 0;
                _lastTrimUtc = now;
            }
        }

        public void RecordOverlayTelemetry(
            MediaOverlayTelemetryEvent telemetryEvent,
            MediaOverlayTelemetryOutcomeClass outcomeClass,
            TrackNavigationChangeKind? trackChangeKind = null)
        {
            if (_state.TryRecordOverlayTelemetry(
                    telemetryEvent,
                    outcomeClass,
                    trackChangeKind,
                    RuntimeTuningConfig.MediaOverlayTelemetryFlushEveryEvents,
                    RuntimeTuningConfig.MediaOverlayTelemetryFlushIntervalSeconds,
                    out MediaOverlayTelemetrySnapshot snapshot))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Overlay telemetry window events={snapshot.WindowEvents} track={snapshot.Track} plain={snapshot.Plain} noSession={snapshot.NoSession} hiddenCanceled={snapshot.HiddenCanceled} hiddenOther={snapshot.HiddenOther} directChange={snapshot.DirectChange} sameTrackRestart={snapshot.SameTrackRestartChange} sourceSwitched={snapshot.SourceSwitchedChange} browserBlocked={snapshot.BrowserCandidateBlocked} browserConverged={snapshot.BrowserCandidateConverged} browserConvergedAfterActiveConflict={snapshot.BrowserCandidateConvergedAfterActiveConflict} browserConvergedAfterStaleRival={snapshot.BrowserCandidateConvergedAfterStaleRival} farPositionCorrectionWins={snapshot.BrowserFarPositionCorrectionWin} pendingLoading={snapshot.PendingLoadingFallback} sameSourceConflictLoading={snapshot.SameSourceConflictLoadingFallback} unchangedFallback={snapshot.UnchangedFallback}",
                    nameof(MediaOverlayEngine.SendWithBestEffortOverlayAsync));
            }
        }
    }
}
