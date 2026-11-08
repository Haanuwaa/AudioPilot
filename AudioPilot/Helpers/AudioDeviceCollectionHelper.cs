using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Helpers
{
    internal static class AudioDeviceCollectionHelper
    {
        /// <summary>Releases all owned devices, continuing after an individual disposal failure.</summary>
        internal static void DisposeDevices<T>(IEnumerable<T?> devices, Action<T, Exception>? onDisposeFailure = null)
            where T : class, IDisposable
        {
            foreach (T? device in devices)
            {
                if (device == null) continue;
                try { device.Dispose(); }
                catch (Exception ex) { onDisposeFailure?.Invoke(device, ex); }
            }
        }

        public static List<MMDevice> MaterializeDevices(MMDeviceCollection collection, Action<int, Exception>? onMaterializeFailure = null)
        {
            int count = collection.Count;
            var devices = new List<MMDevice>(count);
            for (int index = 0; index < count; index++)
            {
                MMDevice? device;
                try
                {
                    device = collection[index];
                }
                catch (Exception ex)
                {
                    onMaterializeFailure?.Invoke(index, ex);
                    continue;
                }

                if (device != null)
                {
                    devices.Add(device);
                }
            }

            return devices;
        }

        public static List<CycleDevice> ProjectCycleDevices(
            MMDeviceCollection collection,
            Action<int, Exception>? onMaterializeFailure = null,
            Action<MMDevice?, Exception>? onDisposeFailure = null)
        {
            List<MMDevice> devices = MaterializeDevices(collection, onMaterializeFailure);
            try
            {
                var result = new List<CycleDevice>(devices.Count);
                for (int index = 0; index < devices.Count; index++)
                {
                    MMDevice? device = devices[index];
                    if (device == null || string.IsNullOrWhiteSpace(device.ID))
                    {
                        continue;
                    }

                    result.Add(new CycleDevice
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        StableId = AudioEndpointIdentity.TryGetStableId(device),
                    });
                }

                return result;
            }
            finally
            {
                DisposeDevices(devices, onDisposeFailure);
            }
        }

        /// <summary>Materializes owned devices, releasing acquired entries if enumeration fails.</summary>
        internal static List<MMDevice> MaterializeDevicesStrict(MMDeviceCollection devices)
        {
            int count = devices.Count;
            var materialized = new List<MMDevice>(count);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    var device = devices[index];
                    if (device != null) materialized.Add(device);
                }
            }
            catch
            {
                DisposeDevices(materialized);
                throw;
            }

            return materialized;
        }

        internal static string BuildDeviceFingerprint(List<MMDevice> devices)
        {
            var builder = new System.Text.StringBuilder(devices.Count * 48);
            for (int index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (device == null)
                {
                    continue;
                }

                try
                {
                    builder.Append(device.ID);
                    builder.Append('|');
                }
                catch
                {
                }
            }

            return builder.ToString();
        }
    }
}
