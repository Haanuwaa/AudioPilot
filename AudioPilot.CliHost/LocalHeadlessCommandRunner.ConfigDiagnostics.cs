using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Services.UI.MediaOverlay;
using Newtonsoft.Json;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public Task RefreshAsync()
        {
            return Task.CompletedTask;
        }

        public bool SetStartupEnabled(bool enabled)
        {
            try
            {
                Settings currentSettings = SettingsService.LoadSettings();
                Settings updatedSettings = AppSettingsWorkflowCoordinator.CloneSettings(currentSettings);
                updatedSettings.RunAtStartup = enabled;
                SettingsService.SaveSettings(updatedSettings);

                if (enabled)
                {
                    StartupService.AddToStartup();
                }
                else
                {
                    StartupService.RemoveFromStartup();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool OpenStartupSettings()
        {
            return false;
        }

        public string GetStartupStatus(bool jsonOutput)
        {
            return CliOutputFormatter.FormatStartupStatus(StartupService.IsInStartupWithValidPath(), jsonOutput);
        }

        public string GetStatus(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                GetActiveOutputDeviceInfos(),
                GetActiveInputDeviceInfos());

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];

            return CliOutputFormatter.FormatStatusSnapshot(
                StartupService.IsInStartupWithValidPath(),
                GetActiveOutputDeviceInfos().Count,
                GetActiveInputDeviceInfos().Count,
                settings.DeviceSwitching.Output.CycleDevices.Count,
                settings.DeviceSwitching.Input.CycleDevices.Count,
                AudioService.TryGetCurrentInputListenState(out bool listenEnabled, out _) ? listenEnabled : null,
                AudioService.TryGetCurrentInputListenTargetOutputDeviceName(out string? monitorTargetOutputDeviceName, out _)
                    ? monitorTargetOutputDeviceName
                    : null,
                warnings,
                jsonOutput,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                redactOutput: redactOutput);
        }

        public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(GetLogRootDirectory());

            string settingsPath = SettingsService.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = Directory.Exists(settingsBackupDirectory)
                ? [.. Directory.GetFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
                : [];

            return CliOutputFormatter.FormatDiagnosticsStatus(
                logInventory.LogFilePath,
                logInventory.LogFileExists,
                logInventory.LogFileBytes,
                logInventory.LogBackupDirectory,
                logInventory.LogBackupFiles,
                AppConstants.Files.LogBackupRetentionCount,
                AppConstants.Logging.LogBackupMaxAgeDays,
                settingsPath,
                settingsBackupDirectory,
                settingsBackupFiles,
                AppConstants.Files.SettingsBackupRetentionCount,
                jsonOutput,
                settings.Miscellaneous.BluetoothReconnectEnabled,
                BluetoothReconnectRuntimeConfig.MaxAttempts,
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                BluetoothReconnectRuntimeConfig.CooldownMs,
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                RuntimeTuningConfig.SwitchRetryDelayMs,
                RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                RuntimeTuningConfig.SwitchMaxRetries,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                includeSensitivePaths: showPaths,
                redactOutput: redactOutput);
        }

        public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-logs-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-logs-invalid-path] {message}");
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(GetLogRootDirectory(), fullPath, redactOutput);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-unavailable", Error = ex.Message }))
                    : (false, $"[diag-code:diagnostics-export-logs-unavailable] {ex.Message}");
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-failed", Error = "Failed to export log archive." }))
                    : (false, "[diag-code:diagnostics-export-logs-failed] Failed to export log archive.");
            }
        }

        public Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            return Task.FromResult(ExportDiagnosticBundle(path, allowAnyPath, detailLevel, includeSensitive, jsonOutput));
        }

        private (bool Success, string Output) ExportDiagnosticBundle(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-bundle-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip diagnostic bundle exports are supported.";
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-bundle-invalid-path] {message}");
                }

                bool redactBundleOutput = !includeSensitive;
                DiagnosticBundlePayloads payloads = new(
                    StatusJson: GetDiagnosticsStatus(jsonOutput: true, showPaths: includeSensitive, redactOutput: redactBundleOutput),
                    HistoryJson: GetDiagnosticsHistory(jsonOutput: true, limit: 100, type: null, redactOutput: redactBundleOutput),
                    MediaStatusJson: GetDiagnosticBundleMediaStatusJson(redactBundleOutput),
                    ConfigValidationJson: GetConfigValidation(jsonOutput: true, redactOutput: redactBundleOutput).Output);

                DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(
                    GetLogRootDirectory(),
                    fullPath,
                    payloads,
                    includeSensitive);
                return (true, CliOutputFormatter.FormatDiagnosticBundleExportResult(result, detailLevel, jsonOutput));
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-failed", Error = "Failed to export diagnostic bundle." }))
                    : (false, "[diag-code:diagnostics-export-bundle-failed] Failed to export diagnostic bundle.");
            }
        }

        private static string GetDiagnosticBundleMediaStatusJson(bool redactOutput)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = new MediaOverlayCommandService()
                    .GetCurrentMediaSnapshotAsync()
                    .GetAwaiter()
                    .GetResult();
                return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput: true, redactOutput);
            }
            catch (Exception ex)
            {
                return CliOutputFormatter.SerializeCliJson(new
                {
                    Available = false,
                    DiagCode = "diagnostics-bundle-media-status-unavailable",
                    Error = ex.GetType().Name,
                });
            }
        }

        public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput)
        {
            try
            {
                PerAppAudioRoutingResetResult result = AudioService.ResetAllPerAppAudioRouting();
                return (result.Success, CliOutputFormatter.FormatPerAppAudioResetResult(result, jsonOutput));
            }
            catch
            {
                PerAppAudioRoutingResetResult failed = new(Success: false, HadAssignments: false);
                return (false, CliOutputFormatter.FormatPerAppAudioResetResult(failed, jsonOutput));
            }
        }


        public (bool Found, string? Value, string? Error) GetConfig(string key)
        {
            Settings settings = SettingsService.LoadSettings();
            bool found = CliConfigManager.TryGet(settings, key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetConfigList(bool jsonOutput)
        {
            return CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetConfig(string key, string value)
        {
            Settings settings = SettingsService.LoadSettings();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            try
            {
                SettingsService.SaveSettings(settings);

                if (string.Equals(key, "run-at-startup", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SetStartupEnabled(settings.RunAtStartup);
                }

                return (true, null);
            }
            catch
            {
                return (false, "Failed to persist config value.");
            }
        }

        public (bool Found, string? Value, string? Error) GetRuntime(string key)
        {
            bool found = CliRuntimeManager.TryGet(key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetRuntimeList(bool jsonOutput)
        {
            return CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetRuntime(string key, string value)
        {
            return CliRuntimeManager.TrySet(key, value, out string? error)
                ? (true, null)
                : (false, error);
        }

        public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput)
        {
            return CliOutputFormatter.FormatExecutionHistory(
                _executionHistory.GetEntries(limit, TryParseExecutionHistoryKind(type)),
                jsonOutput,
                redactOutput);
        }

        public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput)
        {
            ExecutionHistoryEntry? entry = _executionHistory.GetEntry(opId);
            return entry == null
                ? (false, CliOutputFormatter.FormatExecutionHistoryNotFound(opId, jsonOutput))
                : (true, CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput));
        }

        public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                GetActiveOutputDeviceInfos(),
                GetActiveInputDeviceInfos());

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];

            return (
                warnings.Count == 0,
                CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput, redactOutput));
        }

        public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SettingsTransferService.ExportSettings(settings, fullPath);
                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), DiagCode = "config-export-success" }))
                    : (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-export-failed", nameof(ExportConfig), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }


        public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                Settings current = SettingsService.LoadSettings();
                string importJson = SettingsTransferService.ReadImportText(fullPath, SettingsService.ReadTextFileWithSettingsLock);
                Settings imported = SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport);
                SettingsService.SaveSettings(imported);

                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, Mode = replaceImport ? "replace" : "merge", DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                    : (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {(replaceImport ? "replace" : "merge")} mode.");
            }
            catch (InvalidDataException ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }

        public async Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput)
        {
            try
            {
                HashSet<string> wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(logger: Logger.Instance).ConfigureAwait(false);
                HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
                HashSet<string> allNetworks = [.. wifiNetworks, .. nlmNetworks];
                List<string> sortedNetworks = [.. allNetworks.Order()];

                return CliOutputFormatter.FormatNetworkList(sortedNetworks, jsonOutput, redactOutput);
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-network-list-failed", nameof(GetNetworkListAsync), ex);

                if (jsonOutput)
                {
                    return CliOutputFormatter.SerializeCliJson(new
                    {
                        Success = false,
                        DiagCode = "network-list-failed",
                        Error = "Failed to scan networks."
                    });
                }

                return "[diag-code:network-list-failed] Failed to scan networks.";
            }
        }


        private static string FormatDiagnostic(SettingsDiagnostic warning)
        {
            return $"[diag-code:{warning.Code}] {warning.Message} {warning.SuggestedAction}";
        }

        private string GetLogRootDirectory()
        {
            return _audioOverrides?.GetLogRootDirectory?.Invoke() ?? AppDataPaths.GetWritableDataRoot();
        }


        private static ExecutionHistoryKind? TryParseExecutionHistoryKind(string? type)
        {
            return type switch
            {
                null or "" => null,
                "routine" => ExecutionHistoryKind.Routine,
                "switch" => ExecutionHistoryKind.Switch,
                "media" => ExecutionHistoryKind.Media,
                "mute" => ExecutionHistoryKind.Mute,
                _ => null,
            };
        }

        private void RecordCliActionHistory(ExecutionHistoryKind kind, string action, bool success, bool skipped, string summary, string? reason = null, string? target = null, string? outputDeviceName = null, string? inputDeviceName = null, bool? enabled = null, string? diagCode = null, double? elapsedMs = null, IReadOnlyDictionary<string, string>? details = null)
        {
            _executionHistory.Record(new ExecutionHistoryEntry(
                OpId: $"cli-{action}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: kind,
                Source: "cli",
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: target,
                Enabled: enabled,
                DiagCode: diagCode,
                ElapsedMs: elapsedMs,
                Details: details));
        }


        internal static bool DisposeForCleanup(IDisposable? disposable, string operation, string disposalTarget, ILogger? logger = null)
        {
            if (disposable == null)
            {
                return true;
            }

            try
            {
                disposable.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                ILogger cleanupLogger = logger ?? Logger.Instance;
                if (cleanupLogger.IsEnabled(LogLevel.Trace))
                {
                    cleanupLogger.Trace(
                        "LocalHeadlessCommandRunner",
                        () => $"headless-cleanup-dispose-ignored | target={disposalTarget} exceptionType={ex.GetType().Name}",
                        operation);
                }

                return false;
            }
        }
    }
}
