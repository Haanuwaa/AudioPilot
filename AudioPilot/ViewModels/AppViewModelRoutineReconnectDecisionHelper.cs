using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineReconnectDecisionHelper
{
    internal readonly record struct RoutineReconnectDecision(
        bool ShouldAttempt,
        string? SkipReason,
        CycleDevice? ConfiguredDevice);

    public static RoutineReconnectDecision Evaluate(string? deviceId, string? deviceName, bool alreadyActive)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new RoutineReconnectDecision(false, "no-target-device-id", null);
        }

        var configuredDevice = new CycleDevice
        {
            Id = deviceId,
            Name = deviceName ?? string.Empty,
        };

        if (alreadyActive)
        {
            return new RoutineReconnectDecision(false, "already-active", configuredDevice);
        }

        return new RoutineReconnectDecision(true, null, configuredDevice);
    }
}
