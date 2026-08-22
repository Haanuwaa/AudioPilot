using NAudio.CoreAudioApi;

namespace AudioPilot.Helpers;

internal static class AudioEndpointIdentity
{
    /// <summary>Reads the optional Windows 11 24H2 stable ID without changing its opaque, case-sensitive value.</summary>
    internal static string? TryGetStableId(MMDevice device)
    {
        try
        {
            var key = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 12);
            return device.Properties.Contains(key) && device.Properties[key].Value is string id && id.Length > 0 ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static Guid? TryGetContainerId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDevice(endpointId);
            var key = new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
            return device.Properties.Contains(key) && device.Properties[key].Value is Guid id && id != Guid.Empty ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
