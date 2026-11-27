using System.Diagnostics;
using System.IO;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public async Task<string> GetNetworkListFromCliAsync(bool jsonOutput, bool redactOutput = false)
        {
            var wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(logger: _logger).ConfigureAwait(false);
            HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
            HashSet<string> allNetworks = [.. wifiNetworks.Ssids, .. nlmNetworks];
            List<string> sortedNetworks = [.. allNetworks.Order()];

            return CliOutputFormatter.FormatNetworkList(sortedNetworks, jsonOutput, redactOutput, wifiNetworks.Warning);
        }

        public async Task<bool> SetStartupEnabledFromCliAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            var (Updated, _) = await SetConfigFromCliAsync("run-at-startup", enabled ? "true" : "false", cancellationToken);
            return Updated;
        }

        public bool IsStartupEnabledFromCli()
        {
            return _startup.IsInStartupWithValidPath();
        }

        public bool OpenStartupSettingsFromCli()
        {
            try
            {
                ShowWindow();
                if ((CurrentSettings ?? _settings.LoadSettings()).Startup.UseTaskScheduler)
                {
                    return TryOpenStartupSettingsUri(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskschd.msc"));
                }
                return TryOpenStartupSettingsUri("ms-settings:startupapps")
                    || TryOpenStartupSettingsUri("shell:startup");
            }
            catch
            {
                return false;
            }
        }

        public string GetStartupStatusFromCli(bool jsonOutput)
        {
            bool startupEnabled = IsStartupEnabledFromCli();
            return CliOutputFormatter.FormatStartupStatus(startupEnabled, jsonOutput);
        }

        public string GetStatusFromCli(bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            List<CycleDevice> outputDevices = GetActiveOutputDeviceInfos();
            List<CycleDevice> inputDevices = GetActiveInputDeviceInfos();
            SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, outputDevices, inputDevices);
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            var (currentInputListenEnabled, listenMonitorTargetOutputDeviceName) = GetCurrentListenStatusSnapshot();

            ProcessLifecycleMonitorStatus processMonitor = _routineRuntime.ProcessMonitor is FallbackProcessLifecycleMonitor monitor
                ? monitor.GetStatus() : new(_routineRuntime.ProcessMonitor.IsRunning ? "active" : "inactive", null);

            return CliOutputFormatter.FormatStatusSnapshot(
                IsStartupEnabledFromCli(),
                outputDevices.Count,
                inputDevices.Count,
                OutputCycleDevices.Count,
                InputCycleDevices.Count,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
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
                processMonitorMode: processMonitor.Mode,
                processMonitorFallbackReason: processMonitor.FallbackReason);
        }

        public string GetDiagnosticsStatusFromCli(bool jsonOutput, bool showPaths, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(_logger.RootDirectory);

            string settingsPath = _settings.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = GetSortedFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*");

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
                    RuntimeHealth: CaptureRuntimeHealth()),
                jsonOutput, includeSensitivePaths: showPaths, redactOutput: redactOutput);
        }

        private RuntimeHealthSnapshot CaptureRuntimeHealth()
        {
            ProcessLifecycleMonitorStatus monitor = _routineRuntime.ProcessMonitor is FallbackProcessLifecycleMonitor fallback
                ? fallback.GetStatus() : new(_routineRuntime.ProcessMonitor.IsRunning ? "active" : "inactive", null);
            return new(DateTimeOffset.UtcNow, "gui", monitor.Mode, monitor.FallbackReason,
                _hotkeys.CaptureRuntimeSnapshot(), _routineRuntime.CaptureWorkSnapshot() with { HasAutomaticRoutines = HasAutomaticRoutines }, _logger.TotalDroppedCount,
                _deviceCache.CaptureHealth(),
                _settings.CaptureHealth(IsPersistedAutoSaveEnabled(), _autoSaveDebounceCts != null || _isSaving || _isApplyingSettings || IsSavingRoutines),
                _logger.CaptureHealth(), Volatile.Read(ref _lastResumeHealth), ComThreadingHelper.CaptureRuntimeHealth());
        }

        public async Task<(bool Success, string Output)> TestProcessMonitorFromCliAsync(bool jsonOutput, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            ProcessMonitorDiagnosticResult result = await RunProcessMonitorDiagnosticAsync(linked.Token).ConfigureAwait(false);
            return (result.Success, CliOutputFormatter.FormatProcessMonitorDiagnostic(result, jsonOutput));
        }

        public string GetDiagnosticsHistoryFromCli(bool jsonOutput, int? limit, string? type, bool redactOutput = false)
        {
            return GetDiagnosticsHistoryOutput(jsonOutput, limit, type, redactOutput);
        }

        public (bool Found, string Output) GetDiagnosticsHistoryDetailFromCli(string opId, bool jsonOutput, bool redactOutput = false)
        {
            return GetDiagnosticsHistoryDetailOutput(opId, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) ExportLogsFromCli(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-path-blocked", pathError ?? "Export path is not allowed.", jsonOutput));
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-invalid-path", message, jsonOutput));
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(_logger.RootDirectory, fullPath, redactOutput);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-unavailable", ex.Message, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-diagnostics-export-logs-failed", nameof(ExportLogsFromCli), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-logs-failed", "Failed to export log archive.", jsonOutput));
            }
        }

        public async Task<(bool Success, string Output)> ExportDiagnosticBundleFromCliAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            var (Success, Output, _) = await ExportDiagnosticBundleCoreAsync(path, allowAnyPath, detailLevel, includeSensitive, jsonOutput);
            return (Success, Output);
        }

        private async Task<(bool Success, string Output, DiagnosticBundleExportResult? Export)> ExportDiagnosticBundleCoreAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-path-blocked", pathError ?? "Export path is not allowed.", jsonOutput), null);
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip diagnostic bundle exports are supported.";
                    return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-invalid-path", message, jsonOutput), null);
                }

                bool redactBundleOutput = !includeSensitive;
                string statusJson = GetDiagnosticsStatusFromCli(jsonOutput: true, showPaths: includeSensitive, redactOutput: redactBundleOutput);
                string historyJson = GetDiagnosticsHistoryFromCli(jsonOutput: true, limit: 100, type: null, redactOutput: redactBundleOutput);
                var (mediaJson, mediaIssue) = await CaptureDiagnosticBundleMediaStatusAsync(redactBundleOutput);
                DiagnosticBundlePayloads payloads = new(
                    StatusJson: statusJson,
                    HistoryJson: historyJson,
                    MediaStatusJson: mediaJson,
                    ConfigValidationJson: GetConfigValidationFromCli(jsonOutput: true, redactOutput: redactBundleOutput).Output,
                    CollectionIssues: mediaIssue is { } issue ? [issue] : []);

                string? logRoot = _logger.RootDirectory;
                DiagnosticBundleExportResult result = await Task.Run(
                    () => DiagnosticBundleExportService.ExportBundle(
                        logRoot,
                        fullPath,
                        payloads,
                        includeSensitive));
                return (true, CliOutputFormatter.FormatDiagnosticBundleExportResult(result, detailLevel, jsonOutput), result);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-diagnostics-export-bundle-failed", nameof(ExportDiagnosticBundleFromCliAsync), ex);
                return (false, CliOutputFormatter.FormatCommandError(3, "diagnostics-export-bundle-failed", "Failed to export diagnostic bundle.", jsonOutput), null);
            }
        }

        internal async Task<(string Json, DiagnosticBundleCollectionIssue? Issue)> CaptureDiagnosticBundleMediaStatusAsync(bool redactOutput)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = await _audioCommandCoordinator.GetCurrentMediaSnapshotAsync(throwOnCaptureFailure: true);
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

        internal async Task<(bool Success, string Output)> ResetPerAppAudioRoutingFromCliAsync(bool jsonOutput, CancellationToken cancellationToken)
        {
            try
            {
                PerAppAudioRoutingResetResult result = await ResetPerAppAudioRoutingCoreAsync(cancellationToken);
                return (result.Success, CliOutputFormatter.FormatPerAppAudioResetResult(result, jsonOutput));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error("AppViewModel", "cli-reset-per-app-audio-failed", nameof(ResetPerAppAudioRoutingFromCliAsync), ex);
                PerAppAudioRoutingResetResult failed = new(Success: false, HadAssignments: false);
                return (false, CliOutputFormatter.FormatPerAppAudioResetResult(failed, jsonOutput));
            }
        }

        internal IReadOnlyList<SettingsDiagnostic> GetStartupConfigurationWarnings(Settings settings, bool includeDeviceAvailability) =>
            SettingsValidationService.EvaluateDiagnostics(settings,
                _hasDeviceInventorySnapshot && _outputInventoryComplete ? _outputDevices : null,
                _hasDeviceInventorySnapshot && _inputInventoryComplete ? _inputDevices : null,
                includeDeviceAvailability).Warnings;

        public Localization.LocalizedText? GetConfigurationLoadWarningForUi()
        {
            return _settings.LastLoadWarningResource;
        }

        private static string FormatDiagnostic(SettingsDiagnostic warning)
        {
            return $"[diag-code:{warning.Code}] {warning.Message} {warning.SuggestedAction}";
        }

        private static List<string> BuildDiagnosticMessages(IReadOnlyList<SettingsDiagnostic> warnings)
        {
            var messages = new List<string>(warnings.Count);
            for (int index = 0; index < warnings.Count; index++)
            {
                messages.Add(FormatDiagnostic(warnings[index]));
            }

            return messages;
        }

        private static List<string> GetSortedFiles(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            string[] files = Directory.GetFiles(directory, pattern);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return [.. files];
        }

        private SettingsDiagnosticsResult BuildSettingsDiagnostics()
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            List<CycleDevice> outputDevices;
            List<CycleDevice> inputDevices;

            if (_outputDevices.Count > 0 || _inputDevices.Count > 0)
            {
                outputDevices = CloneDeviceInfoList(_outputDevices);
                inputDevices = CloneDeviceInfoList(_inputDevices);
            }
            else
            {
                outputDevices = GetActiveOutputDeviceInfos();
                inputDevices = GetActiveInputDeviceInfos();
            }

            return SettingsValidationService.EvaluateDiagnostics(
                settings,
                outputDevices,
                inputDevices);
        }

        private static bool TryOpenStartupSettingsUri(string uri)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
