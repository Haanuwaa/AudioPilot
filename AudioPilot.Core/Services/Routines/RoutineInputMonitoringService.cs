using AudioPilot.Logging;

namespace AudioPilot.Services.Routines;

internal readonly record struct RoutineInputMonitoringApplicationResult(
    bool Success,
    IRoutineAudioChange? Change = null,
    string? FailureDetail = null,
    bool Changed = false);

/// <summary>Applies input monitoring ("Listen to this device") state and restores the prior state on deactivation if unchanged externally.</summary>
internal static class RoutineInputMonitoringService
{
    internal static RoutineInputMonitoringApplicationResult Apply(
        AudioDeviceService audio,
        Logger logger,
        bool enable,
        bool captureRestore,
        string operationId,
        Func<(string? DeviceId, string? DeviceName, string? StableId)>? getMonitorOutput = null)
    {
        try
        {
            bool hasOriginal = audio.TryGetCurrentInputListenState(out bool original, out _);
            if (hasOriginal && original == enable)
            {
                return new RoutineInputMonitoringApplicationResult(true, Changed: false);
            }

            (string? outputId, string? outputName, string? outputStableId) = getMonitorOutput?.Invoke() ?? (null, null, null);
            InputListenStateChangeResult applied = audio.SetCurrentInputListenState(outputId, outputName, enable, outputStableId);
            if (!applied.Applied)
            {
                string detail = string.IsNullOrWhiteSpace(applied.Error)
                    ? "Input monitoring could not be changed."
                    : $"Input monitoring could not be changed ({applied.Error}).";
                logger.Warning("RoutineInputMonitoring", () => $"routine-input-monitoring-failed | target={enable} error={applied.Error} opId={operationId}");
                return new RoutineInputMonitoringApplicationResult(false, FailureDetail: detail);
            }

            logger.Debug("RoutineInputMonitoring", () => $"routine-input-monitoring-applied | enabled={enable} verified={applied.Verified} opId={operationId}");
            IRoutineAudioChange? restoration = captureRestore && hasOriginal
                ? new InputMonitoringChange(audio, logger, applied.InputDeviceId, original, enable, getMonitorOutput, operationId)
                : null;
            return new RoutineInputMonitoringApplicationResult(true, restoration, Changed: true);
        }
        catch (Exception ex)
        {
            logger.Warning("RoutineInputMonitoring", $"routine-input-monitoring-threw | opId={operationId}", nameof(Apply), ex);
            return new RoutineInputMonitoringApplicationResult(false, FailureDetail: "Input monitoring could not be changed.");
        }
    }

    private sealed class InputMonitoringChange(
        AudioDeviceService audio,
        Logger logger,
        string? inputDeviceId,
        bool original,
        bool applied,
        Func<(string? DeviceId, string? DeviceName, string? StableId)>? getMonitorOutput,
        string operationId) : IRoutineAudioChange
    {
        public void Restore()
        {
            try
            {
                if (!audio.TryGetInputListenState(inputDeviceId, out bool current, out _))
                {
                    logger.Debug("RoutineInputMonitoring", () => $"routine-input-monitoring-restore-skipped | opId={operationId} reason=endpoint-unavailable");
                    return;
                }

                if (current != applied)
                {
                    logger.Debug("RoutineInputMonitoring", () => $"routine-input-monitoring-restore-skipped | opId={operationId} reason=newer-change");
                    return;
                }

                (string? outputId, string? outputName, string? outputStableId) = getMonitorOutput?.Invoke() ?? (null, null, null);
                audio.SetInputListenState(inputDeviceId, outputId, outputName, original, outputStableId);
                logger.Debug("RoutineInputMonitoring", () => $"routine-input-monitoring-restored | enabled={original} opId={operationId}");
            }
            catch (Exception ex)
            {
                logger.Warning("RoutineInputMonitoring", $"routine-input-monitoring-restore-failed | opId={operationId}", nameof(Restore), ex);
            }
        }

        public void Dispose()
        {
        }
    }
}
