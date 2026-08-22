using System.Diagnostics;
using System.IO;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public async Task<string> GetNetworkListFromCliAsync(bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                HashSet<string> wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(logger: _logger).ConfigureAwait(false);
                HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
                HashSet<string> allNetworks = [.. wifiNetworks, .. nlmNetworks];
                List<string> sortedNetworks = [.. allNetworks.Order()];

                return CliOutputFormatter.FormatNetworkList(sortedNetworks, jsonOutput, redactOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-network-list-failed", nameof(GetNetworkListFromCliAsync), ex);

                if (jsonOutput)
                {
                    return SerializeCliJson(new
                    {
                        Success = false,
                        DiagCode = "network-list-failed",
                        Error = "Failed to scan networks."
                    });
                }

                return "[diag-code:network-list-failed] Failed to scan networks.";
            }
        }

        public bool SetStartupEnabledFromCli(bool enabled)
        {
            try
            {
                string startupRegistryOpId = $"startup-registry:{Guid.NewGuid():N}";
                Settings? currentSettings;
                lock (_settingsLock)
                {
                    currentSettings = _cachedSettings != null
                        ? AppSettingsWorkflowCoordinator.CloneSettings(_cachedSettings)
                        : null;
                }
                currentSettings ??= _settings.LoadSettings();

                Settings updatedSettings = AppStartupRegistryCoordinator.CreateStartupUpdatedSettings(currentSettings, enabled);
                _settings.SaveSettings(updatedSettings);

                lock (_settingsLock)
                {
                    _cachedSettings = updatedSettings;
                }
                UpdateLastSettingsWriteTime();
                SetRunAtStartupInternal(enabled);
                SyncMirroredSettingsDraftsFromLiveState(runAtStartup: enabled);
                OnPropertyChanged(nameof(RunAtStartup));
                NotifyAutoSaveStateChanged();

                if (enabled)
                {
                    _startup.AddToStartup(startupRegistryOpId);
                }
                else
                {
                    _startup.RemoveFromStartup(startupRegistryOpId);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", () => $"startup-cli-update-failed | enabled={enabled} error={ex.GetType().Name}", nameof(SetStartupEnabledFromCli), ex);
                return false;
            }
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
            return FormatStartupStatus(startupEnabled, jsonOutput);
        }

        public string GetStatusFromCli(bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            var diagnostics = BuildSettingsDiagnostics();
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            var (currentInputListenEnabled, listenMonitorTargetOutputDeviceName) = GetCurrentListenStatusSnapshot();

            return FormatStatusSnapshot(
                IsStartupEnabledFromCli(),
                GetActiveOutputDeviceInfos().Count,
                GetActiveInputDeviceInfos().Count,
                OutputCycleDevices.Count,
                InputCycleDevices.Count,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
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
                jsonOutput,
                redactOutput: redactOutput);
        }

        public string GetDiagnosticsStatusFromCli(bool jsonOutput, bool showPaths, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            LogFileInventory logInventory = LogArchiveExportService.GetInventory(AppDataPaths.GetWritableDataRoot());

            string settingsPath = _settings.GetSettingsPath();
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string settingsBackupDirectory = Path.Combine(settingsDirectory ?? string.Empty, AppConstants.Files.BackupFolderName);
            List<string> settingsBackupFiles = GetSortedFiles(settingsBackupDirectory, AppConstants.Files.SettingsFileName + ".bak*");

            return FormatDiagnosticsStatus(
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
                jsonOutput,
                includeSensitivePaths: showPaths,
                redactOutput: redactOutput);
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
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-logs-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip log exports are supported.";
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-logs-invalid-path] {message}");
                }

                LogArchiveExportResult result = LogArchiveExportService.ExportLogs(AppDataPaths.GetWritableDataRoot(), fullPath, redactOutput);
                return (true, CliOutputFormatter.FormatLogExportResult(result, detailLevel, jsonOutput, redactOutput));
            }
            catch (InvalidOperationException ex)
            {
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-unavailable", Error = ex.Message }))
                    : (false, $"[diag-code:diagnostics-export-logs-unavailable] {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-diagnostics-export-logs-failed", nameof(ExportLogsFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-logs-failed", Error = "Failed to export log archive." }))
                    : (false, "[diag-code:diagnostics-export-logs-failed] Failed to export log archive.");
            }
        }

        public async Task<(bool Success, string Output)> ExportDiagnosticBundleFromCliAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:diagnostics-export-bundle-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    const string message = "Only .zip diagnostic bundle exports are supported.";
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-invalid-path", Error = message }))
                        : (false, $"[diag-code:diagnostics-export-bundle-invalid-path] {message}");
                }

                bool redactBundleOutput = !includeSensitive;
                DiagnosticBundlePayloads payloads = new(
                    StatusJson: GetDiagnosticsStatusFromCli(jsonOutput: true, showPaths: includeSensitive, redactOutput: redactBundleOutput),
                    HistoryJson: GetDiagnosticsHistoryFromCli(jsonOutput: true, limit: 100, type: null, redactOutput: redactBundleOutput),
                    MediaStatusJson: await GetDiagnosticBundleMediaStatusJsonAsync(redactBundleOutput),
                    ConfigValidationJson: GetConfigValidationFromCli(jsonOutput: true, redactOutput: redactBundleOutput).Output);

                string writableDataRoot = AppDataPaths.GetWritableDataRoot();
                DiagnosticBundleExportResult result = await Task.Run(
                    () => DiagnosticBundleExportService.ExportBundle(
                        writableDataRoot,
                        fullPath,
                        payloads,
                        includeSensitive));
                return (true, CliOutputFormatter.FormatDiagnosticBundleExportResult(result, detailLevel, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-diagnostics-export-bundle-failed", nameof(ExportDiagnosticBundleFromCliAsync), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "diagnostics-export-bundle-failed", Error = "Failed to export diagnostic bundle." }))
                    : (false, "[diag-code:diagnostics-export-bundle-failed] Failed to export diagnostic bundle.");
            }
        }

        internal async Task<string> GetDiagnosticBundleMediaStatusJsonAsync(bool redactOutput)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = await _cliOverlayCoordinator.GetCurrentMediaSnapshotAsync();
                return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput: true, redactOutput);
            }
            catch (Exception ex)
            {
                return SerializeCliJson(new
                {
                    Available = false,
                    DiagCode = "diagnostics-bundle-media-status-unavailable",
                    Error = ex.GetType().Name,
                });
            }
        }

        public (bool Success, string Output) ResetPerAppAudioRoutingFromCli(bool jsonOutput)
        {
            try
            {
                PerAppAudioRoutingResetResult result = _audio.ResetAllPerAppAudioRouting();
                return (result.Success, CliOutputFormatter.FormatPerAppAudioResetResult(result, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-reset-per-app-audio-failed", nameof(ResetPerAppAudioRoutingFromCli), ex);
                PerAppAudioRoutingResetResult failed = new(Success: false, HadAssignments: false);
                return (false, CliOutputFormatter.FormatPerAppAudioResetResult(failed, jsonOutput));
            }
        }

        public IReadOnlyList<string> GetConfigurationWarningsForUi()
        {
            IReadOnlyList<SettingsDiagnostic> diagnostics = GetConfigurationWarningDiagnosticsForUi();
            var warnings = new List<string>();
            for (int index = 0; index < diagnostics.Count; index++)
            {
                warnings.Add(FormatDiagnosticForUi(diagnostics[index]));
            }

            string? loadWarning = GetConfigurationLoadWarningForUi();
            if (!string.IsNullOrWhiteSpace(loadWarning))
            {
                warnings.Add(loadWarning);
            }

            return warnings;
        }

        public IReadOnlyList<SettingsDiagnostic> GetConfigurationWarningDiagnosticsForUi()
        {
            return BuildSettingsDiagnostics().Warnings;
        }

        public string? GetConfigurationLoadWarningForUi()
        {
            return _settings.LastLoadUserWarning;
        }

        private static string FormatDiagnosticForUi(SettingsDiagnostic warning)
        {
            return $"{warning.Message} {warning.SuggestedAction}".Trim();
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


        public static string FormatStartupStatus(bool startupEnabled, bool jsonOutput)
        {
            return CliOutputFormatter.FormatStartupStatus(startupEnabled, jsonOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                [],
                jsonOutput,
                redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string> warnings,
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
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                bluetoothReconnectEnabled,
                bluetoothReconnectMaxAttempts,
                bluetoothReconnectAttemptTimeoutMs,
                bluetoothReconnectCooldownMs,
                bluetoothReconnectOnlyLikely,
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
                bluetoothReconnectTimeoutCircuitOpenMs,
                redactOutput: redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string> warnings,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                redactOutput: redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool jsonOutput)
        {
            return FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                null,
                null,
                [],
                jsonOutput);
        }

        public static string FormatDeviceList(string kind, IReadOnlyList<CycleDevice> devices, bool jsonOutput, bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public static string FormatCycleList(string kind, IReadOnlyList<CycleDevice> cycleDevices, bool jsonOutput, bool redactOutput = false)
        {
            return CliOutputFormatter.FormatCycleList(kind, cycleDevices, jsonOutput, redactOutput);
        }

        public static string FormatCycleValidation(
            string kind,
            IReadOnlyList<string> duplicateDeviceNames,
            IReadOnlyList<string> disconnectedDeviceNames,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatCycleValidation(kind, duplicateDeviceNames, disconnectedDeviceNames, jsonOutput, redactOutput);
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
            return CliOutputFormatter.FormatCycleTest(
                kind,
                configuredCount,
                connectedConfiguredCount,
                hasDefaultInputDevice,
                reasons,
                jsonOutput,
                redactOutput);
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
            bool jsonOutput,
            bool includeSensitivePaths = false,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDiagnosticsStatus(
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
                bluetoothReconnectEnabled,
                bluetoothReconnectMaxAttempts,
                bluetoothReconnectAttemptTimeoutMs,
                bluetoothReconnectCooldownMs,
                bluetoothReconnectOnlyLikely,
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
                bluetoothReconnectTimeoutCircuitOpenMs,
                includeSensitivePaths: includeSensitivePaths,
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
            bool includeSensitivePaths = false,
            bool redactOutput = false)
        {
            return CliOutputFormatter.FormatDiagnosticsStatus(
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
                includeSensitivePaths: includeSensitivePaths,
                redactOutput: redactOutput);
        }


        private static bool TryOpenStartupSettingsUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
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
