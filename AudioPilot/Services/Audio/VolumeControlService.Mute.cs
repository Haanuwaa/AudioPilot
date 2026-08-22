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
        public MuteOperationResult ApplyMuteSettings(bool muteMic, bool muteSound, bool deafen)
        {
            var recordingDevices = GetDistinctItemsForOperation(
                _deviceEnumerator.GetAllDefaultRecordingDevices(),
                static device => device.ID,
                static device => device.Dispose());
            var playbackDevices = GetDistinctItemsForOperation(
                _deviceEnumerator.GetAllDefaultPlaybackDevices(),
                static device => device.ID,
                static device => device.Dispose());

            try
            {
                MuteOperationResult recordingResult = ApplyMuteToDevices(
                    recordingDevices,
                    muteMic || deafen,
                    "recording",
                    nameof(ApplyMuteSettings));
                MuteOperationResult playbackResult = ApplyMuteToDevices(
                    playbackDevices,
                    muteSound || deafen,
                    "playback",
                    nameof(ApplyMuteSettings));

                return MuteOperationResult.Combine(recordingResult, playbackResult);
            }
            finally
            {
                foreach (var device in recordingDevices)
                {
                    device.Dispose();
                }
                foreach (var device in playbackDevices)
                {
                    device.Dispose();
                }
            }
        }

        public void ApplyMuteSettingsDirect(
            bool muteMic,
            bool muteSound,
            bool deafen,
            MMDevice? playbackDevice,
            MMDevice? recordingDevice,
            MMDeviceEnumerator enumerator)
        {
            if (recordingDevice != null &&
                AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var recordingVolume))
            {
                try
                {
                    recordingVolume.Mute = muteMic || deafen;
                    _logger.Trace("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.MuteApply} | deviceType=recording device={LogPrivacy.Device(recordingDevice.FriendlyName)} muted={muteMic || deafen}");
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(ApplyMuteSettingsDirect), ex);
                }
            }

            if (playbackDevice != null &&
                AudioDeviceHelper.TryGetEndpointVolume(_logger, playbackDevice, out var playbackVolume))
            {
                try
                {
                    playbackVolume.Mute = muteSound || deafen;
                    _logger.Trace("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.MuteApply} | deviceType=playback device={LogPrivacy.Device(playbackDevice.FriendlyName)} muted={muteSound || deafen}");
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(ApplyMuteSettingsDirect), ex);
                }
            }

            MMDevice? commsDevice = null;
            try
            {
                commsDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                if (commsDevice != null &&
                    (playbackDevice == null || commsDevice.ID != playbackDevice.ID))
                {
                    if (AudioDeviceHelper.TryGetEndpointVolume(_logger, commsDevice, out var commsVolume))
                    {
                        commsVolume.Mute = muteSound || deafen;
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
