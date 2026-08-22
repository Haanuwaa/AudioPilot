using System.Text;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public static string FormatExecutionHistory(IReadOnlyList<ExecutionHistoryEntry> entries, bool jsonOutput, bool redactOutput = false)
        {
            List<CliExecutionHistoryEntrySnapshot> snapshots =
            [
                .. entries.Select(entry => CreateExecutionHistorySnapshot(entry, redactOutput))
            ];

            if (jsonOutput)
            {
                return SerializeCliJson(new CliExecutionHistorySnapshot(snapshots));
            }

            if (snapshots.Count == 0)
            {
                return "No execution history is available for the current session.";
            }

            var builder = new StringBuilder();
            foreach (CliExecutionHistoryEntrySnapshot entry in snapshots)
            {
                builder.Append(entry.TimestampUtc);
                builder.Append(" | ");
                builder.Append(entry.Kind);
                builder.Append(" | ");
                builder.Append(entry.Action);
                builder.Append(" | ");
                builder.Append(entry.Success ? "success" : entry.Skipped ? "skipped" : "failed");
                builder.Append(" | ");
                builder.Append(entry.Summary);

                if (!string.IsNullOrWhiteSpace(entry.RoutineName))
                {
                    builder.Append(" | routine: ");
                    builder.Append(entry.RoutineName);
                }

                if (!string.IsNullOrWhiteSpace(entry.OutputDeviceName))
                {
                    builder.Append(" | output: ");
                    builder.Append(entry.OutputDeviceName);
                }

                if (!string.IsNullOrWhiteSpace(entry.InputDeviceName))
                {
                    builder.Append(" | input: ");
                    builder.Append(entry.InputDeviceName);
                }

                if (!string.IsNullOrWhiteSpace(entry.Reason))
                {
                    builder.Append(" | reason: ");
                    builder.Append(entry.Reason);
                }

                if (!string.IsNullOrWhiteSpace(entry.DiagCode))
                {
                    builder.Append(" | diag: ");
                    builder.Append(entry.DiagCode);
                }

                if (entry.ElapsedMs.HasValue)
                {
                    builder.Append(" | elapsedMs: ");
                    builder.Append(entry.ElapsedMs.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                }

                builder.Append(" | opId: ");
                builder.AppendLine(entry.OpId);
            }

            return builder.ToString().TrimEnd();
        }

        public static string FormatExecutionHistoryDetail(ExecutionHistoryEntry entry, bool jsonOutput, bool redactOutput = false)
        {
            CliExecutionHistoryEntrySnapshot snapshot = CreateExecutionHistorySnapshot(entry, redactOutput);
            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            var lines = new List<string>
            {
                $"opId: {snapshot.OpId}",
                $"timestampUtc: {snapshot.TimestampUtc}",
                $"kind: {snapshot.Kind}",
                $"source: {snapshot.Source}",
                $"action: {snapshot.Action}",
                $"success: {snapshot.Success}",
                $"skipped: {snapshot.Skipped}",
                $"summary: {snapshot.Summary}",
            };

            AppendOptionalDetailLine(lines, "reason", snapshot.Reason);
            AppendOptionalDetailLine(lines, "routineId", snapshot.RoutineId);
            AppendOptionalDetailLine(lines, "routineName", snapshot.RoutineName);
            AppendOptionalDetailLine(lines, "target", snapshot.Target);
            AppendOptionalDetailLine(lines, "outputDeviceName", snapshot.OutputDeviceName);
            AppendOptionalDetailLine(lines, "inputDeviceName", snapshot.InputDeviceName);
            AppendOptionalDetailLine(lines, "outputSucceeded", snapshot.OutputSucceeded?.ToString());
            AppendOptionalDetailLine(lines, "inputSucceeded", snapshot.InputSucceeded?.ToString());
            AppendOptionalDetailLine(lines, "enabled", snapshot.Enabled?.ToString());
            AppendOptionalDetailLine(lines, "awaitingAppCompletion", snapshot.AwaitingAppCompletion?.ToString());
            AppendOptionalDetailLine(lines, "appOutputApplied", snapshot.AppOutputApplied?.ToString());
            AppendOptionalDetailLine(lines, "appInputApplied", snapshot.AppInputApplied?.ToString());
            AppendOptionalDetailLine(lines, "diagCode", snapshot.DiagCode);
            AppendOptionalDetailLine(lines, "elapsedMs", snapshot.ElapsedMs?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Details is { Count: > 0 })
            {
                foreach (KeyValuePair<string, string> detail in snapshot.Details.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    AppendOptionalDetailLine(lines, $"detail.{detail.Key}", detail.Value);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static string FormatExecutionHistoryNotFound(string opId, bool jsonOutput)
        {
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = false,
                    DiagCode = "diagnostics-history-not-found",
                    Error = $"No execution history entry with opId '{opId}' was found in the current session.",
                });
            }

            return $"[diag-code:diagnostics-history-not-found] No execution history entry with opId '{opId}' was found in the current session.";
        }

        public static string FormatDiagnosticsStatus(
            string logFilePath,
            bool logFileExists,
            long logFileBytes,
            string logBackupDirectory,
            IReadOnlyList<string> logBackupFiles,
            int logBackupRetentionCount,
            int logBackupMaxAgeDays,
            string settingsPath,
            string settingsBackupDirectory,
            IReadOnlyList<string> settingsBackupFiles,
            int settingsBackupRetentionCount,
            bool jsonOutput,
            bool includeSensitivePaths = false,
            bool redactOutput = false)
        {
            return FormatDiagnosticsStatus(
                logFilePath,
                logFileExists,
                logFileBytes,
                logBackupDirectory,
                logBackupFiles,
                logBackupRetentionCount,
                logBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                settingsBackupRetentionCount,
                jsonOutput,
                bluetoothReconnectEnabled: false,
                bluetoothReconnectMaxAttempts: 0,
                bluetoothReconnectAttemptTimeoutMs: 0,
                bluetoothReconnectCooldownMs: 0,
                bluetoothReconnectOnlyLikely: false,
                outputSwitchDebounceMs: 0,
                inputSwitchDebounceMs: 0,
                switchRetryDelayMs: 0,
                switchRetryMaxDelayMs: 0,
                switchMaxRetries: 0,
                hotplugRefreshDebounceMs: 0,
                hotplugConnectedOverlaySuppressAfterSwitchMs: 0,
                mixerSessionRefreshDebounceMs: 0,
                mixerSnapshotCacheInteractiveMs: 0,
                mixerSnapshotCacheBackgroundMs: 0,
                mixerDiagnosticsSummaryWindowSeconds: 0,
                mixerCacheWindowDiagnosticsLogEveryNRefreshes: 0,
                resumeHotkeyRetryDelayMs: 0,
                bluetoothReconnectSuccessObservedRecheckIntervalMs: 0,
                bluetoothReconnectTimeoutCircuitThreshold: 0,
                bluetoothReconnectTimeoutCircuitOpenMs: 0,
                includeSensitivePaths: includeSensitivePaths,
                includeRuntimeTuning: false,
                includeBluetoothReconnect: false,
                redactOutput: redactOutput);
        }

        public static string FormatDiagnosticsStatus(
            string logFilePath,
            bool logFileExists,
            long logFileBytes,
            string logBackupDirectory,
            IReadOnlyList<string> logBackupFiles,
            int logBackupRetentionCount,
            int logBackupMaxAgeDays,
            string settingsPath,
            string settingsBackupDirectory,
            IReadOnlyList<string> settingsBackupFiles,
            int settingsBackupRetentionCount,
            bool jsonOutput,
            bool bluetoothReconnectEnabled,
            int bluetoothReconnectMaxAttempts,
            int bluetoothReconnectAttemptTimeoutMs,
            int bluetoothReconnectCooldownMs,
            bool bluetoothReconnectOnlyLikely,
            int outputSwitchDebounceMs,
            int inputSwitchDebounceMs,
            int switchRetryDelayMs,
            int switchRetryMaxDelayMs,
            int switchMaxRetries,
            int hotplugRefreshDebounceMs,
            int hotplugConnectedOverlaySuppressAfterSwitchMs,
            int mixerSessionRefreshDebounceMs,
            int mixerSnapshotCacheInteractiveMs,
            int mixerSnapshotCacheBackgroundMs,
            int resumeHotkeyRetryDelayMs,
            int mixerDiagnosticsSummaryWindowSeconds,
            int mixerCacheWindowDiagnosticsLogEveryNRefreshes,
            int bluetoothReconnectSuccessObservedRecheckIntervalMs,
            int bluetoothReconnectTimeoutCircuitThreshold,
            int bluetoothReconnectTimeoutCircuitOpenMs,
            bool includeSensitivePaths = false,
            bool includeRuntimeTuning = true,
            bool includeBluetoothReconnect = true,
            bool redactOutput = false)
        {
            CliBluetoothReconnectSnapshot? bluetoothReconnect = includeBluetoothReconnect
                ? new CliBluetoothReconnectSnapshot(
                    bluetoothReconnectEnabled,
                    bluetoothReconnectMaxAttempts,
                    bluetoothReconnectAttemptTimeoutMs,
                    bluetoothReconnectCooldownMs,
                    bluetoothReconnectOnlyLikely)
                : null;

            CliRuntimeTuningSnapshot? runtimeTuning = includeRuntimeTuning
                ? new CliRuntimeTuningSnapshot(
                    outputSwitchDebounceMs,
                    inputSwitchDebounceMs,
                    switchRetryDelayMs,
                    switchRetryMaxDelayMs,
                    switchMaxRetries,
                    hotplugRefreshDebounceMs,
                    hotplugConnectedOverlaySuppressAfterSwitchMs,
                    mixerSessionRefreshDebounceMs,
                    mixerSnapshotCacheInteractiveMs,
                    mixerSnapshotCacheBackgroundMs,
                    resumeHotkeyRetryDelayMs,
                    mixerDiagnosticsSummaryWindowSeconds,
                    mixerCacheWindowDiagnosticsLogEveryNRefreshes,
                    bluetoothReconnectSuccessObservedRecheckIntervalMs,
                    bluetoothReconnectTimeoutCircuitThreshold,
                    bluetoothReconnectTimeoutCircuitOpenMs)
                : null;

            bool displaySensitivePaths = includeSensitivePaths && !redactOutput;
            string displayedLogFilePath = DisplayPath(logFilePath, displaySensitivePaths, filePath: true, redactOutput);
            string displayedLogBackupDirectory = DisplayPath(logBackupDirectory, displaySensitivePaths, filePath: false, redactOutput);
            List<string> displayedLogBackups = DisplayPaths(logBackupFiles, displaySensitivePaths, redactOutput);
            string displayedSettingsPath = DisplayPath(settingsPath, displaySensitivePaths, filePath: true, redactOutput);
            string displayedSettingsBackupDirectory = DisplayPath(settingsBackupDirectory, displaySensitivePaths, filePath: false, redactOutput);
            List<string> displayedSettingsBackups = DisplayPaths(settingsBackupFiles, displaySensitivePaths, redactOutput);

            var status = new CliDiagnosticsSnapshot(
                displayedLogFilePath,
                logFileExists,
                logFileBytes,
                displayedLogBackupDirectory,
                displayedLogBackups,
                logBackupRetentionCount,
                logBackupMaxAgeDays,
                displayedSettingsPath,
                displayedSettingsBackupDirectory,
                displayedSettingsBackups,
                settingsBackupRetentionCount,
                PathsRedacted: !displaySensitivePaths,
                bluetoothReconnect,
                runtimeTuning);

            if (jsonOutput)
            {
                return SerializeCliJson(status);
            }

            string baseOutput = $"log file: {status.LogFilePath}{Environment.NewLine}" +
                       $"log exists: {status.LogFileExists}{Environment.NewLine}" +
                       $"log bytes: {status.LogFileBytes}{Environment.NewLine}" +
                       $"log backup dir: {status.LogBackupDirectory}{Environment.NewLine}" +
                       $"log backups: {status.LogBackupFiles.Count}{Environment.NewLine}" +
                       $"log retention: count={status.LogBackupRetentionCount}, days={status.LogBackupMaxAgeDays}{Environment.NewLine}" +
                       $"settings file: {status.SettingsPath}{Environment.NewLine}" +
                       $"settings backup dir: {status.SettingsBackupDirectory}{Environment.NewLine}" +
                       $"settings backups: {status.SettingsBackupFiles.Count}{Environment.NewLine}" +
                       $"settings retention: count={status.SettingsBackupRetentionCount}{Environment.NewLine}" +
                       $"paths redacted: {status.PathsRedacted}";

            if (status.BluetoothReconnect == null && status.RuntimeTuning == null)
            {
                return baseOutput;
            }

            if (status.BluetoothReconnect == null)
            {
                return baseOutput + Environment.NewLine + BuildRuntimeText(status.RuntimeTuning);
            }

            string bluetoothText =
                $"bluetooth reconnect enabled: {status.BluetoothReconnect.Enabled}{Environment.NewLine}" +
                $"bluetooth reconnect max attempts: {status.BluetoothReconnect.MaxAttempts}{Environment.NewLine}" +
                $"bluetooth reconnect attempt timeout ms: {status.BluetoothReconnect.AttemptTimeoutMs}{Environment.NewLine}" +
                $"bluetooth reconnect cooldown ms: {status.BluetoothReconnect.CooldownMs}{Environment.NewLine}" +
                $"bluetooth reconnect only likely: {status.BluetoothReconnect.OnlyLikelyBluetoothEndpoints}";

            string runtimeText = BuildRuntimeText(status.RuntimeTuning);

            if (string.IsNullOrWhiteSpace(runtimeText))
            {
                return baseOutput + Environment.NewLine + bluetoothText;
            }

            return baseOutput + Environment.NewLine + bluetoothText + Environment.NewLine + runtimeText;
        }

        internal static string FormatLogExportResult(LogArchiveExportResult result, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput = false)
        {
            int missingAtExportCount = result.Entries.Count(entry => entry.Status == "missing-at-export");
            bool partialExport = missingAtExportCount > 0;

            if (jsonOutput)
            {
                object payload = detailLevel == CliDiagnosticsExportDetailLevel.Manifest
                    ? new
                    {
                        Success = true,
                        DiagCode = "diagnostics-export-logs-success",
                        DetailLevel = "manifest",
                        PartialExport = partialExport,
                        MissingAtExportCount = missingAtExportCount,
                        ExportPath = FormatPath(result.ExportPath, redactOutput),
                        FileCount = result.ExportedFileCount,
                        result.ExportedBytes,
                        ArchiveFormat = "zip",
                        Entries = result.Entries.Select(entry => new
                        {
                            entry.Status,
                            entry.SourceKind,
                            SourcePath = FormatPath(entry.SourcePath, redactOutput),
                            entry.ArchiveEntry,
                            entry.Bytes,
                        }).ToArray(),
                    }
                    : new
                    {
                        Success = true,
                        DiagCode = "diagnostics-export-logs-success",
                        DetailLevel = "summary",
                        PartialExport = partialExport,
                        MissingAtExportCount = missingAtExportCount,
                        ExportPath = FormatPath(result.ExportPath, redactOutput),
                        FileCount = result.ExportedFileCount,
                        result.ExportedBytes,
                        ArchiveFormat = "zip",
                    };

                return SerializeCliJson(payload);
            }

            string fileLabel = result.ExportedFileCount == 1 ? "log file" : "log files";
            string summary = $"[diag-code:diagnostics-export-logs-success] Exported {result.ExportedFileCount} {fileLabel} to {FormatPath(result.ExportPath, redactOutput)}.";
            if (missingAtExportCount > 0)
            {
                string missingLabel = missingAtExportCount == 1 ? "entry was" : "entries were";
                summary += $" {missingAtExportCount} additional {missingLabel} unavailable during export.";
            }

            if (detailLevel != CliDiagnosticsExportDetailLevel.Manifest)
            {
                return summary;
            }

            var builder = new StringBuilder(summary);
            builder.AppendLine();
            builder.Append("manifest:");

            for (int index = 0; index < result.Entries.Count; index++)
            {
                LogArchiveExportEntryResult entry = result.Entries[index];
                builder.AppendLine();
                builder.Append("- ");
                builder.Append(entry.Status);
                builder.Append(' ');
                builder.Append(entry.SourceKind);
                builder.Append(" log -> ");
                builder.Append(entry.ArchiveEntry);
                builder.Append(" from ");
                builder.Append(FormatPath(entry.SourcePath, redactOutput));

                if (entry.Bytes > 0)
                {
                    builder.Append(" (");
                    builder.Append(entry.Bytes);
                    builder.Append(" bytes)");
                }
            }

            return builder.ToString();
        }

        internal static string FormatDiagnosticBundleExportResult(DiagnosticBundleExportResult result, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput)
        {
            string redactionMode = result.IncludeSensitive ? "sensitive" : "redacted";

            if (jsonOutput)
            {
                object payload = detailLevel == CliDiagnosticsExportDetailLevel.Manifest
                    ? new
                    {
                        Success = true,
                        DiagCode = "diagnostics-export-bundle-success",
                        DetailLevel = "manifest",
                        RedactionMode = redactionMode,
                        result.PartialExport,
                        ExportPath = FormatPath(result.ExportPath, redactOutput: !result.IncludeSensitive),
                        FileCount = result.ExportedFileCount,
                        result.ExportedBytes,
                        ArchiveFormat = "zip",
                        Entries = result.Entries.Select(static entry => new
                        {
                            entry.Status,
                            entry.SourceKind,
                            entry.ArchiveEntry,
                            entry.Bytes,
                        }).ToArray(),
                    }
                    : new
                    {
                        Success = true,
                        DiagCode = "diagnostics-export-bundle-success",
                        DetailLevel = "summary",
                        RedactionMode = redactionMode,
                        result.PartialExport,
                        ExportPath = FormatPath(result.ExportPath, redactOutput: !result.IncludeSensitive),
                        FileCount = result.ExportedFileCount,
                        result.ExportedBytes,
                        ArchiveFormat = "zip",
                    };

                return SerializeCliJson(payload);
            }

            string summary = $"[diag-code:diagnostics-export-bundle-success] Exported redacted diagnostic bundle with {result.ExportedFileCount} files to {FormatPath(result.ExportPath, redactOutput: true)}.";
            if (result.IncludeSensitive)
            {
                summary = $"[diag-code:diagnostics-export-bundle-success] Exported sensitive diagnostic bundle with {result.ExportedFileCount} files to {FormatPath(result.ExportPath, redactOutput: false)}.";
            }

            if (result.PartialExport)
            {
                summary += " Some optional entries were unavailable.";
            }

            if (detailLevel != CliDiagnosticsExportDetailLevel.Manifest)
            {
                return summary;
            }

            var builder = new StringBuilder(summary);
            builder.AppendLine();
            builder.Append("manifest:");
            foreach (DiagnosticBundleExportEntryResult entry in result.Entries)
            {
                builder.AppendLine();
                builder.Append("- ");
                builder.Append(entry.Status);
                builder.Append(' ');
                builder.Append(entry.SourceKind);
                builder.Append(" -> ");
                builder.Append(entry.ArchiveEntry);
                if (entry.Bytes > 0)
                {
                    builder.Append(" (");
                    builder.Append(entry.Bytes);
                    builder.Append(" bytes)");
                }
            }

            return builder.ToString();
        }

        internal static string FormatPerAppAudioResetResult(PerAppAudioRoutingResetResult result, bool jsonOutput)
        {
            string diagCode;
            string message;

            if (!result.Success)
            {
                diagCode = "diagnostics-reset-per-app-audio-failed";
                message = "Failed to reset per-application audio assignments.";
            }
            else if (!result.HadAssignments)
            {
                diagCode = "diagnostics-reset-per-app-audio-noop";
                message = "No persisted per-application audio assignments were found to reset.";
            }
            else
            {
                diagCode = "diagnostics-reset-per-app-audio-success";
                message = "Per-application audio assignments were reset.";
            }

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    result.Success,
                    result.HadAssignments,
                    DiagCode = diagCode,
                    message,
                });
            }

            return $"[diag-code:{diagCode}] {message}";
        }
    }
}
