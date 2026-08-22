using System.Text;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public void AddRoutine()
        {
            AudioRoutine? routine = OpenRoutineEditor(existingRoutine: null);
            if (routine == null)
            {
                return;
            }

            SelectedSettingsTabIndex = 2;
            Routines.Add(routine);
            ReindexRoutines();
            SelectedRoutineIndex = Routines.Count - 1;
        }

        public void EditSelectedRoutine()
        {
            AudioRoutine? selected = SelectedRoutine;
            if (selected == null)
            {
                return;
            }

            AudioRoutine? updated = OpenRoutineEditor(selected);
            if (updated == null)
            {
                return;
            }

            int selectedIndex = SelectedRoutineIndex;
            updated.DisplayOrder = selected.DisplayOrder;
            updated.Enabled = selected.Enabled;

            selected.PropertyChanged -= OnRoutinePropertyChanged;
            Routines[selectedIndex] = updated;
            AttachRoutinePropertyHandler(updated);
            ReindexRoutines();
            SelectedRoutineIndex = selectedIndex;
            HasUnsavedRoutineChanges = true;
        }

        public void DuplicateSelectedRoutine()
        {
            AudioRoutine? selected = SelectedRoutine;
            if (selected == null)
            {
                return;
            }

            int selectedIndex = SelectedRoutineIndex;
            AudioRoutine duplicate = CreateRoutineDuplicate(selected, Routines, BuildNextRoutineName);
            Routines.Insert(selectedIndex + 1, duplicate);
            SelectedRoutineIndex = selectedIndex + 1;
        }

        public async Task CopySelectedRoutineAsync()
        {
            AudioRoutine? selected = SelectedRoutine;
            if (selected == null)
            {
                return;
            }

            string clipboardText = BuildRoutineClipboardText(selected);
            if (RoutineClipboardTextWriter(clipboardText))
            {
                return;
            }

            await _dialogs.ShowErrorAsync(
                "Unable to copy routine details to the clipboard.");
        }

        public void RemoveSelectedRoutine()
        {
            List<int> selectedIndices = GetSelectedRoutineIndices();
            if (selectedIndices.Count == 0)
            {
                return;
            }

            int nextSelectedIndex = selectedIndices[0];
            for (int i = selectedIndices.Count - 1; i >= 0; i--)
            {
                Routines.RemoveAt(selectedIndices[i]);
            }

            SelectedRoutines.Clear();
            ReindexRoutines();
            SelectedRoutineIndex = Routines.Count == 0 ? -1 : Math.Min(nextSelectedIndex, Routines.Count - 1);
            HasUnsavedRoutineChanges = true;
        }

        public void MoveSelectedRoutineUp()
        {
            if (SelectedRoutineIndex <= 0 || SelectedRoutineIndex >= Routines.Count)
            {
                return;
            }

            int index = SelectedRoutineIndex;
            Routines.Move(index, index - 1);
            ReindexRoutines();
            SelectedRoutineIndex = index - 1;
            HasUnsavedRoutineChanges = true;
        }

        public void MoveSelectedRoutineDown()
        {
            if (SelectedRoutineIndex < 0 || SelectedRoutineIndex >= Routines.Count - 1)
            {
                return;
            }

            int index = SelectedRoutineIndex;
            Routines.Move(index, index + 1);
            ReindexRoutines();
            SelectedRoutineIndex = index + 1;
            HasUnsavedRoutineChanges = true;
        }

        public void EnableSelectedRoutines()
        {
            SetSelectedRoutinesEnabled(true);
        }

        public void DisableSelectedRoutines()
        {
            SetSelectedRoutinesEnabled(false);
        }

        private void SetSelectedRoutinesEnabled(bool enabled)
        {
            if (!HasSelectedRoutines)
            {
                return;
            }

            foreach (AudioRoutine routine in SelectedRoutines)
            {
                if (routine.Enabled != enabled)
                {
                    routine.Enabled = enabled;
                }
            }

            OnPropertyChanged(nameof(SelectedRoutine));
            OnPropertyChanged(nameof(HasSelectedRoutine));
            OnPropertyChanged(nameof(HasSingleSelectedRoutine));
            OnPropertyChanged(nameof(HasNoSelectedRoutine));
            OnPropertyChanged(nameof(CanEnableSelectedRoutines));
            OnPropertyChanged(nameof(CanDisableSelectedRoutines));
        }

        private List<int> GetSelectedRoutineIndices()
        {
            if (SelectedRoutines.Count > 0)
            {
                HashSet<AudioRoutine> selectedRoutineSet = [.. SelectedRoutines];
                var selectedIndices = new List<int>();

                for (int index = 0; index < Routines.Count; index++)
                {
                    if (selectedRoutineSet.Contains(Routines[index]))
                    {
                        selectedIndices.Add(index);
                    }
                }

                if (selectedIndices.Count > 0)
                {
                    return selectedIndices;
                }
            }

            return SelectedRoutineIndex >= 0 && SelectedRoutineIndex < Routines.Count
                ? [SelectedRoutineIndex]
                : [];
        }

        internal static List<AudioRoutine> CloneRoutines(IEnumerable<AudioRoutine>? routines)
        {
            if (routines == null)
            {
                return [];
            }

            var cloned = new List<AudioRoutine>();
            int displayOrder = 1;
            foreach (AudioRoutine? routine in routines)
            {
                if (routine == null)
                {
                    continue;
                }

                AudioRoutine clone = routine.Clone();
                clone.DisplayOrder = displayOrder++;
                cloned.Add(clone);
            }

            return cloned;
        }

        internal static AudioRoutine CreateRoutineDuplicate(
            AudioRoutine source,
            IEnumerable<AudioRoutine> existingRoutines,
            Func<string> fallbackNameFactory)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(existingRoutines);
            ArgumentNullException.ThrowIfNull(fallbackNameFactory);

            AudioRoutine duplicate = source.Clone();
            duplicate.Id = Guid.NewGuid().ToString("N");
            duplicate.Name = BuildDuplicateRoutineName(source.Name, existingRoutines.Select(static routine => routine.Name), fallbackNameFactory);
            duplicate.Enabled = false;
            duplicate.Hotkey = string.Empty;
            duplicate.LastRunUtc = null;
            duplicate.LastRunState = RoutineLastRunState.Never;
            duplicate.LastRunDetail = string.Empty;
            duplicate.HasConflict = false;
            duplicate.ConflictSummary = string.Empty;
            return duplicate;
        }

        internal static string BuildDuplicateRoutineName(
            string? sourceName,
            IEnumerable<string?> existingNames,
            Func<string> fallbackNameFactory)
        {
            ArgumentNullException.ThrowIfNull(existingNames);
            ArgumentNullException.ThrowIfNull(fallbackNameFactory);

            var usedNames = new HashSet<string>(
                existingNames.Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name!.Trim()),
                StringComparer.OrdinalIgnoreCase);

            string resolvedBaseName = string.IsNullOrWhiteSpace(sourceName)
                ? fallbackNameFactory()
                : sourceName.Trim();
            string preferredName = BuildDuplicateRoutineNameCandidate(resolvedBaseName, suffixNumber: null);

            if (!usedNames.Contains(preferredName))
            {
                return preferredName;
            }

            for (int suffix = 2; suffix <= usedNames.Count + 2; suffix++)
            {
                string candidate = BuildDuplicateRoutineNameCandidate(resolvedBaseName, suffix);
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            return BuildDuplicateRoutineNameCandidate(resolvedBaseName, usedNames.Count + 2);
        }

        internal static string BuildRoutineClipboardText(AudioRoutine routine)
        {
            ArgumentNullException.ThrowIfNull(routine);

            var builder = new StringBuilder();
            builder.AppendLine($"Routine: {FormatRoutineClipboardValue(routine.Name, fallback: "Unnamed routine")}");
            builder.AppendLine($"Status: {(routine.Enabled ? "Enabled" : "Disabled")}");
            builder.AppendLine($"Output: {FormatRoutineClipboardValue(routine.OutputDeviceName, fallback: "Not configured")}");
            builder.AppendLine($"Input: {FormatRoutineClipboardValue(routine.InputDeviceName, fallback: "Not configured")}");
            builder.AppendLine($"Triggers: {routine.TriggerSummary}");

            if (routine.HasVolumeTarget)
            {
                var volumeTargets = new List<string>();
                if (routine.MasterVolumePercent.HasValue)
                {
                    volumeTargets.Add($"Master: {routine.MasterVolumePercent.Value}%");
                }
                if (routine.MicVolumePercent.HasValue)
                {
                    volumeTargets.Add($"Mic: {routine.MicVolumePercent.Value}%");
                }
                builder.AppendLine($"Volume targets: {string.Join(", ", volumeTargets)}");
            }

            if (routine.HasConflict)
            {
                builder.AppendLine($"Conflict: {FormatRoutineClipboardValue(routine.ConflictSummary, fallback: "Conflict detected")}");
            }

            return builder.ToString().TrimEnd();
        }

        private List<AudioRoutine> GetPersistedRoutineSnapshot()
        {
            Settings? settings = GetCachedSettingsSnapshot();
            return CloneRoutines(settings?.Routines.Items);
        }

        internal bool HasRoutineEdits()
        {
            if (HasUnsavedRoutineChanges)
            {
                return true;
            }

            Settings? cachedCopy = GetCachedSettingsSnapshot();
            if (cachedCopy == null)
            {
                return Routines.Count > 0;
            }

            return !AreRoutineListsEquivalent(Routines, cachedCopy.Routines.Items);
        }

        public Task SaveRoutinesAsync()
        {
            return SaveRoutinesAsync(resetRemovedPerAppRouting: false, autoSave: false);
        }

        private Task SaveRoutinesFromButtonAsync()
        {
            return SaveRoutinesAsync(resetRemovedPerAppRouting: true, autoSave: false);
        }

        private async Task SaveRoutinesAsync(bool resetRemovedPerAppRouting, bool autoSave)
        {
            Settings? cachedCopy = GetCachedSettingsSnapshot();
            Settings? newSettings = cachedCopy == null
                ? null
                : AppRoutineSettingsCoordinator.BuildSavedRoutineSettings(cachedCopy, Routines, _outputDevices, _inputDevices);

            RoutineSaveValidationResult validationResult = AppRoutineSaveEntryCoordinator.ValidateSave(
                cachedCopy,
                newSettings,
                ValidateSettingsForCommit);

            if (!validationResult.CanProceed)
            {
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(validationResult.WarningMessage ?? string.Empty, validationResult.WarningCaption);
                }

                return;
            }

            if (validationResult.RequiresConfirmation)
            {
                if (autoSave)
                {
                    return;
                }

                if (!await AppRoutineSaveEntryCoordinator.ShouldProceedWithConfirmationAsync(
                    validationResult,
                    (message, caption) => _dialogs.ShowAsync(AppDialogRequest.Confirm(
                        message,
                        caption,
                        AppDialogKind.Warning,
                        "_Save anyway",
                        "_Go back"))))
                {
                    return;
                }
            }

            if (newSettings == null)
            {
                return;
            }

            Settings validatedSettings = newSettings;
            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            IsSavingRoutines = true;

            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    await Task.Run(() => _settings.SaveSettings(validatedSettings));

                    if (resetRemovedPerAppRouting)
                    {
                        await ResetRemovedPerAppRoutingTargetsAsync(cachedCopy!.Routines.Items, validatedSettings.Routines.Items);
                    }

                    await InvokeOnDispatcherAsync(() =>
                    {
                        AppRoutineSaveEntryCoordinator.RunSaveSuccessSideEffects(
                            validatedSettings,
                            persistUiState: () =>
                            {
                                lock (_settingsLock)
                                {
                                    _cachedSettings = validatedSettings;
                                }

                                UpdateLastSettingsWriteTime();
                            },
                            registerRoutineHotkeys: settings => RegisterRoutineHotkeysFromSettings(settings, context: "save-routines"),
                            applyRoutinesFromSettings: ApplyRoutinesFromSettings,
                            showSuccess: static (_, _) => { });
                    });

                    if (!autoSave)
                    {
                        await _dialogs.ShowSuccessAsync("Routine changes applied successfully.", DialogText.Captions.Success);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "save-routines-failed", nameof(SaveRoutinesAsync), ex);
                if (!autoSave)
                {
                    await _dialogs.ShowErrorAsync("Failed to apply routine changes.");
                }
            }
            finally
            {
                IsSavingRoutines = false;
                TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(SaveRoutinesAsync));
            }
        }

        internal static IReadOnlyList<RemovedPerAppRoutingTarget> CalculateRemovedPerAppRoutingTargets(
            IEnumerable<AudioRoutine>? previousRoutines,
            IEnumerable<AudioRoutine>? nextRoutines)
        {
            return AppViewModelRemovedRoutingTargetHelper.CalculateRemovedPerAppRoutingTargets(previousRoutines, nextRoutines);
        }

        internal static IReadOnlyList<int> FindRunningProcessIdsForExecutablePath(
            string normalizedTriggerPath,
            IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
        {
            return AppViewModelRoutineProcessSnapshotHelper.FindRunningProcessIdsForExecutablePath(normalizedTriggerPath, processSnapshots);
        }

        private async Task ResetRemovedPerAppRoutingTargetsAsync(
            IEnumerable<AudioRoutine>? previousRoutines,
            IEnumerable<AudioRoutine>? nextRoutines)
        {
            IReadOnlyList<RemovedPerAppRoutingTarget> removedTargets = CalculateRemovedPerAppRoutingTargets(previousRoutines, nextRoutines);
            if (removedTargets.Count == 0)
            {
                return;
            }

            try
            {
                List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                    () => CaptureProcessSnapshots(GetCaptureOptionsForTriggerTargets(removedTargets.Select(static target => target.NormalizedTriggerPath))));
                foreach (RemovedPerAppRoutingTarget target in removedTargets)
                {
                    IReadOnlyList<int> runningProcessIds = FindRunningProcessIdsForTriggerTarget(target.NormalizedTriggerPath, processSnapshots);
                    foreach (int processId in runningProcessIds)
                    {
                        _audio.TryResetApplicationDeviceRouting(
                            (uint)processId,
                            target.ResetOutput,
                            target.ResetInput,
                            $"routine-remove:{RoutineTriggerPathHelper.GetTriggerDisplayName(target.NormalizedTriggerPath)}:{processId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", () => $"removed-per-app-routing-reset-failed | error={ex.GetType().Name}", nameof(ResetRemovedPerAppRoutingTargetsAsync), ex);
            }
        }
    }
}
