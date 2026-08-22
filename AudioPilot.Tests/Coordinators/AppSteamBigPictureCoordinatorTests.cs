using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSteamBigPictureCoordinatorTests
{
    [Theory]
    [InlineData(true, false, 1, false, true)]
    [InlineData(true, false, 0, true, true)]
    [InlineData(true, false, 0, false, false)]
    [InlineData(false, false, 1, true, false)]
    [InlineData(true, true, 1, true, false)]
    public void BuildLoopContext_ReturnsExpectedMonitorState(
        bool monitoringEnabled,
        bool isCleaningUp,
        int watchedRoutineCount,
        bool hasActiveSessions,
        bool expectedShouldMonitor)
    {
        List<AudioRoutine> watchedRoutines = [.. Enumerable.Range(1, watchedRoutineCount)
            .Select(index => new AudioRoutine
            {
                Id = $"routine-{index}",
                Name = $"Routine {index}",
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = $"out-{index}",
            })];

        SteamBigPictureLoopContext context = AppSteamBigPictureCoordinator.BuildLoopContext(
            monitoringEnabled,
            isCleaningUp,
            watchedRoutines,
            hasActiveSessions);

        Assert.Equal(expectedShouldMonitor, context.ShouldMonitor);
        Assert.Equal(expectedShouldMonitor ? watchedRoutineCount : 0, context.WatchedRoutines.Count);
    }

    [Fact]
    public void ResolveStateChange_ReturnsNoAction_WhenStateDidNotChange()
    {
        SteamBigPictureStateChangeResult result = AppSteamBigPictureCoordinator.ResolveStateChange(
            previousDetected: true,
            isSteamBigPictureActive: true,
            watchedRoutines: []);

        Assert.True(result.NextDetectedState);
        Assert.False(result.StateChanged);
        Assert.Equal(SteamBigPictureStateChangeAction.None, result.Action);
        Assert.Empty(result.ActivationRoutines);
    }

    [Fact]
    public void ResolveStateChange_ReturnsDeactivate_OnFallingEdge()
    {
        SteamBigPictureStateChangeResult result = AppSteamBigPictureCoordinator.ResolveStateChange(
            previousDetected: true,
            isSteamBigPictureActive: false,
            watchedRoutines: []);

        Assert.False(result.NextDetectedState);
        Assert.True(result.StateChanged);
        Assert.Equal(SteamBigPictureStateChangeAction.Deactivate, result.Action);
        Assert.Empty(result.ActivationRoutines);
    }

    [Fact]
    public void ResolveStateChange_ReturnsEligibleActivationRoutines_OnRisingEdge()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new()
            {
                Id = "eligible-output",
                Name = "Eligible Output",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "eligible-input",
                Name = "Eligible Input",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                InputDeviceId = "in-1",
                InputDeviceName = "Mic",
            },
            new()
            {
                Id = "eligible-volume",
                Name = "Eligible Volume",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                MasterVolumePercent = 40,
            },
            new()
            {
                Id = "disabled",
                Name = "Disabled",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-2",
            },
            new()
            {
                Id = "targetless",
                Name = "Targetless",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
            }
        ];

        SteamBigPictureStateChangeResult result = AppSteamBigPictureCoordinator.ResolveStateChange(
            previousDetected: false,
            isSteamBigPictureActive: true,
            watchedRoutines);

        Assert.True(result.NextDetectedState);
        Assert.True(result.StateChanged);
        Assert.Equal(SteamBigPictureStateChangeAction.Activate, result.Action);
        Assert.Equal(["eligible-output", "eligible-input", "eligible-volume"], [.. result.ActivationRoutines.Select(static routine => routine.Id)]);
        Assert.All(result.ActivationRoutines, routine => Assert.NotSame(routine, watchedRoutines.First(source => source.Id == routine.Id)));
    }

    [Fact]
    public void ResolveSignalEvaluation_QueuesConfirmationCheck_WhenCloseSignalStillReadsActive()
    {
        SteamBigPictureSignalEvaluationDecision decision = AppSteamBigPictureCoordinator.ResolveSignalEvaluation(
            previousDetected: true,
            isSteamBigPictureActive: true,
            watchedRoutines: [],
            allowConfirmationCheck: true);

        Assert.False(decision.StateChange.StateChanged);
        Assert.True(decision.ShouldQueueConfirmationCheck);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void ResolveSignalEvaluation_SkipsConfirmationCheck_WhenNotNeeded(
        bool previousDetected,
        bool isSteamBigPictureActive,
        bool allowConfirmationCheck,
        bool expected)
    {
        SteamBigPictureSignalEvaluationDecision decision = AppSteamBigPictureCoordinator.ResolveSignalEvaluation(
            previousDetected,
            isSteamBigPictureActive,
            watchedRoutines: [],
            allowConfirmationCheck);

        Assert.Equal(expected, decision.ShouldQueueConfirmationCheck);
    }
}
