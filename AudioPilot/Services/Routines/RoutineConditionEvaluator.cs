using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

internal static class RoutineConditionEvaluator
{
    internal static (string Code, string Reason)? Evaluate(RoutineConditions conditions, RoutineExecutionOperations operations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (conditions.Validate() is { } error) return ("routine-condition-invalid", error);
        if (conditions.TimeWindow is { } window && !window.Contains(operations.UtcNow()))
            return ("routine-condition-outside-time-window", "The current day or time is outside this routine’s allowed window.");
        if (conditions.Device is { } device)
        {
            bool? available = device.GetAvailability(operations.GetActiveDevices(device.Playback));
            if (!available.HasValue) return ("routine-condition-unavailable", "The required audio device could not be identified uniquely.");
            if (available.Value != conditions.DeviceAvailable)
                return conditions.DeviceAvailable
                    ? ("routine-condition-device-unavailable", "The required audio device is unavailable.")
                    : ("routine-condition-device-available", "The audio device required to be unavailable is available.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(conditions.RunningAppPath))
        {
            if (operations.IsApplicationRunning == null) return ("routine-condition-unavailable", "The required application could not be checked.");
            if (!operations.IsApplicationRunning(conditions.RunningAppPath)) return ("routine-condition-app-not-running", "The required application is not running.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(conditions.ConnectedNetwork))
        {
            var networks = operations.GetConnectedNetworks?.Invoke();
            if (networks == null) return ("routine-condition-unavailable", "The required network could not be checked.");
            if (!networks.Contains(conditions.ConnectedNetwork.Trim(), StringComparer.OrdinalIgnoreCase))
                return ("routine-condition-network-disconnected", "The required network is not connected.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}
