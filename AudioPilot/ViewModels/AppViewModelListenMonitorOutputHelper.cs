using System.Collections.ObjectModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelListenMonitorOutputHelper
    {
        public static CycleDevice RefreshOptions(
            ObservableCollection<CycleDevice> targetDevices,
            IEnumerable<CycleDevice> availableDevices,
            string? currentSelectedId,
            string? currentSelectedName,
            string? preferredDeviceId = null,
            string? preferredDeviceName = null)
        {
            List<CycleDevice> availableDeviceList = [.. availableDevices];
            CycleDevice selectedDevice = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                new CycleDevice
                {
                    Id = preferredDeviceId ?? currentSelectedId ?? string.Empty,
                    Name = preferredDeviceName ?? currentSelectedName ?? string.Empty,
                },
                availableDeviceList);
            string selectedId = selectedDevice.Id ?? string.Empty;

            List<CycleDevice> normalizedDevices =
            [
                new CycleDevice
                {
                    Id = string.Empty,
                    Name = "Default output",
                }
            ];

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CycleDevice? device in availableDeviceList)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id) || !seenIds.Add(device.Id))
                {
                    continue;
                }

                normalizedDevices.Add(new CycleDevice
                {
                    Id = device.Id,
                    Name = device.Name,
                });
            }

            bool selectedExists = AppViewModelDeviceCycleHelper.ContainsDevice(normalizedDevices, selectedId);
            if (!selectedExists && !string.IsNullOrWhiteSpace(selectedId))
            {
                string unavailableName = string.IsNullOrWhiteSpace(selectedDevice.Name)
                    ? selectedId
                    : selectedDevice.Name;
                normalizedDevices.Add(new CycleDevice
                {
                    Id = selectedId,
                    Name = $"Configured output (unavailable): {unavailableName}",
                });
            }

            SyncListenMonitorDevices(targetDevices, normalizedDevices);

            return selectedDevice;
        }

        private static void SyncListenMonitorDevices(ObservableCollection<CycleDevice> targetDevices, List<CycleDevice> sourceDevices)
        {
            int sharedCount = Math.Min(targetDevices.Count, sourceDevices.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                CycleDevice current = targetDevices[index];
                CycleDevice next = sourceDevices[index];

                if (!string.Equals(current.Id, next.Id, StringComparison.Ordinal)
                    || !string.Equals(current.Name, next.Name, StringComparison.Ordinal))
                {
                    targetDevices[index] = new CycleDevice
                    {
                        Id = next.Id,
                        Name = next.Name,
                    };
                }
            }

            while (targetDevices.Count > sourceDevices.Count)
            {
                targetDevices.RemoveAt(targetDevices.Count - 1);
            }

            for (int index = targetDevices.Count; index < sourceDevices.Count; index++)
            {
                CycleDevice next = sourceDevices[index];
                targetDevices.Add(new CycleDevice
                {
                    Id = next.Id,
                    Name = next.Name,
                });
            }
        }
    }
}
