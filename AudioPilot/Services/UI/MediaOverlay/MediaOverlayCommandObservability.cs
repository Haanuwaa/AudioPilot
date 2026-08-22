namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayCommandObservability
    {
        private bool _sawBlockedBrowserCandidate;
        private bool _sawBrowserCandidateConvergence;
        private bool _sawBrowserActiveConflictResolvedConvergence;
        private bool _sawBrowserStaleRivalResolvedConvergence;
        private bool _sawBrowserFarPositionCorrectionWin;
        private bool _sawSameSourceConflictLoadingFallback;
        private string? _lastConvergedBrowserSourceAppUserModelId;
        private string? _lastConvergedBrowserTrackFingerprint;

        public void RecordPreferredSourceObservation(MediaOverlayPreferredSourceObservation observation)
        {
            if (observation.BrowserCandidateBlocked)
            {
                _sawBlockedBrowserCandidate = true;
            }

            if (observation.BrowserCandidateConverged)
            {
                _sawBrowserCandidateConvergence = true;
                _lastConvergedBrowserSourceAppUserModelId = observation.BrowserSourceAppUserModelId;
                _lastConvergedBrowserTrackFingerprint = observation.BrowserTrackFingerprint;
            }

            if (observation.BrowserConvergedAfterConflict)
            {
                _sawBrowserActiveConflictResolvedConvergence = true;
            }

            if (observation.BrowserConvergedAfterStaleRival)
            {
                _sawBrowserStaleRivalResolvedConvergence = true;
            }

            if (observation.BrowserFarPositionCorrectionWin)
            {
                _sawBrowserFarPositionCorrectionWin = true;
            }
        }

        public void RecordTrackNavigationDiagnostics(MediaOverlayTrackNavigationDiagnostics diagnostics)
        {
            if (diagnostics.Outcome == "loading"
                && diagnostics.SameSourceConflictObserved
                && diagnostics.SameSourceConflictActive)
            {
                _sawSameSourceConflictLoadingFallback = true;
            }
        }

        public bool HasCommittedBrowserConvergenceForSnapshot(MediaOverlaySessionSnapshot snapshot)
        {
            if (!_sawBrowserCandidateConvergence
                || string.IsNullOrWhiteSpace(_lastConvergedBrowserSourceAppUserModelId)
                || string.IsNullOrWhiteSpace(_lastConvergedBrowserTrackFingerprint)
                || string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId))
            {
                return false;
            }

            if (!string.Equals(snapshot.SourceAppUserModelId, _lastConvergedBrowserSourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string snapshotTrackFingerprint = MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateTrackFingerprint(snapshot);
            return string.Equals(snapshotTrackFingerprint, _lastConvergedBrowserTrackFingerprint, StringComparison.Ordinal);
        }

        public MediaOverlayTelemetryOutcomeClass ClassifyTrackTelemetryOutcome(
            MediaOverlayResult result,
            SnapshotCaptureResult capture)
        {
            if (result.IsTrackMessage)
            {
                return _sawBrowserFarPositionCorrectionWin
                    ? MediaOverlayTelemetryOutcomeClass.BrowserFarPositionCorrectionWin
                    : _sawBrowserActiveConflictResolvedConvergence
                    ? MediaOverlayTelemetryOutcomeClass.BrowserCandidateConvergedAfterActiveConflict
                    : _sawBrowserStaleRivalResolvedConvergence
                    ? MediaOverlayTelemetryOutcomeClass.BrowserCandidateConvergedAfterStaleRival
                    : _sawBrowserCandidateConvergence
                    ? MediaOverlayTelemetryOutcomeClass.BrowserCandidateConverged
                    : capture.ChangeKind == TrackNavigationChangeKind.SameTrackRestart
                    ? MediaOverlayTelemetryOutcomeClass.SameTrackRestartChange
                    : capture.ChangeKind == TrackNavigationChangeKind.SourceSwitched
                    ? MediaOverlayTelemetryOutcomeClass.SourceSwitchedChange
                    : MediaOverlayTelemetryOutcomeClass.DirectChange;
            }

            if (capture.Outcome == TrackNavigationRecoveryOutcome.Loading)
            {
                return _sawSameSourceConflictLoadingFallback
                    ? MediaOverlayTelemetryOutcomeClass.SameSourceConflictLoadingFallback
                    : MediaOverlayTelemetryOutcomeClass.PendingLoadingFallback;
            }

            if (capture.Outcome == TrackNavigationRecoveryOutcome.Unchanged)
            {
                return _sawBlockedBrowserCandidate
                    ? MediaOverlayTelemetryOutcomeClass.BrowserCandidateBlocked
                    : MediaOverlayTelemetryOutcomeClass.UnchangedFallback;
            }

            return MediaOverlayTelemetryOutcomeClass.None;
        }
    }
}
