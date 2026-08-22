using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceDefaultEndpointCollectionHelper
    {
        private static readonly Role[] EndpointRoles = [Role.Console, Role.Multimedia, Role.Communications];

        public static List<T?> GetDistinctDefaultDevicesForRoles<T>(
            Func<Role, T> getDefaultDevice,
            Func<T, string> getId,
            Action<T> dispose,
            Action<Role> onRoleMissing,
            Action<Role, Exception, T?> onRoleFailure)
            where T : class
        {
            var devices = new List<T?>(EndpointRoles.Length);
            var deviceMap = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            foreach (Role role in EndpointRoles)
            {
                T? device = null;
                try
                {
                    device = getDefaultDevice(role);
                    string deviceId = getId(device);

                    if (!deviceMap.TryGetValue(deviceId, out T? mappedDevice))
                    {
                        deviceMap[deviceId] = device;
                        devices.Add(device);
                    }
                    else
                    {
                        dispose(device);
                        devices.Add(mappedDevice);
                    }
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
                {
                    onRoleMissing(role);
                    devices.Add(null);
                }
                catch (Exception ex)
                {
                    onRoleFailure(role, ex, device);
                    if (device != null)
                    {
                        dispose(device);
                    }

                    devices.Add(null);
                }
            }

            return devices;
        }
    }
}
