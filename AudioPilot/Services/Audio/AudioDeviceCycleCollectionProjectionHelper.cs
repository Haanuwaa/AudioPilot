using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceCycleCollectionProjectionHelper
    {
        public static List<TOutput> Project<TCollection, TDevice, TOutput>(
            TCollection devices,
            Func<TCollection, Action<TDevice?, Exception>?, List<TOutput>> projectDevices,
            Func<TDevice?, string?> getFriendlyName,
            Func<TDevice?, string?> getId,
            Action<string> logTrace)
            where TDevice : class
        {
            ArgumentNullException.ThrowIfNull(projectDevices);
            ArgumentNullException.ThrowIfNull(getFriendlyName);
            ArgumentNullException.ThrowIfNull(getId);
            ArgumentNullException.ThrowIfNull(logTrace);

            return projectDevices(
                devices,
                (device, ex) =>
                {
                    string deviceLabel = LogPrivacy.Device(getFriendlyName(device));
                    string idLabel = LogPrivacy.Id(getId(device));
                    logTrace($"Ignored dispose exception for enumerated {deviceLabel} {idLabel}: {ex.GetType().Name}");
                });
        }
    }
}
