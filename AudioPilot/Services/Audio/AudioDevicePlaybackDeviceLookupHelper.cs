using System.Runtime.InteropServices;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDevicePlaybackDeviceLookupHelper
    {
        public static List<TDevice> ResolveActivePlaybackDevicesById<TDevice>(
            IReadOnlyCollection<string> deviceIds,
            Func<string, TDevice?> getDevice,
            Func<TDevice, bool> isPlaybackDevice,
            Func<TDevice, bool> isActive,
            Action<TDevice> disposeDevice,
            Action<COMException> logComException,
            Action<Exception> logException)
            where TDevice : class
        {
            ArgumentNullException.ThrowIfNull(deviceIds);
            ArgumentNullException.ThrowIfNull(getDevice);
            ArgumentNullException.ThrowIfNull(isPlaybackDevice);
            ArgumentNullException.ThrowIfNull(isActive);
            ArgumentNullException.ThrowIfNull(disposeDevice);
            ArgumentNullException.ThrowIfNull(logComException);
            ArgumentNullException.ThrowIfNull(logException);

            List<TDevice> devices = new(deviceIds.Count);

            foreach (string deviceId in deviceIds)
            {
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    continue;
                }

                TDevice? device = null;
                try
                {
                    device = getDevice(deviceId);
                    if (device == null || !isPlaybackDevice(device) || !isActive(device))
                    {
                        if (device != null)
                        {
                            disposeDevice(device);
                        }

                        continue;
                    }

                    devices.Add(device);
                }
                catch (COMException ex)
                {
                    logComException(ex);
                    if (device != null)
                    {
                        disposeDevice(device);
                    }
                }
                catch (Exception ex)
                {
                    logException(ex);
                    if (device != null)
                    {
                        disposeDevice(device);
                    }
                }
            }

            return devices;
        }
    }
}
