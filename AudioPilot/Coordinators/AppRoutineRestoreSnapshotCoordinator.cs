using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct RoutineRestoreDeviceInfo(
        string DeviceId,
        string DeviceName,
        float? VolumePercent = null,
        bool? IsMuted = null);

    internal static class AppRoutineRestoreSnapshotCoordinator
    {
        /// <summary>
        /// Captures the default audio state that should be restored when a stateful routine deactivates.
        /// </summary>
        /// <remarks>
        /// Snapshot capture is limited to routines whose trigger can remain active over time and only when restore-on-
        /// deactivate is enabled. Each flow is captured only when the routine can change that flow, so an output-only
        /// routine does not later restore the user's input device.
        /// </remarks>
        internal static AppViewModel.RoutineAudioRestoreSnapshot? CaptureSnapshot(
            AudioRoutine? routine,
            Func<RoutineRestoreDeviceInfo> getDefaultPlaybackDeviceInfo,
            Func<RoutineRestoreDeviceInfo> getDefaultRecordingDeviceInfo,
            Logger logger)
        {
            if (routine == null || !routine.RestorePreviousAudioOnDeactivate || !routine.IsStatefulTrigger)
            {
                return null;
            }

            string previousOutputDeviceId = string.Empty;
            string previousOutputDeviceName = string.Empty;
            string previousInputDeviceId = string.Empty;
            string previousInputDeviceName = string.Empty;
            float? previousOutputVolumePercent = null;
            bool? previousOutputMuted = null;
            float? previousInputVolumePercent = null;
            bool? previousInputMuted = null;
            bool shouldCaptureOutputDevice = ShouldCaptureOutputDevice(routine);
            bool shouldCaptureInputDevice = ShouldCaptureInputDevice(routine);
            bool shouldCaptureOutputVolume = shouldCaptureOutputDevice || routine.MasterVolumePercent.HasValue;
            bool shouldCaptureInputVolume = shouldCaptureInputDevice || routine.MicVolumePercent.HasValue;

            if (!shouldCaptureOutputVolume && !shouldCaptureInputVolume)
            {
                logger.Info(
                    "AppViewModel",
                    () => $"routine-restore-snapshot-skipped | {AppViewModel.BuildRoutineExecutionLogContext(routine, "stateful-capture", showOverlay: false, applicationProcessId: null)} reason=no-restorable-targets");
                return null;
            }

            if (shouldCaptureOutputVolume)
            {
                try
                {
                    RoutineRestoreDeviceInfo outputDevice = getDefaultPlaybackDeviceInfo();
                    if (shouldCaptureOutputDevice)
                    {
                        previousOutputDeviceId = outputDevice.DeviceId;
                        previousOutputDeviceName = outputDevice.DeviceName;
                    }

                    previousOutputVolumePercent = outputDevice.VolumePercent;
                    previousOutputMuted = outputDevice.IsMuted;
                }
                catch (Exception ex)
                {
                    logger.Warning("AppViewModel", "routine-restore-output-snapshot-capture-failed", nameof(CaptureSnapshot), ex);
                }
            }

            if (shouldCaptureInputVolume)
            {
                try
                {
                    RoutineRestoreDeviceInfo inputDevice = getDefaultRecordingDeviceInfo();
                    if (shouldCaptureInputDevice)
                    {
                        previousInputDeviceId = inputDevice.DeviceId;
                        previousInputDeviceName = inputDevice.DeviceName;
                    }

                    previousInputVolumePercent = inputDevice.VolumePercent;
                    previousInputMuted = inputDevice.IsMuted;
                }
                catch (Exception ex)
                {
                    logger.Warning("AppViewModel", "routine-restore-input-snapshot-capture-failed", nameof(CaptureSnapshot), ex);
                }
            }

            if (string.IsNullOrWhiteSpace(previousOutputDeviceId) &&
                string.IsNullOrWhiteSpace(previousInputDeviceId) &&
                !previousOutputVolumePercent.HasValue &&
                !previousInputVolumePercent.HasValue)
            {
                logger.Info(
                    "AppViewModel",
                    () => $"routine-restore-snapshot-skipped | {AppViewModel.BuildRoutineExecutionLogContext(routine, "stateful-capture", showOverlay: false, applicationProcessId: null)} reason=no-restorable-state");
                return null;
            }

            AppViewModel.RoutineAudioRestoreSnapshot snapshot = new(
                previousOutputDeviceId,
                previousOutputDeviceName,
                previousInputDeviceId,
                previousInputDeviceName,
                previousOutputVolumePercent,
                previousOutputMuted,
                previousInputVolumePercent,
                previousInputMuted);

            logger.Info(
                "AppViewModel",
                () => $"routine-restore-snapshot-captured | {AppViewModel.BuildRoutineExecutionLogContext(routine, "stateful-capture", showOverlay: false, applicationProcessId: null)} {AppViewModel.BuildRoutineRestoreSnapshotLogContext(snapshot)}");

            return snapshot;
        }

        private static bool ShouldCaptureOutputDevice(AudioRoutine routine)
        {
            return !routine.SwitchOutputPerApp && !string.IsNullOrWhiteSpace(routine.OutputDeviceId);
        }

        private static bool ShouldCaptureInputDevice(AudioRoutine routine)
        {
            return !routine.SwitchOutputPerApp && !string.IsNullOrWhiteSpace(routine.InputDeviceId);
        }
    }
}
