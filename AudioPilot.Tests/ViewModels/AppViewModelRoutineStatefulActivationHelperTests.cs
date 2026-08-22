using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineStatefulActivationHelperTests
{
    [Fact]
    public async Task ExecuteAsync_RegistersStatefulSession_WhenExecutionSucceeds()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelRoutineStatefulActivationHelperTests), "routine-stateful-activation.log");
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            TriggerKind = RoutineTriggerKind.Application,
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };

        int registerCalls = 0;
        AppViewModel.RoutineAudioRestoreSnapshot snapshot = new("prev-out", "Old Speakers", "", "");

        RoutineStatefulActivationExecutionResult result = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
            routine,
            rootProcessId: 321,
            showOverlay: true,
            executionSource: "app-start",
            loggerScope.Logger,
            static _ => new AppViewModel.RoutineAudioRestoreSnapshot("prev-out", "Old Speakers", "", ""),
            static (_, _, _, _) => Task.FromResult(new AppViewModel.RoutineExecutionResult(
                Success: true,
                OutputDeviceName: "Speakers",
                InputDeviceName: null,
                AwaitingAppCompletion: false,
                AppOutputApplied: true,
                AppInputApplied: false)),
            (_, processId, restoreSnapshot) =>
            {
                registerCalls++;
                Assert.Equal(321, processId);
                Assert.Equal(snapshot, restoreSnapshot);
            },
            static (audioRoutine, source, showOverlay, processId) => $"routineId={audioRoutine.Id} source={source} showOverlay={showOverlay} processId={processId}",
            static executionResult => $"success={executionResult.Success}");

        Assert.True(result.Result.Success);
        Assert.True(result.HasRestoreSnapshot);
        Assert.Equal(1, registerCalls);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRegistration_WhenExecutionFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelRoutineStatefulActivationHelperTests), "routine-stateful-activation-failure.log");
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            TriggerKind = RoutineTriggerKind.Application,
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };

        int registerCalls = 0;

        RoutineStatefulActivationExecutionResult result = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
            routine,
            rootProcessId: 321,
            showOverlay: true,
            executionSource: "app-start",
            loggerScope.Logger,
            static _ => null,
            static (_, _, _, _) => Task.FromResult(new AppViewModel.RoutineExecutionResult(
                Success: false,
                OutputDeviceName: null,
                InputDeviceName: null,
                AwaitingAppCompletion: false,
                AppOutputApplied: false,
                AppInputApplied: false)),
            (_, _, _) => registerCalls++,
            static (audioRoutine, source, showOverlay, processId) => $"routineId={audioRoutine.Id} source={source} showOverlay={showOverlay} processId={processId}",
            static executionResult => $"success={executionResult.Success}");

        Assert.False(result.Result.Success);
        Assert.False(result.HasRestoreSnapshot);
        Assert.Equal(0, registerCalls);
    }
}
