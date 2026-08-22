using AudioPilot.Models;

namespace AudioPilot.Cli
{
    internal static class CliCycleManager
    {
        public static bool TryAddDevice(
            List<CycleDevice> cycle,
            IReadOnlyList<CycleDevice> activeDevices,
            string deviceId,
            out CycleDevice? addedDevice,
            out string errorCode,
            out string message)
        {
            ArgumentNullException.ThrowIfNull(cycle);
            ArgumentNullException.ThrowIfNull(activeDevices);

            string normalizedDeviceId = NormalizeId(deviceId);
            if (string.IsNullOrWhiteSpace(normalizedDeviceId))
            {
                addedDevice = null;
                errorCode = "cycle-device-id-missing";
                message = "Device id cannot be empty.";
                return false;
            }

            if (cycle.Any(device => string.Equals(device.Id, normalizedDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                addedDevice = null;
                errorCode = "cycle-device-already-configured";
                message = $"Device '{normalizedDeviceId}' is already configured in the cycle.";
                return false;
            }

            CycleDevice? activeDevice = activeDevices.FirstOrDefault(device => string.Equals(device.Id, normalizedDeviceId, StringComparison.OrdinalIgnoreCase));
            if (activeDevice == null)
            {
                addedDevice = null;
                errorCode = "cycle-device-not-found";
                message = $"Active device '{normalizedDeviceId}' was not found.";
                return false;
            }

            addedDevice = new CycleDevice
            {
                Id = activeDevice.Id,
                Name = activeDevice.Name,
            };

            cycle.Add(addedDevice);
            return BuildSuccess(out errorCode, out message);
        }

        public static bool TryRemoveDevice(
            List<CycleDevice> cycle,
            string deviceId,
            out CycleDevice? removedDevice,
            out string errorCode,
            out string message)
        {
            ArgumentNullException.ThrowIfNull(cycle);

            string normalizedDeviceId = NormalizeId(deviceId);
            if (string.IsNullOrWhiteSpace(normalizedDeviceId))
            {
                removedDevice = null;
                errorCode = "cycle-device-id-missing";
                message = "Device id cannot be empty.";
                return false;
            }

            int index = cycle.FindIndex(device => string.Equals(device.Id, normalizedDeviceId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                removedDevice = null;
                errorCode = "cycle-device-not-configured";
                message = $"Device '{normalizedDeviceId}' is not configured in the cycle.";
                return false;
            }

            removedDevice = cycle[index];
            cycle.RemoveAt(index);
            return BuildSuccess(out errorCode, out message);
        }

        public static bool TryReorder(
            List<CycleDevice> cycle,
            IReadOnlyList<string> orderedDeviceIds,
            out string errorCode,
            out string message)
        {
            ArgumentNullException.ThrowIfNull(cycle);
            ArgumentNullException.ThrowIfNull(orderedDeviceIds);

            if (cycle.Count == 0)
            {
                errorCode = "cycle-reorder-empty";
                message = "Cannot reorder an empty cycle.";
                return false;
            }

            List<string> normalizedIds = [.. orderedDeviceIds.Select(NormalizeId).Where(static id => !string.IsNullOrWhiteSpace(id))];
            if (normalizedIds.Count != cycle.Count)
            {
                errorCode = "cycle-reorder-count-mismatch";
                message = $"Reorder requires exactly {cycle.Count} device ids.";
                return false;
            }

            HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (string normalizedId in normalizedIds)
            {
                if (!seenIds.Add(normalizedId))
                {
                    errorCode = "cycle-reorder-duplicate-device";
                    message = $"Device '{normalizedId}' was specified more than once in the reorder request.";
                    return false;
                }
            }

            Dictionary<string, CycleDevice> existingById = cycle.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            foreach (string normalizedId in normalizedIds)
            {
                if (!existingById.ContainsKey(normalizedId))
                {
                    errorCode = "cycle-reorder-unknown-device";
                    message = $"Device '{normalizedId}' is not configured in the cycle.";
                    return false;
                }
            }

            List<CycleDevice> reordered = [.. normalizedIds.Select(id => existingById[id])];
            cycle.Clear();
            cycle.AddRange(reordered);
            return BuildSuccess(out errorCode, out message);
        }

        private static string NormalizeId(string? deviceId)
        {
            return deviceId?.Trim() ?? string.Empty;
        }

        private static bool BuildSuccess(out string errorCode, out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;
            return true;
        }
    }
}
