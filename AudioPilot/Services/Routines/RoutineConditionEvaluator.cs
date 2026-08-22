using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

internal static class RoutineConditionEvaluator
{
    internal static (string Code, string Reason)? Evaluate(RoutineConditions conditions, RoutineExecutionOperations operations, CancellationToken cancellationToken)
        => Evaluate(conditions, new RoutineReadOperations(operations.GetActiveDevices)
        {
            UtcNow = operations.UtcNow,
            IsApplicationRunning = operations.IsApplicationRunning,
            GetConnectedNetworks = operations.GetConnectedNetworks,
            GetDefaultDevice = operations.GetDefaultDevice,
        }, cancellationToken);

    internal static (string Code, string Reason)? Evaluate(RoutineConditions conditions, RoutineReadOperations operations, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conditions.Validate() is { } error) return ("routine-condition-invalid", error);
            if (conditions.TimeWindow is { } window && !window.Contains(operations.UtcNow()))
                return ("routine-condition-outside-time-window", "The current day or time is outside this routine’s allowed window.");
            if (conditions.Device is { } device)
            {
                if (conditions.DeviceMustBeDefault)
                {
                    if (operations.GetDefaultDevice == null) return ("routine-condition-unavailable", "The current default device could not be checked.");
                    var current = operations.GetDefaultDevice(device.Playback);
                    if (current == null || string.IsNullOrEmpty(current.Id)) return ("routine-condition-unavailable", "The current default device could not be checked.");
                    if (string.IsNullOrEmpty(device.Id) || !string.Equals(device.Id, current.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        var (resolved, ambiguous) = device.Resolve(operations.GetActiveDevices(device.Playback));
                        if (ambiguous) return ("routine-condition-unavailable", "The required audio device could not be identified uniquely.");
                        if (resolved == null || !string.Equals(resolved.Id, current.Id, StringComparison.OrdinalIgnoreCase))
                            return ("routine-condition-device-not-default", "The required audio device is not the current default.");
                    }
                }
                else
                {
                    bool? available = device.GetAvailability(operations.GetActiveDevices(device.Playback));
                    if (!available.HasValue) return ("routine-condition-unavailable", "The required audio device could not be identified uniquely.");
                    if (available.Value != conditions.DeviceAvailable)
                        return conditions.DeviceAvailable
                            ? ("routine-condition-device-unavailable", "The required audio device is unavailable.")
                            : ("routine-condition-device-available", "The audio device required to be unavailable is available.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(conditions.RunningAppPath))
            {
                if (operations.IsApplicationRunning == null) return ("routine-condition-unavailable", "The required application could not be checked.");
                bool? running = operations.IsApplicationRunning(conditions.RunningAppPath);
                if (!running.HasValue) return ("routine-condition-unavailable", "The application state could not be determined reliably.");
                if (running.Value != conditions.ApplicationMustBeRunning)
                    return conditions.ApplicationMustBeRunning ? ("routine-condition-app-not-running", "The required application is not running.")
                        : ("routine-condition-app-running", "The application required to be stopped is running.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(conditions.ConnectedNetwork))
            {
                var networks = operations.GetConnectedNetworks?.Invoke();
                if (networks == null) return ("routine-condition-unavailable", "The required network could not be checked.");
                if (networks.Contains(conditions.ConnectedNetwork.Trim(), StringComparer.OrdinalIgnoreCase) != conditions.NetworkMustBeConnected)
                    return conditions.NetworkMustBeConnected ? ("routine-condition-network-disconnected", "The required network is not connected.")
                        : ("routine-condition-network-connected", "The network required to be disconnected is connected.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ("routine-condition-unavailable", "A required state could not be checked reliably."); }
    }
}
