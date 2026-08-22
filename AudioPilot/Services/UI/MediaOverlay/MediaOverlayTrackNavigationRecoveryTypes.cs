using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal readonly record struct TrackNavigationStreakDecision(
        bool ForceAlternateAfterStreak,
        int UnchangedStreak,
        int StagnantPositionStreak);

    internal enum TrackNavigationRecoveryOutcome
    {
        Changed,
        Loading,
        Unchanged,
    }

    internal enum TrackNavigationFallbackClassification
    {
        Confirmed,
        Missing,
        MetadataPending,
        Loading,
        Unchanged,
    }

    internal enum TrackNavigationChangeKind
    {
        TrackChanged,
        SameTrackRestart,
        SourceSwitched,
    }

    internal readonly record struct TrackNavigationRecoveryDisposition(
        TrackNavigationRecoveryOutcome Outcome,
        TrackNavigationFallbackClassification FallbackClassification)
    {
        public static TrackNavigationRecoveryDisposition Changed =>
            new(TrackNavigationRecoveryOutcome.Changed, TrackNavigationFallbackClassification.Confirmed);

        public static TrackNavigationRecoveryDisposition Loading(TrackNavigationFallbackClassification fallbackClassification) =>
            new(TrackNavigationRecoveryOutcome.Loading, fallbackClassification);

        public static TrackNavigationRecoveryDisposition Unchanged =>
            new(TrackNavigationRecoveryOutcome.Unchanged, TrackNavigationFallbackClassification.Unchanged);
    }

    internal readonly record struct TrackNavigationRecoveryContext(
        MediaOverlayCommand Command,
        SessionSnapshot OriginalBaseline,
        SessionSnapshot Baseline,
        SessionSnapshot EffectiveBaseline,
        string? PreferredSourceForCommand,
        Dictionary<string, SessionSnapshot> PreCommandSnapshots,
        MediaOverlayCommandPolicy CommandPolicy,
        bool PreferredSourceIsCommandTarget,
        bool IsInGraceWindow,
        long CommandSequence,
        DateTimeOffset DeadlineUtc);

    internal readonly record struct InitialTrackNavigationSamplingResult(
        SessionSnapshot Latest,
        SessionSnapshot Fallback,
        SessionSnapshot ResolvedSnapshot,
        bool SawSessionDrop,
        bool Completed,
        TrackNavigationChangeKind ChangeKind = TrackNavigationChangeKind.TrackChanged);

    internal readonly record struct SessionDropResolutionResult(
        SessionSnapshot Snapshot,
        int PollAttempts,
        bool EndedByDeadline,
        bool UsedExtendedTrackLoadRecovery,
        int ElapsedMs);

    internal readonly record struct UnchangedRecoveryProbeResult(
        SessionSnapshot Snapshot,
        bool StableBaselineRepeated);

    internal readonly record struct TrackLoadRecoveryResult(
        SessionSnapshot Snapshot,
        bool Attempted,
        bool EndedByDeadline);
}
