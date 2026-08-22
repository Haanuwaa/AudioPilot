using System.Runtime.InteropServices;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceActiveCycleResolverHelper
    {
        public static TCycle? TryResolve<TDevice, TCycle>(
            string deviceId,
            string fallbackName,
            bool isDisposed,
            Func<string, TDevice?> getDevice,
            Func<TDevice, bool> isActive,
            Func<string, string, Func<string?>, TCycle?> createCycleDevice,
            Func<TDevice, string?> getFriendlyName,
            Action<TDevice> disposeDevice,
            Action<string> logTrace)
            where TDevice : class
        {
            ArgumentNullException.ThrowIfNull(getDevice);
            ArgumentNullException.ThrowIfNull(isActive);
            ArgumentNullException.ThrowIfNull(createCycleDevice);
            ArgumentNullException.ThrowIfNull(getFriendlyName);
            ArgumentNullException.ThrowIfNull(disposeDevice);
            ArgumentNullException.ThrowIfNull(logTrace);

            if (string.IsNullOrWhiteSpace(deviceId) || isDisposed)
            {
                return default;
            }

            TDevice? device = null;
            try
            {
                device = getDevice(deviceId);
                if (device == null || !isActive(device))
                {
                    return default;
                }

                return createCycleDevice(deviceId, fallbackName, () => getFriendlyName(device));
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
            {
                return default;
            }
            catch (COMException ex)
            {
                logTrace($"Active cycle device lookup failed: {ex.HResult:X8}");
                return default;
            }
            catch (Exception ex)
            {
                logTrace($"Active cycle device lookup failed: {ex.GetType().Name}");
                return default;
            }
            finally
            {
                if (device != null)
                {
                    disposeDevice(device);
                }
            }
        }
    }
}
