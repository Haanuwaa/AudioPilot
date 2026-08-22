using System.Collections.ObjectModel;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelDeviceCycleHelper
    {
        public static List<CycleDevice> CloneCycleDevices(IEnumerable<CycleDevice>? devices)
        {
            List<CycleDevice> cloned = [];
            if (devices == null)
            {
                return cloned;
            }

            foreach (CycleDevice device in devices)
            {
                if (!IsUsableDevice(device))
                {
                    continue;
                }

                cloned.Add(CloneDevice(device));
            }

            return cloned;
        }

        public static int AddAllMissingDevices(IEnumerable<CycleDevice> availableDevices, ObservableCollection<CycleDevice> targetCycleDevices)
        {
            int added = 0;
            foreach (CycleDevice device in availableDevices)
            {
                if (!IsUsableDevice(device) || ContainsDevice(targetCycleDevices, device.Id))
                {
                    continue;
                }

                targetCycleDevices.Add(CloneDevice(device));

                added++;
            }

            return added;
        }

        public static bool IsAllDevicesSelection(int selectedIndex)
        {
            return selectedIndex == 0;
        }

        public static int ToDeviceIndex(int selectedIndex)
        {
            return selectedIndex - 1;
        }

        public static int ToComboIndex(int deviceIndex)
        {
            return deviceIndex < 0 ? -1 : deviceIndex + 1;
        }

        public static bool CanAddAnyDevice(IEnumerable<CycleDevice> availableDevices, IEnumerable<CycleDevice> cycleDevices)
        {
            foreach (CycleDevice device in availableDevices)
            {
                if (IsUsableDevice(device) && !ContainsDevice(cycleDevices, device.Id))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool ContainsDevice(IEnumerable<CycleDevice> cycleDevices, string deviceId)
        {
            foreach (CycleDevice device in cycleDevices)
            {
                if (HasMatchingId(device, deviceId))
                {
                    return true;
                }
            }

            return false;
        }

        public static int FindNextAvailableDeviceIndex(List<CycleDevice> availableDevices, IEnumerable<CycleDevice> cycleDevices, int currentSelection)
        {
            if (availableDevices.Count == 0)
            {
                return -1;
            }

            int start = currentSelection < 0 ? 0 : currentSelection + 1;

            for (int offset = 0; offset < availableDevices.Count; offset++)
            {
                int idx = (start + offset) % availableDevices.Count;
                CycleDevice candidate = availableDevices[idx];
                if (IsUsableDevice(candidate) && !ContainsDevice(cycleDevices, candidate.Id))
                {
                    return idx;
                }
            }

            return -1;
        }

        public static void ApplyCycleFromSettingsCore(ObservableCollection<CycleDevice> targetCycleDevices, IEnumerable<CycleDevice>? configuredDevices)
        {
            SyncCycleDevices(targetCycleDevices, configuredDevices);
        }

        public static List<CycleDevice> ReconcilePersistedCycleDevices(IEnumerable<CycleDevice>? configuredDevices, IReadOnlyList<CycleDevice>? availableDevices)
        {
            List<CycleDevice> reconciled = [];
            if (configuredDevices == null)
            {
                return reconciled;
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (CycleDevice device in configuredDevices)
            {
                CycleDevice resolved = ReconcilePersistedDevice(device, availableDevices);
                if (!IsUsableDevice(resolved) || !seen.Add(resolved.Id))
                {
                    continue;
                }

                reconciled.Add(resolved);
            }

            return reconciled;
        }

        public static CycleDevice ReconcilePersistedDevice(CycleDevice? configuredDevice, IReadOnlyList<CycleDevice>? availableDevices)
        {
            if (configuredDevice == null)
            {
                return new CycleDevice();
            }

            if (!IsUsableDevice(configuredDevice))
            {
                return CloneDevice(configuredDevice);
            }

            if (PersistedAudioDeviceResolver.TryResolveMatch(configuredDevice, availableDevices) is CycleDevice resolvedMatch)
            {
                return CloneDevice(resolvedMatch);
            }

            return CloneDevice(configuredDevice);
        }

        public static bool SyncCycleDevices(ObservableCollection<CycleDevice> targetDevices, IEnumerable<CycleDevice>? sourceDevices)
        {
            List<CycleDevice> normalizedDevices = NormalizeDevices(sourceDevices);

            if (AreDeviceCollectionsEquivalent(targetDevices, normalizedDevices))
            {
                return false;
            }

            int sharedCount = Math.Min(targetDevices.Count, normalizedDevices.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                CycleDevice current = targetDevices[index];
                CycleDevice next = normalizedDevices[index];

                if (!string.Equals(current.Id, next.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.Name, next.Name, StringComparison.Ordinal))
                {
                    targetDevices[index] = CloneDevice(next);
                }
            }

            while (targetDevices.Count > normalizedDevices.Count)
            {
                targetDevices.RemoveAt(targetDevices.Count - 1);
            }

            for (int index = targetDevices.Count; index < normalizedDevices.Count; index++)
            {
                targetDevices.Add(CloneDevice(normalizedDevices[index]));
            }

            return true;
        }

        public static int LoadAvailableDevices(
            List<CycleDevice> targetDevices,
            ObservableCollection<string> targetNames,
            List<CycleDevice> refreshedDevices,
            int previousSelectedIndex,
            string allDevicesOptionLabel,
            bool includeAllOption = false)
        {
            bool hadAllSelection = includeAllOption && IsAllDevicesSelection(previousSelectedIndex);

            int previousDeviceIndex = includeAllOption
                ? ToDeviceIndex(previousSelectedIndex)
                : previousSelectedIndex;

            string? selectedDeviceId =
                previousDeviceIndex >= 0 && previousDeviceIndex < targetDevices.Count
                    ? targetDevices[previousDeviceIndex].Id
                    : null;

            DisposeDevices(targetDevices);
            targetNames.Clear();
            targetDevices.AddRange(refreshedDevices);

            if (includeAllOption)
            {
                targetNames.Add(allDevicesOptionLabel);
            }

            AppendDeviceNames(targetNames, refreshedDevices);

            if (!string.IsNullOrWhiteSpace(selectedDeviceId))
            {
                int selectedDevicePosition = FindDeviceIndexById(targetDevices, selectedDeviceId);
                if (selectedDevicePosition >= 0)
                {
                    return includeAllOption ? ToComboIndex(selectedDevicePosition) : selectedDevicePosition;
                }
            }

            if (hadAllSelection)
            {
                return 0;
            }

            int maxValidSelection = includeAllOption ? targetDevices.Count : targetDevices.Count - 1;
            if (previousSelectedIndex < 0)
            {
                return -1;
            }

            return previousSelectedIndex > maxValidSelection ? -1 : previousSelectedIndex;
        }

        public static void ReindexCycleDevices(ObservableCollection<CycleDevice> devices)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                devices[i].DisplayOrder = i + 1;
            }
        }

        private static void DisposeDevices(List<CycleDevice> devices)
        {
            devices.Clear();
        }

        private static void AppendDeviceNames(ObservableCollection<string> targetNames, IEnumerable<CycleDevice> devices)
        {
            foreach (CycleDevice device in devices)
            {
                targetNames.Add(device.Name);
            }
        }

        private static List<CycleDevice> NormalizeDevices(IEnumerable<CycleDevice>? devices)
        {
            List<CycleDevice> normalized = [];
            if (devices == null)
            {
                return normalized;
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (CycleDevice device in devices)
            {
                if (!IsUsableDevice(device) || !seen.Add(device.Id))
                {
                    continue;
                }

                normalized.Add(CloneDevice(device));
            }

            return normalized;
        }

        private static int FindDeviceIndexById(List<CycleDevice> devices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return -1;
            }

            for (int index = 0; index < devices.Count; index++)
            {
                if (HasMatchingId(devices[index], deviceId))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool HasMatchingId(CycleDevice device, string? deviceId)
        {
            return !string.IsNullOrWhiteSpace(deviceId)
                && string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUsableDevice(CycleDevice? device)
        {
            return device != null && !string.IsNullOrWhiteSpace(device.Id);
        }

        private static CycleDevice CloneDevice(CycleDevice device)
        {
            return new CycleDevice
            {
                Id = device.Id,
                Name = device.Name,
            };
        }

        private static bool AreDeviceCollectionsEquivalent(ObservableCollection<CycleDevice> current, List<CycleDevice> next)
        {
            if (current.Count != next.Count)
            {
                return false;
            }

            for (int index = 0; index < current.Count; index++)
            {
                if (!AreEquivalent(current[index], next[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreEquivalent(CycleDevice current, CycleDevice next)
        {
            return string.Equals(current.Id, next.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Name, next.Name, StringComparison.Ordinal);
        }
    }
}
