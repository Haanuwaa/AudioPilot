using AudioPilot.Coordinators;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineRestoreDeviceInfoHelper
    {
        public static string? GetDeviceId<TDevice>(Func<TDevice?> getDefaultDevice, Func<TDevice, string?> getId)
            where TDevice : class, IDisposable
        {
            ArgumentNullException.ThrowIfNull(getDefaultDevice);
            ArgumentNullException.ThrowIfNull(getId);

            TDevice? device = null;
            try
            {
                device = getDefaultDevice();
                return device == null ? null : getId(device);
            }
            finally
            {
                device?.Dispose();
            }
        }

        public static RoutineRestoreDeviceInfo GetDeviceInfo<TDevice>(
            Func<TDevice?> getDefaultDevice,
            Func<TDevice, string?> getId,
            Func<TDevice, string?> getFriendlyName)
            where TDevice : class, IDisposable
        {
            ArgumentNullException.ThrowIfNull(getDefaultDevice);
            ArgumentNullException.ThrowIfNull(getId);
            ArgumentNullException.ThrowIfNull(getFriendlyName);

            TDevice? device = null;
            try
            {
                device = getDefaultDevice();
                return new RoutineRestoreDeviceInfo(
                    device == null ? string.Empty : getId(device) ?? string.Empty,
                    device == null ? string.Empty : getFriendlyName(device) ?? string.Empty);
            }
            finally
            {
                device?.Dispose();
            }
        }
    }
}
