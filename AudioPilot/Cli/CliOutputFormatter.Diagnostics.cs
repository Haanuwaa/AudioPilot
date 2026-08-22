using System.Text;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        internal static string FormatProcessMonitorDiagnostic(ProcessMonitorDiagnosticResult result, bool jsonOutput)
        {
            if (jsonOutput) return SerializeCliJson(result);
            string message = result.Success
                ? "WMI process-start and process-stop event delivery confirmed."
                : result.Outcome == "probe-busy"
                    ? "Another process monitor test is still running or cleaning up. Wait before retrying."
                : result.Outcome == "access-denied"
                    ? "WMI process events were denied by Windows permissions. AudioPilot can use its polling fallback."
                    : $"WMI process-event test did not complete successfully: {result.Outcome}.";
            return $"{message}{Environment.NewLine}subscription started: {result.SubscriptionStarted}{Environment.NewLine}start event received: {result.StartEventReceived}{Environment.NewLine}stop event received: {result.StopEventReceived}{Environment.NewLine}administrator: {result.IsAdministrator}{Environment.NewLine}failure reason: {result.FailureReason ?? "none"}";
        }

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
                builder.Append(entry.Outcome);
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
                $"outcome: {snapshot.Outcome}",
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
            AppendOptionalDetailLine(lines, "traceId", snapshot.TraceId);
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

        public static string FormatExecutionHistoryNotFound(string opId, bool jsonOutput, bool redactOutput = false)
        {
            opId = redactOutput ? RedactLabel(opId, "op-id") : opId;
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

        internal static string FormatDiagnosticsStatus(CliDiagnosticsSnapshot snapshot, bool jsonOutput,
            bool includeSensitivePaths = false, bool redactOutput = false)
        {
            bool displaySensitivePaths = includeSensitivePaths && !redactOutput;
            var status = snapshot with
            {
                LogFilePath = DisplayPath(snapshot.LogFilePath, displaySensitivePaths, filePath: true, redactOutput),
                LogBackupDirectory = DisplayPath(snapshot.LogBackupDirectory, displaySensitivePaths, filePath: false, redactOutput),
                LogBackupFiles = DisplayPaths(snapshot.LogBackupFiles, displaySensitivePaths, redactOutput),
                SettingsPath = DisplayPath(snapshot.SettingsPath, displaySensitivePaths, filePath: true, redactOutput),
                SettingsBackupDirectory = DisplayPath(snapshot.SettingsBackupDirectory, displaySensitivePaths, filePath: false, redactOutput),
                SettingsBackupFiles = DisplayPaths(snapshot.SettingsBackupFiles, displaySensitivePaths, redactOutput),
                PathsRedacted = !displaySensitivePaths,
            };
            RuntimeProcessSnapshot? process = status.Process;
            RuntimeHealthSnapshot? runtimeHealth = status.RuntimeHealth;

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

            if (process != null)
                baseOutput += $"{Environment.NewLine}process: pid={process.ProcessId}, architecture={process.Architecture}, allocatedBytes={process.AllocatedBytes}, managedBytes={process.ManagedBytes}, privateBytes={process.PrivateBytes}, handles={process.Handles}";

            if (runtimeHealth != null)
            {
                baseOutput += $"{Environment.NewLine}runtime host: {runtimeHealth.HostMode}{Environment.NewLine}process monitor: {runtimeHealth.ProcessMonitorMode}";
                if (runtimeHealth.ProcessMonitorFallbackReason != null) baseOutput += $" ({runtimeHealth.ProcessMonitorFallbackReason})";
                baseOutput += $"{Environment.NewLine}dropped log messages: {runtimeHealth.DroppedLogMessages}";
                if (runtimeHealth.Hotkeys is { } hotkeys)
                    baseOutput += $"{Environment.NewLine}hotkeys: bindings={hotkeys.BindingCount}, captureSuspended={hotkeys.CaptureSuspended}, keyboardMessages={hotkeys.KeyboardMessageHostRunning}, keyboardHook={hotkeys.KeyboardHookRunning}, mouseHook={hotkeys.MouseHookRunning}";
                if (runtimeHealth.RoutineWork is { } routines)
                    baseOutput += $"{Environment.NewLine}automatic routines: {(routines.AutomaticPaused ? "paused" : routines.HasAutomaticRoutines == false ? "none configured" : routines.HasAutomaticRoutines == true ? "active" : "not paused")}{Environment.NewLine}automatic resume UTC: {routines.AutomaticResumeUtc?.ToString("O") ?? "not scheduled"}{Environment.NewLine}routine work: active={routines.ActiveTasks}, deferred={routines.DeferredOperations}, debounceSlots={routines.DebounceSlots.Count}, stopping={routines.Stopping}";
                if (runtimeHealth.Audio is { } audio)
                    baseOutput += $"{Environment.NewLine}cached audio defaults: output={audio.OutputDefault}, input={audio.InputDefault}, splitOutputRoles={audio.OutputRolesSplit}, splitInputRoles={audio.InputRolesSplit}, refreshPending={audio.RefreshPending}, stale={audio.Stale}, lastRefreshedUtc={audio.LastRefreshedUtc?.ToString("O") ?? "not sampled"}";
                if (runtimeHealth.Settings is { } settings)
                    baseOutput += $"{Environment.NewLine}settings health: autoSave={settings.AutoSaveEnabled}, pending={settings.SavePending}, fallbackStorage={settings.UsingFallback}, recoveryWarning={settings.RecoveryWarning}{Environment.NewLine}last save: utc={settings.LastSaveAttemptUtc?.ToString("O") ?? "none"}, succeeded={settings.LastSaveSucceeded?.ToString() ?? "not recorded"}, error={settings.LastSaveError ?? "none"}";
                if (runtimeHealth.Logging is { } logging)
                    baseOutput += $"{Environment.NewLine}logging health: level={logging.Level}, redaction={logging.RedactionEnabled}, queued={logging.QueuedMessages}{Environment.NewLine}last log write: utc={logging.LastWriteAttemptUtc?.ToString("O") ?? "none"}, succeeded={logging.LastWriteSucceeded?.ToString() ?? "not recorded"}, error={logging.LastWriteError ?? "none"}";
                if (runtimeHealth.Recovery is { } recovery)
                    baseOutput += $"{Environment.NewLine}last resume recovery: utc={recovery.CompletedUtc:O}, succeeded={recovery.Succeeded}, hotkeyAttempts={recovery.HotkeyAttempts}, hotkeyFailures={recovery.HotkeyFailedCount}";
            }

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
                        CollectionIssueCount = result.CollectionIssues?.Count ?? 0,
                        CollectionIssues = result.CollectionIssues ?? [],
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
                        CollectionIssueCount = result.CollectionIssues?.Count ?? 0,
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

            if (result.CollectionIssues is { Count: > 0 })
            {
                summary += " Some diagnostic information could not be collected; see manifest.json.";
            }
            else if (result.PartialExport)
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
            foreach (DiagnosticBundleCollectionIssue issue in result.CollectionIssues ?? [])
            {
                builder.AppendLine();
                builder.Append($"- collection failed: {issue.ArchiveEntry} ({issue.DiagCode}; {issue.ErrorType})");
            }
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
