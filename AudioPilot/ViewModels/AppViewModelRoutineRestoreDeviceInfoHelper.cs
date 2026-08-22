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
    }
}
