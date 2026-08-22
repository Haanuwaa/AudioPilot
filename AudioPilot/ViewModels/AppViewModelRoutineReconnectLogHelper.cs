using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineReconnectLogHelper
{
    internal static string BuildSkippedMessage(AudioRoutine routine, BluetoothReconnectDeviceKind deviceKind, string opId, int? applicationProcessId, string reason)
    {
        return $"routine-target-reconnect-skipped | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: ResolveFlow(deviceKind), opId, applicationProcessId, perAppRouting: routine.SwitchOutputPerApp)} kind={deviceKind} reason={reason}";
    }

    internal static string BuildStartedMessage(AudioRoutine routine, BluetoothReconnectDeviceKind deviceKind, string opId, int? applicationProcessId)
    {
        return $"routine-target-reconnect-started | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: ResolveFlow(deviceKind), opId, applicationProcessId, perAppRouting: routine.SwitchOutputPerApp)} kind={deviceKind}";
    }

    internal static string BuildCompletedMessage(AudioRoutine routine, BluetoothReconnectDeviceKind deviceKind, string opId, int? applicationProcessId, BluetoothReconnectAttemptResult reconnectResult)
    {
        return $"routine-target-reconnect-completed | {AppViewModel.BuildRoutineDeviceSwitchLogContext(routine, flow: ResolveFlow(deviceKind), opId, applicationProcessId, perAppRouting: routine.SwitchOutputPerApp)} kind={deviceKind} attempted={reconnectResult.Attempted} connected={reconnectResult.Connected} attempts={reconnectResult.Attempts} cooldownSkips={reconnectResult.CooldownSkips}";
    }

    private static string ResolveFlow(BluetoothReconnectDeviceKind deviceKind)
    {
        return deviceKind == BluetoothReconnectDeviceKind.Output ? "output" : "input";
    }
}
