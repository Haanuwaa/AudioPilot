using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private void OnRoutinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (AudioRoutine routine in e.OldItems.OfType<AudioRoutine>())
                {
                    routine.PropertyChanged -= OnRoutinePropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (AudioRoutine routine in e.NewItems.OfType<AudioRoutine>())
                {
                    AttachRoutinePropertyHandler(routine);
                }
            }

            ReindexRoutines();
            if (!_suppressRoutineRuntimeStatePruning)
            {
                PruneStaleRoutineRuntimeStates();
            }

            HasUnsavedRoutineChanges = true;
            OnPropertyChanged(nameof(HasRoutines));
            OnPropertyChanged(nameof(HasNoRoutines));
            RefreshHotkeyConflictIndicators();
            RefreshRoutineConflictIndicators();
            ApplyRoutineRuntimeStateToAllRoutines();
            UpdateAutomaticRoutineTriggerStates();
            UpdateRoutineLastRunRefreshTimerState(
                "routines-collection-changed",
                forceRefresh: _isWindowVisible && IsRoutinesTabActive);
            QueueAutoSave(nameof(Routines));
        }

        private void OnRoutinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (IsRoutineRuntimeOnlyProperty(e.PropertyName))
            {
                return;
            }

            HasUnsavedRoutineChanges = true;
            RefreshHotkeyConflictIndicators();
            RefreshRoutineConflictIndicators();
            UpdateAutomaticRoutineTriggerStates();
            if (ReferenceEquals(sender, SelectedRoutine))
            {
                OnPropertyChanged(nameof(SelectedRoutine));
                OnPropertyChanged(nameof(HasSelectedRoutine));
                OnPropertyChanged(nameof(HasSingleSelectedRoutine));
                OnPropertyChanged(nameof(HasNoSelectedRoutine));
            }

            if (sender is AudioRoutine routine && SelectedRoutines.Contains(routine))
            {
                OnPropertyChanged(nameof(CanEnableSelectedRoutines));
                OnPropertyChanged(nameof(CanDisableSelectedRoutines));
            }

            QueueAutoSave(nameof(Routines));
        }

        private void UpdateRoutineLastRunState(AudioRoutine routine, RoutineExecutionResult result)
        {
            if (result.Skipped)
            {
                return;
            }

            RoutineLastRunState state = result.AwaitingAppCompletion
                ? RoutineLastRunState.WaitingForApp
                : result.Success
                    ? RoutineLastRunState.Succeeded
                    : RoutineLastRunState.Failed;
            SetRoutineLastRunState(routine, state);
        }

        private bool ApplyRoutineAbsoluteVolume(bool playback, string? targetDeviceId, int targetPercent, string reason)
        {
            string flow = playback ? "playback" : "recording";

            try
            {
                if (ApplyRoutineAbsoluteVolumeOverrideForTests is Func<bool, string?, int, string, bool> overrideHandler)
                {
                    return overrideHandler(playback, targetDeviceId, targetPercent, reason);
                }

                using MMDevice? device = playback
                    ? _audio.TryGetPlaybackDeviceForRoutine(targetDeviceId)
                    : _audio.TryGetRecordingDeviceForRoutine(targetDeviceId);

                if (device == null)
                {
                    _logger.Warning("AppViewModel", () => $"routine-volume-apply-failed | flow={flow} reason=no-target-device targetId={FormatRoutineLogIdentifier(targetDeviceId)} opId={NormalizeRoutineLogValue(reason)}");
                    return false;
                }

                if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(_logger, device, targetPercent, reason, muteAtZero: true, unmuteAboveZero: true, out _))
                {
                    _logger.Warning("AppViewModel", () => $"routine-volume-apply-failed | flow={flow} reason=endpoint-volume-unavailable targetId={FormatRoutineLogIdentifier(targetDeviceId)} opId={NormalizeRoutineLogValue(reason)}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", $"routine-volume-apply-failed | flow={flow}", nameof(ApplyRoutineAbsoluteVolume), ex);
                return false;
            }
        }

        private void SetRoutineLastRunState(AudioRoutine routine, RoutineLastRunState state, string? detail = null, DateTimeOffset? timestamp = null)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string normalizedRoutineId = NormalizeRoutineId(routine.Id);
            RoutineRuntimeState runtimeStateSnapshot;
            lock (_routineRuntimeStateLock)
            {
                RoutineRuntimeState runtimeState = GetOrCreateRoutineRuntimeStateUnderLock(normalizedRoutineId);
                runtimeState.LastRunState = state;
                runtimeState.LastRunDetail = detail ?? string.Empty;
                runtimeState.LastRunUtc = timestamp ?? DateTimeOffset.UtcNow;
                runtimeStateSnapshot = CloneRoutineRuntimeState(runtimeState);
            }

            ApplyRoutineRuntimeStateOnDispatcher(
                routine,
                normalizedRoutineId,
                runtimeStateSnapshot);
        }

        private void SetRoutineLastRunState(string? routineId, RoutineLastRunState state, string? detail = null, DateTimeOffset? timestamp = null)
        {
            string normalizedRoutineId = NormalizeRoutineId(routineId);
            RoutineRuntimeState runtimeStateSnapshot;
            lock (_routineRuntimeStateLock)
            {
                RoutineRuntimeState runtimeState = GetOrCreateRoutineRuntimeStateUnderLock(normalizedRoutineId);
                runtimeState.LastRunState = state;
                runtimeState.LastRunDetail = detail ?? string.Empty;
                runtimeState.LastRunUtc = timestamp ?? DateTimeOffset.UtcNow;
                runtimeStateSnapshot = CloneRoutineRuntimeState(runtimeState);
            }

            ApplyRoutineRuntimeStateOnDispatcher(
                routine: null,
                normalizedRoutineId,
                runtimeStateSnapshot);
        }

        private void ApplyRoutineRuntimeStateOnDispatcher(
            AudioRoutine? routine,
            string routineId,
            RoutineRuntimeState runtimeState)
        {
            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            void Apply()
            {
                if (routine != null)
                {
                    ApplyRoutineRuntimeState(routine, runtimeState);
                }

                ApplyRoutineRuntimeStateToMatchingRoutine(routineId, runtimeState);
                UpdateRoutineLastRunRefreshTimerState("routine-last-run-state", forceRefresh: _isWindowVisible && IsRoutinesTabActive);
            }

            if (_dispatcher.CheckAccess())
            {
                Apply();
                return;
            }

            try
            {
                _ = _dispatcher.InvokeAsync(Apply, DispatcherPriority.Send);
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping routine runtime state apply because shutdown is in progress", nameof(SetRoutineLastRunState), ex);
            }
        }

        private void ApplyRoutineRuntimeStateToAllRoutines()
        {
            foreach (AudioRoutine routine in Routines)
            {
                string routineId = NormalizeRoutineId(routine.Id);
                RoutineRuntimeState runtimeState = GetRoutineRuntimeStateSnapshot(routineId) ?? new RoutineRuntimeState();
                ApplyRoutineRuntimeState(routine, runtimeState);
            }
        }

        private void ApplyRoutineRuntimeStateToMatchingRoutine(string routineId, RoutineRuntimeState runtimeState)
        {
            foreach (AudioRoutine routine in Routines)
            {
                if (string.Equals(NormalizeRoutineId(routine.Id), routineId, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRoutineRuntimeState(routine, runtimeState);
                    return;
                }
            }
        }

        private static void ApplyRoutineRuntimeState(AudioRoutine routine, RoutineRuntimeState runtimeState)
        {
            routine.LastRunUtc = runtimeState.LastRunUtc;
            routine.LastRunState = runtimeState.LastRunState;
            routine.LastRunDetail = runtimeState.LastRunDetail;
        }

        private RoutineRuntimeState GetOrCreateRoutineRuntimeState(string routineId)
        {
            lock (_routineRuntimeStateLock)
            {
                return GetOrCreateRoutineRuntimeStateUnderLock(routineId);
            }
        }

        private RoutineRuntimeState? GetOrCreateRoutineRuntimeState(string routineId, bool createIfMissing)
        {
            lock (_routineRuntimeStateLock)
            {
                if (_routineRuntimeStates.TryGetValue(routineId, out RoutineRuntimeState? state))
                {
                    return state;
                }

                if (!createIfMissing)
                {
                    return null;
                }

                return GetOrCreateRoutineRuntimeStateUnderLock(routineId);
            }
        }

        private RoutineRuntimeState? GetRoutineRuntimeStateSnapshot(string routineId)
        {
            lock (_routineRuntimeStateLock)
            {
                return _routineRuntimeStates.TryGetValue(routineId, out RoutineRuntimeState? state)
                    ? CloneRoutineRuntimeState(state)
                    : null;
            }
        }

        private RoutineRuntimeState GetOrCreateRoutineRuntimeStateUnderLock(string routineId)
        {
            if (!_routineRuntimeStates.TryGetValue(routineId, out RoutineRuntimeState? state))
            {
                state = new RoutineRuntimeState();
                _routineRuntimeStates[routineId] = state;
            }

            return state;
        }

        private static RoutineRuntimeState CloneRoutineRuntimeState(RoutineRuntimeState state)
        {
            return new RoutineRuntimeState
            {
                LastRunUtc = state.LastRunUtc,
                LastRunState = state.LastRunState,
                LastRunDetail = state.LastRunDetail,
            };
        }

        private void PruneStaleRoutineRuntimeStates()
        {
            lock (_routineRuntimeStateLock)
            {
                if (_routineRuntimeStates.Count == 0)
                {
                    return;
                }

                HashSet<string> activeRoutineIds = [..
                    Routines.Select(static routine => NormalizeRoutineId(routine.Id))];

                if (activeRoutineIds.Count == _routineRuntimeStates.Count)
                {
                    return;
                }

                List<string>? staleRoutineIds = null;
                foreach (string routineId in _routineRuntimeStates.Keys)
                {
                    if (!activeRoutineIds.Contains(routineId))
                    {
                        staleRoutineIds ??= [];
                        staleRoutineIds.Add(routineId);
                    }
                }

                if (staleRoutineIds == null)
                {
                    return;
                }

                for (int index = 0; index < staleRoutineIds.Count; index++)
                {
                    _routineRuntimeStates.Remove(staleRoutineIds[index]);
                }
            }
        }

        private void RefreshRoutineConflictIndicators()
        {
            IReadOnlyDictionary<string, string> summaries = AppViewModelRoutineConflictHelper.BuildConflictSummaries(Routines);

            foreach (AudioRoutine routine in Routines)
            {
                string routineId = NormalizeRoutineId(routine.Id);
                if (summaries.TryGetValue(routineId, out string? summary))
                {
                    routine.HasConflict = true;
                    routine.ConflictSummary = summary;
                    continue;
                }

                routine.HasConflict = false;
                routine.ConflictSummary = string.Empty;
                routine.HotkeyWarningKind = HotkeyWarningKind.None;
                routine.HotkeyWarningSummary = string.Empty;
            }
        }

        private void RefreshRoutineHotkeyWarningIndicators(RoutineHotkeyRegistrationResult result)
        {
            Dictionary<string, AudioRoutine> routinesById = Routines.ToDictionary(routine => NormalizeRoutineId(routine.Id), StringComparer.OrdinalIgnoreCase);

            foreach (AudioRoutine routine in Routines)
            {
                routine.HotkeyWarningKind = HotkeyWarningKind.None;
                routine.HotkeyWarningSummary = string.Empty;
            }

            foreach ((string routineId, int hotkeyId) in result.AttemptedHotkeyIdsByRoutineId)
            {
                if (!routinesById.TryGetValue(NormalizeRoutineId(routineId), out AudioRoutine? routine))
                {
                    continue;
                }

                HotkeyRegistrationOutcome outcome = _hotkeys.GetLastRegistrationOutcome(hotkeyId);
                switch (outcome.Kind)
                {
                    case HotkeyRegistrationOutcomeKind.ExternalConflict:
                        routine.HotkeyWarningKind = HotkeyWarningKind.ExternalConflict;
                        routine.HotkeyWarningSummary = "Routine hotkey is unavailable on this system right now. Windows or another app may already be using it.";
                        break;

                    case HotkeyRegistrationOutcomeKind.Fallback:
                        routine.HotkeyWarningKind = HotkeyWarningKind.Fallback;
                        routine.HotkeyWarningSummary = "Routine hotkey registered with degraded delivery. AudioPilot had to fall back to hook-only capture for it.";
                        break;
                }
            }
        }

        private void RefreshRoutineLastRunStatusDisplays()
        {
            foreach (AudioRoutine routine in Routines)
            {
                routine.RefreshLastRunStatusText();
            }
        }

        private void OnRoutineLastRunRefreshTimerTick(object? sender, EventArgs e)
        {
            if (_isCleaningUp)
            {
                return;
            }

            RefreshRoutineLastRunStatusDisplays();
            UpdateRoutineLastRunRefreshTimerState("timer-tick");
        }

        private void UpdateRoutineLastRunRefreshTimerState(string context, bool forceRefresh = false)
        {
            if (_routineLastRunRefreshTimer == null)
            {
                return;
            }

            if (forceRefresh)
            {
                RefreshRoutineLastRunStatusDisplays();
            }

            bool shouldRun = ShouldRunRoutineLastRunRefreshTimer();
            bool isRunning = _routineLastRunRefreshTimer.IsEnabled;
            if (shouldRun == isRunning)
            {
                return;
            }

            if (shouldRun)
            {
                _routineLastRunRefreshTimer.Start();
            }
            else
            {
                _routineLastRunRefreshTimer.Stop();
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AppViewModel",
                    () => $"routine-last-run-refresh-timer-state | active={shouldRun} context={context} visible={_isWindowVisible} routinesTab={IsRoutinesTabActive} trackedRoutineCount={Routines.Count}",
                    nameof(UpdateRoutineLastRunRefreshTimerState));
            }
        }

        private bool ShouldRunRoutineLastRunRefreshTimer()
        {
            return !_isCleaningUp
                && _isWindowVisible
                && IsRoutinesTabActive
                && Routines.Any(ShouldTrackRoutineLastRunStatus);
        }

        private static bool ShouldTrackRoutineLastRunStatus(AudioRoutine routine)
        {
            return routine.LastRunUtc.HasValue
                && routine.LastRunState != RoutineLastRunState.Never
                && routine.LastRunState != RoutineLastRunState.WaitingForApp;
        }

        private static RoutineExecutionResult BuildSkippedRoutineExecutionResult()
        {
            return new RoutineExecutionResult(
                Success: true,
                OutputDeviceName: null,
                InputDeviceName: null,
                Skipped: true);
        }

        private static bool IsRoutineRuntimeOnlyProperty(string? propertyName)
        {
            return propertyName is nameof(AudioRoutine.HasConflict)
                or nameof(AudioRoutine.ConflictSummary)
                or nameof(AudioRoutine.HotkeyWarningKind)
                or nameof(AudioRoutine.HotkeyWarningSummary)
                or nameof(AudioRoutine.HasHotkeyWarning)
                or nameof(AudioRoutine.LastRunUtc)
                or nameof(AudioRoutine.LastRunState)
                or nameof(AudioRoutine.LastRunDetail)
                or nameof(AudioRoutine.LastRunStatusText);
        }

        private static string NormalizeRoutineId(string? routineId)
        {
            return string.IsNullOrWhiteSpace(routineId)
                ? "unknown"
                : routineId.Trim();
        }
    }
}
