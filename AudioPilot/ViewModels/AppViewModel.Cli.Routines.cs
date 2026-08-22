using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using Newtonsoft.Json;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public string GetRoutineListFromCli(bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            return CliOutputFormatter.FormatRoutineList(AppViewModel.CloneRoutines(settings.Routines.Items), jsonOutput, redactOutput);
        }

        public Task<CliExecutionResult> RunRoutineFromCliAsync(string selector, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = CurrentSettings ?? _settings.LoadSettings();
            var coordinator = new CliRoutineCommandCoordinator(
                _routineProcessSnapshotProvider,
                (routine, processId) => ExecuteRoutineAsync(routine, showOverlay: true, applicationProcessId: processId, executionSource: "cli"),
                RecordExecutionHistory);
            return coordinator.RunAsync(CloneRoutines(settings.Routines.Items), selector, jsonOutput, redactOutput);
        }

        public CliExecutionResult SetRoutineEnabledFromCli(string selector, bool enabled, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines.Items, selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return CliRoutineCommandCoordinator.BuildErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            bool updated = routine.Enabled != enabled;
            routine.Enabled = enabled;

            try
            {
                _settings.SaveSettings(settings);
                PublishCliPersistedSettings(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineStateChange(routine, enabled, updated, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-set-failed", nameof(SetRoutineEnabledFromCli), ex);
                return CliRoutineCommandCoordinator.BuildErrorResult(3, "routine-update-failed", $"Failed to update routine '{routine.Name}'.", jsonOutput, routine, redactOutput: redactOutput);
            }
        }

        public async Task<CliExecutionResult> SetRoutineEnabledFromCliAsync(string selector, bool enabled, bool jsonOutput, bool redactOutput = false)
        {
            AudioRoutine? selectedRoutine = null;
            try
            {
                CliSettingsMutationOutcome<CliExecutionResult> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    settings =>
                    {
                        CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines.Items, selector);
                        if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
                        {
                            return new CliSettingsMutationDecision<CliExecutionResult>(
                                CliRoutineCommandCoordinator.BuildErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput),
                                Persist: false);
                        }

                        selectedRoutine = resolution.Routine;
                        bool updated = selectedRoutine.Enabled != enabled;
                        selectedRoutine.Enabled = enabled;
                        return new CliSettingsMutationDecision<CliExecutionResult>(
                            new CliExecutionResult(0, CliOutputFormatter.FormatRoutineStateChange(selectedRoutine, enabled, updated, jsonOutput, redactOutput)),
                            Persist: true);
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
                _logger.Error("AppViewModel", "cli-routine-set-failed", nameof(SetRoutineEnabledFromCliAsync), ex);
                return CliRoutineCommandCoordinator.BuildErrorResult(3, "routine-update-failed", $"Failed to update routine '{selectedRoutine?.Name ?? selector}'.", jsonOutput, selectedRoutine, redactOutput: redactOutput);
            }
        }

        public CliExecutionResult CreateRoutineFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Create(settings, draft!);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Created", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-create-failed", nameof(CreateRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-create-failed", $"Failed to create routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public async Task<CliExecutionResult> CreateRoutineFromCliAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            (bool Loaded, string? FullPath, AudioRoutine? Draft, CliExecutionResult Error) = await Task.Run(() =>
            {
                bool success = TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput);
                return (success, fullPath, draft, errorResult);
            }, ShutdownToken);
            if (!Loaded)
            {
                return Error;
            }

            return await ExecuteRoutineSettingsMutationFromCliAsync(
                settings => RoutineMutationCoordinator.Create(settings, Draft!),
                mutation => new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Created", jsonOutput, redactOutput)),
                () => BuildCliRoutineMutationError(3, "routine-create-failed", $"Failed to create routine from {CliOutputFormatter.FormatPath(FullPath!, redactOutput)}.", jsonOutput),
                "cli-routine-create-failed",
                nameof(CreateRoutineFromCliAsync),
                jsonOutput);
        }

        public CliExecutionResult UpdateRoutineFromCli(string selector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Update(settings, selector, draft!);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Updated", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-update-failed", nameof(UpdateRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-update-failed", $"Failed to update routine from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public async Task<CliExecutionResult> UpdateRoutineFromCliAsync(string selector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            (bool Loaded, string? FullPath, AudioRoutine? Draft, CliExecutionResult Error) = await Task.Run(() =>
            {
                bool success = TryLoadRoutineDraftForCli(path, allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, jsonOutput);
                return (success, fullPath, draft, errorResult);
            }, ShutdownToken);
            if (!Loaded)
            {
                return Error;
            }

            return await ExecuteRoutineSettingsMutationFromCliAsync(
                settings => RoutineMutationCoordinator.Update(settings, selector, Draft!),
                mutation => new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Updated", jsonOutput, redactOutput)),
                () => BuildCliRoutineMutationError(3, "routine-update-failed", $"Failed to update routine from {CliOutputFormatter.FormatPath(FullPath!, redactOutput)}.", jsonOutput),
                "cli-routine-update-failed",
                nameof(UpdateRoutineFromCliAsync),
                jsonOutput);
        }

        public CliExecutionResult DeleteRoutineFromCli(string selector, bool jsonOutput, bool redactOutput = false)
        {
            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Delete(settings, selector);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Deleted", jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-delete-failed", nameof(DeleteRoutineFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-delete-failed", "Failed to delete routine.", jsonOutput);
            }
        }

        public Task<CliExecutionResult> DeleteRoutineFromCliAsync(string selector, bool jsonOutput, bool redactOutput = false)
        {
            return ExecuteRoutineSettingsMutationFromCliAsync(
                settings => RoutineMutationCoordinator.Delete(settings, selector),
                mutation => new CliExecutionResult(0, CliOutputFormatter.FormatRoutineMutationResult(mutation.Routine!, mutation.ErrorCode, "Deleted", jsonOutput, redactOutput)),
                () => BuildCliRoutineMutationError(3, "routine-delete-failed", "Failed to delete routine.", jsonOutput),
                "cli-routine-delete-failed",
                nameof(DeleteRoutineFromCliAsync),
                jsonOutput);
        }

        public CliExecutionResult ImportRoutinesFromCli(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            if (!TryLoadRoutineCollectionForCli(path, allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, jsonOutput))
            {
                return errorResult;
            }

            Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
            RoutineMutationCoordinator.RoutineMutationResult mutation = RoutineMutationCoordinator.Import(settings, routines!, replaceImport);
            if (!mutation.Success)
            {
                return BuildCliRoutineMutationError(mutation.ExitCode, mutation.ErrorCode, mutation.Message, jsonOutput);
            }

            try
            {
                PersistCliSettingsMutation(settings);
                return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineImportResult(mutation.ImportedCount, replaceImport, jsonOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-import-failed", nameof(ImportRoutinesFromCli), ex);
                return BuildCliRoutineMutationError(3, "routine-import-failed", $"Failed to import routines from {CliOutputFormatter.FormatPath(fullPath!, redactOutput)}.", jsonOutput);
            }
        }

        public async Task<CliExecutionResult> ImportRoutinesFromCliAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            (bool Loaded, string? FullPath, List<AudioRoutine>? Routines, CliExecutionResult Error) loaded = await Task.Run(() =>
            {
                bool success = TryLoadRoutineCollectionForCli(path, allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, jsonOutput);
                return (success, fullPath, routines, errorResult);
            }, ShutdownToken);
            if (!loaded.Loaded)
            {
                return loaded.Error;
            }

            return await ExecuteRoutineSettingsMutationFromCliAsync(
                settings => RoutineMutationCoordinator.Import(settings, loaded.Routines!, replaceImport),
                mutation => new CliExecutionResult(0, CliOutputFormatter.FormatRoutineImportResult(mutation.ImportedCount, replaceImport, jsonOutput)),
                () => BuildCliRoutineMutationError(3, "routine-import-failed", $"Failed to import routines from {CliOutputFormatter.FormatPath(loaded.FullPath!, redactOutput)}.", jsonOutput),
                "cli-routine-import-failed",
                nameof(ImportRoutinesFromCliAsync),
                jsonOutput);
        }

        private static CliExecutionResult BuildCliRoutineMutationError(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? new CliExecutionResult(exitCode, CliCommandExecutor.BuildJsonErrorPayload(exitCode, errorCode, message))
                : new CliExecutionResult(exitCode, $"[diag-code:{errorCode}] {message}");
        }

        private bool TryLoadRoutineDraftForCli(string path, bool allowAnyPath, out string? fullPath, out AudioRoutine? draft, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineDraft(
                path,
                _settings.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out draft,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildCliRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routine.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private bool TryLoadRoutineCollectionForCli(string path, bool allowAnyPath, out string? fullPath, out List<AudioRoutine>? routines, out CliExecutionResult errorResult, bool jsonOutput)
        {
            if (!CliRoutineTransferHelper.TryLoadRoutineCollection(
                path,
                _settings.GetSettingsPath(),
                allowAnyPath,
                out fullPath,
                out routines,
                out string? errorCode,
                out string? errorMessage))
            {
                errorResult = BuildCliRoutineMutationError(5, errorCode ?? "routine-import-invalid", errorMessage ?? "Failed to load routines.", jsonOutput);
                return false;
            }

            errorResult = default;
            return true;
        }

        private void PersistCliSettingsMutation(Settings settings)
        {
            _settings.SaveSettings(settings);
            PublishCliPersistedSettings(settings);
        }

        private async Task<CliExecutionResult> ExecuteRoutineSettingsMutationFromCliAsync(
            Func<Settings, RoutineMutationCoordinator.RoutineMutationResult> mutation,
            Func<RoutineMutationCoordinator.RoutineMutationResult, CliExecutionResult> buildSuccess,
            Func<CliExecutionResult> buildPersistenceFailure,
            string failureEvent,
            string methodName,
            bool jsonOutput)
        {
            try
            {
                CliSettingsMutationOutcome<CliExecutionResult> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
                    GetCachedSettingsSnapshot,
                    () => Task.Run(_settings.LoadSettings),
                    settings =>
                    {
                        RoutineMutationCoordinator.RoutineMutationResult result = mutation(settings);
                        CliExecutionResult cliResult = result.Success
                            ? buildSuccess(result)
                            : BuildCliRoutineMutationError(result.ExitCode, result.ErrorCode, result.Message, jsonOutput);
                        return new CliSettingsMutationDecision<CliExecutionResult>(cliResult, Persist: result.Success);
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
                _logger.Error("AppViewModel", failureEvent, methodName, ex);
                return buildPersistenceFailure();
            }
        }

        public (bool Success, string Output) ExportRoutinesFromCli(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = (CurrentSettings ?? _settings.LoadSettings()).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:routine-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                List<AudioRoutine> routines = [.. settings.Routines.Items
                    .Where(static routine => routine != null)
                    .Select(static routine => routine.Clone())];

                string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, Newtonsoft.Json.Formatting.Indented);

                AtomicFileWriter.WriteAllText(fullPath, payload);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-export-failed", nameof(ExportRoutinesFromCli), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = "Failed to export routines." }))
                    : (false, "[diag-code:routine-export-failed] Failed to export routines.");
            }
        }

        public async Task<(bool Success, string Output)> ExportRoutinesFromCliAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput = false)
        {
            try
            {
                Settings settings = (GetCachedSettingsSnapshot() ?? await Task.Run(_settings.LoadSettings)).Clone();
                if (!CliPathPolicy.TryResolveConfigPath(path, _settings.GetSettingsPath(), allowAnyPath, out string fullPath, out string? pathError))
                {
                    return jsonOutput
                        ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-path-blocked", Error = pathError ?? "Export path is not allowed." }))
                        : (false, $"[diag-code:routine-export-path-blocked] {pathError ?? "Export path is not allowed."}");
                }

                List<AudioRoutine> routines = [.. settings.Routines.Items
                    .Where(static routine => routine != null)
                    .Select(static routine => routine.Clone())];
                string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    SchemaVersion = Settings.CurrentSchemaVersion,
                    Routines = routines,
                }, Newtonsoft.Json.Formatting.Indented);

                await Task.Run(() => AtomicFileWriter.WriteAllText(fullPath, payload), ShutdownToken);
                return (true, CliOutputFormatter.FormatRoutineExportResult(fullPath, routines.Count, jsonOutput, redactOutput));
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "cli-routine-export-failed", nameof(ExportRoutinesFromCliAsync), ex);
                return jsonOutput
                    ? (false, SerializeCliJson(new { Success = false, DiagCode = "routine-export-failed", Error = "Failed to export routines." }))
                    : (false, "[diag-code:routine-export-failed] Failed to export routines.");
            }
        }
    }
}
