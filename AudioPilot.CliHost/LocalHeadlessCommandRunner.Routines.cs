using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public IReadOnlyList<AudioRoutine> GetRoutinesSnapshot()
        {
            Settings settings = SettingsService.LoadSettings();
            return CloneRoutines(settings.Routines?.Items ?? []);
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

            if (!routine.HasExecutionTarget)
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: "Routine has no configured targets.", outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineProcessSnapshotProvider, out int? processId, out string? errorCode, out string? errorMessage))
            {
                RecordRoutineHistory(routine, success: false, skipped: true, outputDeviceName: null, inputDeviceName: null, reason: errorMessage, outputSucceeded: null, inputSucceeded: null);
                return BuildRoutineErrorResult(
                    5,
                    errorCode ?? "routine-target-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath),
                    requiresRunningTargetApplication: true,
                    redactOutput: redactOutput);
            }

            AudioService.UpdateRoleConfiguration(settings.DeviceSwitching.Output.SwitchRoles, settings.DeviceSwitching.Input.SwitchRoles);
            bool muteMic = TryGetDefaultCaptureMute() ?? false;
            bool muteSound = TryGetDefaultPlaybackMute() ?? false;
            RoutineExecutionResult result = await CreateRoutineExecutionService(settings).ExecuteAsync(
                routine, new RoutineExecutionOptions(settings.DeviceSwitching.PreserveAudioLevels, muteMic, muteSound,
                    Deafen: false, AllowDeferredRouting: false), processId, _lifetimeCts.Token);
            if (result.SkipCode != null)
            {
                RecordRoutineHistory(routine, false, true, null, null, result.SkipReason, null, null, result);
                return BuildRoutineErrorResult(5, result.SkipCode, result.SkipReason ?? "Routine conditions were not met.", jsonOutput, routine, redactOutput: redactOutput);
            }
            string? volumeFailure = result.MasterVolumeSucceeded == false && result.MicVolumeSucceeded == false
                ? "Master and microphone volume could not be applied."
                : result.MasterVolumeSucceeded == false ? "Master volume could not be applied."
                : result.MicVolumeSucceeded == false ? "Microphone volume could not be applied." : null;
            RecordRoutineHistory(routine, result.Success, skipped: false, result.OutputDeviceName, result.InputDeviceName,
                result.CommunicationsFailureDetail ?? result.MuteFailureDetail ?? result.OutputFailureDetail ?? result.InputFailureDetail ?? volumeFailure,
                result.OutputSucceeded, result.InputSucceeded, result);
            if (!result.Success)
            {
                string message = $"Failed to run routine '{routine.Name}'.";
                if (volumeFailure != null) message += " " + volumeFailure;
                if (result.MuteFailureDetail != null) message += " " + result.MuteFailureDetail;
                if (result.CommunicationsFailureDetail != null) message += " " + result.CommunicationsFailureDetail;
                return BuildRoutineErrorResult(3, "routine-run-failed", message, jsonOutput, routine,
                    outputSucceeded: result.OutputSucceeded,
                    appliedOutputDeviceName: result.OutputDeviceName,
                    outputFailureDetail: result.OutputFailureDetail,
                    inputSucceeded: result.InputSucceeded,
                    appliedInputDeviceName: result.InputDeviceName,
                    inputFailureDetail: result.InputFailureDetail,
                    redactOutput: redactOutput);
            }
            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine,
                result.OutputDeviceName, result.InputDeviceName, jsonOutput, redactOutput));
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
                string payload = JsonSerializer.Serialize(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, SettingsJson.Options);

                SettingsTransferService.EnsureTransferTextSizeAllowed(payload);
                AtomicFileWriter.WriteAllText(fullPath, payload);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LocalHeadlessCommandRunner", "cli-routine-export-failed", nameof(ExportRoutines), ex);
                string message = ex is InvalidDataException ? ex.Message : "Failed to export routines.";
                return jsonOutput
                    ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = message }))
                    : (false, $"[diag-code:routine-export-failed] {message}");
            }
        }


        private static CliExecutionResult BuildRoutineErrorResult(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? targetApplicationName = null,
            bool? requiresRunningTargetApplication = null,
            bool? outputSucceeded = null,
            string? appliedOutputDeviceName = null,
            string? outputFailureDetail = null,
            bool? inputSucceeded = null,
            string? appliedInputDeviceName = null,
            string? inputFailureDetail = null,
            bool redactOutput = false)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: true, routine, targetApplicationName, requiresRunningTargetApplication, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput))
                : new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput: false, routine, targetApplicationName, requiresRunningTargetApplication, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput));
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

        private RoutineExecutionService CreateRoutineExecutionService(Settings settings)
        {
            RoutineExecutionOperations native = RoutineExecutionOperations.Create(AudioService, () => BluetoothReconnectCoordinator,
                BluetoothReconnectOptions.FromSettings(settings), Logger.Instance, processSnapshots: _routineProcessSnapshotProvider);
            RoutineExecutionOperations operations = native with
            {
                SwitchRolesAsync = _audioOverrides == null ? native.SwitchRolesAsync : null,
                ApplyOwnedVolume = _audioOverrides == null ? native.ApplyOwnedVolume : null,
                GetActiveDevices = playback => playback ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(),
                WaitForReconnectAsync = async (playback, target, result, opId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitForReconnectReadinessAsync(() => native.FindActiveDevice(playback, target) != null, result, opId);
                    cancellationToken.ThrowIfCancellationRequested();
                },
                SwitchDefaultAsync = request =>
                {
                    if (request.Playback && _audioOverrides?.SwitchAudioDeviceAsync is { } outputSwitch)
                        return ValueTask.FromResult(outputSwitch(request.Target.Id, request.Options.MuteMic, request.Options.MuteSound,
                            request.Options.Deafen, request.Options.PreserveAudioLevels, request.OperationId));
                    if (!request.Playback && _audioOverrides?.SwitchInputDeviceToAsync is { } inputSwitch)
                        return ValueTask.FromResult(inputSwitch(request.Target.Id, request.Target.Name, request.OperationId));
                    return native.SwitchDefaultAsync(request);
                },
                SwitchApplicationAsync = (playback, target, processId, opId) =>
                {
                    var handler = playback ? _audioOverrides?.SwitchApplicationOutputDeviceDetailedAsync : _audioOverrides?.SwitchApplicationInputDeviceDetailedAsync;
                    return handler != null ? ValueTask.FromResult(handler(processId, target.Id, target.Name, opId))
                        : native.SwitchApplicationAsync(playback, target, processId, opId);
                },
                ApplyVolume = (playback, targetId, percent, opId) => _audioOverrides == null
                    ? native.ApplyVolume(playback, targetId, percent, opId)
                    : TrySetEndpointVolume(playback, targetId, percent, out _, out _),
            };
            return new RoutineExecutionService(operations, Logger.Instance);
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


        private void RecordRoutineHistory(AudioRoutine routine, bool success, bool skipped, string? outputDeviceName, string? inputDeviceName, string? reason, bool? outputSucceeded, bool? inputSucceeded, RoutineExecutionResult? result = null)
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
                DiagCode: skipped ? result?.SkipCode ?? "routine-run-skipped" : success ? "routine-run-success" : (result?.HasPartialSuccess ?? ((outputSucceeded == true && inputSucceeded == false) || (outputSucceeded == false && inputSucceeded == true))) ? "routine-run-partial" : "routine-run-failed",
                Details: new Dictionary<string, string>
                {
                    ["trigger"] = routine.TriggerKind.ToString(),
                    ["executionSource"] = "cli",
                    ["masterVolumeSucceeded"] = result?.MasterVolumeSucceeded?.ToString() ?? "none",
                    ["micVolumeSucceeded"] = result?.MicVolumeSucceeded?.ToString() ?? "none",
                    ["communicationsOutputSucceeded"] = result?.CommunicationsOutputSucceeded?.ToString() ?? "none",
                    ["communicationsInputSucceeded"] = result?.CommunicationsInputSucceeded?.ToString() ?? "none",
                    ["outputMuteAction"] = routine.OutputMuteAction.ToString(),
                    ["inputMuteAction"] = routine.InputMuteAction.ToString(),
                    ["outputMuteSucceeded"] = result?.OutputMuteSucceeded?.ToString() ?? "none",
                    ["inputMuteSucceeded"] = result?.InputMuteSucceeded?.ToString() ?? "none",
                }));
        }

    }
}
