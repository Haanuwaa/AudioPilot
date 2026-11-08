using System.Text;
using System.Windows;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using static AudioPilot.Services.Routines.RoutineApplicationRouting;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public void AddRoutine() => AddRoutine(owner: null);

        internal void AddRoutine(Window? owner)
        {
            AudioRoutine? routine = OpenRoutineEditor(existingRoutine: null, owner);
            if (routine == null)
            {
                return;
            }

            SelectedSettingsTabIndex = 2;
            Routines.Add(routine);
            ReindexRoutines();
            SelectedRoutineIndex = Routines.Count - 1;
        }

        public void EditSelectedRoutine() => EditRoutine(SelectedRoutine, owner: null);

        internal void EditRoutine(AudioRoutine? selected, Window? owner)
        {
            if (selected == null || !Routines.Contains(selected))
            {
                return;
            }

            AudioRoutine? updated = OpenRoutineEditor(selected, owner);
            int selectedIndex = Routines.IndexOf(selected);
            if (updated == null || selectedIndex < 0)
            {
                return;
            }

            updated.DisplayOrder = selected.DisplayOrder;
            updated.Enabled = selected.Enabled;

            selected.PropertyChanged -= OnRoutinePropertyChanged;
            Routines[selectedIndex] = updated;
            AttachRoutinePropertyHandler(updated);
            ReindexRoutines();
            SelectedRoutineIndex = selectedIndex;
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
            if (_isApplyingRoutines || !CanEditManagedList) return;
            List<int> selectedIndices = GetSelectedRoutineIndices();
            if (selectedIndices.Count == 0)
            {
                return;
            }

            int nextSelectedIndex = selectedIndices[0];
            _isApplyingRoutines = true;
            try
            {
                for (int i = selectedIndices.Count - 1; i >= 0; i--)
                {
                    AudioRoutine removed = Routines[selectedIndices[i]];
                    removed.PropertyChanged -= OnRoutinePropertyChanged;
                    Routines.RemoveAt(selectedIndices[i]);
                }

                SelectedRoutines.Clear();
                ReindexRoutines();
                SelectedRoutineIndex = Routines.Count == 0 ? -1 : Math.Min(nextSelectedIndex, Routines.Count - 1);
            }
            finally { _isApplyingRoutines = false; }
            RefreshAfterRoutineCollectionChanged();
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
            duplicate.HotkeyCycleGroup = string.Empty;
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
            builder.AppendLine(routine.RoutineDetailsTargetSummary);
            builder.AppendLine($"Triggers: {routine.TriggerSummary}");
            if (routine.NotifyOnAutomaticFailure) builder.AppendLine("Notifications: Automatic action failures (once until recovery)");
            if (routine.Conditions.HasRequirements)
                builder.AppendLine($"Conditions: {routine.ConditionsSummary}");

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

        private Task<bool> SaveRoutinesFromButtonAsync()
        {
            return SaveRoutinesAsync(resetRemovedPerAppRouting: true, autoSave: false);
        }

        private async Task<bool> SaveRoutinesAsync(bool resetRemovedPerAppRouting, bool autoSave)
        {
            if (IsResettingSettings) return false;

            Settings? cachedCopy = GetCachedSettingsSnapshot();
            List<AudioRoutine> routinesAtSaveStart = CloneRoutines(Routines);
            Settings? newSettings = cachedCopy == null
                ? null
                : AppRoutineSettingsCoordinator.BuildSavedRoutineSettings(cachedCopy, routinesAtSaveStart, _outputDevices, _inputDevices);

            RoutineSaveValidationResult validationResult = AppRoutineSaveEntryCoordinator.ValidateSave(
                cachedCopy,
                newSettings,
                ValidateSettingsForCommit);

            if (!validationResult.CanProceed)
            {
                await ReportSettingsSaveProblemAsync(SettingsFeedbackTabs.Routines, validationResult.WarningMessage ?? "Review this routine's settings.");
                if (!autoSave)
                {
                    await _dialogs.ShowWarningAsync(validationResult.WarningMessage ?? string.Empty, validationResult.WarningCaption);
                }

                return false;
            }

            if (validationResult.RequiresConfirmation)
            {
                if (autoSave)
                {
                    await ReportSettingsSaveProblemAsync(SettingsFeedbackTabs.Routines, "Resolve routine conflicts, or click Save routines to review them.");
                    return false;
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
                    return false;
                }
            }

            if (newSettings == null)
            {
                return false;
            }

            SettingsSavePatch savePatch = SettingsSavePatch.Capture(cachedCopy, newSettings);
            Settings validatedSettings = newSettings;
            long autoSaveRevisionAtStart = Volatile.Read(ref _autoSaveDirtyRevision);
            IsSavingRoutines = true;

            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    validatedSettings = RebaseSettingsForSave(savePatch, out Settings latest);
                    cachedCopy = latest;
                    await Task.Run(() => _settings.SaveSettings(validatedSettings));
                    if (CommitSavedSettingsDuringShutdown(validatedSettings)) return;

                    if (resetRemovedPerAppRouting)
                    {
                        await ResetRemovedPerAppRoutingTargetsAsync(cachedCopy!.Routines.Items, validatedSettings.Routines.Items);
                    }

                    await InvokeOnDispatcherAsync(() =>
                    {
                        if (CommitSavedSettingsDuringShutdown(validatedSettings)) return;
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
                            applyRoutinesFromSettings: savedRoutines =>
                            {
                                if (AreRoutineListsEquivalent(Routines, routinesAtSaveStart))
                                {
                                    ApplyRoutinesFromSettings(savedRoutines);
                                }
                                else
                                {
                                    HasUnsavedRoutineChanges = HasRoutineEdits();
                                    _logger.Debug("AppViewModel", "routine-save-preserved-newer-edits");
                                }
                            },
                            showSuccess: static (_, _) => { });
                        ClearSettingsSaveProblems(SettingsFeedbackTabs.Routines, autoSaveRevisionAtStart);
                    });

                    _shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
                    ReportHotkeyAvailability();
                    if (!autoSave)
                    {
                        await ShowSettingsSaveFeedbackAsync("Routine changes applied successfully.", SettingsFeedbackTabs.Routines);
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "save-routines-failed", nameof(SaveRoutinesAsync), ex);
                await ReportSettingsSaveProblemAsync(SettingsFeedbackTabs.Routines, string.Empty, validation: false);
                if (autoSave && !_isCleaningUp && !_backgroundWorkCts.IsCancellationRequested)
                {
                    await _shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
                }
                if (!autoSave)
                {
                    await _dialogs.ShowErrorAsync("Failed to apply routine changes.");
                }
                return false;
            }
            finally
            {
                IsSavingRoutines = false;
                TryQueueTrailingAutoSave(autoSaveRevisionAtStart, nameof(SaveRoutinesAsync));
            }
        }

        private async Task ResetRemovedPerAppRoutingTargetsAsync(
            IEnumerable<AudioRoutine>? previousRoutines,
            IEnumerable<AudioRoutine>? nextRoutines)
        {
            IReadOnlyList<RemovedPerAppRoutingTarget> removedTargets = AppViewModelRemovedRoutingTargetHelper.CalculateRemovedPerAppRoutingTargets(previousRoutines, nextRoutines);
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
