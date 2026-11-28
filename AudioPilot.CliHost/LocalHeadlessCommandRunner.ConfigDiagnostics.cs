using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Services.UI.MediaOverlay;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public Task RefreshAsync()
        {
            return Task.CompletedTask;
        }

        public async Task<(bool Success, string Output)> TestProcessMonitorAsync(bool jsonOutput)
        {
            ProcessMonitorDiagnosticResult result = await ProcessMonitorDiagnosticService.RunAsync().ConfigureAwait(false);
            return (result.Success, CliOutputFormatter.FormatProcessMonitorDiagnostic(result, jsonOutput));
        }

        public Task<bool> SetStartupEnabledAsync(bool enabled) => Task.FromResult(SetStartupEnabled(enabled));

        public bool SetStartupEnabled(bool enabled)
        {
            try
            {
                Settings currentSettings = LoadSettings();
                Settings updatedSettings = AppSettingsWorkflowCoordinator.CloneSettings(currentSettings);
                updatedSettings.Startup.Enabled = enabled;
                return StartupService.ApplyRegistration(enabled, updatedSettings.Startup.UseTaskScheduler,
                    () => SettingsService.SaveSettings(updatedSettings), allowIncompleteDisable: true) == StartupRegistrationOutcome.Applied;
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
            Settings settings = LoadSettings();
            List<CycleDevice> outputDevices = GetActiveOutputDeviceInfos();
            List<CycleDevice> inputDevices = GetActiveInputDeviceInfos();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                outputDevices,
                inputDevices);

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];

            InputListenStatusSnapshot listenStatus = AudioService.GetCurrentInputListenStatus();

            return CliOutputFormatter.FormatStatusSnapshot(
                StartupService.IsInStartupWithValidPath(),
                outputDevices.Count,
                inputDevices.Count,
                settings.DeviceSwitching.Output.CycleDevices.Count,
                settings.DeviceSwitching.Input.CycleDevices.Count,
                listenStatus.Enabled,
                listenStatus.MonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                settings.DeviceSwitching.BluetoothReconnectEnabled,
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
                redactOutput: redactOutput,
                listenMonitorTargetUnavailable: listenStatus.MonitorTargetUnavailable);
        }

        public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput)
        {
            Settings settings = LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(GetLogRootDirectory());

            string settingsPath = SettingsService.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = Directory.Exists(settingsBackupDirectory)
                ? [.. Directory.GetFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
                : [];

            return CliOutputFormatter.FormatDiagnosticsStatus(
                new CliDiagnosticsSnapshot(
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
                    BluetoothReconnect: CliBluetoothReconnectSnapshot.Capture(settings.DeviceSwitching.BluetoothReconnectEnabled),
                    RuntimeTuning: CliRuntimeTuningSnapshot.Capture(),
                    Process: RuntimeProcessSnapshot.Capture(),
                    RuntimeHealth: new RuntimeHealthSnapshot(DateTimeOffset.UtcNow, "headless", "inactive", null, null, null, _logger.TotalDroppedCount,
                    Settings: SettingsService.CaptureHealth(settings.App.AutoSaveEnabled, savePending: false), Logging: _logger.CaptureHealth(),
                    AudioWorker: ComThreadingHelper.CaptureRuntimeHealth())),
                jsonOutput, includeSensitivePaths: showPaths, redactOutput: redactOutput);
        }

        public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-path-blocked", pathError ?? "Export path is not allowed.", jsonOutput));
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-invalid-path", message, jsonOutput));
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(GetLogRootDirectory(), fullPath, redactOutput);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-unavailable", ex.Message, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-diagnostics-export-logs-failed", nameof(ExportLogs), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-failed", "Failed to export log archive.", jsonOutput));
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
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-path-blocked", pathError ?? "Export path is not allowed.", jsonOutput));
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip diagnostic bundle exports are supported.";
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-invalid-path", message, jsonOutput));
                }

                bool redactBundleOutput = !includeSensitive;
                string statusJson = GetDiagnosticsStatus(jsonOutput: true, showPaths: includeSensitive, redactOutput: redactBundleOutput);
                string historyJson = GetDiagnosticsHistory(jsonOutput: true, limit: 100, type: null, redactOutput: redactBundleOutput);
                var (mediaJson, mediaIssue) = CaptureDiagnosticBundleMediaStatus(redactBundleOutput);
                DiagnosticBundlePayloads payloads = new(
                    StatusJson: statusJson,
                    HistoryJson: historyJson,
                    MediaStatusJson: mediaJson,
                    ConfigValidationJson: GetConfigValidation(jsonOutput: true, redactOutput: redactBundleOutput).Output,
                    CollectionIssues: mediaIssue is { } issue ? [issue] : []);

                DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(
                    GetLogRootDirectory(),
                    fullPath,
                    payloads,
                    includeSensitive);
                return (true, CliOutputFormatter.FormatDiagnosticBundleExportResult(result, detailLevel, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-diagnostics-export-bundle-failed", nameof(ExportDiagnosticBundle), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-failed", "Failed to export diagnostic bundle.", jsonOutput));
            }
        }

        private static (string Json, DiagnosticBundleCollectionIssue? Issue) CaptureDiagnosticBundleMediaStatus(bool redactOutput)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = new MediaOverlayCommandService()
                    .GetCurrentMediaSnapshotAsync(throwOnCaptureFailure: true)
                    .GetAwaiter()
                    .GetResult();
                return (CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput: true, redactOutput), null);
            }
            catch (Exception ex)
            {
                var issue = new DiagnosticBundleCollectionIssue("diagnostics/media-status.json", "diagnostics-bundle-media-status-unavailable",
                    ex is MediaSnapshotUnavailableException unavailable ? unavailable.ErrorType : ex.GetType().Name);
                return (CliOutputFormatter.SerializeCliErrorJson(
                    new
                    {
                        Available = false,
                        issue.DiagCode,
                    },
                    3,
                    issue.DiagCode,
                    issue.ErrorType), issue);
            }
        }

        public async Task<(bool Success, string Output)> ResetPerAppAudioRoutingAsync(bool jsonOutput)
        {
            try
            {
                PerAppAudioRoutingResetResult result = await AudioService.ResetAllPerAppAudioRoutingAsync(_lifetimeCts.Token).ConfigureAwait(false);
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
            Settings settings = LoadSettings();
            bool found = CliConfigManager.TryGet(settings, key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetConfigList(bool jsonOutput)
        {
            return CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetConfig(string key, string value)
        {
            Settings previous = LoadSettings();
            Settings settings = previous.Clone();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            if (!SettingsCommitValidator.TryValidateConfigMutation(previous, settings, out string? validationError))
            {
                return (false, validationError);
            }

            try
            {
                string trimmedKey = key.Trim();
                bool forceStartup = string.Equals(trimmedKey, "run-at-startup", StringComparison.OrdinalIgnoreCase)
                    || (settings.Startup.Enabled && string.Equals(trimmedKey, "use-scheduled-startup", StringComparison.OrdinalIgnoreCase));
                StartupRegistrationOutcome startupOutcome = StartupRegistrationOutcome.Applied;
                SettingsPersistenceTransaction.Save(previous, settings, candidate => SettingsService.SaveSettings(candidate),
                    (candidate, persist) => startupOutcome = StartupService.ApplyRegistration(
                        candidate.Startup.Enabled,
                        candidate.Startup.UseTaskScheduler,
                        persist,
                        allowIncompleteDisable: forceStartup && !candidate.Startup.Enabled),
                    forceStartup: forceStartup);

                _logger.ApplyLogLevel(settings);
                return startupOutcome == StartupRegistrationOutcome.Applied
                    ? (true, null)
                    : (false, "Startup was disabled in settings, but AudioPilot could not check or remove its scheduled task.");
            }
            catch (Exception ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-set-failed", nameof(SetConfig), ex);
                return (false, ex is InvalidDataException ? ex.Message : "Failed to persist config value.");
            }
        }

        public (bool Found, string? Value, string? Error) GetRuntime(string key)
        {
            _ = LoadSettings();
            bool found = CliRuntimeManager.TryGet(key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public string GetRuntimeList(bool jsonOutput)
        {
            _ = LoadSettings();
            return CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput);
        }

        public (bool Updated, string? Error) SetRuntime(string key, string value)
        {
            _ = LoadSettings();
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
                ? (false, CliOutputFormatter.FormatExecutionHistoryNotFound(opId, jsonOutput, redactOutput))
                : (true, CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput));
        }

        public Task<CliExecutionResult> ValidateConfigFileAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) =>
            CliConfigFileValidator.ValidateAsync(path, SettingsService.GetSettingsPath(), allowAnyPath, jsonOutput, redactOutput, _lifetimeCts.Token);

        public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput)
        {
            Settings settings = LoadSettings();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
                settings,
                GetActiveOutputDeviceInfos(),
                GetActiveInputDeviceInfos());

            List<string> warnings =
            [
                .. diagnostics.Warnings.Select(FormatDiagnostic)
            ];
            CliConfigFileValidator.AppendDuplicateHotkeyWarnings(settings, warnings);

            return (
                warnings.Count == 0,
                CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput, redactOutput));
        }

        public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                Settings settings = LoadSettings();
                string settingsPath = SettingsService.GetSettingsPath();
                if (!CliPathPolicy.TryResolveConfigPath(path, settingsPath, allowAnyPath, out string fullPath, out string? pathError))
                {
                    return (false, CliOutputFormatter.FormatCommandError(3, "config-export-path-blocked", pathError ?? "Export path is not allowed.", jsonOutput));
                }

                SettingsTransferService.EnsureNotActiveSettingsPath(fullPath, settingsPath);
                SettingsTransferService.ExportSettings(settings, fullPath);
                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), DiagCode = "config-export-success" }))
                    : (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-export-failed", nameof(ExportConfig), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "config-export-failed", ex.Message, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-export-failed", nameof(ExportConfig), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "config-export-failed", "Failed to export config.", jsonOutput));
            }
        }

        public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return (false, CliOutputFormatter.FormatCommandError(3, "config-import-path-blocked", pathError ?? "Import path is not allowed.", jsonOutput));
                }

                if (!File.Exists(fullPath))
                {
                    string displayPath = CliOutputFormatter.FormatPath(fullPath, redactOutput);
                    return (false, CliOutputFormatter.FormatCommandError(new { Path = displayPath }, 3, "config-import-file-missing", $"Import file not found: {displayPath}", jsonOutput));
                }

                Settings current = _commandSettings ?? SettingsService.LoadSettingsReadOnly();
                string importJson = SettingsTransferService.ReadImportText(fullPath, SettingsService.ReadTextFileWithSettingsLock);
                Settings imported = AppSettingsTransferCoordinator.PrepareImport(importJson, current, replaceImport);
                StartupRegistrationOutcome startupOutcome = StartupRegistrationOutcome.Applied;
                if (current.Startup.Enabled != imported.Startup.Enabled
                    || (imported.Startup.Enabled && current.Startup.UseTaskScheduler != imported.Startup.UseTaskScheduler))
                {
                    startupOutcome = StartupService.ApplyRegistration(imported.Startup.Enabled, imported.Startup.UseTaskScheduler,
                        () => SettingsService.SaveSettings(imported), allowIncompleteDisable: true);
                }
                else
                {
                    SettingsService.SaveSettings(imported);
                }

                _logger.ApplyLogLevel(imported);
                if (startupOutcome != StartupRegistrationOutcome.Applied)
                {
                    return BuildConfigImportFailure(
                        "config-import-startup-incomplete",
                        "Startup was disabled in settings, but AudioPilot could not check or remove its scheduled task.",
                        jsonOutput,
                        redactOutput);
                }

                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { Success = true, Mode = replaceImport ? "replace" : "merge", DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                    : (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {(replaceImport ? "replace" : "merge")} mode.");
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex is ArgumentException ? "Imported config file is empty." : ex.Message, jsonOutput, redactOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput, redactOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("LocalHeadlessCommandRunner", "cli-config-import-failed", nameof(ImportConfig), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "config-import-failed", "Failed to import config.", jsonOutput));
            }
        }

        public Task<(bool Success, string Output)> PreviewConfigImportAsync(string path, bool replaceImport,
            bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(
            CliConfigFileValidator.PreviewImport(path, SettingsService.GetSettingsPath(), () => SettingsService.LoadSettingsReadOnly(),
                replaceImport, allowAnyPath, jsonOutput, redactOutput, _lifetimeCts.Token));

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput, bool redactOutput)
        {
            if (redactOutput) message = CliOutputFormatter.RedactQuotedLiterals(message);
            return (false, CliOutputFormatter.FormatCommandError(3, diagCode, message, jsonOutput));
        }

        public async Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput)
        {
            var wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(logger: _logger).ConfigureAwait(false);
            HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
            HashSet<string> allNetworks = [.. wifiNetworks.Ssids, .. nlmNetworks];
            List<string> sortedNetworks = [.. allNetworks.Order()];

            return CliOutputFormatter.FormatNetworkList(sortedNetworks, jsonOutput, redactOutput, wifiNetworks.Warning);
        }

        private static string FormatDiagnostic(SettingsDiagnostic warning)
        {
            return $"[diag-code:{warning.Code}] {warning.Message} {warning.SuggestedAction}";
        }

        private string? GetLogRootDirectory()
        {
            return _audioOverrides?.GetLogRootDirectory?.Invoke() ?? _logger.RootDirectory;
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
