using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineDirectSwitchLogHelper
{
    internal static string BuildStartedMessage(AudioRoutine routine, string flow, string opId, int? applicationProcessId, string? currentDeviceName = null)
    {
        string message = $"routine-{flow}-switch-started | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow, opId, applicationProcessId, perAppRouting: false)}";
        return string.IsNullOrWhiteSpace(currentDeviceName)
            ? message
            : $"{message} currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }

    internal static string BuildCompletedMessage(AudioRoutine routine, string flow, string opId, int? applicationProcessId, bool success, string? deviceName)
    {
        return $"routine-{flow}-switch-completed | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow, opId, applicationProcessId, perAppRouting: false)} success={success} deviceName={AppViewModel.FormatRoutineLogDevice(deviceName)}";
    }

    internal static string BuildOutputSkippedNoDefaultMessage(AudioRoutine routine, int? applicationProcessId)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-output");
        return $"routine-output-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "output", opId, applicationProcessId, perAppRouting: false)} reason=no-default-playback-device";
    }

    internal static string BuildOutputAlreadyTargetMessage(AudioRoutine routine, int? applicationProcessId, string? currentDeviceName)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-output");
        return $"routine-output-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "output", opId, applicationProcessId, perAppRouting: false)} reason=already-target currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }

    internal static string BuildInputSkippedNoDefaultMessage(AudioRoutine routine, int? applicationProcessId)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-input");
        return $"routine-input-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "input", opId, applicationProcessId, perAppRouting: false)} reason=no-default-recording-device";
    }

    internal static string BuildInputAlreadyTargetMessage(AudioRoutine routine, int? applicationProcessId, string? currentDeviceName)
    {
        string opId = AppViewModel.CreateRoutineOperationId("routine-input");
        return $"routine-input-switch-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: "input", opId, applicationProcessId, perAppRouting: false)} reason=already-target currentDevice={AppViewModel.FormatRoutineLogDevice(currentDeviceName)}";
    }
}
