using AudioPilot.Services.UI.MediaOverlay;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayTrackNavigationRecoveryStateMachineTests
{
    [Fact]
    public void RecoveryStateMachine_AllowsNormalUnchangedAndLateLoadFlow()
    {
        var stateMachine = new MediaOverlayTrackNavigationRecoveryStateMachine();

        stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.UnchangedRecovery);
        stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.LateTrackLoadRecovery);
        stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.FinalEvaluation);
        stateMachine.Complete(TrackNavigationRecoveryOutcome.Loading);

        Assert.Equal(TrackNavigationRecoveryPhase.Completed, stateMachine.CurrentPhase);
        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, stateMachine.Outcome);
    }

    [Fact]
    public void RecoveryStateMachine_AllowsRepeatedLateLoadProbe()
    {
        var stateMachine = new MediaOverlayTrackNavigationRecoveryStateMachine();

        stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.LateTrackLoadRecovery);
        stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.LateTrackLoadRecovery);

        Assert.Equal(TrackNavigationRecoveryPhase.LateTrackLoadRecovery, stateMachine.CurrentPhase);
    }

    [Theory]
    [InlineData((int)TrackNavigationRecoveryPhase.SessionDropRecovery, (int)TrackNavigationRecoveryPhase.UnchangedRecovery)]
    [InlineData((int)TrackNavigationRecoveryPhase.FinalEvaluation, (int)TrackNavigationRecoveryPhase.LateTrackLoadRecovery)]
    [InlineData((int)TrackNavigationRecoveryPhase.Completed, (int)TrackNavigationRecoveryPhase.FinalEvaluation)]
    public void RecoveryStateMachine_RejectsInvalidBacktracking(
        int currentPhaseValue,
        int invalidNextPhaseValue)
    {
        TrackNavigationRecoveryPhase currentPhase = (TrackNavigationRecoveryPhase)currentPhaseValue;
        TrackNavigationRecoveryPhase invalidNextPhase = (TrackNavigationRecoveryPhase)invalidNextPhaseValue;
        var stateMachine = new MediaOverlayTrackNavigationRecoveryStateMachine();
        AdvanceFromInitial(stateMachine, currentPhase);

        Assert.Throws<InvalidOperationException>(() => stateMachine.AdvanceTo(invalidNextPhase));
    }

    private static void AdvanceFromInitial(
        MediaOverlayTrackNavigationRecoveryStateMachine stateMachine,
        TrackNavigationRecoveryPhase targetPhase)
    {
        switch (targetPhase)
        {
            case TrackNavigationRecoveryPhase.SessionDropRecovery:
                stateMachine.AdvanceTo(targetPhase);
                break;
            case TrackNavigationRecoveryPhase.FinalEvaluation:
                stateMachine.AdvanceTo(targetPhase);
                break;
            case TrackNavigationRecoveryPhase.Completed:
                stateMachine.Complete(TrackNavigationRecoveryOutcome.Unchanged);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetPhase));
        }
    }
}
