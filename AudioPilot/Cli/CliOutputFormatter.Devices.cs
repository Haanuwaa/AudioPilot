using System.Text;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public static string FormatDeviceList(string kind, IReadOnlyList<CycleDevice> devices, bool jsonOutput, bool redactOutput = false)
        {
            var items = new List<CliDeviceItem>(devices.Count);
            for (int index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                items.Add(new CliDeviceItem(FormatDeviceId(device.Id, redactOutput), FormatDeviceName(device.Name, redactOutput)));
            }

            items.Sort(static (left, right) =>
            {
                int byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                if (byName != 0)
                {
                    return byName;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
            });

            if (jsonOutput)
            {
                return SerializeCliJson(new CliDeviceListSnapshot(kind, items));
            }

            if (items.Count == 0)
            {
                return $"No active {kind} devices found.";
            }

            var builder = new StringBuilder();
            for (int index = 0; index < items.Count; index++)
            {
                builder.Append(index + 1);
                builder.Append(". ");
                builder.AppendLine(items[index].Name);
            }

            return builder.ToString().TrimEnd();
        }

        public static string FormatDeviceGetResult(string kind, CycleDevice device, bool jsonOutput, bool redactOutput = false)
        {
            string name = FormatDeviceName(device.Name, redactOutput);
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Kind = kind,
                    Device = new CliDeviceItem(FormatDeviceId(device.Id, redactOutput), name),
                    DiagCode = "device-get-success",
                });
            }

            return $"[diag-code:device-get-success] {kind} device '{name}' ({FormatDeviceId(device.Id, redactOutput)}).";
        }

        public static string FormatDeviceGetError(string kind, string diagCode, string message, bool jsonOutput, bool redactOutput = false)
        {
            string formattedMessage = redactOutput ? RedactWarnings([message], redactOutput)[0] : message;
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Kind = kind,
                    Success = false,
                    DiagCode = diagCode,
                    Error = formattedMessage,
                });
            }

            return $"[diag-code:{diagCode}] {formattedMessage}";
        }

        public static string FormatDeviceFindResult(string kind, string query, IReadOnlyList<CycleDevice> devices, bool jsonOutput, bool redactOutput = false)
        {
            var items = new List<CliDeviceItem>(devices.Count);
            for (int index = 0; index < devices.Count; index++)
            {
                items.Add(new CliDeviceItem(FormatDeviceId(devices[index].Id, redactOutput), FormatDeviceName(devices[index].Name, redactOutput)));
            }

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Kind = kind,
                    Query = FormatDeviceName(query, redactOutput),
                    Devices = items,
                    DiagCode = "device-find-success",
                });
            }

            if (items.Count == 0)
            {
                return $"[diag-code:device-find-none] No {kind} devices matched '{FormatDeviceName(query, redactOutput)}'.";
            }

            var builder = new StringBuilder();
            builder.Append("[diag-code:device-find-success] Matching ");
            builder.Append(kind);
            builder.Append(" devices for '");
            builder.Append(FormatDeviceName(query, redactOutput));
            builder.AppendLine("':");
            for (int index = 0; index < items.Count; index++)
            {
                builder.Append(index + 1);
                builder.Append(". ");
                builder.Append(items[index].Name);
                builder.Append(" (");
                builder.Append(items[index].Id);
                builder.AppendLine(")");
            }

            return builder.ToString().TrimEnd();
        }

        public static string FormatCycleList(string kind, IReadOnlyList<CycleDevice> cycleDevices, bool jsonOutput, bool redactOutput = false)
        {
            var cycle = new List<CliCycleItem>(cycleDevices.Count);
            for (int index = 0; index < cycleDevices.Count; index++)
            {
                var device = cycleDevices[index];
                cycle.Add(new CliCycleItem(index + 1, FormatDeviceId(device.Id, redactOutput), FormatDeviceName(device.Name, redactOutput)));
            }

            if (jsonOutput)
            {
                return SerializeCliJson(new CliCycleSnapshot(kind, cycle));
            }

            if (cycle.Count == 0)
            {
                return $"No configured {kind} cycle devices.";
            }

            var builder = new StringBuilder();
            foreach (var item in cycle)
            {
                builder.Append(item.Order);
                builder.Append(". ");
                builder.AppendLine(item.Name);
            }

            return builder.ToString().TrimEnd();
        }

        public static string FormatCycleValidation(
            string kind,
            IReadOnlyList<string> duplicateDeviceNames,
            IReadOnlyList<string> disconnectedDeviceNames,
            bool jsonOutput,
            bool redactOutput = false)
        {
            var snapshot = new CliCycleValidationSnapshot(
                kind,
                RedactDeviceNames(duplicateDeviceNames, redactOutput),
                RedactDeviceNames(disconnectedDeviceNames, redactOutput),
                duplicateDeviceNames.Count == 0 && disconnectedDeviceNames.Count == 0);

            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            if (snapshot.IsValid)
            {
                return $"{kind} cycle is valid.";
            }

            var lines = new List<string> { $"{kind} cycle has issues:" };
            if (snapshot.DuplicateDeviceNames.Count > 0)
            {
                lines.Add($"- duplicate devices: {string.Join(", ", snapshot.DuplicateDeviceNames)}");
            }

            if (snapshot.DisconnectedDeviceNames.Count > 0)
            {
                lines.Add($"- disconnected devices: {string.Join(", ", snapshot.DisconnectedDeviceNames)}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static string FormatCycleTest(
            string kind,
            int configuredCount,
            int connectedConfiguredCount,
            bool hasDefaultInputDevice,
            IReadOnlyList<string> reasons,
            bool jsonOutput,
            bool redactOutput = false)
        {
            bool canSwitch = reasons.Count == 0;
            var snapshot = new CliCycleTestSnapshot(
                kind,
                configuredCount,
                connectedConfiguredCount,
                hasDefaultInputDevice,
                canSwitch,
                redactOutput ? RedactQuotedLiteralList(reasons) : reasons);

            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            if (snapshot.CanSwitch)
            {
                return $"{kind} cycle test passed.";
            }

            return $"{kind} cycle test failed: {string.Join(", ", snapshot.Reasons)}";
        }

        public static string FormatCycleMutationResult(
            string kind,
            string action,
            string diagCode,
            IReadOnlyList<CycleDevice> cycleDevices,
            string? deviceId,
            string? deviceName,
            bool jsonOutput,
            bool redactOutput = false)
        {
            var cycle = new List<CliCycleItem>(cycleDevices.Count);
            for (int index = 0; index < cycleDevices.Count; index++)
            {
                cycle.Add(new CliCycleItem(index + 1, FormatDeviceId(cycleDevices[index].Id, redactOutput), FormatDeviceName(cycleDevices[index].Name, redactOutput)));
            }

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = true,
                    Kind = kind,
                    Action = action,
                    DiagCode = diagCode,
                    DeviceId = FormatOptionalDeviceId(deviceId, redactOutput),
                    DeviceName = FormatOptionalDeviceName(deviceName, redactOutput),
                    Cycle = cycle,
                });
            }

            string actionText = action switch
            {
                "add" => $"Added '{FormatDeviceName(deviceName, redactOutput)}' to {kind} cycle.",
                "remove" => $"Removed '{FormatDeviceName(deviceName, redactOutput)}' from {kind} cycle.",
                "reorder" => $"Reordered {kind} cycle.",
                _ => $"Updated {kind} cycle.",
            };

            return $"[diag-code:{diagCode}] {actionText}{Environment.NewLine}{FormatCycleList(kind, cycleDevices, jsonOutput: false, redactOutput)}";
        }
    }
}
