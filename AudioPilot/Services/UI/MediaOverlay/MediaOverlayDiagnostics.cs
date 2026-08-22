namespace AudioPilot.Services.UI.MediaOverlay
{
    internal readonly record struct MediaOverlayDelayAssistResult(
        bool CompletedWithinBudget,
        bool ObservedEvent,
        MediaEventAssistOutcome EventAssistOutcome = default);

    internal readonly record struct MediaOverlayPreferredSourceObservation(
        bool BrowserCandidateBlocked,
        string? BrowserBlockedReason,
        bool BrowserCandidateConverged,
        string? BrowserConvergenceReason,
        string? BrowserSourceAppUserModelId = null,
        string? BrowserTrackFingerprint = null,
        bool BrowserSameSourceConflictObserved = false,
        bool BrowserSameSourceConflictActive = false,
        int BrowserSameSourceDistinctCandidateCount = 0,
        int BrowserSameSourceActiveRivalCount = 0,
        int BrowserSameSourceReinforcedRivalCount = 0,
        int BrowserSameSourceStaleRivalCount = 0,
        bool BrowserConvergedAfterConflict = false,
        bool BrowserConvergedAfterStaleRival = false,
        bool BrowserFarPositionCorrectionWin = false,
        string? BrowserRivalReasonClasses = null,
        string? BrowserPromotionMode = null,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayTrackNavigationDiagnostics(
        string FinalPhase,
        string Outcome,
        string FinalChangeKind,
        bool SawSessionDrop,
        bool UsedSessionDropRecovery,
        bool UsedLateTrackLoadRecovery,
        bool UsedRecoveredAlternateSource,
        string FinalFallbackClassification,
        bool SameSourceConflictObserved = false,
        bool SameSourceConflictActive = false,
        int SameSourceDistinctCandidateCount = 0,
        int SameSourceActiveRivalCount = 0,
        int SameSourceReinforcedRivalCount = 0,
        int SameSourceStaleRivalCount = 0,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayPlayPauseDiagnostics(
        string FinalPath,
        string Outcome,
        bool UsedEventAssist,
        bool UsedChangedBySourceSnapshots,
        bool UsedImmediateCurrentEvidence,
        bool ReusedBaselineMetadata,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayCommandResult(
        MediaOverlayCommand Command,
        MediaOverlayResult Overlay,
        string DiagCode,
        double? ElapsedMs = null,
        MediaOverlayTrackNavigationDiagnostics? TrackNavigationDiagnostics = null,
        MediaOverlayPlayPauseDiagnostics? PlayPauseDiagnostics = null)
    {
        public static MediaOverlayCommandResult From(
            MediaOverlayCommand command,
            MediaOverlayResult overlay,
            MediaOverlayTrackNavigationDiagnostics? trackNavigationDiagnostics,
            MediaOverlayPlayPauseDiagnostics? playPauseDiagnostics,
            string? diagCodeOverride = null)
        {
            return new MediaOverlayCommandResult(
                command,
                overlay,
                string.IsNullOrWhiteSpace(diagCodeOverride)
                    ? ClassifyDiagCode(command, overlay, trackNavigationDiagnostics, playPauseDiagnostics)
                    : diagCodeOverride,
                trackNavigationDiagnostics?.ElapsedMs ?? playPauseDiagnostics?.ElapsedMs,
                trackNavigationDiagnostics,
                playPauseDiagnostics);
        }

        private static string ClassifyDiagCode(
            MediaOverlayCommand command,
            MediaOverlayResult overlay,
            MediaOverlayTrackNavigationDiagnostics? trackNavigationDiagnostics,
            MediaOverlayPlayPauseDiagnostics? playPauseDiagnostics)
        {
            if (IsCommandSendFailure(command, overlay))
            {
                return "media-command-send-failed";
            }

            if (trackNavigationDiagnostics is { } trackDiagnostics)
            {
                return trackDiagnostics.Outcome switch
                {
                    "changed" => "media-overlay-track-changed",
                    "unchanged" => "media-overlay-track-unchanged",
                    "loading" => "media-overlay-track-loading",
                    _ => "media-overlay-track-fallback",
                };
            }

            if (playPauseDiagnostics is { } playPauseDiagnosticsValue)
            {
                return playPauseDiagnosticsValue.Outcome switch
                {
                    "hidden" => "media-overlay-no-session",
                    "changed" => "media-overlay-play-pause-resolved",
                    _ => "media-overlay-play-pause-fallback",
                };
            }

            if (overlay.Kind == MediaOverlayResultKind.Hidden)
            {
                return "media-overlay-hidden";
            }

            if (overlay.Kind == MediaOverlayResultKind.TrackMessage)
            {
                return command is MediaOverlayCommand.NextTrack or MediaOverlayCommand.PreviousTrack
                    ? "media-overlay-track-changed"
                    : "media-overlay-play-pause-resolved";
            }

            string message = overlay.Message ?? string.Empty;
            if (message.Contains("unchanged", StringComparison.OrdinalIgnoreCase))
            {
                return "media-overlay-track-unchanged";
            }

            if (message.Contains("loading", StringComparison.OrdinalIgnoreCase))
            {
                return "media-overlay-track-loading";
            }

            if (message.Contains("No active media", StringComparison.OrdinalIgnoreCase))
            {
                return "media-overlay-no-session";
            }

            return command is MediaOverlayCommand.NextTrack or MediaOverlayCommand.PreviousTrack
                ? "media-overlay-track-fallback"
                : "media-overlay-play-pause-resolved";
        }

        private static bool IsCommandSendFailure(MediaOverlayCommand command, MediaOverlayResult overlay)
        {
            string? message = overlay.Message;
            if (overlay.Kind != MediaOverlayResultKind.PlainMessage || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return command switch
            {
                MediaOverlayCommand.PlayPause => string.Equals(message, "Play/pause failed", StringComparison.Ordinal),
                MediaOverlayCommand.NextTrack => string.Equals(message, "Next track failed", StringComparison.Ordinal),
                MediaOverlayCommand.PreviousTrack => string.Equals(message, "Previous track failed", StringComparison.Ordinal),
                _ => string.Equals(message, "Media command failed", StringComparison.Ordinal),
            };
        }
    }

    internal readonly record struct MediaOverlayPlayPauseResolutionResult(
        PlayPauseSnapshotResolution Resolution,
        MediaOverlayPlayPauseDiagnostics Diagnostics);
}
