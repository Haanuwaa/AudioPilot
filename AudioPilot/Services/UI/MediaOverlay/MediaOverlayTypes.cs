using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Services.UI.MediaOverlay
{
    public enum MediaOverlayCommand
    {
        PlayPause,
        NextTrack,
        PreviousTrack,
    }

    public readonly record struct MediaOverlayResult(
        MediaOverlayResultKind Kind,
        string Header,
        string? Message,
        string? Title,
        string? Artist)
    {
        public bool ShowOverlay => Kind != MediaOverlayResultKind.Hidden;
        public bool UseTrackFormatting => Kind == MediaOverlayResultKind.TrackMessage;
        public bool IsPlainMessage => Kind == MediaOverlayResultKind.PlainMessage;
        public bool IsTrackMessage => Kind == MediaOverlayResultKind.TrackMessage;

        public static MediaOverlayResult Hidden => new(MediaOverlayResultKind.Hidden, string.Empty, null, null, null);

        public static MediaOverlayResult Plain(string message) => new(MediaOverlayResultKind.PlainMessage, string.Empty, message, null, null);

        public static MediaOverlayResult Track(string header, string title, string? artist) => new(MediaOverlayResultKind.TrackMessage, header, null, title, artist);
    }

    public enum MediaOverlayResultKind
    {
        Hidden,
        PlainMessage,
        TrackMessage,
    }

    internal readonly record struct MediaOverlayAlternateCandidateScore(
        int EvidenceScore,
        int QualityScore,
        bool NearStart,
        bool TimelineTransitionObserved,
        bool PositionMovedBackwardFromPre,
        bool SourceDiffersFromBaseline,
        bool SourceWasPresentPreCommand,
        bool HasRecentSignalForSource);

    internal enum MediaEventAssistKind
    {
        None,
        CurrentSessionChanged,
        SessionsChanged,
        TimelinePropertiesChanged,
        PlaybackInfoChanged,
        MediaPropertiesChanged,
    }

    internal readonly record struct MediaEventAssistOutcome(
        bool ObservedEvent,
        string? SignaledSourceAppUserModelId,
        MediaEventAssistKind EventKind = MediaEventAssistKind.None);

    internal enum PreferredSourceCandidateVerdict
    {
        Accept,
        Reject,
        PendingCorroboration,
    }

    internal enum PreferredSourceCandidateReason
    {
        NoReferenceContext,
        MissingOrDifferentSource,
        SameTrack,
        TimelineTransition,
        PlayingNearStart,
        PlayingWithoutMetadata,
        PausedNearStartWhileReferencePaused,
        PausedSibling,
        AmbiguousSameSourceNearStart,
        FarPositionDelta,
        MetadataFallback,
        RecentSignalCorroborated,
        BrowserConvergenceCorroborated,
    }

    internal readonly record struct PreferredSourceCandidateDecision(
        PreferredSourceCandidateVerdict Verdict,
        PreferredSourceCandidateReason Reason)
    {
        public bool IsAccepted => Verdict == PreferredSourceCandidateVerdict.Accept;
        public bool IsRejected => Verdict == PreferredSourceCandidateVerdict.Reject;
        public bool IsPending => Verdict == PreferredSourceCandidateVerdict.PendingCorroboration;

        public static PreferredSourceCandidateDecision Accept(PreferredSourceCandidateReason reason) =>
            new(PreferredSourceCandidateVerdict.Accept, reason);

        public static PreferredSourceCandidateDecision Reject(PreferredSourceCandidateReason reason) =>
            new(PreferredSourceCandidateVerdict.Reject, reason);

        public static PreferredSourceCandidateDecision Pending(PreferredSourceCandidateReason reason) =>
            new(PreferredSourceCandidateVerdict.PendingCorroboration, reason);
    }

    internal readonly record struct PreferredSourceCandidateResolution(
        PreferredSourceCandidateDecision InitialDecision,
        PreferredSourceCandidateDecision FinalDecision)
    {
        public bool IsAccepted => FinalDecision.IsAccepted;
        public bool IsPending => FinalDecision.IsPending;
        public bool WasUpgraded => !InitialDecision.IsAccepted && FinalDecision.IsAccepted;
    }

    internal enum BrowserPendingCandidateReasonClass
    {
        None,
        AmbiguousNearStart,
        MetadataPending,
        FarPosition,
        PausedSibling,
        RejectedOther,
    }

    internal readonly record struct BrowserSameSourceEvidence(
        BrowserPendingCandidateReasonClass ReasonClass,
        string Fingerprint,
        string TrackFingerprint,
        long? PositionSeconds,
        bool HasRelevantRecentSignal);

    internal enum BrowserSameSourcePromotionKind
    {
        None,
        StableRepetition,
        FarPositionCorrection,
        PostConflictRecorroboration,
    }

    internal enum BrowserSameSourceRivalFreshness
    {
        None,
        Active,
        Reinforced,
        Stale,
    }

    internal readonly record struct BrowserSameSourceWinnerElectionResult(
        bool HasWinner,
        bool WinnerIsCurrentCandidate,
        string? WinningTrackFingerprint,
        BrowserPendingCandidateReasonClass WinningReasonClass,
        BrowserSameSourcePromotionKind PromotionKind,
        int ActiveRivalCount,
        int ReinforcedRivalCount,
        int StaleRivalCount,
        string RivalReasonClasses,
        bool StaleRivalIgnored);

    internal readonly record struct BrowserSameSourceLedgerObservation(
        BrowserPendingCandidateReasonClass ReasonClass,
        bool StableObservationCorroborated,
        bool FarPositionCorrectionCorroborated,
        bool PostConflictRecorroborated = false,
        bool ConflictObserved = false,
        bool ConflictActive = false,
        int DistinctCandidateCount = 0,
        int ActiveRivalCount = 0,
        int ReinforcedRivalCount = 0,
        int StaleRivalCount = 0,
        string RivalReasonClasses = "<none>",
        bool StaleRivalIgnored = false,
        BrowserSameSourceWinnerElectionResult WinnerElection = default)
    {
        public bool HasConverged => StableObservationCorroborated
            || FarPositionCorrectionCorroborated
            || PostConflictRecorroborated;

        public BrowserSameSourcePromotionKind PromotionKind =>
            FarPositionCorrectionCorroborated
                ? BrowserSameSourcePromotionKind.FarPositionCorrection
                : PostConflictRecorroborated
                ? BrowserSameSourcePromotionKind.PostConflictRecorroboration
                : StableObservationCorroborated
                ? BrowserSameSourcePromotionKind.StableRepetition
                : BrowserSameSourcePromotionKind.None;
    }

    internal readonly record struct BrowserSameSourceCommandSummary(
        bool ConflictObserved = false,
        bool ConflictActive = false,
        int DistinctCandidateCount = 0,
        int ActiveRivalCount = 0,
        int ReinforcedRivalCount = 0,
        int StaleRivalCount = 0,
        bool HasBlockedRivalEvidence = false,
        bool HasPendingCandidateEvidence = false,
        bool HasPendingNonWinnerRivalEvidence = false,
        string RivalReasonClasses = "<none>",
        BrowserSameSourceWinnerElectionResult WinnerElection = default)
    {
        public bool HasRivalEvidence => DistinctCandidateCount > 1;
    }

    public readonly record struct MediaOverlaySessionSnapshot(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? PlaybackStatus,
        string? Title,
        string? Artist,
        string? AlbumTitle,
        string? SourceAppUserModelId,
        long? PositionSeconds)
    {
        public static MediaOverlaySessionSnapshot Empty => new(null, null, null, null, null, null);
    }

    internal readonly record struct PlayPauseSnapshotResolution(
        MediaOverlaySessionSnapshot Baseline,
        MediaOverlaySessionSnapshot Snapshot)
    {
        public static PlayPauseSnapshotResolution Empty => new(MediaOverlaySessionSnapshot.Empty, MediaOverlaySessionSnapshot.Empty);
    }

    internal readonly record struct MediaOverlayCommandPolicy(
        bool PreferBaselineSourceForSampling,
        bool AllowSingleCandidateMetadataChangeFallback,
        bool AcceptUnchangedTrackFallback,
        bool AllowRecoveredSourceOverride)
    {
        public static MediaOverlayCommandPolicy For(MediaOverlayCommand command)
        {
            return command switch
            {
                MediaOverlayCommand.PlayPause => new MediaOverlayCommandPolicy(
                    PreferBaselineSourceForSampling: true,
                    AllowSingleCandidateMetadataChangeFallback: true,
                    AcceptUnchangedTrackFallback: true,
                    AllowRecoveredSourceOverride: false),
                MediaOverlayCommand.NextTrack or MediaOverlayCommand.PreviousTrack => new MediaOverlayCommandPolicy(
                    PreferBaselineSourceForSampling: true,
                    AllowSingleCandidateMetadataChangeFallback: true,
                    AcceptUnchangedTrackFallback: false,
                    AllowRecoveredSourceOverride: true),
                _ => new MediaOverlayCommandPolicy(
                    PreferBaselineSourceForSampling: false,
                    AllowSingleCandidateMetadataChangeFallback: false,
                    AcceptUnchangedTrackFallback: true,
                    AllowRecoveredSourceOverride: false),
            };
        }
    }

    internal readonly record struct MediaOverlayTimingProfile(
        int InitialSettleDelayMs,
        int RetryDelayMs,
        int MaxAttempts,
        int MaxCaptureDurationMs,
        int ExtraAttemptsAfterSessionDrop,
        int SessionDropRecoveryInitialDelayMs,
        int SessionDropRecoveryRetryDelayMs,
        int SessionDropRecoveryAttempts,
        int SessionDropTrackLoadRecoveryInitialDelayMs,
        int SessionDropTrackLoadRecoveryRetryDelayMs,
        int SessionDropTrackLoadRecoveryAttempts,
        int UnchangedRecoveryInitialDelayMs,
        int UnchangedRecoveryRetryDelayMs,
        int UnchangedRecoveryAttempts,
        int FirstCommandGraceWindowMs,
        int GraceRecoveryInitialDelayMs,
        int GraceRecoveryRetryDelayMs,
        int GraceRecoveryAttempts,
        int PlayPauseSettleInitialDelayMs,
        int PlayPauseSettleRetryDelayMs,
        int PlayPauseSettleAttempts,
        int TrackLoadRecoveryInitialDelayMs,
        int TrackLoadRecoveryRetryDelayMs,
        int TrackLoadRecoveryAttempts,
        int StagnantTrackRecoveryInitialDelayMs,
        int StagnantTrackRecoveryRetryDelayMs,
        int StagnantTrackRecoveryAttempts)
    {
        public static MediaOverlayTimingProfile Default => new(
            AppConstants.MediaOverlay.InitialSettleDelayMs,
            AppConstants.MediaOverlay.RetryDelayMs,
            AppConstants.MediaOverlay.MaxAttempts,
            AppConstants.MediaOverlay.MaxCaptureDurationMs,
            AppConstants.MediaOverlay.ExtraAttemptsAfterSessionDrop,
            AppConstants.MediaOverlay.SessionDropRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.SessionDropRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.SessionDropRecoveryAttempts,
            AppConstants.MediaOverlay.SessionDropTrackLoadRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.SessionDropTrackLoadRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.SessionDropTrackLoadRecoveryAttempts,
            AppConstants.MediaOverlay.UnchangedRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.UnchangedRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.UnchangedRecoveryAttempts,
            AppConstants.MediaOverlay.FirstCommandGraceWindowMs,
            AppConstants.MediaOverlay.GraceRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.GraceRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.GraceRecoveryAttempts,
            AppConstants.MediaOverlay.PlayPauseSettleInitialDelayMs,
            AppConstants.MediaOverlay.PlayPauseSettleRetryDelayMs,
            AppConstants.MediaOverlay.PlayPauseSettleAttempts,
            AppConstants.MediaOverlay.TrackLoadRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.TrackLoadRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.TrackLoadRecoveryAttempts,
            AppConstants.MediaOverlay.StagnantTrackRecoveryInitialDelayMs,
            AppConstants.MediaOverlay.StagnantTrackRecoveryRetryDelayMs,
            AppConstants.MediaOverlay.StagnantTrackRecoveryAttempts);
    }

    internal readonly record struct SnapshotCaptureResult(
        MediaOverlaySessionSnapshot Snapshot,
        bool SawSessionDrop,
        TrackNavigationRecoveryDisposition RecoveryDisposition,
        TrackNavigationChangeKind ChangeKind = TrackNavigationChangeKind.TrackChanged,
        bool UsedRecoveredAlternateSource = false)
    {
        public TrackNavigationRecoveryOutcome Outcome => RecoveryDisposition.Outcome;
        public TrackNavigationFallbackClassification FallbackClassification => RecoveryDisposition.FallbackClassification;
        public bool SessionDropStillMissing => RecoveryDisposition.FallbackClassification == TrackNavigationFallbackClassification.Missing;
        public bool MetadataPendingAfterSessionDrop => RecoveryDisposition.FallbackClassification == TrackNavigationFallbackClassification.MetadataPending;
        public bool PendingTrackChangeAfterSessionDrop => RecoveryDisposition.Outcome == TrackNavigationRecoveryOutcome.Loading
            && RecoveryDisposition.FallbackClassification == TrackNavigationFallbackClassification.Loading;
    }

    internal readonly record struct MediaOverlaySourceMemorySelection(
        string? StickySource,
        string? RecoveredSource);
}
