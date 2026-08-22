using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace AudioPilot.Services.Audio
{
    internal interface IInputListenAudioDeviceResolver
    {
        IAudioEndpointInfo? GetDefaultRecordingEndpoint();
        IAudioEndpointInfo? GetDefaultPlaybackEndpoint();
        IAudioEndpointInfo? TryGetPlaybackEndpoint(string deviceId);
        IReadOnlyList<CycleDevice> GetActivePlaybackDeviceInfos();
    }

    internal sealed class InputListenAudioDeviceResolver(
        Func<MMDevice?> getDefaultRecordingDevice,
        Func<MMDevice?> getDefaultPlaybackDevice,
        Func<string, MMDevice?> tryGetPlaybackDeviceById,
        Func<List<CycleDevice>> getActivePlaybackDeviceInfos) : IInputListenAudioDeviceResolver
    {
        public IAudioEndpointInfo? GetDefaultRecordingEndpoint()
        {
            MMDevice? device = getDefaultRecordingDevice();
            return device == null ? null : new MmDeviceAudioEndpointInfo(device);
        }

        public IAudioEndpointInfo? GetDefaultPlaybackEndpoint()
        {
            MMDevice? device = getDefaultPlaybackDevice();
            return device == null ? null : new MmDeviceAudioEndpointInfo(device);
        }

        public IAudioEndpointInfo? TryGetPlaybackEndpoint(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            MMDevice? device = tryGetPlaybackDeviceById(deviceId);
            if (device == null)
            {
                return null;
            }

            if (device.State != NDeviceState.Active || device.DataFlow != DataFlow.Render)
            {
                device.Dispose();
                return null;
            }

            return new MmDeviceAudioEndpointInfo(device);
        }

        public IReadOnlyList<CycleDevice> GetActivePlaybackDeviceInfos()
        {
            return getActivePlaybackDeviceInfos();
        }
    }
}
