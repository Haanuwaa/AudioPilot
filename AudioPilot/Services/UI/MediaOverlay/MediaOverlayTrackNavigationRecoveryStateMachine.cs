namespace AudioPilot.Services.UI.MediaOverlay
{
    internal enum TrackNavigationRecoveryPhase
    {
        InitialSampling,
        SessionDropRecovery,
        UnchangedRecovery,
        LateTrackLoadRecovery,
        FinalEvaluation,
        Completed,
    }

    internal sealed class MediaOverlayTrackNavigationRecoveryStateMachine
    {
        public TrackNavigationRecoveryPhase CurrentPhase { get; private set; } = TrackNavigationRecoveryPhase.InitialSampling;
        public TrackNavigationRecoveryOutcome? Outcome { get; private set; }

        public void AdvanceTo(TrackNavigationRecoveryPhase nextPhase)
        {
            if (!CanTransition(CurrentPhase, nextPhase))
            {
                throw new InvalidOperationException($"Invalid media recovery transition: {CurrentPhase} -> {nextPhase}.");
            }

            CurrentPhase = nextPhase;
        }

        public void Complete(TrackNavigationRecoveryOutcome outcome)
        {
            AdvanceTo(TrackNavigationRecoveryPhase.Completed);
            Outcome = outcome;
        }

        internal static bool CanTransition(TrackNavigationRecoveryPhase currentPhase, TrackNavigationRecoveryPhase nextPhase)
        {
            return currentPhase switch
            {
                TrackNavigationRecoveryPhase.InitialSampling => nextPhase is
                    TrackNavigationRecoveryPhase.SessionDropRecovery or
                    TrackNavigationRecoveryPhase.UnchangedRecovery or
                    TrackNavigationRecoveryPhase.LateTrackLoadRecovery or
                    TrackNavigationRecoveryPhase.FinalEvaluation or
                    TrackNavigationRecoveryPhase.Completed,
                TrackNavigationRecoveryPhase.SessionDropRecovery => nextPhase is
                    TrackNavigationRecoveryPhase.LateTrackLoadRecovery or
                    TrackNavigationRecoveryPhase.FinalEvaluation or
                    TrackNavigationRecoveryPhase.Completed,
                TrackNavigationRecoveryPhase.UnchangedRecovery => nextPhase is
                    TrackNavigationRecoveryPhase.LateTrackLoadRecovery or
                    TrackNavigationRecoveryPhase.FinalEvaluation or
                    TrackNavigationRecoveryPhase.Completed,
                TrackNavigationRecoveryPhase.LateTrackLoadRecovery => nextPhase is
                    TrackNavigationRecoveryPhase.LateTrackLoadRecovery or
                    TrackNavigationRecoveryPhase.FinalEvaluation or
                    TrackNavigationRecoveryPhase.Completed,
                TrackNavigationRecoveryPhase.FinalEvaluation => nextPhase == TrackNavigationRecoveryPhase.Completed,
                TrackNavigationRecoveryPhase.Completed => false,
                _ => false,
            };
        }
    }
}
