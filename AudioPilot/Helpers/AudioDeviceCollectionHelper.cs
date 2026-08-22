using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Helpers
{
    internal static class AudioDeviceCollectionHelper
    {
        public static List<MMDevice> MaterializeDevices(MMDeviceCollection collection, Action<int, Exception>? onMaterializeFailure = null)
        {
            var devices = new List<MMDevice>(collection.Count);
            for (int index = 0; index < collection.Count; index++)
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
                    });
                }

                return result;
            }
            finally
            {
                for (int index = 0; index < devices.Count; index++)
                {
                    MMDevice? device = devices[index];
                    if (device == null)
                    {
                        continue;
                    }

                    try
                    {
                        device.Dispose();
                    }
                    catch (Exception ex)
                    {
                        onDisposeFailure?.Invoke(device, ex);
                    }
                }
            }
        }
    }
}
