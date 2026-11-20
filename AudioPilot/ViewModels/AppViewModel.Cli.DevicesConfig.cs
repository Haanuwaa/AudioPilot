using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public (bool CanSwitch, string Output) PreviewSwitchFromCli(bool output, bool reverse, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            var cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();

            var snapshot = AppSwitchCycleStateResolver.BuildCycleSnapshotState([.. cycleDevices], activeDevices);
            var resolvedCycle = snapshot.ConnectedCycle.Concat(snapshot.SkippedDevices).ToList();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                resolvedCycle,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (
                    false,
                    CliOutputFormatter.FormatCycleTest(
                        kind,
                        preflight.ConfiguredCount,
                        preflight.ConnectedConfiguredCount,
                        preflight.HasDefaultInputDevice,
                        preflight.Reasons,
                        jsonOutput,
                        redactOutput));
            }

            string? currentId = GetCurrentDeviceIdFromCli(output);
            int currentIndex = FindCycleDeviceIndex(snapshot.ConnectedCycle, currentId);
            int targetIndex = AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, snapshot.ConnectedCycle.Count, reverse);
            return (true, CliOutputFormatter.FormatSwitchPreview(kind, currentId, snapshot.ConnectedCycle[targetIndex], jsonOutput, redactOutput));
        }

        private static int FindCycleDeviceIndex(List<CycleDevice> cycleDevices, string? currentId)
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
            bool redactOutput = false,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            int normalizedTimeoutMs = Math.Max(0, timeoutMs);
            string scope = outputOnly ? "output" : inputOnly ? "input" : "any";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool outputFound = (outputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveOutputDeviceInfos(), deviceId);
                bool inputFound = (inputOnly || (!outputOnly && !inputOnly)) && ContainsDeviceId(GetActiveInputDeviceInfos(), deviceId);

                bool found = outputFound || inputFound;
                if (found)
                {
                    return (true, CliOutputFormatter.FormatDeviceWaitResult(deviceId, true, scope, stopwatch.ElapsedMilliseconds, normalizedTimeoutMs, jsonOutput, redactOutput));
                }

                int nextDelayMs = CliWaitTiming.ResolveRemainingDelayMs(stopwatch.ElapsedMilliseconds, normalizedTimeoutMs, 250);
                if (nextDelayMs <= 0)
                {
                    break;
                }

                await Task.Delay(nextDelayMs, cancellationToken);
            }

            return (false, CliOutputFormatter.FormatDeviceWaitResult(deviceId, false, scope, stopwatch.ElapsedMilliseconds, normalizedTimeoutMs, jsonOutput, redactOutput));
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
            return CliOutputFormatter.FormatDeviceList(kind, devices, jsonOutput, redactOutput);
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
            return CliOutputFormatter.FormatCycleList(kind, output ? OutputCycleDevices : InputCycleDevices, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidationFromCli(bool output, bool jsonOutput, bool redactOutput = false)
        {
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? OutputCycleDevices : InputCycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = CliOutputFormatter.FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
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
                CliOutputFormatter.FormatCycleTest(
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

        public Task<(bool Success, string Output)> AddCycleDeviceFromCliAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput = false, CancellationToken cancellationToken = default)
        {
            return UpdateCycleFromCliAsync(output, "add", deviceId, null, jsonOutput, redactOutput, cancellationToken);
        }

        public (bool Success, string Output) RemoveCycleDeviceFromCli(bool output, string deviceId, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public Task<(bool Success, string Output)> RemoveCycleDeviceFromCliAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput = false, CancellationToken cancellationToken = default)
        {
            return UpdateCycleFromCliAsync(output, "remove", deviceId, null, jsonOutput, redactOutput, cancellationToken);
        }

        public (bool Success, string Output) ReorderCycleFromCli(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput = false)
        {
            return UpdateCycleFromCli(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
        }

        public Task<(bool Success, string Output)> ReorderCycleFromCliAsync(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput = false, CancellationToken cancellationToken = default)
        {
            return UpdateCycleFromCliAsync(output, "reorder", null, deviceIds, jsonOutput, redactOutput, cancellationToken);
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
                ValidateCliDraftConflicts(CurrentSettings ?? _settings.LoadSettings(), settings);
                _settings.SaveSettings(settings);
                PublishCliPersistedSettings(settings);
                return result;
            }
            catch (CliDraftConflictException ex)
            {
                return (false, CliCycleManager.FormatMutationFailure(action, ex.Message, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-cycle-update-failed", nameof(UpdateCycleFromCli), ex);
                return (false, CliCycleManager.FormatMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private async Task<(bool Success, string Output)> UpdateCycleFromCliAsync(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
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
                    settings => Task.Run(() => _settings.SaveSettings(settings), linked.Token),
                    PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ValidateCliDraftConflicts, linked.Token);

                return outcome.Result;
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (CliDraftConflictException ex)
            {
                return (false, CliCycleManager.FormatMutationFailure(action, ex.Message, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-cycle-update-failed", nameof(UpdateCycleFromCliAsync), ex);
                return (false, CliCycleManager.FormatMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private (bool Success, string Output) ApplyCycleMutation(
            Settings settings, bool output, string action, string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds, bool jsonOutput, bool redactOutput)
        {
            return CliCycleManager.ApplyMutation(
                output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices,
                output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(),
                output ? "output" : "input", action, deviceId, orderedDeviceIds, jsonOutput, redactOutput);
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
            long languageRevision = Volatile.Read(ref _languageEditRevision);
            long themeRevision = Volatile.Read(ref _themeEditRevision);
            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            if (!CliConfigManager.TrySet(settings, key, value, out string? error))
            {
                return (false, error);
            }

            try
            {
                ValidateCliDraftConflicts(CurrentSettings ?? _settings.LoadSettings(), settings);
                StartupRegistrationOutcome startupOutcome = SaveSettingsWithStartupRegistration(settings,
                    forceStartup: key.Trim().Equals("run-at-startup", StringComparison.OrdinalIgnoreCase));

                if (IsLanguageConfigKey(key)) PublishCliLanguage(settings, languageRevision);
                else if (key.Trim().Equals("theme", StringComparison.OrdinalIgnoreCase)) PublishCliTheme(settings, themeRevision);
                else PublishCliPersistedSettings(settings);

                return startupOutcome == StartupRegistrationOutcome.Applied
                    ? (true, null) : (false, IncompleteCliStartupDisableMessage);
            }
            catch (CliDraftConflictException ex)
            {
                return (false, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-set-failed", nameof(SetConfigFromCli), ex);
                return (false, ex is InvalidDataException ? ex.Message : "Failed to persist config value.");
            }
        }

        public async Task<(bool Updated, string? Error)> SetConfigFromCliAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            long languageRevision = Volatile.Read(ref _languageEditRevision);
            long themeRevision = Volatile.Read(ref _themeEditRevision);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken, cancellationToken);
            try
            {
                StartupRegistrationOutcome startupOutcome = StartupRegistrationOutcome.Applied;
                CliSettingsMutationOutcome<(bool Updated, string? Error)> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    settings =>
                    {
                        bool updated = CliConfigManager.TrySet(settings, key, value, out string? error);
                        return new CliSettingsMutationDecision<(bool Updated, string? Error)>((updated, error), Persist: updated);
                    },
                    settings => Task.Run(() =>
                    {
                        startupOutcome = SaveSettingsWithStartupRegistration(settings,
                            forceStartup: key.Trim().Equals("run-at-startup", StringComparison.OrdinalIgnoreCase));
                    }, linkedCancellation.Token),
                    IsLanguageConfigKey(key) ? settings => PublishCliLanguage(settings, languageRevision)
                        : key.Trim().Equals("theme", StringComparison.OrdinalIgnoreCase) ? settings => PublishCliTheme(settings, themeRevision)
                        : PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ValidateCliDraftConflicts, linkedCancellation.Token);

                return startupOutcome == StartupRegistrationOutcome.Applied
                    ? outcome.Result : (false, IncompleteCliStartupDisableMessage);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (CliDraftConflictException ex)
            {
                return (false, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-set-failed", nameof(SetConfigFromCliAsync), ex);
                return (false, ex is InvalidDataException ? ex.Message : "Failed to persist config value.");
            }
        }

        private const string IncompleteCliStartupDisableMessage =
            "Startup was disabled in settings, but AudioPilot could not check or remove its scheduled task.";

        private static bool IsLanguageConfigKey(string key) => key.Trim().Equals("language", StringComparison.OrdinalIgnoreCase);

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

        public async Task<CliExecutionResult> ValidateConfigFileFromCliAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            return await CliConfigFileValidator.ValidateAsync(path, _settings.GetSettingsPath(), allowAnyPath, jsonOutput, redactOutput, linked.Token).ConfigureAwait(false);
        }

        public (bool IsValid, string Output) GetConfigValidationFromCli(bool jsonOutput, bool redactOutput = false)
        {
            var diagnostics = BuildSettingsDiagnostics();
            List<string> warnings = BuildDiagnosticMessages(diagnostics.Warnings);

            return (warnings.Count == 0, CliOutputFormatter.FormatConfigValidation(warnings, jsonOutput, redactOutput));
        }

        public (bool Success, string Output) ExportConfigFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
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
                if (jsonOutput)
                {
                    return (true, CliOutputFormatter.SerializeCliJson(new { ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), Success = true, DiagCode = "config-export-success" }));
                }

                return (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-export-failed", nameof(ExportConfigFromCli), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
                    : (false, "[diag-code:config-export-failed] Failed to export config.");
            }
        }

        public async Task<(bool Success, string Output)> ExportConfigFromCliAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            try
            {
                Settings settings = (GetCachedSettingsSnapshot() ?? await Task.Run(_settings.LoadSettings)).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:config-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                await Task.Run(() => SettingsTransferService.ExportSettings(settings, fullPath), linked.Token);
                return jsonOutput
                    ? (true, CliOutputFormatter.SerializeCliJson(new { ExportPath = CliOutputFormatter.FormatPath(fullPath, redactOutput), Success = true, DiagCode = "config-export-success" }))
                    : (true, $"[diag-code:config-export-success] Exported config to {CliOutputFormatter.FormatPath(fullPath, redactOutput)}.");
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-export-failed", nameof(ExportConfigFromCliAsync), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-export-failed", Error = "Failed to export config." }))
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
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-path-blocked", Error = pathError ?? "Import path is not allowed." }))
                        : (false, $"[diag-code:config-import-path-blocked] {pathError ?? "Import path is not allowed."}");
                }

                if (!File.Exists(fullPath))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-file-missing", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }))
                        : (false, $"[diag-code:config-import-file-missing] Import file not found: {CliOutputFormatter.FormatPath(fullPath, redactOutput)}");
                }

                Settings current = CurrentSettings ?? _settings.LoadSettingsReadOnly();
                string importJson = SettingsTransferService.ReadImportText(fullPath, _settings.ReadTextFileWithSettingsLock);
                Settings imported = AppSettingsTransferCoordinator.PrepareImport(importJson, current, replaceImport);
                ValidateCliDraftConflicts(current, imported);
                SaveSettingsWithStartupRegistration(imported);
                PublishCliPersistedSettings(imported);

                string mode = replaceImport ? "replace" : "merge";
                if (jsonOutput)
                {
                    return (true, CliOutputFormatter.SerializeCliJson(new { Success = true, Mode = mode, DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) }));
                }

                return (true, $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {mode} mode.");
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput, redactOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput, redactOutput);
            }
            catch (CliDraftConflictException ex)
            {
                return BuildConfigImportFailure("gui-draft-conflict", ex.Message, jsonOutput, redactOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCli), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        internal async Task<(bool Success, string Output)> PreviewConfigImportFromCliAsync(string path, bool replaceImport,
            bool allowAnyPath, bool jsonOutput, bool redactOutput, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            CancellationToken token = linked.Token;
            await _settingsWriteSemaphore.WaitAsync(token);
            try
            {
                return await Task.Run(() => CliConfigFileValidator.PreviewImport(path, _settings.GetSettingsPath(),
                    () => GetCachedSettingsSnapshot() ?? _settings.LoadSettingsReadOnly(),
                    replaceImport, allowAnyPath, jsonOutput, redactOutput, token), token);
            }
            finally { _settingsWriteSemaphore.Release(); }
        }

        public async Task<(bool Success, string Output)> ImportConfigFromCliAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            try
            {
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
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

                string importJson = await Task.Run(() => SettingsTransferService.ReadImportText(fullPath, _settings.ReadTextFileWithSettingsLock), linked.Token);
                CliSettingsMutationOutcome<(bool Success, string Output)> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(() => _settings.LoadSettingsReadOnly(), linked.Token),
                    current =>
                    {
                        Settings imported = AppSettingsTransferCoordinator.PrepareImport(importJson, current, replaceImport);
                        string mode = replaceImport ? "replace" : "merge";
                        string output = jsonOutput
                            ? CliOutputFormatter.SerializeCliJson(new { Success = true, Mode = mode, DiagCode = "config-import-success", Path = CliOutputFormatter.FormatPath(fullPath, redactOutput) })
                            : $"[diag-code:config-import-success] Imported config from {CliOutputFormatter.FormatPath(fullPath, redactOutput)} using {mode} mode.";
                        return new CliSettingsMutationDecision<(bool Success, string Output)>((true, output), Persist: true, ReplacementSettings: imported);
                    },
                    settings => Task.Run(() => SaveSettingsWithStartupRegistration(settings), linked.Token),
                    PublishCliPersistedSettings,
                    _settingsWriteSemaphore,
                    ValidateCliDraftConflicts, linked.Token);
                return outcome.Result;
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput, redactOutput);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput, redactOutput);
            }
            catch (CliDraftConflictException ex)
            {
                return BuildConfigImportFailure("gui-draft-conflict", ex.Message, jsonOutput, redactOutput);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-config-import-failed", nameof(ImportConfigFromCliAsync), ex);
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "config-import-failed", Error = "Failed to import config." }))
                    : (false, "[diag-code:config-import-failed] Failed to import config.");
            }
        }

        private void PublishCliTheme(Settings settings, long editRevision)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => PublishCliTheme(settings, editRevision));
                return;
            }
            bool preserveDraft = CurrentSettings is { } previous && (SettingsThemeDraft != previous.App.Theme || Theme != previous.App.Theme);
            lock (_settingsLock) _cachedSettings = settings;
            UpdateLastSettingsWriteTime();
            if (!preserveDraft && Volatile.Read(ref _themeEditRevision) == editRevision && !_isCleaningUp && !IsRuntimeShutdownRequested)
            {
                using var suppression = SuppressAutoSave();
                Theme = settings.App.Theme;
            }
            QueueSettingsFeedbackRefresh();
        }

        private void PublishCliPersistedSettings(Settings settings)
        {
            ApplyExternallyReloadedSettings(settings, context: "cli");
            lock (_settingsLock)
            {
                _cachedSettings = settings;
            }

            UpdateLastSettingsWriteTime();
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput, bool redactOutput)
        {
            if (redactOutput) message = CliOutputFormatter.RedactQuotedLiterals(message);
            return jsonOutput
                ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }

        public Task<CliExecutionResult> SwitchToDeviceFromCliAsync(CliCommand command)
        {
            bool output = command.Action == CliAction.SwitchOutputToDevice;
            return CliDirectDeviceSwitch.ExecuteAsync(command,
                () => output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(),
                () => GetCurrentDeviceIdFromCli(output),
                async target => await TrackDeviceSwitchAsync(async () =>
                {
                    await StopAudioEndpointTestAsync(AudioPilot.Services.Audio.Testing.AudioEndpointTestStopReason.Replaced);
                    string opId = $"cli-direct:{Guid.NewGuid():N}";
                    bool success;
                    if (output)
                    {
                        (success, _) = await _audio.SwitchAudioDeviceAsync(target.Id,
                            command.MuteMic, command.MuteSound, command.Deafen || Deafen, PreserveAudioLevels, opId: opId);
                        if (success)
                        {
                            _overlay.Show(OverlayDeviceKind.Output, "Switched output device", target.Name);
                            ScheduleOutputPostSwitchRefresh(opId);
                        }
                    }
                    else
                    {
                        (success, _) = await _audio.SwitchInputDeviceToAsync(target.Id, target.Name, PreserveAudioLevels,
                            (kind, title, message) => _overlay.Show(kind, title, message), opId);
                    }
                    return success;
                }, output, reverse: false, source: "cli", requestedName: target.Name, direct: true));
        }

        public ValueTask<bool> SwitchOutputFromCliAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) =>
            SwitchDevicesAsync(muteMic, muteSound, deafen, reverse, source: "cli");

        public ValueTask<bool> SwitchInputFromCliAsync(bool reverse) => SwitchInputDevicesAsync(reverse, source: "cli");
    }
}
