using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineStatefulActivationHelperTests
{
    [Fact]
    public async Task ManualExecution_DoesNotCaptureOrReplaceAutomaticRestoration()
    {
        using var logger = new TestLoggerScope(nameof(AppViewModelRoutineStatefulActivationHelperTests), "manual.log");
        var routine = new AudioRoutine { TriggerKind = RoutineTriggerKind.Application, MasterVolumePercent = 50 };
        var execution = new RoutineExecutionResult(true, null, null, false, false, false);
        var result = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(routine, 42, true, "hotkey",
            logger.Logger, _ => throw new InvalidOperationException("Manual execution captured restoration."),
            (_, _, processId, _) =>
            {
                Assert.Equal(42, processId);
                return Task.FromResult(execution);
            }, (_, _, _) => throw new InvalidOperationException("Manual execution registered a lifetime."),
            (_, _, _, _) => "manual", _ => "success", trackLifetime: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(execution, result.Result);
        Assert.False(result.HasRestoreSnapshot);
    }

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
        RoutineAudioRestoreSnapshot snapshot = new("prev-out", "Old Speakers", "", "");

        RoutineStatefulActivationExecutionResult result = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
            routine,
            rootProcessId: 321,
            showOverlay: true,
            executionSource: "app-start",
            loggerScope.Logger,
            static _ => new RoutineAudioRestoreSnapshot("prev-out", "Old Speakers", "", ""),
            static (_, _, _, _) => Task.FromResult(new RoutineExecutionResult(
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
            static executionResult => $"success={executionResult.Success}", cancellationToken: TestContext.Current.CancellationToken);

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
            static (_, _, _, _) => Task.FromResult(new RoutineExecutionResult(
                Success: false,
                OutputDeviceName: null,
                InputDeviceName: null,
                AwaitingAppCompletion: false,
                AppOutputApplied: false,
                AppInputApplied: false)),
            (_, _, _) => registerCalls++,
            static (audioRoutine, source, showOverlay, processId) => $"routineId={audioRoutine.Id} source={source} showOverlay={showOverlay} processId={processId}",
            static executionResult => $"success={executionResult.Success}", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Result.Success);
        Assert.False(result.HasRestoreSnapshot);
        Assert.Equal(0, registerCalls);
    }
}
