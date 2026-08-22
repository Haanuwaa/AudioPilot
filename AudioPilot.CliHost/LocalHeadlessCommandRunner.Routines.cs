using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using Newtonsoft.Json;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public string GetRoutineList(bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            return CliOutputFormatter.FormatRoutineList(CloneRoutines(settings.Routines?.Items ?? []), jsonOutput, redactOutput);
        }

        public async Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            List<AudioRoutine> routines = CloneRoutines(settings.Routines?.Items ?? []);
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(routines, routineSelector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            if (!routine.Enabled)
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: "Routine is disabled.", outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(5, "routine-disabled", $"Routine '{routine.Name}' is disabled.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (string.IsNullOrWhiteSpace(routine.OutputDeviceId) && string.IsNullOrWhiteSpace(routine.InputDeviceId))
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: "Routine has no configured targets.", outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineProcessSnapshotProvider, out int? processId, out string? errorCode, out string? errorMessage))
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: errorMessage, outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(
                    5,
                    errorCode ?? "routine-trigger-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                    requiresRunningTriggerProcess: true,
                    redactOutput: redactOutput);
            }

            string? outputDeviceName = null;
            bool outputFailed = false;
            bool? outputSucceeded = null;
            string? outputFailureDetail = null;
            if (!string.IsNullOrWhiteSpace(routine.OutputDeviceId))
            {
                RoutineSwitchExecutionResult outputResult = await ExecuteRoutineOutputSwitchAsync(routine, settings, processId);
                outputFailed = !outputResult.Success;
                outputSucceeded = outputResult.Success;
                outputFailureDetail = outputResult.FailureDetail;

                if (outputResult.Success)
                {
                    outputDeviceName = outputResult.DeviceName;
                }
            }

            string? inputDeviceName = null;
            bool inputFailed = false;
            bool? inputSucceeded = null;
            string? inputFailureDetail = null;
            if (!string.IsNullOrWhiteSpace(routine.InputDeviceId))
            {
                RoutineSwitchExecutionResult inputResult = await ExecuteRoutineInputSwitchAsync(routine, processId);
                inputFailed = !inputResult.Success;
                inputSucceeded = inputResult.Success;
                inputFailureDetail = inputResult.FailureDetail;

                if (inputResult.Success)
                {
                    inputDeviceName = inputResult.DeviceName;
                }
            }

            if (outputFailed || inputFailed)
            {
                RecordRoutineHistory(routine, success: false, skipped: false, outputDeviceName, inputDeviceName, outputFailureDetail ?? inputFailureDetail ?? "Routine execution failed.", outputSucceeded, inputSucceeded);
                return BuildRoutineErrorResult(
                    3,
                    "routine-run-failed",
                    $"Failed to run routine '{routine.Name}'.",
                    jsonOutput,
                    routine,
                    outputSucceeded: outputSucceeded,
                    appliedOutputDeviceName: outputSucceeded == true ? outputDeviceName ?? routine.OutputDeviceName : null,
                    outputFailureDetail: outputSucceeded == false ? outputFailureDetail : null,
                    inputSucceeded: inputSucceeded,
                    appliedInputDeviceName: inputSucceeded == true ? inputDeviceName ?? routine.InputDeviceName : null,
                    inputFailureDetail: inputSucceeded == false ? inputFailureDetail : null,
                    redactOutput: redactOutput);
            }

            RecordRoutineHistory(routine, success: true, skipped: false, outputDeviceName, inputDeviceName, reason: null, outputSucceeded, inputSucceeded);

            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine, outputDeviceName, inputDeviceName, jsonOutput, redactOutput));
        }

        public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines?.Items ?? [], routineSelector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildRoutineErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            bool updated = routine.Enabled != enabled;
            routine.Enabled = enabled;

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineStateChange(routine, enabled, updated, jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineErrorResult(3, "routine-update-failed", $"Failed to update routine '{routine.Name}'.", jsonOutput, routine, redactOutput: redactOutput);
            }
        }

        public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineDraft(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Create(settings, draft!);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Created", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-create-failed", $"Failed to create routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineDraft(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Update(settings, routineSelector, draft!);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Updated", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-update-failed", $"Failed to update routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Delete(settings, routineSelector);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Deleted", jsonOutput, redactOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-delete-failed", "Failed to delete routine.", jsonOutput);
            }
        }

        public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            if (!TryLoadRoutineCollection(path, allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = SettingsService.LoadSettings();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Import(settings, routines!, replaceImport);
            if (!mutation.Success)
            {
                return BuildRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                SettingsService.SaveSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineImportResult(mutation.ImportedCount, replaceImport, jsonOutput));
            }
            catch
            {
                return BuildRoutineMutationError(3, "routine-import-failed", $"Failed to import routines from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }


        public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                if (!CliPathPolicy.TryResolveConfigPath(path, SettingsService.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "routine-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:routine-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                List<AudioRoutine> routines = CloneRoutines(settings.Routines?.Items ?? []);
                string payload = JsonConvert.SerializeObject(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, Formatting.Indented);

                AtomicFileWriter.WriteAllText(fullPath, payload);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch
            {
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = "Failed to export routines." }))
                    : (false, "[diag-code:routine-export-failed] Failed to export routines.");
            }
        }


        private static CliExecutionResult BuildRoutineErrorResult(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? triggerApplicationName = null,
            bool? requiresRunningTriggerProcess = null,
            bool? outputSucceeded = null,
            string? appliedOutputDeviceName = null,
            string? outputFailureDetail = null,
            bool? inputSucceeded = null,
            string? appliedInputDeviceName = null,
            string? inputFailureDetail = null,
            bool redactOutput = false)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: true, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput))
                : new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: false, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput));
        }

        private static CliExecutionResult BuildRoutineMutationError(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliCommandExecutor.BuildJsonErrorPayload(exitCode, errorCode, message))
                : new CliExecutionResult(exitCode, $"[diag-code:{errorCode}] {message}");
        }

        private bool TryLoadRoutineDraft(string path, bool allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineDraft(
                path,
                SettingsService.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out draft,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routine.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private bool TryLoadRoutineCollection(string path, bool allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineCollection(
                path,
                SettingsService.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out routines,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routines.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private async Task<RoutineSwitchExecutionResult> ExecuteRoutineOutputSwitchAsync(AudioRoutine routine, Settings settings, int? processId)
        {
            await TryReconnectRoutineTargetAsync(
                routine.OutputDeviceId,
                routine.OutputDeviceName,
                BluetoothReconnectDeviceKind.Output,
                settings,
                configured => AudioService.TryGetActivePlaybackCycleEntry(configured.Id, configured.Name),
                opId: $"routine-output-reconnect:{routine.Id}");

            if (routine.SwitchOutputPerApp && processId is > 0)
            {
                string opId = $"routine-app-output:{routine.Id}:{processId.Value}";

                try
                {
                    ProcessAudioDeviceSwitchResult result = _audioOverrides?.SwitchApplicationOutputDeviceDetailedAsync != null
                        ? _audioOverrides.SwitchApplicationOutputDeviceDetailedAsync((uint)processId.Value, routine.OutputDeviceId, routine.OutputDeviceName, opId)
                        : await AudioService.SwitchApplicationOutputDeviceDetailedAsync(
                            (uint)processId.Value,
                            routine.OutputDeviceId,
                            routine.OutputDeviceName,
                            opId: opId);

                    bool switchApplied = result.Result == ProcessAudioRoutingResult.Applied;

                    return new RoutineSwitchExecutionResult(
                        switchApplied,
                        result.DeviceName,
                        switchApplied ? null : BuildPerAppRoutingFailureDetail("output", result.Result));
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-output-switch-failed", nameof(ExecuteRoutineOutputSwitchAsync), ex);
                    return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("output", ex));
                }
            }

            MMDevice? currentDefault = null;
            try
            {
                if (_audioOverrides?.GetDefaultPlaybackDeviceSnapshot != null)
                {
                    (string? currentDeviceId, string? currentDeviceName) = _audioOverrides.GetDefaultPlaybackDeviceSnapshot();
                    if (string.IsNullOrWhiteSpace(currentDeviceId))
                    {
                        return new RoutineSwitchExecutionResult(false, null, "No default output device is available.");
                    }

                    if (string.Equals(currentDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RoutineSwitchExecutionResult(true, currentDeviceName);
                    }

                    bool currentMuteMic = TryGetDefaultCaptureMute() ?? false;
                    bool currentMuteSound = TryGetDefaultPlaybackMute() ?? false;
                    bool currentDeafen = currentMuteMic && currentMuteSound;
                    string directOutputOpId = $"routine-output:{routine.Id}";
                    (bool switchSucceeded, string? switchedDeviceName) = _audioOverrides.SwitchAudioDeviceAsync != null
                        ? _audioOverrides.SwitchAudioDeviceAsync(currentDeviceId, routine.OutputDeviceId, currentMuteMic, currentMuteSound, currentDeafen, settings.Miscellaneous.PreserveAudioLevels, directOutputOpId)
                    : await AudioService.SwitchAudioDeviceAsync(
                        currentDeviceId,
                        routine.OutputDeviceId,
                        currentMuteMic,
                        currentMuteSound,
                        currentDeafen,
                        settings.Miscellaneous.PreserveAudioLevels,
                        opId: directOutputOpId);

                    return new RoutineSwitchExecutionResult(switchSucceeded, switchedDeviceName, switchSucceeded ? null : "Failed to switch the default output device.");
                }

                currentDefault = AudioService.GetDefaultPlaybackDevice();
                if (currentDefault == null)
                {
                    return new RoutineSwitchExecutionResult(false, null, "No default output device is available.");
                }

                if (string.Equals(currentDefault.ID, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return new RoutineSwitchExecutionResult(true, currentDefault.FriendlyName);
                }

                bool muteMic = TryGetDefaultCaptureMute() ?? false;
                bool muteSound = TryGetDefaultPlaybackMute() ?? false;
                bool deafen = muteMic && muteSound;

                string opId = $"routine-output:{routine.Id}";
                (bool success, string? deviceName) = _audioOverrides?.SwitchAudioDeviceAsync != null
                    ? _audioOverrides.SwitchAudioDeviceAsync(currentDefault.ID, routine.OutputDeviceId, muteMic, muteSound, deafen, settings.Miscellaneous.PreserveAudioLevels, opId)
                    : await AudioService.SwitchAudioDeviceAsync(
                        currentDefault.ID,
                        routine.OutputDeviceId,
                        muteMic,
                        muteSound,
                        deafen,
                        settings.Miscellaneous.PreserveAudioLevels,
                        opId: opId);

                return new RoutineSwitchExecutionResult(success, deviceName, success ? null : "Failed to switch the default output device.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-output-switch-failed", nameof(ExecuteRoutineOutputSwitchAsync), ex);
                return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("output", ex));
            }
            finally
            {
                DisposeForCleanup(currentDefault, nameof(ExecuteRoutineOutputSwitchAsync), "routine-output-current-default");
            }
        }

        private async Task<RoutineSwitchExecutionResult> ExecuteRoutineInputSwitchAsync(AudioRoutine routine, int? processId)
        {
            Settings settings = SettingsService.LoadSettings();
            await TryReconnectRoutineTargetAsync(
                routine.InputDeviceId,
                routine.InputDeviceName,
                BluetoothReconnectDeviceKind.Input,
                settings,
                configured => AudioService.TryGetActiveRecordingCycleEntry(configured.Id, configured.Name),
                opId: $"routine-input-reconnect:{routine.Id}");

            if (routine.SwitchOutputPerApp && processId is > 0)
            {
                string opId = $"routine-app-input:{routine.Id}:{processId.Value}";

                try
                {
                    ProcessAudioDeviceSwitchResult result = _audioOverrides?.SwitchApplicationInputDeviceDetailedAsync != null
                        ? _audioOverrides.SwitchApplicationInputDeviceDetailedAsync((uint)processId.Value, routine.InputDeviceId, routine.InputDeviceName, opId)
                        : await AudioService.SwitchApplicationInputDeviceDetailedAsync(
                            (uint)processId.Value,
                            routine.InputDeviceId,
                            routine.InputDeviceName,
                            opId: opId);

                    bool switchApplied = result.Result == ProcessAudioRoutingResult.Applied;

                    return new RoutineSwitchExecutionResult(
                        switchApplied,
                        result.DeviceName,
                        switchApplied ? null : BuildPerAppRoutingFailureDetail("input", result.Result));
                }
                catch (Exception ex)
                {
                    Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-input-switch-failed", nameof(ExecuteRoutineInputSwitchAsync), ex);
                    return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("input", ex));
                }
            }

            try
            {
                string directInputOpId = $"routine-input:{routine.Id}";
                (bool switchSucceeded, string? switchedDeviceName) = _audioOverrides?.SwitchInputDeviceToAsync != null
                    ? _audioOverrides.SwitchInputDeviceToAsync(routine.InputDeviceId, routine.InputDeviceName, directInputOpId)
                    : await AudioService.SwitchInputDeviceToAsync(
                        routine.InputDeviceId,
                        routine.InputDeviceName,
                        settings.Miscellaneous.PreserveAudioLevels,
                        showOverlay: null,
                        opId: directInputOpId);

                return new RoutineSwitchExecutionResult(switchSucceeded, switchedDeviceName, switchSucceeded ? null : "Failed to switch the default input device.");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "routine-input-switch-failed", nameof(ExecuteRoutineInputSwitchAsync), ex);
                return new RoutineSwitchExecutionResult(false, null, BuildRoutineSwitchExceptionDetail("input", ex));
            }
        }

        private static string BuildPerAppRoutingFailureDetail(string flow, ProcessAudioRoutingResult result)
        {
            return result switch
            {
                ProcessAudioRoutingResult.DeferredNoAudio => $"Per-app {flow} routing is pending until the application produces audio.",
                _ => $"Failed to apply per-app {flow} routing.",
            };
        }

        private static string BuildRoutineSwitchExceptionDetail(string flow, Exception exception)
        {
            return $"{char.ToUpperInvariant(flow[0])}{flow[1..]} switch threw {exception.GetType().Name}.";
        }

        private async Task TryReconnectRoutineTargetAsync(
            string? deviceId,
            string? deviceName,
            BluetoothReconnectDeviceKind deviceKind,
            Settings settings,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            string opId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            var configuredDevice = new CycleDevice
            {
                Id = deviceId,
                Name = deviceName ?? string.Empty,
            };

            if (tryResolveActiveDevice(configuredDevice) != null)
            {
                return;
            }

            BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                [configuredDevice],
                [],
                deviceKind,
                BluetoothReconnectOptions.FromSettings(settings),
                opId);

            if (reconnectResult.Attempted)
            {
                await WaitForConditionAsync(
                    () => tryResolveActiveDevice(configuredDevice) != null,
                    RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs,
                    RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                    usePollingFallbackWhenOverridesPresent: true);
            }
        }


        private static List<AudioRoutine> CloneRoutines(IEnumerable<AudioRoutine>? routines)
        {
            if (routines == null)
            {
                return [];
            }

            var cloned = new List<AudioRoutine>();
            foreach (AudioRoutine? routine in routines)
            {
                if (routine == null)
                {
                    continue;
                }

                cloned.Add(routine.Clone());
            }

            return cloned;
        }


        private void RecordRoutineHistory(AudioRoutine routine, bool success, bool skipped, string? outputDeviceName, string? inputDeviceName, string? reason, bool? outputSucceeded, bool? inputSucceeded)
        {
            _executionHistory.Record(new ExecutionHistoryEntry(
                OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Routine,
                Source: "cli",
                Action: "routine-run",
                Success: success,
                Skipped: skipped,
                Summary: skipped ? $"Routine '{routine.Name}' skipped." : success ? $"Routine '{routine.Name}' completed." : $"Routine '{routine.Name}' failed.",
                Reason: reason,
                RoutineId: routine.Id,
                RoutineName: routine.Name,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: routine.TargetSummary,
                OutputSucceeded: outputSucceeded,
                InputSucceeded: inputSucceeded,
                DiagCode: skipped ? "routine-run-skipped" : success ? "routine-run-success" : IsPartialRoutineResult(outputSucceeded, inputSucceeded) ? "routine-run-partial" : "routine-run-failed",
                Details: new Dictionary<string, string>
                {
                    ["trigger"] = routine.TriggerKind.ToString(),
                    ["executionSource"] = "cli",
                }));
        }

        private static bool IsPartialRoutineResult(bool? outputSucceeded, bool? inputSucceeded)
        {
            return (outputSucceeded == true && inputSucceeded == false)
                || (outputSucceeded == false && inputSucceeded == true);
        }
    }
}
