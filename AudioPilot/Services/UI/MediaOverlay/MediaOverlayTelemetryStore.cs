namespace AudioPilot.Services.UI.MediaOverlay
{
    internal enum MediaOverlayTelemetryEvent
    {
        TrackShown,
        PlainShown,
        HiddenNoSession,
        HiddenCanceled,
        HiddenOther,
    }

    internal enum MediaOverlayTelemetryOutcomeClass
    {
        None,
        DirectChange,
        SameTrackRestartChange,
        SourceSwitchedChange,
        BrowserCandidateBlocked,
        BrowserCandidateConverged,
        BrowserCandidateConvergedAfterActiveConflict,
        BrowserCandidateConvergedAfterStaleRival,
        BrowserFarPositionCorrectionWin,
        PendingLoadingFallback,
        SameSourceConflictLoadingFallback,
        UnchangedFallback,
    }

    internal readonly record struct MediaOverlayTelemetrySnapshot(
        int WindowEvents,
        int Track,
        int Plain,
        int HiddenNoSession,
        int HiddenCanceled,
        int HiddenOther,
        int DirectChange,
        int SameTrackRestartChange,
        int SourceSwitchedChange,
        int BrowserCandidateBlocked,
        int BrowserCandidateConverged,
        int BrowserCandidateConvergedAfterActiveConflict,
        int BrowserCandidateConvergedAfterStaleRival,
        int BrowserFarPositionCorrectionWin,
        int PendingLoadingFallback,
        int SameSourceConflictLoadingFallback,
        int UnchangedFallback);

    internal sealed class MediaOverlayTelemetryStore
    {
        private readonly Lock _lock = new();
        private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
        private int _windowEvents;
        private int _hiddenNoSession;
        private int _hiddenCanceled;
        private int _hiddenOther;
        private int _plain;
        private int _track;
        private int _directChange;
        private int _sameTrackRestartChange;
        private int _sourceSwitchedChange;
        private int _browserCandidateBlocked;
        private int _browserCandidateConverged;
        private int _browserCandidateConvergedAfterActiveConflict;
        private int _browserCandidateConvergedAfterStaleRival;
        private int _browserFarPositionCorrectionWin;
        private int _pendingLoadingFallback;
        private int _sameSourceConflictLoadingFallback;
        private int _unchangedFallback;

        public bool TryRecord(
            MediaOverlayTelemetryEvent telemetryEvent,
            MediaOverlayTelemetryOutcomeClass outcomeClass,
            TrackNavigationChangeKind? trackChangeKind,
            int flushEveryEvents,
            int flushIntervalSeconds,
            out MediaOverlayTelemetrySnapshot snapshot)
        {
            lock (_lock)
            {
                _windowEvents++;

                switch (telemetryEvent)
                {
                    case MediaOverlayTelemetryEvent.HiddenNoSession:
                        _hiddenNoSession++;
                        break;
                    case MediaOverlayTelemetryEvent.HiddenCanceled:
                        _hiddenCanceled++;
                        break;
                    case MediaOverlayTelemetryEvent.TrackShown:
                        _track++;
                        break;
                    case MediaOverlayTelemetryEvent.PlainShown:
                        _plain++;
                        break;
                    default:
                        _hiddenOther++;
                        break;
                }

                bool countTrackChangeKind = telemetryEvent == MediaOverlayTelemetryEvent.TrackShown
                    && trackChangeKind.HasValue;
                if (countTrackChangeKind)
                {
                    switch (trackChangeKind!.Value)
                    {
                        case TrackNavigationChangeKind.SameTrackRestart:
                            _sameTrackRestartChange++;
                            break;
                        case TrackNavigationChangeKind.SourceSwitched:
                            _sourceSwitchedChange++;
                            break;
                        default:
                            _directChange++;
                            break;
                    }
                }

                switch (outcomeClass)
                {
                    case MediaOverlayTelemetryOutcomeClass.DirectChange:
                        if (!countTrackChangeKind)
                        {
                            _directChange++;
                        }
                        break;
                    case MediaOverlayTelemetryOutcomeClass.SameTrackRestartChange:
                        if (!countTrackChangeKind)
                        {
                            _sameTrackRestartChange++;
                        }
                        break;
                    case MediaOverlayTelemetryOutcomeClass.SourceSwitchedChange:
                        if (!countTrackChangeKind)
                        {
                            _sourceSwitchedChange++;
                        }
                        break;
                    case MediaOverlayTelemetryOutcomeClass.BrowserCandidateBlocked:
                        _browserCandidateBlocked++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.BrowserCandidateConverged:
                        _browserCandidateConverged++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.BrowserCandidateConvergedAfterActiveConflict:
                        _browserCandidateConvergedAfterActiveConflict++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.BrowserCandidateConvergedAfterStaleRival:
                        _browserCandidateConvergedAfterStaleRival++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.BrowserFarPositionCorrectionWin:
                        _browserFarPositionCorrectionWin++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.PendingLoadingFallback:
                        _pendingLoadingFallback++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.SameSourceConflictLoadingFallback:
                        _sameSourceConflictLoadingFallback++;
                        break;
                    case MediaOverlayTelemetryOutcomeClass.UnchangedFallback:
                        _unchangedFallback++;
                        break;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool flushForCount = _windowEvents >= flushEveryEvents;
                bool flushForTime = (now - _windowStartUtc).TotalSeconds >= flushIntervalSeconds;
                if (!flushForCount && !flushForTime)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = new MediaOverlayTelemetrySnapshot(
                    WindowEvents: _windowEvents,
                    Track: _track,
                    Plain: _plain,
                    HiddenNoSession: _hiddenNoSession,
                    HiddenCanceled: _hiddenCanceled,
                    HiddenOther: _hiddenOther,
                    DirectChange: _directChange,
                    SameTrackRestartChange: _sameTrackRestartChange,
                    SourceSwitchedChange: _sourceSwitchedChange,
                    BrowserCandidateBlocked: _browserCandidateBlocked,
                    BrowserCandidateConverged: _browserCandidateConverged,
                    BrowserCandidateConvergedAfterActiveConflict: _browserCandidateConvergedAfterActiveConflict,
                    BrowserCandidateConvergedAfterStaleRival: _browserCandidateConvergedAfterStaleRival,
                    BrowserFarPositionCorrectionWin: _browserFarPositionCorrectionWin,
                    PendingLoadingFallback: _pendingLoadingFallback,
                    SameSourceConflictLoadingFallback: _sameSourceConflictLoadingFallback,
                    UnchangedFallback: _unchangedFallback);

                _windowStartUtc = now;
                _windowEvents = 0;
                _hiddenNoSession = 0;
                _hiddenCanceled = 0;
                _hiddenOther = 0;
                _plain = 0;
                _track = 0;
                _directChange = 0;
                _sameTrackRestartChange = 0;
                _sourceSwitchedChange = 0;
                _browserCandidateBlocked = 0;
                _browserCandidateConverged = 0;
                _browserCandidateConvergedAfterActiveConflict = 0;
                _browserCandidateConvergedAfterStaleRival = 0;
                _browserFarPositionCorrectionWin = 0;
                _pendingLoadingFallback = 0;
                _sameSourceConflictLoadingFallback = 0;
                _unchangedFallback = 0;
                return true;
            }
        }
    }
}
