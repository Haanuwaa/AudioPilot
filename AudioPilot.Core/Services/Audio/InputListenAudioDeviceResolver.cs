using System.Runtime.InteropServices;
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
        private const int ErrorNotFound = unchecked((int)0x80070490);

        public IAudioEndpointInfo? GetDefaultRecordingEndpoint()
        {
            MMDevice? device = getDefaultRecordingDevice();
            return device == null ? null : new MmDeviceAudioEndpointInfo(device);
        }

        public IAudioEndpointInfo? GetDefaultPlaybackEndpoint()
        {
            try
            {
                MMDevice? device = getDefaultPlaybackDevice();
                return device == null ? null : new MmDeviceAudioEndpointInfo(device);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is COMException comEx && comEx.HResult == ErrorNotFound)
            {
                return null;
            }
            catch (COMException comEx) when (comEx.HResult == ErrorNotFound)
            {
                return null;
            }
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

            try
            {
                if (device.State == NDeviceState.Active && device.DataFlow == DataFlow.Render)
                {
                    var endpoint = new MmDeviceAudioEndpointInfo(device);
                    device = null;
                    return endpoint;
                }

                return null;
            }
            finally
            {
                device?.Dispose();
            }
        }

        public IReadOnlyList<CycleDevice> GetActivePlaybackDeviceInfos()
        {
            return getActivePlaybackDeviceInfos();
        }
    }
}
