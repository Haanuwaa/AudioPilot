using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    public readonly record struct MuteOperationResult(int AttemptedEndpointCount, int SucceededEndpointCount, int FailedEndpointCount)
    {
        public bool HasTargets => AttemptedEndpointCount > 0;
        public bool FullySucceeded => HasTargets && FailedEndpointCount == 0 && SucceededEndpointCount == AttemptedEndpointCount;
        public bool HasFailures => !HasTargets || FailedEndpointCount > 0;

        public static MuteOperationResult Combine(MuteOperationResult left, MuteOperationResult right) =>
            new(
                left.AttemptedEndpointCount + right.AttemptedEndpointCount,
                left.SucceededEndpointCount + right.SucceededEndpointCount,
                left.FailedEndpointCount + right.FailedEndpointCount);
    }

    public partial class VolumeControlService
    {
        internal bool? CaptureEndpointMuteForDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using MMDevice device = enumerator.GetDevice(deviceId);
                return device.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                _logger.Warning("VolumeControlService", () => $"switch-mute-capture-failed | endpoint={LogPrivacy.Id(deviceId)}", nameof(CaptureEndpointMuteForDeviceId), ex);
                return null;
            }
        }

        public void ApplyMuteSettingsDirect(
            bool? muteMic,
            bool? muteSound,
            bool deafen,
            MMDevice? playbackDevice,
            MMDevice? recordingDevice,
            MMDeviceEnumerator enumerator,
            string? communicationsDeviceId,
            Func<bool>? shouldContinue = null,
            Func<bool, bool?, bool?>? resolveMuteOverride = null)
        {
            if (shouldContinue?.Invoke() == false) return;
            bool? recordingMute = deafen ? true : muteMic;
            if (resolveMuteOverride != null) recordingMute = resolveMuteOverride(false, recordingMute);
            if (recordingMute.HasValue && recordingDevice != null &&
                AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var recordingVolume))
            {
                try
                {
                    recordingVolume.Mute = recordingMute.Value;
                    _logger.Trace("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.MuteApply} | deviceType=recording device={LogPrivacy.Device(recordingDevice.FriendlyName)} muted={recordingMute.Value}");
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(ApplyMuteSettingsDirect), ex);
                }
            }

            if (shouldContinue?.Invoke() == false) return;
            bool? playbackMute = deafen ? true : muteSound;
            if (resolveMuteOverride != null) playbackMute = resolveMuteOverride(true, playbackMute);
            if (playbackMute.HasValue && playbackDevice != null &&
                AudioDeviceHelper.TryGetEndpointVolume(_logger, playbackDevice, out var playbackVolume))
            {
                try
                {
                    playbackVolume.Mute = playbackMute.Value;
                    _logger.Trace("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.MuteApply} | deviceType=playback device={LogPrivacy.Device(playbackDevice.FriendlyName)} muted={playbackMute.Value}");
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(ApplyMuteSettingsDirect), ex);
                }
            }

            MMDevice? commsDevice = null;
            try
            {
                if (shouldContinue?.Invoke() == false || !playbackMute.HasValue || string.IsNullOrEmpty(communicationsDeviceId)) return;
                commsDevice = enumerator.GetDevice(communicationsDeviceId);
                if (commsDevice != null &&
                    (playbackDevice == null || commsDevice.ID != playbackDevice.ID))
                {
                    if (AudioDeviceHelper.TryGetEndpointVolume(_logger, commsDevice, out var commsVolume))
                    {
                        commsVolume.Mute = playbackMute.Value;
                    }
                }
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(ApplyMuteSettingsDirect), ex);
            }
            finally
            {
                commsDevice?.Dispose();
            }
        }

        public MuteOperationResult SetMicrophoneMute(bool mute)
        {
            if (_disposed)
            {
                _logger.Trace("VolumeControlService",
                    "SetMicrophoneMute called while service is disposed");
                return default;
            }

            var devices = GetDistinctItemsForOperation(
                _deviceEnumerator.GetAllDefaultRecordingDevices(),
                static device => device.ID,
                static device => device.Dispose());

            try
            {
                return ApplyMuteToDevices(devices, mute, "recording", nameof(SetMicrophoneMute));
            }
            finally
            {
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }

        public MuteOperationResult SetPlaybackMute(bool mute)
        {
            if (_disposed)
            {
                _logger.Trace("VolumeControlService",
                    "SetPlaybackMute called while service is disposed");
                return default;
            }

            var devices = GetDistinctItemsForOperation(
                _deviceEnumerator.GetAllDefaultPlaybackDevices(),
                static device => device.ID,
                static device => device.Dispose());

            try
            {
                return ApplyMuteToDevices(devices, mute, "playback", nameof(SetPlaybackMute));
            }
            finally
            {
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }

        private MuteOperationResult ApplyMuteToDevices(
            List<MMDevice> devices,
            bool mute,
            string deviceType,
            string operationName)
        {
            int succeeded = 0;
            int failed = 0;

            foreach (MMDevice device in devices)
            {
                if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, device, out var volume))
                {
                    failed++;
                    continue;
                }

                try
                {
                    volume.Mute = mute;
                    succeeded++;
                    _logger.Trace(
                        "VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.MuteApply} | deviceType={deviceType} device={LogPrivacy.Device(device.FriendlyName)} muted={mute}");
                }
                catch (COMException ex)
                {
                    failed++;
                    AudioDeviceHelper.LogComException(_logger, operationName, ex);
                }
                catch (Exception ex)
                {
                    failed++;
                    AudioDeviceHelper.LogException(_logger, operationName, ex);
                }
            }

            return new MuteOperationResult(devices.Count, succeeded, failed);
        }

        internal static List<T> GetDistinctItemsForOperation<T>(
            IEnumerable<T?> items,
            Func<T, string> getId,
            Action<T> dispose)
            where T : class
        {
            var results = new List<T>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenInstances = new HashSet<T>(ReferenceEqualityComparer.Instance);

            foreach (T? item in items)
            {
                if (item == null || !seenInstances.Add(item))
                {
                    continue;
                }

                string id = getId(item);
                if (seenIds.Add(id))
                {
                    results.Add(item);
                    continue;
                }

                dispose(item);
            }

            return results;
        }
    }
}
