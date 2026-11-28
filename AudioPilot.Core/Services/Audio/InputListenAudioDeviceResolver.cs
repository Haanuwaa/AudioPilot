using System.Runtime.InteropServices;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace AudioPilot.Services.Audio
{
    internal interface IInputListenAudioDeviceResolver
    {
        IAudioEndpointInfo? GetDefaultRecordingEndpoint();
        IAudioEndpointInfo? TryGetRecordingEndpoint(string deviceId) => null;
        IAudioEndpointInfo? GetDefaultPlaybackEndpoint();
        IAudioEndpointInfo? TryGetPlaybackEndpoint(string deviceId);
        IReadOnlyList<CycleDevice> GetActivePlaybackDeviceInfos();
    }

    internal sealed class InputListenAudioDeviceResolver(
        Func<MMDevice?> getDefaultRecordingDevice,
        Func<MMDevice?> getDefaultPlaybackDevice,
        Func<string, MMDevice?> tryGetPlaybackDeviceById,
        Func<List<CycleDevice>> getActivePlaybackDeviceInfos,
        Func<string, MMDevice?>? tryGetRecordingDeviceById = null) : IInputListenAudioDeviceResolver
    {
        private const int ErrorNotFound = unchecked((int)0x80070490);

        public IAudioEndpointInfo? GetDefaultRecordingEndpoint()
        {
            MMDevice? device = getDefaultRecordingDevice();
            return device == null ? null : new MmDeviceAudioEndpointInfo(device);
        }

        public IAudioEndpointInfo? TryGetRecordingEndpoint(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || tryGetRecordingDeviceById == null)
            {
                return null;
            }

            MMDevice? device = tryGetRecordingDeviceById(deviceId);
            if (device == null)
            {
                return null;
            }

            try
            {
                if (device.State == NDeviceState.Active && device.DataFlow == DataFlow.Capture)
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
