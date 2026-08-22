using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public const string JsonSchemaVersion = "1.0.0";

        private static readonly JsonSerializerOptions CliJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly Regex QuotedLiteralRegex = MyRegex();

        public static string FormatStartupStatus(bool startupEnabled, bool jsonOutput)
        {
            if (jsonOutput)
            {
                return SerializeCliJson(new { StartupEnabled = startupEnabled });
            }

            return startupEnabled ? "enabled" : "disabled";
        }

        private static string BuildRuntimeText(CliRuntimeTuningSnapshot? runtime)
        {
            if (runtime == null)
            {
                return string.Empty;
            }

            return
                $"runtime output switch debounce ms: {runtime.OutputSwitchDebounceMs}{Environment.NewLine}" +
                $"runtime input switch debounce ms: {runtime.InputSwitchDebounceMs}{Environment.NewLine}" +
                $"runtime switch retry delay ms: {runtime.SwitchRetryDelayMs}{Environment.NewLine}" +
                $"runtime switch retry max delay ms: {runtime.SwitchRetryMaxDelayMs}{Environment.NewLine}" +
                $"runtime switch max retries: {runtime.SwitchMaxRetries}{Environment.NewLine}" +
                $"runtime hotplug refresh debounce ms: {runtime.HotplugRefreshDebounceMs}{Environment.NewLine}" +
                $"runtime hotplug connected overlay suppress ms: {runtime.HotplugConnectedOverlaySuppressAfterSwitchMs}{Environment.NewLine}" +
                $"runtime mixer session refresh debounce ms: {runtime.MixerSessionRefreshDebounceMs}{Environment.NewLine}" +
                $"runtime mixer snapshot cache interactive ms: {runtime.MixerSnapshotCacheInteractiveMs}{Environment.NewLine}" +
                $"runtime mixer snapshot cache background ms: {runtime.MixerSnapshotCacheBackgroundMs}{Environment.NewLine}" +
                $"runtime resume hotkey retry delay ms: {runtime.ResumeHotkeyRetryDelayMs}{Environment.NewLine}" +
                $"runtime mixer diagnostics summary window seconds: {runtime.MixerDiagnosticsSummaryWindowSeconds}{Environment.NewLine}" +
                $"runtime mixer diagnostics log every n refreshes: {runtime.MixerCacheWindowDiagnosticsLogEveryNRefreshes}{Environment.NewLine}" +
                $"runtime bluetooth reconnect success observed recheck interval ms: {runtime.BluetoothReconnectSuccessObservedRecheckIntervalMs}{Environment.NewLine}" +
                $"runtime bluetooth reconnect timeout circuit threshold: {runtime.BluetoothReconnectTimeoutCircuitThreshold}{Environment.NewLine}" +
                $"runtime bluetooth reconnect timeout circuit open ms: {runtime.BluetoothReconnectTimeoutCircuitOpenMs}";
        }

        private static string DisplayPath(string path, bool includeSensitivePaths, bool filePath, bool redactOutput)
        {
            if (includeSensitivePaths)
            {
                return path;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return "<redacted>";
            }

            if (redactOutput)
            {
                return FormatPath(path, redactOutput);
            }

            if (filePath)
            {
                string fileName = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(fileName) ? "<redacted>" : fileName;
            }

            return "<redacted>";
        }

        private static List<string> DisplayPaths(IReadOnlyList<string> paths, bool includeSensitivePaths, bool redactOutput)
        {
            var displayed = new List<string>(paths.Count);
            for (int index = 0; index < paths.Count; index++)
            {
                displayed.Add(DisplayPath(paths[index], includeSensitivePaths, filePath: true, redactOutput));
            }

            return displayed;
        }

        private static string FormatListenState(bool? enabled)
        {
            return enabled.HasValue
                ? (enabled.Value ? "enabled" : "disabled")
                : "unknown";
        }

        private static string FormatMonitorTarget(string? monitorTargetOutputDeviceName)
        {
            return string.IsNullOrWhiteSpace(monitorTargetOutputDeviceName)
                ? "unknown"
                : monitorTargetOutputDeviceName;
        }

        private static string BuildWarningSection(IReadOnlyList<string> warnings)
        {
            if (warnings.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("warnings:");
            foreach (string warning in warnings)
            {
                builder.Append("- ");
                builder.AppendLine(warning);
            }

            return builder.ToString().TrimEnd();
        }

        public static string SerializeCliJson<T>(T data)
        {
            return JsonSerializer.Serialize(new CliJsonEnvelope<T>(JsonSchemaVersion, data), CliJsonOptions);
        }

        public static string FormatDeviceName(string? deviceName, bool redactOutput)
        {
            return redactOutput ? RedactLabel(deviceName, "device") : deviceName ?? string.Empty;
        }

        public static string FormatDeviceId(string? deviceId, bool redactOutput)
        {
            return redactOutput ? RedactLabel(deviceId, "device-id") : deviceId ?? string.Empty;
        }

        public static string? FormatOptionalDeviceName(string? deviceName, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(deviceName) ? deviceName : FormatDeviceName(deviceName, redactOutput);
        }

        public static string? FormatOptionalDeviceId(string? deviceId, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(deviceId) ? deviceId : FormatDeviceId(deviceId, redactOutput);
        }

        public static string FormatRoutineName(string? routineName, bool redactOutput)
        {
            return redactOutput ? RedactLabel(routineName, "routine") : routineName ?? string.Empty;
        }

        public static string FormatMediaField(string? value, bool redactOutput)
        {
            return redactOutput ? RedactLabel(value, "media") : value ?? string.Empty;
        }

        public static string? FormatOptionalMediaField(string? value, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(value) ? value : FormatMediaField(value, redactOutput);
        }

        public static string FormatMediaSource(string? source, bool redactOutput)
        {
            return redactOutput ? RedactLabel(source, "source") : source ?? string.Empty;
        }

        public static string FormatNetworkName(string? networkName, bool redactOutput)
        {
            return redactOutput ? RedactLabel(networkName, "network") : networkName ?? string.Empty;
        }

        public static string FormatNetworkList(IReadOnlyList<string> networkNames, bool jsonOutput, bool redactOutput)
        {
            string[] formattedNetworks =
            [
                .. networkNames.Select(networkName => FormatNetworkName(networkName, redactOutput))
            ];

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = true,
                    Count = formattedNetworks.Length,
                    Networks = formattedNetworks
                });
            }

            if (formattedNetworks.Length == 0)
            {
                return "No networks detected.";
            }

            return string.Join(Environment.NewLine, formattedNetworks);
        }

        public static string? FormatOptionalMediaSource(string? source, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(source) ? source : FormatMediaSource(source, redactOutput);
        }

        private static string GetVolumeLabel(string kind)
        {
            return kind switch
            {
                "master" => "Master volume",
                "mic" => "Microphone volume",
                _ => "Volume",
            };
        }

        private static string GetMuteStatusDiagCode(string target)
        {
            return target switch
            {
                "mic" => "mute-mic-status",
                "sound" => "mute-sound-status",
                "deafen" => "deafen-status",
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };
        }

        private static string GetMuteStatusLabel(string target)
        {
            return target switch
            {
                "mic" => "Microphone mute",
                "sound" => "Playback mute",
                "deafen" => "Deafen",
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };
        }

        private static string FormatRoutineConflictSummary(
            AudioRoutine routine,
            IReadOnlyDictionary<string, string> conflictSummaries,
            bool redactOutput)
        {
            string conflictKey = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id.Trim();
            if (!conflictSummaries.TryGetValue(conflictKey, out string? summary) || string.IsNullOrWhiteSpace(summary))
            {
                return string.Empty;
            }

            return redactOutput ? RedactRoutineConflictSummary(summary) : summary;
        }

        public static string? FormatOptionalRoutineName(string? routineName, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(routineName) ? routineName : FormatRoutineName(routineName, redactOutput);
        }

        public static string FormatPath(string? path, bool redactOutput)
        {
            return redactOutput ? RedactLabel(path, "path") : path ?? string.Empty;
        }

        public static string? FormatOptionalPath(string? path, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(path) ? path : FormatPath(path, redactOutput);
        }

        public static string FormatProcessName(string? processName, bool redactOutput)
        {
            return redactOutput ? RedactLabel(processName, "process") : processName ?? string.Empty;
        }

        public static string? FormatOptionalProcessName(string? processName, bool redactOutput)
        {
            return string.IsNullOrWhiteSpace(processName) ? processName : FormatProcessName(processName, redactOutput);
        }

        public static IReadOnlyList<string> RedactWarnings(IReadOnlyList<string> warnings, bool redactOutput)
        {
            if (!redactOutput || warnings.Count == 0)
            {
                return warnings;
            }

            string[] redacted = new string[warnings.Count];
            for (int index = 0; index < warnings.Count; index++)
            {
                redacted[index] = RedactDiagnosticWarning(warnings[index]);
            }

            return redacted;
        }

        public static IReadOnlyList<string> RedactDeviceNames(IReadOnlyList<string> names, bool redactOutput)
        {
            if (!redactOutput || names.Count == 0)
            {
                return names;
            }

            string[] redacted = new string[names.Count];
            for (int index = 0; index < names.Count; index++)
            {
                redacted[index] = FormatDeviceName(names[index], redactOutput);
            }

            return redacted;
        }

        public static string RedactQuotedLiterals(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value ?? string.Empty;
            }

            return QuotedLiteralRegex.Replace(value, static match => $"'{RedactLabel(match.Groups[1].Value, "value")}'");
        }

        private static string RedactDiagnosticWarning(string? warning)
        {
            string redacted = RedactQuotedLiterals(warning);
            if (string.IsNullOrWhiteSpace(redacted))
            {
                return redacted;
            }

            redacted = DisconnectedDeviceWarningRegex().Replace(redacted, static match =>
            {
                string prefix = match.Groups["prefix"].Value;
                string names = match.Groups["names"].Value;
                string suffix = match.Groups["suffix"].Value;
                string[] redactedNames =
                [
                    .. names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(static name => FormatDeviceName(name, redactOutput: true))
                ];
                return prefix + string.Join(", ", redactedNames) + suffix;
            });

            redacted = MatchingIdsRegex().Replace(redacted, static match =>
            {
                string ids = match.Groups["ids"].Value;
                string[] redactedIds =
                [
                    .. ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(static id => FormatDeviceId(id, redactOutput: true))
                ];
                return "Matching IDs: " + string.Join(", ", redactedIds);
            });

            return redacted;
        }

        private static IReadOnlyList<string> RedactQuotedLiteralList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return values;
            }

            string[] redacted = new string[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                redacted[index] = RedactQuotedLiterals(values[index]);
            }

            return redacted;
        }

        private static string RedactRoutineMessage(string message, AudioRoutine? routine, string? triggerApplicationName)
        {
            string redacted = message;

            if (!string.IsNullOrWhiteSpace(routine?.Name))
            {
                redacted = redacted.Replace(routine.Name, FormatRoutineName(routine.Name, redactOutput: true), StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(triggerApplicationName))
            {
                redacted = redacted.Replace(triggerApplicationName, FormatProcessName(triggerApplicationName, redactOutput: true), StringComparison.Ordinal);
            }

            return RedactQuotedLiterals(redacted);
        }

        private static string RedactRoutineConflictSummary(string summary)
        {
            const string prefix = "Application trigger for ";
            const string marker = " conflicts with";

            int startIndex = summary.IndexOf(prefix, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                startIndex = summary.IndexOf(marker, StringComparison.Ordinal);
            }
            if (startIndex >= 0)
            {
                int markerIndex = summary.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex > startIndex + prefix.Length)
                {
                    string triggerTarget = summary[(startIndex + prefix.Length)..markerIndex];
                    string redactedTarget = RedactLabel(triggerTarget, "process");
                    return string.Concat(
                        summary.AsSpan(0, startIndex + prefix.Length),
                        redactedTarget,
                        summary.AsSpan(markerIndex));
                }
            }

            return RedactQuotedLiterals(summary);
        }

        private static string RedactLabel(string? value, string kind)
        {
            return $"{kind}[{LogPrivacy.Label(value)}]";
        }

        private static CliExecutionHistoryEntrySnapshot CreateExecutionHistorySnapshot(ExecutionHistoryEntry entry, bool redactOutput)
        {
            IReadOnlyDictionary<string, string>? details = entry.Details?.ToDictionary(
                    static pair => pair.Key,
                    pair => redactOutput ? RedactQuotedLiterals(pair.Value) : pair.Value,
                    StringComparer.OrdinalIgnoreCase);

            return new CliExecutionHistoryEntrySnapshot(
                entry.OpId,
                entry.TimestampUtc.ToString("O"),
                entry.Kind.ToString().ToLowerInvariant(),
                entry.Source,
                entry.Action,
                entry.Success,
                entry.Skipped,
                redactOutput ? RedactQuotedLiterals(entry.Summary) : entry.Summary,
                redactOutput ? RedactQuotedLiterals(entry.Reason) : entry.Reason,
                entry.RoutineId,
                FormatOptionalRoutineName(entry.RoutineName, redactOutput),
                FormatOptionalDeviceName(entry.OutputDeviceName, redactOutput),
                FormatOptionalDeviceName(entry.InputDeviceName, redactOutput),
                RedactExecutionHistoryTarget(entry, redactOutput),
                entry.OutputSucceeded,
                entry.InputSucceeded,
                entry.Enabled,
                entry.AwaitingAppCompletion,
                entry.AppOutputApplied,
                entry.AppInputApplied,
                entry.DiagCode,
                entry.ElapsedMs,
                details);
        }

        private static void AppendOptionalDetailLine(List<string> lines, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{label}: {value}");
            }
        }

        private static string? RedactExecutionHistoryTarget(ExecutionHistoryEntry entry, bool redactOutput)
        {
            if (!redactOutput || string.IsNullOrWhiteSpace(entry.Target))
            {
                return entry.Target;
            }

            string target = entry.Target;
            target = ReplaceSensitiveValue(target, entry.OutputDeviceName, FormatDeviceName(entry.OutputDeviceName, redactOutput: true));
            target = ReplaceSensitiveValue(target, entry.InputDeviceName, FormatDeviceName(entry.InputDeviceName, redactOutput: true));
            target = ReplaceSensitiveValue(target, entry.RoutineName, FormatRoutineName(entry.RoutineName, redactOutput: true));
            return RedactQuotedLiterals(target);
        }

        private static string ReplaceSensitiveValue(string text, string? rawValue, string redactedValue)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(rawValue))
            {
                return text;
            }

            return text.Replace(rawValue, redactedValue, StringComparison.Ordinal);
        }

        [GeneratedRegex(@"(?<prefix>\b(?:Output|Input) cycle includes disconnected devices?: )(?<names>[^.]+)(?<suffix>\.)", RegexOptions.CultureInvariant)]
        private static partial Regex DisconnectedDeviceWarningRegex();

        [GeneratedRegex(@"Matching IDs: (?<ids>[^.]+)", RegexOptions.CultureInvariant)]
        private static partial Regex MatchingIdsRegex();

        private static string FormatOptionalOutput(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
        }

        private sealed record CliStatusSnapshot(
            bool StartupEnabled,
            int AvailableOutputDevices,
            int AvailableInputDevices,
            int ConfiguredOutputCycleDevices,
            int ConfiguredInputCycleDevices,
            bool? CurrentInputListenEnabled,
            string? ListenMonitorTargetOutputDeviceName,
            IReadOnlyList<string> Warnings,
            CliBluetoothReconnectSnapshot? BluetoothReconnect,
            CliRuntimeTuningSnapshot? RuntimeTuning);

        private sealed record CliListenStatusSnapshot(
            bool? CurrentInputListenEnabled,
            string? ListenMonitorTargetOutputDeviceName);

        private sealed record CliMediaStatusSnapshot(
            bool HasSession,
            string? PlaybackStatus,
            string? Title,
            string? Artist,
            string? AlbumTitle,
            string? SourceAppUserModelId,
            long? PositionSeconds);

        private sealed record CliRoutineListSnapshot(IReadOnlyList<CliRoutineItem> Routines);

        private sealed record CliRoutineItem(
            int Order,
            string Id,
            string Name,
            bool Enabled,
            string Hotkey,
            string TriggerMode,
            bool UsesApplicationTrigger,
            string TriggerAppPath,
            string ScheduleTime,
            string ScheduleDays,
            string ScheduleTimeZone,
            bool RestorePreviousAudioOnDeactivate,
            bool SwitchOutputPerApp,
            bool ShowInTrayMenu,
            string TriggerSummary,
            string OutputDeviceId,
            string OutputDeviceName,
            string InputDeviceId,
            string InputDeviceName,
            string TargetSummary,
            string ConflictSummary);

        private sealed record CliRoutineRunSnapshot(
            string Id,
            string Name,
            bool Enabled,
            string TriggerMode,
            bool UsesApplicationTrigger,
            string TriggerAppPath,
            string ScheduleTime,
            string ScheduleDays,
            string ScheduleTimeZone,
            bool RestorePreviousAudioOnDeactivate,
            bool SwitchOutputPerApp,
            bool ShowInTrayMenu,
            string TriggerSummary,
            string? AppliedOutputDeviceName,
            string? AppliedInputDeviceName,
            string DiagCode);

        private sealed record CliRoutineStateSnapshot(
            string Id,
            string Name,
            bool Enabled,
            bool Updated,
            string DiagCode);

        private sealed record CliRoutineErrorSnapshot(
            string Code,
            string Message,
            int ExitCode,
            string? RoutineId,
            string? RoutineName,
            string? TriggerMode,
            bool? UsesApplicationTrigger,
            string? TriggerAppPath,
            string? ScheduleTime,
            string? ScheduleDays,
            string? ScheduleTimeZone,
            string? TriggerApplicationName,
            bool? RequiresRunningTriggerProcess,
            bool? RestorePreviousAudioOnDeactivate,
            bool? SwitchOutputPerApp,
            string? OutputDeviceId,
            string? InputDeviceId,
            bool? OutputSucceeded,
            string? AppliedOutputDeviceName,
            string? OutputFailureDetail,
            bool? InputSucceeded,
            string? AppliedInputDeviceName,
            string? InputFailureDetail,
            bool PartialFailure);

        private sealed record CliExecutionHistorySnapshot(IReadOnlyList<CliExecutionHistoryEntrySnapshot> Entries);

        private sealed record CliExecutionHistoryEntrySnapshot(
            string OpId,
            string TimestampUtc,
            string Kind,
            string Source,
            string Action,
            bool Success,
            bool Skipped,
            string? Summary,
            string? Reason,
            string? RoutineId,
            string? RoutineName,
            string? OutputDeviceName,
            string? InputDeviceName,
            string? Target,
            bool? OutputSucceeded,
            bool? InputSucceeded,
            bool? Enabled,
            bool? AwaitingAppCompletion,
            bool? AppOutputApplied,
            bool? AppInputApplied,
            string? DiagCode,
            double? ElapsedMs,
            IReadOnlyDictionary<string, string>? Details);

        private sealed record CliDiagnosticsSnapshot(
            string LogFilePath,
            bool LogFileExists,
            long LogFileBytes,
            string LogBackupDirectory,
            IReadOnlyList<string> LogBackupFiles,
            int LogBackupRetentionCount,
            int LogBackupMaxAgeDays,
            string SettingsPath,
            string SettingsBackupDirectory,
            IReadOnlyList<string> SettingsBackupFiles,
            int SettingsBackupRetentionCount,
            bool PathsRedacted,
            CliBluetoothReconnectSnapshot? BluetoothReconnect,
            CliRuntimeTuningSnapshot? RuntimeTuning);

        private sealed record CliBluetoothReconnectSnapshot(
            bool Enabled,
            int MaxAttempts,
            int AttemptTimeoutMs,
            int CooldownMs,
            bool OnlyLikelyBluetoothEndpoints);

        private sealed record CliRuntimeTuningSnapshot(
            int OutputSwitchDebounceMs,
            int InputSwitchDebounceMs,
            int SwitchRetryDelayMs,
            int SwitchRetryMaxDelayMs,
            int SwitchMaxRetries,
            int HotplugRefreshDebounceMs,
            int HotplugConnectedOverlaySuppressAfterSwitchMs,
            int MixerSessionRefreshDebounceMs,
            int MixerSnapshotCacheInteractiveMs,
            int MixerSnapshotCacheBackgroundMs,
            int ResumeHotkeyRetryDelayMs,
            int MixerDiagnosticsSummaryWindowSeconds,
            int MixerCacheWindowDiagnosticsLogEveryNRefreshes,
            int BluetoothReconnectSuccessObservedRecheckIntervalMs,
            int BluetoothReconnectTimeoutCircuitThreshold,
            int BluetoothReconnectTimeoutCircuitOpenMs);

        private sealed record CliDeviceListSnapshot(string Kind, IReadOnlyList<CliDeviceItem> Devices);

        private sealed record CliDeviceItem(string Id, string Name);

        private sealed record CliCycleSnapshot(string Kind, IReadOnlyList<CliCycleItem> Devices);

        private sealed record CliCycleItem(int Order, string Id, string Name);

        private sealed record CliCycleValidationSnapshot(
            string Kind,
            IReadOnlyList<string> DuplicateDeviceNames,
            IReadOnlyList<string> DisconnectedDeviceNames,
            bool IsValid);

        private sealed record CliCycleTestSnapshot(
            string Kind,
            int ConfiguredCount,
            int ConnectedConfiguredCount,
            bool HasDefaultInputDevice,
            bool CanSwitch,
            IReadOnlyList<string> Reasons);

        private sealed record CliConfigValidationSnapshot(
            bool IsValid,
            IReadOnlyList<string> Warnings);

        private sealed record CliJsonEnvelope<T>(string SchemaVersion, T Data);

        [GeneratedRegex("'([^']+)'", RegexOptions.Compiled)]
        private static partial Regex MyRegex();
    }
}
