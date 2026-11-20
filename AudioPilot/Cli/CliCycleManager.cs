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
                StableId = activeDevice.StableId
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

            HashSet<string> seenIds = [with(StringComparer.OrdinalIgnoreCase)];
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

        internal static (bool Success, string Output) ApplyMutation(
            List<CycleDevice> cycleDevices,
            List<CycleDevice> activeDevices,
            string kind,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            string? deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);

            bool updated;
            string? error;
            switch (action)
            {
                case "add":
                    if (!TryResolveCycleMutationSelector(activeDevices, kind, deviceId ?? string.Empty, out string resolvedAddDeviceId, out string? addSelectorError))
                    {
                        return (false, FormatMutationFailure(action, addSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedAddDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = TryAddDevice(cycleDevices, activeDevices, resolvedAddDeviceId, out _, out _, out string addMessage);
                    error = addMessage;
                    break;
                case "remove":
                    if (!TryResolveCycleMutationSelector(cycleDevices, kind, deviceId ?? string.Empty, out string resolvedRemoveDeviceId, out string? removeSelectorError, selector => $"Device '{selector}' is not configured in the cycle."))
                    {
                        return (false, FormatMutationFailure(action, removeSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedRemoveDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = TryRemoveDevice(cycleDevices, resolvedRemoveDeviceId, out _, out _, out string removeMessage);
                    error = removeMessage;
                    break;
                case "reorder":
                    updated = TryReorder(cycleDevices, orderedDeviceIds ?? [], out _, out string reorderMessage);
                    error = reorderMessage;
                    break;
                default:
                    updated = false;
                    error = "Unsupported cycle action.";
                    break;
            }

            if (!updated)
            {
                return (false, FormatMutationFailure(action, error ?? "Failed to update cycle.", jsonOutput));
            }

            return (true, CliOutputFormatter.FormatCycleMutationResult(kind, action, GetCycleDiagCode(action), cycleDevices, deviceId, deviceName, jsonOutput, redactOutput));
        }

        private static string? FindCycleMutationDeviceName(List<CycleDevice> cycleDevices, List<CycleDevice> activeDevices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return cycleDevices[index].Name;
                }
            }

            for (int index = 0; index < activeDevices.Count; index++)
            {
                if (string.Equals(activeDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return activeDevices[index].Name;
                }
            }

            return null;
        }

        private static bool TryResolveCycleMutationSelector(IReadOnlyList<CycleDevice> devices, string kind, string selectorSpec, out string resolvedDeviceId, out string? error, Func<string, string>? notFoundMessageFactory = null)
        {
            resolvedDeviceId = selectorSpec;
            error = null;

            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selectorSpec);
            if (resolution.Success && resolution.Device != null)
            {
                resolvedDeviceId = resolution.Device.Id;
                return true;
            }

            error = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : notFoundMessageFactory?.Invoke(resolution.Selector) ?? CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return false;
        }

        private static string GetCycleDiagCode(string action)
        {
            return action switch
            {
                "add" => "cycle-add-success",
                "remove" => "cycle-remove-success",
                "reorder" => "cycle-reorder-success",
                _ => "cycle-update-success",
            };
        }

        internal static string FormatMutationFailure(string action, string message, bool jsonOutput)
        {
            string diagCode = action switch
            {
                "add" => "cycle-add-failed",
                "remove" => "cycle-remove-failed",
                "reorder" => "cycle-reorder-failed",
                _ => "cycle-update-failed",
            };

            return jsonOutput
                ? CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message })
                : $"[diag-code:{diagCode}] {message}";
        }
    }
}
