using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private sealed class RoutineRuntimeState
        {
            public DateTimeOffset? LastRunUtc { get; set; }
            public RoutineLastRunState LastRunState { get; set; } = RoutineLastRunState.Never;
            public string LastRunDetail { get; set; } = string.Empty;
        }

        internal static Func<int, CancellationToken, Task> RoutineReconnectPostAttemptDelayAsyncForTests { get; set; } = Task.Delay;
        internal static Func<bool, string?, int, string, bool>? ApplyRoutineAbsoluteVolumeOverrideForTests { get; set; }
        internal static Func<string, bool> RoutineClipboardTextWriter { get; set; } = TryWriteRoutineClipboardText;

        internal readonly record struct RoutineOverlayDisplay(string Header, string? OutputDeviceName, string? InputDeviceName);
        internal readonly record struct RemovedPerAppRoutingTarget(string NormalizedTriggerPath, bool ResetOutput, bool ResetInput);
        internal readonly record struct PerAppRoutingSelection(bool Output, bool Input);

        private readonly Dictionary<string, RoutineRuntimeState> _routineRuntimeStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _routineRuntimeStateLock = new();
        private bool _isApplyingRoutines;

        public ObservableCollection<AudioRoutine> Routines { get; } = [];

        public ObservableCollection<AudioRoutine> SelectedRoutines { get; } = [];

        public ObservableCollection<CycleDevice> AvailableRoutineOutputDevices { get; } = [];

        public ObservableCollection<CycleDevice> AvailableRoutineInputDevices { get; } = [];

        private int _selectedRoutineIndex = -1;

        public int SelectedRoutineIndex
        {
            get => _selectedRoutineIndex;
            set
            {
                if (_selectedRoutineIndex == value)
                {
                    return;
                }

                _selectedRoutineIndex = value;
                OnPropertyChanged(nameof(SelectedRoutineIndex));
                OnPropertyChanged(nameof(SelectedRoutine));
                OnPropertyChanged(nameof(HasSelectedRoutine));
                OnPropertyChanged(nameof(HasSingleSelectedRoutine));
                OnPropertyChanged(nameof(HasNoSelectedRoutine));
                OnPropertyChanged(nameof(CanEditSelectedRoutine));
            }
        }

        public AudioRoutine? SelectedRoutine =>
            SelectedRoutineIndex >= 0 && SelectedRoutineIndex < Routines.Count
                ? Routines[SelectedRoutineIndex]
                : null;

        public bool HasSelectedRoutine => SelectedRoutine != null;

        public bool HasSelectedRoutines => SelectedRoutines.Count > 0;

        public bool HasSingleSelectedRoutine => SelectedRoutines.Count == 1 && SelectedRoutine != null;

        public bool HasNoSelectedRoutine => HasRoutines && SelectedRoutine == null;

        public bool HasRoutines => Routines.Count > 0;

        public bool HasNoRoutines => Routines.Count == 0;

        public bool CanEditSelectedRoutine => HasSingleSelectedRoutine;

        public bool CanEnableSelectedRoutines => SelectedRoutines.Any(static routine => !routine.Enabled);

        public bool CanDisableSelectedRoutines => SelectedRoutines.Any(static routine => routine.Enabled);

        private bool _isSavingRoutines;

        public bool IsSavingRoutines
        {
            get => _isSavingRoutines;
            private set
            {
                if (_isSavingRoutines == value)
                {
                    return;
                }

                _isSavingRoutines = value;
                OnPropertyChanged(nameof(IsSavingRoutines));
            }
        }

        private bool _hasUnsavedRoutineChanges;

        public bool HasUnsavedRoutineChanges
        {
            get => _hasUnsavedRoutineChanges;
            private set
            {
                if (_hasUnsavedRoutineChanges == value)
                {
                    return;
                }

                _hasUnsavedRoutineChanges = value;
                OnPropertyChanged(nameof(HasUnsavedRoutineChanges));
            }
        }

        private void InitializeRoutineInfrastructure()
        {
            InitializeRoutineAppStartInfrastructure();
            Routines.CollectionChanged += OnRoutinesCollectionChanged;
        }

        private void InitializeRoutineSelectionTracking()
        {
            SelectedRoutines.CollectionChanged += OnSelectedRoutinesCollectionChanged;
        }

        private void OnSelectedRoutinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSelectedRoutines));
            OnPropertyChanged(nameof(HasSingleSelectedRoutine));
            OnPropertyChanged(nameof(CanEditSelectedRoutine));
            OnPropertyChanged(nameof(CanEnableSelectedRoutines));
            OnPropertyChanged(nameof(CanDisableSelectedRoutines));
        }

        internal void RefreshRoutineDeviceOptions()
        {
            RefreshRoutineDeviceOptionsCore(AvailableRoutineOutputDevices, _outputDevices);
            RefreshRoutineDeviceOptionsCore(AvailableRoutineInputDevices, _inputDevices);
        }

        internal RoutineHotkeyRegistrationResult RegisterRoutineHotkeysFromSettings(Settings settings, string? context = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var result = _hotkeyRegistrationCoordinator.RegisterRoutineHotkeys(settings.Routines.Items, OnRoutineTriggeredFromHotkey);
            _hotkeyRegistrationCoordinator.LogRoutineRegistrationResult(result, context);
            RefreshRoutineHotkeyWarningIndicators(result);
            return result;
        }

        internal void ApplyRoutinesFromSettings(IEnumerable<AudioRoutine>? routines)
        {
            List<AudioRoutine> replacements = CloneRoutines(routines);
            string? selectedRoutineId = SelectedRoutine?.Id;

            _isApplyingRoutines = true;
            try
            {
                DetachRoutinePropertyHandlers();
                SelectedRoutines.Clear();
                Routines.Clear();

                foreach (AudioRoutine routine in replacements)
                {
                    AttachRoutinePropertyHandler(routine);
                    Routines.Add(routine);
                }
            }
            finally
            {
                _isApplyingRoutines = false;
            }

            PruneStaleRoutineRuntimeStates();
            HasUnsavedRoutineChanges = false;

            if (!string.IsNullOrEmpty(selectedRoutineId))
            {
                int newIndex = Routines.FindIndex(r => r.Id == selectedRoutineId);
                SelectedRoutineIndex = newIndex >= 0 ? newIndex : (Routines.Count > 0 ? 0 : -1);
            }
            else
            {
                SelectedRoutineIndex = Routines.Count > 0 ? 0 : -1;
            }

            OnPropertyChanged(nameof(HasRoutines));
            OnPropertyChanged(nameof(HasNoRoutines));
            OnPropertyChanged(nameof(HasNoSelectedRoutine));
            OnPropertyChanged(nameof(Routines));
            OnPropertyChanged(nameof(SelectedRoutine));
            RefreshHotkeyConflictIndicators();
            RefreshRoutineConflictIndicators();
            ApplyRoutineRuntimeStateToAllRoutines();
            RefreshRoutineLastRunStatusDisplays();
            UpdateRoutineLastRunRefreshTimerState("apply-routines");
            UpdateAutomaticRoutineTriggerStates();
        }

        internal IReadOnlyList<AudioRoutine> GetTrayMenuRoutines()
        {
            return
            [
                .. GetPersistedRoutineSnapshot().Where(static routine => routine.Enabled && routine.ShowInTrayMenu)
            ];
        }

        internal async Task RunRoutineFromTrayAsync(string routineId)
        {
            List<AudioRoutine> persistedRoutines = GetPersistedRoutineSnapshot();
            if (!AppViewModelTrayRoutineHelper.TryResolveTrayRoutine(routineId, persistedRoutines, out AudioRoutine? routine))
            {
                return;
            }

            await RunRoutineManuallyAsync(routine, "tray");
        }
        private void AttachRoutinePropertyHandler(AudioRoutine routine)
        {
            routine.PropertyChanged -= OnRoutinePropertyChanged;
            routine.PropertyChanged += OnRoutinePropertyChanged;
        }

        private void DetachRoutinePropertyHandlers()
        {
            foreach (AudioRoutine routine in Routines)
            {
                routine.PropertyChanged -= OnRoutinePropertyChanged;
            }
        }

        private void ReindexRoutines()
        {
            for (int index = 0; index < Routines.Count; index++)
            {
                Routines[index].DisplayOrder = index + 1;
            }
        }

        private AudioRoutine? OpenRoutineEditor(AudioRoutine? existingRoutine)
        {
            RefreshRoutineDeviceOptions();

            Settings hotkeySnapshot = BuildCurrentHotkeySnapshot();
            Settings? persistedSettings = GetCachedSettingsSnapshot();
            hotkeySnapshot.Routines.Items = [];
            HashSet<string> reservedHotkeyKeys = BuildAssignedHotkeyKeySet(hotkeySnapshot);

            var viewModel = new RoutineEditorViewModel(
                AvailableRoutineOutputDevices,
                AvailableRoutineInputDevices,
                existingRoutine,
                suggestedName: existingRoutine == null ? BuildNextRoutineName() : null,
                reservedHotkeyKeys: reservedHotkeyKeys,
                scheduleTimeZoneId: persistedSettings?.Miscellaneous.ScheduleTimeZoneId,
                preloadNetworks: true, otherRoutines: Routines);
            if (existingRoutine != null)
                viewModel.EditorHotkey.AcknowledgeSavedBinding(persistedSettings?.Routines.Items
                    .FirstOrDefault(routine => string.Equals(routine.Id, existingRoutine.Id, StringComparison.OrdinalIgnoreCase))?.Hotkey);
            var window = new RoutineEditorWindow(viewModel, dialogService: _dialogs);

            bool? result = DialogWindowHelper.ShowAppOwnedDialog(window);
            if (result != true || window.ResultRoutine is not { } edited) return null;
            AudioPilot.Services.Routines.RoutineHotkeyGroups.UpdateMemberHotkeys(edited, Routines);
            return edited;
        }

        private string BuildNextRoutineName()
        {
            var usedNames = new HashSet<string>(Routines.Select(routine => routine.Name), StringComparer.OrdinalIgnoreCase);

            for (int index = 1; index <= Routines.Count + 1; index++)
            {
                string candidate = $"Routine {index}";
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            return $"Routine {Routines.Count + 1}";
        }

        private static string FormatRoutineClipboardValue(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string BuildDuplicateRoutineNameCandidate(string? baseName, int? suffixNumber)
        {
            string suffix = suffixNumber.HasValue
                ? $" (Copy {suffixNumber.Value})"
                : " (Copy)";
            int maxBaseLength = Math.Max(0, AudioRoutine.MaxNameLength - suffix.Length);
            string normalizedBaseName = string.IsNullOrWhiteSpace(baseName)
                ? string.Empty
                : baseName.Trim();

            if (normalizedBaseName.Length > maxBaseLength)
            {
                normalizedBaseName = normalizedBaseName[..maxBaseLength].TrimEnd();
            }

            if (normalizedBaseName.Length == 0)
            {
                normalizedBaseName = "Routine";
                if (normalizedBaseName.Length > maxBaseLength)
                {
                    normalizedBaseName = normalizedBaseName[..maxBaseLength].TrimEnd();
                }
            }

            return $"{normalizedBaseName}{suffix}";
        }

        private static bool TryWriteRoutineClipboardText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RefreshRoutineDeviceOptionsCore(ObservableCollection<CycleDevice> target, IEnumerable<CycleDevice> source)
        {
            AppViewModelDeviceCycleHelper.SyncCycleDevices(target, source);
        }

        private static bool AreRoutineListsEquivalent(IEnumerable<AudioRoutine>? left, IEnumerable<AudioRoutine>? right)
        {
            List<AudioRoutine> leftList = CloneRoutines(left);
            List<AudioRoutine> rightList = CloneRoutines(right);

            if (leftList.Count != rightList.Count)
            {
                return false;
            }

            for (int index = 0; index < leftList.Count; index++)
            {
                AudioRoutine leftRoutine = leftList[index];
                AudioRoutine rightRoutine = rightList[index];
                if (!string.Equals(leftRoutine.Id, rightRoutine.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftRoutine.Name, rightRoutine.Name, StringComparison.Ordinal) ||
                    leftRoutine.Enabled != rightRoutine.Enabled ||
                    !string.Equals(leftRoutine.OutputDeviceId, rightRoutine.OutputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftRoutine.OutputDeviceStableId, rightRoutine.OutputDeviceStableId, StringComparison.Ordinal) ||
                    !string.Equals(leftRoutine.OutputDeviceName, rightRoutine.OutputDeviceName, StringComparison.Ordinal) ||
                    !string.Equals(leftRoutine.InputDeviceId, rightRoutine.InputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftRoutine.InputDeviceStableId, rightRoutine.InputDeviceStableId, StringComparison.Ordinal) ||
                    !string.Equals(leftRoutine.InputDeviceName, rightRoutine.InputDeviceName, StringComparison.Ordinal) ||
                    leftRoutine.MasterVolumePercent != rightRoutine.MasterVolumePercent ||
                    leftRoutine.MicVolumePercent != rightRoutine.MicVolumePercent ||
                    leftRoutine.CommunicationsOutput != rightRoutine.CommunicationsOutput || leftRoutine.CommunicationsInput != rightRoutine.CommunicationsInput ||
                    leftRoutine.OutputMuteAction != rightRoutine.OutputMuteAction || leftRoutine.InputMuteAction != rightRoutine.InputMuteAction ||
                    !AreHotkeyStringsEquivalent(leftRoutine.Hotkey, rightRoutine.Hotkey) ||
                    !string.Equals(leftRoutine.HotkeyCycleGroup, rightRoutine.HotkeyCycleGroup, StringComparison.Ordinal) ||
                    leftRoutine.TriggerFingerprint != rightRoutine.TriggerFingerprint ||
                    leftRoutine.Conditions != rightRoutine.Conditions || leftRoutine.TriggerDevice != rightRoutine.TriggerDevice || leftRoutine.DeviceTransition != rightRoutine.DeviceTransition ||
                    leftRoutine.TriggerKind != rightRoutine.TriggerKind ||
                    !string.Equals(leftRoutine.TriggerAppPath, rightRoutine.TriggerAppPath, StringComparison.OrdinalIgnoreCase) ||
                    leftRoutine.ApplicationTriggerMode != rightRoutine.ApplicationTriggerMode ||
                    !string.Equals(leftRoutine.ApplicationTriggerTitlePattern, rightRoutine.ApplicationTriggerTitlePattern, StringComparison.Ordinal) ||
                    leftRoutine.ApplicationTriggerTitleMatchMode != rightRoutine.ApplicationTriggerTitleMatchMode ||
                    leftRoutine.SwitchOutputPerApp != rightRoutine.SwitchOutputPerApp ||
                    !string.Equals(leftRoutine.TargetAppPath, rightRoutine.TargetAppPath, StringComparison.OrdinalIgnoreCase) ||
                    leftRoutine.ShowInTrayMenu != rightRoutine.ShowInTrayMenu ||
                    leftRoutine.RestorePreviousAudioOnDeactivate != rightRoutine.RestorePreviousAudioOnDeactivate ||
                    leftRoutine.NotifyBeforeScheduledRun != rightRoutine.NotifyBeforeScheduledRun ||
                    leftRoutine.ScheduleTime != rightRoutine.ScheduleTime ||
                    !leftRoutine.ScheduleDays.SetEquals(rightRoutine.ScheduleDays) ||
                    !string.Equals(leftRoutine.ScheduleTimeZoneId, rightRoutine.ScheduleTimeZoneId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftRoutine.TriggerNetworkName, rightRoutine.TriggerNetworkName, StringComparison.Ordinal) ||
                    leftRoutine.NetworkTriggerDirection != rightRoutine.NetworkTriggerDirection ||
                    leftRoutine.EnforceTargetsOnDeviceChange != rightRoutine.EnforceTargetsOnDeviceChange)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
