using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal static class RoutineMutationCoordinator
    {
        internal readonly record struct RoutineMutationResult(
            bool Success,
            int ExitCode,
            string ErrorCode,
            string Message,
            AudioRoutine? Routine = null,
            int ImportedCount = 0);

        internal static RoutineMutationResult Create(Settings settings, AudioRoutine draft)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(draft);

            List<AudioRoutine> candidateRoutines = AppViewModel.CloneRoutines(settings.Routines?.Items ?? []);
            if (!TryPrepareRoutineDraft(draft, existingRoutine: null, candidateRoutines.Count + 1, out AudioRoutine? routine, out RoutineMutationResult errorResult))
            {
                return errorResult;
            }

            AudioRoutine preparedRoutine = routine!;
            if (candidateRoutines.Any(existing => string.Equals(existing.Id, preparedRoutine.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return new RoutineMutationResult(false, 5, "routine-id-conflict", $"Routine id '{preparedRoutine.Id}' already exists.");
            }

            candidateRoutines.Add(preparedRoutine);
            return CommitRoutineChanges(settings, candidateRoutines, "create", preparedRoutine, importedCount: 0);
        }

        internal static RoutineMutationResult Update(Settings settings, string selector, AudioRoutine draft)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(selector);
            ArgumentNullException.ThrowIfNull(draft);

            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines?.Items ?? [], selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return new RoutineMutationResult(false, 5, resolution.ErrorCode, resolution.Message);
            }

            AudioRoutine existing = resolution.Routine;
            List<AudioRoutine> candidateRoutines = AppViewModel.CloneRoutines(settings.Routines?.Items ?? []);
            int index = FindRoutineIndex(candidateRoutines, existing.Id);
            if (!TryPrepareRoutineDraft(draft, existing, existing.DisplayOrder, out AudioRoutine? routine, out RoutineMutationResult errorResult))
            {
                return errorResult;
            }

            AudioRoutine preparedRoutine = routine!;
            candidateRoutines[index] = preparedRoutine;

            return CommitRoutineChanges(settings, candidateRoutines, "update", preparedRoutine, importedCount: 0);
        }

        internal static RoutineMutationResult Delete(Settings settings, string selector)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(selector);

            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(settings.Routines?.Items ?? [], selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return new RoutineMutationResult(false, 5, resolution.ErrorCode, resolution.Message);
            }

            AudioRoutine existing = resolution.Routine.Clone();
            List<AudioRoutine> candidateRoutines = AppViewModel.CloneRoutines(settings.Routines?.Items ?? []);
            int index = FindRoutineIndex(candidateRoutines, existing.Id);
            candidateRoutines.RemoveAt(index);

            return CommitRoutineChanges(settings, candidateRoutines, "delete", existing, importedCount: 0);
        }

        internal static RoutineMutationResult Import(Settings settings, IReadOnlyList<AudioRoutine> importedRoutines, bool replaceImport)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(importedRoutines);

            if (importedRoutines.Count == 0)
            {
                return new RoutineMutationResult(false, 5, "routine-import-empty", "Imported routines file does not contain any routines.");
            }

            List<AudioRoutine> candidateRoutines;
            if (replaceImport)
            {
                candidateRoutines = [];
                for (int index = 0; index < importedRoutines.Count; index++)
                {
                    if (!TryPrepareRoutineDraft(importedRoutines[index], existingRoutine: null, index + 1, out AudioRoutine? prepared, out RoutineMutationResult errorResult))
                    {
                        return errorResult;
                    }

                    candidateRoutines.Add(prepared!);
                }
            }
            else
            {
                candidateRoutines = AppViewModel.CloneRoutines(settings.Routines?.Items ?? []);
                int nextDisplayOrder = candidateRoutines.Count + 1;
                foreach (AudioRoutine imported in importedRoutines)
                {
                    if (!TryPrepareRoutineDraft(imported, existingRoutine: null, nextDisplayOrder, out AudioRoutine? prepared, out RoutineMutationResult errorResult))
                    {
                        return errorResult;
                    }

                    AudioRoutine preparedRoutine = prepared!;
                    int existingIndex = FindRoutineIndex(candidateRoutines, preparedRoutine.Id, throwWhenMissing: false);
                    if (existingIndex >= 0)
                    {
                        AudioRoutine existing = candidateRoutines[existingIndex];
                        if (!TryPrepareRoutineDraft(imported, existing, existing.DisplayOrder, out AudioRoutine? replacement, out errorResult))
                        {
                            return errorResult;
                        }

                        candidateRoutines[existingIndex] = replacement!;
                        continue;
                    }

                    candidateRoutines.Add(preparedRoutine);
                    nextDisplayOrder++;
                }
            }

            return CommitRoutineChanges(settings, candidateRoutines, "import", routine: null, importedRoutines.Count);
        }

        private static RoutineMutationResult CommitRoutineChanges(Settings settings, List<AudioRoutine> candidateRoutines, string operation, AudioRoutine? routine, int importedCount)
        {
            Settings candidateSettings = AppSettingsWorkflowCoordinator.CloneSettings(settings);
            candidateSettings.Routines ??= new RoutinesSettings();
            candidateSettings.Routines.Items ??= [];
            candidateSettings.Routines.Items = candidateRoutines;
            SettingsValidationService.Normalize(candidateSettings);

            if (TryFindDuplicateRoutineId(candidateSettings.Routines?.Items ?? [], out string? duplicateId))
            {
                return new RoutineMutationResult(false, 5, "routine-id-conflict", $"Routine id '{duplicateId}' is duplicated.");
            }

            AppViewModel.SettingsCommitValidationResult validation = AppViewModel.ValidateSettingsForCommit(candidateSettings);
            if (validation.HasBlockingIssues)
            {
                string details = string.Join(Environment.NewLine, validation.BlockingMessages);
                string action = operation == "import" ? "import routines" : $"{operation} routine";
                return new RoutineMutationResult(false, 5, "routine-invalid", $"Cannot {action}:{Environment.NewLine}{details}");
            }

            settings.Routines ??= new RoutinesSettings();
            settings.Routines.Items = AppViewModel.CloneRoutines(candidateSettings.Routines?.Items ?? []);

            AudioRoutine? committedRoutine = routine == null
                ? null
                : settings.Routines?.Items?.FirstOrDefault(existing => string.Equals(existing.Id, routine.Id, StringComparison.OrdinalIgnoreCase))?.Clone() ?? routine.Clone();

            string message = operation switch
            {
                "create" => $"Created routine '{committedRoutine?.Name}'.",
                "update" => $"Updated routine '{committedRoutine?.Name}'.",
                "delete" => $"Deleted routine '{routine?.Name}'.",
                "import" => $"Imported {importedCount} routine{(importedCount == 1 ? string.Empty : "s")}",
                _ => "Updated routines.",
            };

            string errorCode = operation switch
            {
                "create" => "routine-created",
                "update" => "routine-updated",
                "delete" => "routine-deleted",
                "import" => "routine-import-success",
                _ => "routine-updated",
            };

            return new RoutineMutationResult(true, 0, errorCode, message, committedRoutine, importedCount);
        }

        private static bool TryPrepareRoutineDraft(AudioRoutine draft, AudioRoutine? existingRoutine, int displayOrder, out AudioRoutine? prepared, out RoutineMutationResult errorResult)
        {
            prepared = draft.Clone();
            prepared.Id = string.IsNullOrWhiteSpace(existingRoutine?.Id)
                ? string.IsNullOrWhiteSpace(prepared.Id) ? Guid.NewGuid().ToString("N") : prepared.Id.Trim()
                : existingRoutine.Id;
            prepared.Name = prepared.Name?.Trim() ?? string.Empty;
            prepared.OutputDeviceId = prepared.OutputDeviceId?.Trim() ?? string.Empty;
            prepared.OutputDeviceName = prepared.OutputDeviceName?.Trim() ?? string.Empty;
            prepared.InputDeviceId = prepared.InputDeviceId?.Trim() ?? string.Empty;
            prepared.InputDeviceName = prepared.InputDeviceName?.Trim() ?? string.Empty;
            prepared.Hotkey = prepared.Hotkey?.Trim() ?? string.Empty;
            prepared.TriggerAppPath = prepared.TriggerAppPath?.Trim() ?? string.Empty;
            prepared.DisplayOrder = displayOrder;

            if (prepared.Name.Length > RoutineEditorViewModel.MaxRoutineNameLength)
            {
                errorResult = new RoutineMutationResult(false, 5, "routine-invalid", $"Routine name must be {RoutineEditorViewModel.MaxRoutineNameLength} characters or fewer.");
                prepared = null;
                return false;
            }

            errorResult = default;
            return true;
        }

        private static bool TryFindDuplicateRoutineId(IEnumerable<AudioRoutine> routines, out string? duplicateId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioRoutine routine in routines)
            {
                string id = routine.Id?.Trim() ?? string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(id))
                {
                    duplicateId = id;
                    return true;
                }
            }

            duplicateId = null;
            return false;
        }

        private static int FindRoutineIndex(List<AudioRoutine> routines, string routineId, bool throwWhenMissing = true)
        {
            for (int index = 0; index < routines.Count; index++)
            {
                if (string.Equals(routines[index].Id, routineId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            if (!throwWhenMissing)
            {
                return -1;
            }

            throw new InvalidOperationException($"Routine '{routineId}' was not found.");
        }
    }
}
