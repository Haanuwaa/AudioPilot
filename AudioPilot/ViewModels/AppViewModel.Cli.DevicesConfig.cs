using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using Newtonsoft.Json;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public (bool CanSwitch, string Output) PreviewSwitchFromCli(bool output, bool reverse, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            var cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();

            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (
                    false,
                    FormatCycleTest(
                        kind,
                        preflight.ConfiguredCount,
                        preflight.ConnectedConfiguredCount,
                        preflight.HasDefaultInputDevice,
                        preflight.Reasons,
                        jsonOutput));
            }

            string? currentId = GetCurrentDeviceIdFromCli(output);
            int currentIndex = FindCycleDeviceIndex(cycleDevices, currentId);
            int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(currentIndex, cycleDevices.Count, reverse);
            string targetId = cycleDevices[targetIndex].Id;
            string targetName = cycleDevices[targetIndex].Name;

            if (jsonOutput)
            {
                return (true, SerializeCliJson(new
                {
                    Kind = kind,
                    DryRun = true,
                    CurrentDeviceId = currentId,
                    TargetDeviceId = targetId,
                    TargetDeviceName = CliOutputFormatter.FormatDeviceName(targetName, redactOutput),
                    DiagCode = "switch-dry-run",
                }));
            }

            return (true, $"[diag-code:switch-dry-run] {kind} switch would target '{CliOutputFormatter.FormatDeviceName(targetName, redactOutput)}' ({targetId}).");
        }

        private static int FindCycleDeviceIndex(ObservableCollection<CycleDevice> cycleDevices, string? currentId)
        {
            if (string.IsNullOrWhiteSpace(currentId))
            {
                return -1;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, currentId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        public string? GetCurrentDeviceIdFromCli(bool output)
        {
            try
            {
                if (DeviceCacheHelper.IsInitialized)
                {
                    if (output)
                    {
                        using var cachedPlayback = DeviceCacheHelper.Instance.GetPrimaryPlaybackDevice();
                        if (cachedPlayback != null)
                        {
                            return cachedPlayback.ID;
                        }
                    }
                    else
                    {
                        using var cachedRecording = DeviceCacheHelper.Instance.GetPrimaryRecordingDevice();
                        if (cachedRecording != null)
                        {
                            return cachedRecording.ID;
                        }
                    }
                }

                if (output)
                {
                    using var device = _audio.GetDefaultPlaybackDevice();
                    return device?.ID;
                }

                using var inputDevice = _audio.GetDefaultRecordingDevice();
                return inputDevice?.ID;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Found, string Output)> WaitForDeviceFromCliAsync(
            string deviceId,
            int timeoutMs,
            bool outputOnly,
            bool inputOnly,
            bool jsonOutput,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            int normalizedTimeoutMs = Math.Max(0, timeoutMs);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool outputFound = (outputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveOutputDeviceInfos(), deviceId);
                bool inputFound = (inputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveInputDeviceInfos(), deviceId);

                bool found = outputFound || inputFound;
                if (found)
                {
                    if (jsonOutput)
                    {
                        return (true, SerializeCliJson(new
                        {
                            DeviceId = deviceId,
                            Found = true,
                            Scope = outputOnly ? "output" : inputOnly ? "input" : "any",
                            ElapsedMs = stopwatch.ElapsedMilliseconds,
                            DiagCode = "wait-device-found",
                        }));
                    }

                    return (true, $"[diag-code:wait-device-found] Device '{deviceId}' is available.");
                }

                int nextDelayMs = CliWaitTiming.ResolveRemainingDelayMs(stopwatch.ElapsedMilliseconds, normalizedTimeoutMs, 250);
                if (nextDelayMs <= 0)
                {
                    break;
                }

                await Task.Delay(nextDelayMs, cancellationToken);
            }

            if (jsonOutput)
            {
                return (false, SerializeCliJson(new
                {
                    DeviceId = deviceId,
                    Found = false,
                    Scope = outputOnly ? "output" : inputOnly ? "input" : "any",
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    TimeoutMs = normalizedTimeoutMs,
                    DiagCode = "wait-device-timeout",
                }));
            }

            return (false, $"[diag-code:wait-device-timeout] Timed out waiting for device '{deviceId}' after {normalizedTimeoutMs}ms.");
        }

        private static bool ContainsDeviceId(List<CycleDevice> devices, string deviceId)
        {
            for (int index = 0; index < devices.Count; index++)
            {
                if (string.Equals(devices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        public string GetDeviceListFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            var devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            return FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public (bool Found, string Output) GetDeviceFromCli(bool output, string selector, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selector);
            if (resolution.Success && resolution.Device != null)
            {
                return (true, CliOutputFormatter.FormatDeviceGetResult(kind, resolution.Device, jsonOutput, redactOutput));
            }

            string message = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return (false, CliOutputFormatter.FormatDeviceGetError(kind, resolution.Ambiguous ? "device-selector-ambiguous" : "device-not-found", message, jsonOutput, redactOutput));
        }

        public (bool Found, string Output) FindDevicesFromCli(bool output, string query, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            IReadOnlyList<CycleDevice> matches = CliDeviceSelectorResolver.FindMatches(devices, query);
            return (matches.Count > 0, CliOutputFormatter.FormatDeviceFindResult(kind, query, matches, jsonOutput, redactOutput));
        }

        public string GetCycleFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            return FormatCycleList(kind, output ? OutputCycleDevices : InputCycleDevices, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidationFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
            return (result.IsValid, formatted);
        }

        public (bool CanSwitch, string Output) GetCycleTestFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            return (
                preflight.CanSwitch,
                FormatCycleTest(
                    kind,
                    preflight.ConfiguredCount,
                    preflight.ConnectedConfiguredCount,
                    preflight.HasDefaultInputDevice,
                    preflight.Reasons,
                    jsonOutput,
                    redactOutput));
        }

        public (bool Success, string Output) AddCycleDeviceFromCli(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "add", deviceId, null, jsonOutput, redactOutput);
        }

        public Task<(bool Success, string Output)> AddCycleDeviceFromCliAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCliAsync(output, "add", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) RemoveCycleDeviceFromCli(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public Task<(bool Success, string Output)> RemoveCycleDeviceFromCliAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCliAsync(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) ReorderCycleFromCli(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
        }

        public Task<(bool Success, string Output)> ReorderCycleFromCliAsync(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCliAsync(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
        }


        private (bool Success, string Output) UpdateCycleFromCli(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            (bool Success, string Output) result = ApplyCycleMutation(settings, output, action, deviceId, orderedDeviceIds, jsonOutput, redactOutput);
            if (!result.Success)
            {
                return result;
            }

            try
            {
                _settings.SaveSettings(settings);
                PublishCliPersistedSettings(settings);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-cycle-update-failed", nameof(UpdateCycleFromCli), ex);
                return (false, FormatCycleMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private async Task<(bool Success, string Output)> UpdateCycleFromCliAsync(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            try
            {
                CliSettingsMutationOutcome<(bool Success, string Output)> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    settings =>
                    {
                        (bool Success, string Output) result = ApplyCycleMutation(settings, output, action, deviceId, orderedDeviceIds, jsonOutput, redactOutput);
                        return new CliSettingsMutationDecision<(bool Success, string Output)>(result, Persist: result.Success);
                    },
                    settings => Task.Run(() => _settings.SaveSettings(settings)),
                    PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ShutdownToken);

                return outcome.Result;
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-cycle-update-failed", nameof(UpdateCycleFromCliAsync), ex);
                return (false, FormatCycleMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private (bool Success, string Output) ApplyCycleMutation(
            Settings settings,
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            List<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            List<CycleDevice> activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            string kind = output ? "output" : "input";
            string? deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);

            bool updated;
            string? error;
            switch (action)
            {
                case "add":
                    if (!TryResolveCycleMutationSelector(activeDevices, kind, deviceId ?? string.Empty, out string resolvedAddDeviceId, out string? addSelectorError))
                    {
                        return (false, FormatCycleMutationFailure(action, addSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedAddDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = CliCycleManager.TryAddDevice(cycleDevices, activeDevices, resolvedAddDeviceId, out _, out _, out string addMessage);
                    error = addMessage;
                    break;
                case "remove":
                    if (!TryResolveCycleMutationSelector(cycleDevices, kind, deviceId ?? string.Empty, out string resolvedRemoveDeviceId, out string? removeSelectorError, selector => $"Device '{selector}' is not configured in the cycle."))
                    {
                        return (false, FormatCycleMutationFailure(action, removeSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                    }

                    deviceId = resolvedRemoveDeviceId;
                    deviceName = FindCycleMutationDeviceName(cycleDevices, activeDevices, deviceId);
                    updated = CliCycleManager.TryRemoveDevice(cycleDevices, resolvedRemoveDeviceId, out _, out _, out string removeMessage);
                    error = removeMessage;
                    break;
                case "reorder":
                    updated = CliCycleManager.TryReorder(cycleDevices, orderedDeviceIds ?? [], out _, out string reorderMessage);
                    error = reorderMessage;
                    break;
                default:
                    updated = false;
                    error = "Unsupported cycle action.";
                    break;
            }

            if (!updated)
            {
                return (false, FormatCycleMutationFailure(action, error ?? "Failed to update cycle.", jsonOutput));
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

        private static string FormatCycleMutationFailure(string action, string message, bool jsonOutput)
        {
            string diagCode = action switch
            {
                "add" => "cycle-add-failed",
                "remove" => "cycle-remove-failed",
                "reorder" => "cycle-reorder-failed",
                _ => "cycle-update-failed",
            };

            return jsonOutput
                ? SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message })
                : $"[diag-code:{diagCode}] {message}";
        }


        private bool HasDefaultInputDevice()
        {
            try
            {
                using var device = _audio.GetDefaultRecordingDevice();
                return device != null;
            }
            catch
            {
                return false;
            }
        }

        public (bool Found, string? Value, string? Error) GetConfigFromCli(string key)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            bool found = CliConfigManager.TryGet(settings, key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public (bool Updated, string? Error) SetConfigFromCli(string key, string value)
        {
            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            try
            {
                _settings.SaveSettings(settings);

                if (string.Equals(key, "run-at-startup", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SetStartupEnabledFromCli(settings.RunAtStartup);
                }

                PublishCliPersistedSettings(settings);

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-set-failed", nameof(SetConfigFromCli), ex);
                return (false, "Failed to persist config value.");
            }
        }

        public async Task<(bool Updated, string? Error)> SetConfigFromCliAsync(string key, string value)
        {
            try
            {
                CliSettingsMutationOutcome<(bool Updated, string? Error)> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    settings =>
                    {
                        bool updated = CliConfigManager.TrySet(settings, key, value, out string? error);
                        return new CliSettingsMutationDecision<(bool Updated, string? Error)>((updated, error), Persist: updated);
                    },
                    settings => Task.Run(() => _settings.SaveSettings(settings)),
                    PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ShutdownToken);

                if (outcome.PersistedSettings != null)
                {
                    if (string.Equals(key, "run-at-startup", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = SetStartupEnabledFromCli(outcome.PersistedSettings.RunAtStartup);
                    }
                }

                return outcome.Result;
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-set-failed", nameof(SetConfigFromCliAsync), ex);
                return (false, "Failed to persist config value.");
            }
        }

        public static (bool Found, string? Value, string? Error) GetRuntimeFromCli(string key)
        {
            bool found = CliRuntimeManager.TryGet(key, out string value, out string? error);
            return (found, found ? value : null, error);
        }

        public static (bool Updated, string? Error) SetRuntimeFromCli(string key, string value)
        {
            return CliRuntimeManager.TrySet(key, value, out string? error)
                ? (true, null)
                : (false, error);
        }

        public (bool IsValid, string Output) GetConfigValidationFromCli(bool jsonOutput, bool redactOutput = false)
        {
            var diagnostics = BuildSettingsDiagnostics();
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            return (warnings.Count == 0, FormatConfigValidation(warnings, jsonOutput, redactOutput));
        }


        public (bool Success, string Output) ExportConfigFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SettingsTransferService.ExportSettings(settings, fullPath);
                if (jsonOutput)
                {
                    return (true, SerializeCliJson(new { ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), Success = true, DiagCode = "config-export-success" }));
                }

                return (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-export-failed", nameof(ExportConfigFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }

        public async Task<(bool Success, string Output)> ExportConfigFromCliAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = (GetCachedSettingsSnapshot() ?? await Task.Run(_settings.LoadSettings)).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                await Task.Run(() => SettingsTransferService.ExportSettings(settings, fullPath), ShutdownToken);
                return jsonOutput
                    ? (true, SerializeCliJson(new { ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), Success = true, DiagCode = "config-export-success" }))
                    : (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-export-failed", nameof(ExportConfigFromCliAsync), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }

        public (bool Success, string Output) ImportConfigFromCli(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                Settings current = (CurrentSettings ?? _settings.LoadSettings()).Clone();
                string importJson = SettingsTransferService.ReadImportText(fullPath, _settings.ReadTextFileWithSettingsLock);
                Settings imported = SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport);
                _settings.SaveSettings(imported);
                PublishCliPersistedSettings(imported);

                string mode = replaceImport ? "replace" : "merge";
                if (jsonOutput)
                {
                    return (true, SerializeCliJson(new { Success = true, Mode = mode, DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }));
                }

                return (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {mode} mode.");
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        public async Task<(bool Success, string Output)> ImportConfigFromCliAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                string importJson = await Task.Run(() => SettingsTransferService.ReadImportText(fullPath, _settings.ReadTextFileWithSettingsLock), ShutdownToken);
                CliSettingsMutationOutcome<(bool Success, string Output)> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    current =>
                    {
                        Settings imported = SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport);
                        string mode = replaceImport ? "replace" : "merge";
                        string output = jsonOutput
                            ? SerializeCliJson(new { Success = true, Mode = mode, DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) })
                            : $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {mode} mode.";
                        return new CliSettingsMutationDecision<(bool Success, string Output)>((true, output), Persist: true, ReplacementSettings: imported);
                    },
                    settings => Task.Run(() => _settings.SaveSettings(settings)),
                    PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ShutdownToken);
                return outcome.Result;
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        private void PublishCliPersistedSettings(Settings settings)
        {
            ApplyExternallyReloadedSettings(settings);
            lock (_settingsLock)
            {
                _cachedSettings = settings;
            }

            UpdateLastSettingsWriteTime();
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? (false, SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }


        public static string FormatConfigValidation(IReadOnlyList<string> warnings, bool jsonOutput, bool redactOutput = false)
        {
            return CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput, redactOutput);
        }

        public async ValueTask<bool> SwitchOutputFromCliAsync(bool muteMic, bool muteSound, bool deafen, bool reverse)
        {
            string? beforeDevice = TryGetCurrentDefaultDeviceName(output: true, reason: "cli-history:output-before");
            bool success = await SwitchDevicesAsync(muteMic, muteSound, deafen, reverse);
            string? afterDevice = TryGetCurrentDefaultDeviceName(output: true, reason: "cli-history:output-after");

            RecordCliActionHistory(
                ExecutionHistoryKind.Switch,
                reverse ? "switch-output-reverse" : "switch-output",
                success,
                skipped: false,
                success ? $"Output switch completed{(string.IsNullOrWhiteSpace(afterDevice) ? string.Empty : $" to '{afterDevice}'")}." : "Output switch failed or was rejected.",
                success ? null : "Output switch failed or was rejected.",
                target: beforeDevice,
                outputDeviceName: afterDevice);

            return success;
        }

        public async ValueTask<bool> SwitchInputFromCliAsync(bool reverse)
        {
            string? beforeDevice = TryGetCurrentDefaultDeviceName(output: false, reason: "cli-history:input-before");
            bool success = await SwitchInputDevicesAsync(reverse);
            string? afterDevice = TryGetCurrentDefaultDeviceName(output: false, reason: "cli-history:input-after");

            RecordCliActionHistory(
                ExecutionHistoryKind.Switch,
                reverse ? "switch-input-reverse" : "switch-input",
                success,
                skipped: false,
                success ? $"Input switch completed{(string.IsNullOrWhiteSpace(afterDevice) ? string.Empty : $" to '{afterDevice}'")}." : "Input switch failed or was rejected.",
                success ? null : "Input switch failed or was rejected.",
                target: beforeDevice,
                inputDeviceName: afterDevice);

            return success;
        }
    }
}
