using System.Collections.ObjectModel;
using System.ComponentModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    internal sealed class TriggerEntry(RoutineTrigger trigger) : INotifyPropertyChanged
    {
        internal RoutineTrigger Trigger { get; private set; } = trigger;
        public string Summary => Trigger.Summary;
        internal void Update(RoutineTrigger value)
        {
            Trigger = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<RoutineTrigger> _initialTriggers;
    private readonly ObservableCollection<TriggerEntry> _triggerEntries = [];
    private int _selectedTriggerIndex;
    private bool _loadingTrigger;
    private bool _triggerEntriesInitialized;
    private bool _isEditingAutomaticTrigger;
    private int _indexBeforeDraft;
    private bool _restoreBeforeDraft;
    private string? _triggerEditorError;
    public bool IsEditingAutomaticTrigger => _isEditingAutomaticTrigger;
    public bool CanSaveRoutine => !_isEditingAutomaticTrigger;
    public bool HasAutomaticTriggers => TriggerEntries.Count > 0;
    public bool ShowTriggerEmptyState => !HasAutomaticTriggers && !_isEditingAutomaticTrigger;
    public string TriggerEditorHeading => _selectedTriggerIndex < 0 ? "New trigger" : "Edit trigger";
    public string TriggerEditorConfirmText => _selectedTriggerIndex < 0 ? "Add trigger" : "Save trigger";
    public string? TriggerEditorError => _triggerEditorError;

    public ObservableCollection<TriggerEntry> TriggerEntries
    {
        get { EnsureTriggerEntries(); return _triggerEntries; }
    }

    private bool HasOtherStatefulTriggers => !_triggerEntriesInitialized
        ? _initialTriggers?.Skip(1).Any(static trigger => trigger.IsStateful) == true
        : _triggerEntries.Where((_, index) => index != _selectedTriggerIndex).Any(static entry => entry.Trigger.IsStateful);
    private bool HasOtherAutomaticTriggers => !_triggerEntriesInitialized
        ? _initialTriggers?.Count > 1
        : _triggerEntries.Where((_, index) => index != _selectedTriggerIndex).Any(static entry => entry.Trigger.Kind != RoutineTriggerKind.Hotkey);

    public bool CanAddAutomaticTrigger => !_isEditingAutomaticTrigger && TriggerEntries.Count < AudioRoutine.MaxAutomaticTriggers;
    public bool CanRemoveTrigger => !_isEditingAutomaticTrigger && _selectedTriggerIndex >= 0 && _selectedTriggerIndex < _triggerEntries.Count;

    public int SelectedTriggerIndex
    {
        get => _selectedTriggerIndex;
        set
        {
            EnsureTriggerEntries();
            if (_loadingTrigger || _isEditingAutomaticTrigger || value < 0 || value >= _triggerEntries.Count || value == _selectedTriggerIndex) return;
            SaveSelectedTrigger();
            _selectedTriggerIndex = value;
            LoadSelectedTrigger();
            OnPropertyChanged();
        }
    }

    private void EnsureTriggerEntries()
    {
        if (_triggerEntriesInitialized) return;
        _triggerEntriesInitialized = true;
        foreach (var trigger in _initialTriggers) _triggerEntries.Add(new(trigger.Copy()));
        if (_triggerEntries.Count == 0 && !IsHotkeyTriggerSelected) _triggerEntries.Add(new(RoutineTrigger.Capture(BuildCurrentRoutine())));
        _selectedTriggerIndex = _triggerEntries.Count > 0 ? 0 : -1;
    }

    private void SaveSelectedTrigger()
    {
        if (_loadingTrigger || _isEditingAutomaticTrigger || _selectedTriggerIndex < 0 || _triggerEntries.Count == 0) return;
        _triggerEntries[_selectedTriggerIndex].Update(RoutineTrigger.Capture(BuildCurrentRoutine(), _triggerEntries[_selectedTriggerIndex].Trigger.Id));
        OnPropertyChanged(nameof(CanRemoveTrigger));
        OnPropertyChanged(nameof(CanAddAutomaticTrigger));
    }

    private void LoadSelectedTrigger() => LoadTrigger(_selectedTriggerIndex >= 0 && _selectedTriggerIndex < _triggerEntries.Count
        ? _triggerEntries[_selectedTriggerIndex].Trigger : new RoutineTrigger { Kind = RoutineTriggerKind.Hotkey });

    private void LoadTrigger(RoutineTrigger trigger)
    {
        bool restore = _restorePreviousAudioOnDeactivate;
        _loadingTrigger = true;
        try
        {
            SelectedTriggerMode = trigger.Kind switch
            {
                RoutineTriggerKind.Application => TriggerModeLabels[1],
                RoutineTriggerKind.AudioPilotStartup => TriggerModeLabels[2],
                RoutineTriggerKind.DeviceChange => TriggerModeLabels[3],
                RoutineTriggerKind.Scheduled => TriggerModeLabels[4],
                RoutineTriggerKind.Network => TriggerModeLabels[5],
                RoutineTriggerKind.SteamBigPicture => TriggerModeLabels[6],
                RoutineTriggerKind.DeviceAvailability => TriggerModeLabels[7],
                RoutineTriggerKind.SessionUnlock => TriggerModeLabels[8],
                RoutineTriggerKind.SystemResume => TriggerModeLabels[9],
                _ => TriggerModeLabels[0],
            };
            SelectedAvailabilityDevice = trigger.Device;
            DeviceTransition = trigger.DeviceTransition;
            RefreshRoutineDeviceChoices();
            TriggerAppPath = trigger.AppPath;
            SelectedApplicationTriggerMode = ApplicationTriggerModeLabels[Enum.IsDefined(trigger.ApplicationMode) ? (int)trigger.ApplicationMode : 0];
            SelectedApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchModeLabels[Enum.IsDefined(trigger.TitleMatchMode) ? (int)trigger.TitleMatchMode : 0];
            ApplicationTriggerTitlePattern = trigger.TitlePattern;
            ScheduleTime = trigger.Time;
            ScheduleDays = trigger.Days == null ? [] : [.. trigger.Days];
            _scheduleTimeZoneId = trigger.TimeZoneId;
            NotifyBeforeScheduledRun = trigger.NotifyBeforeRun;
            TriggerNetworkName = trigger.NetworkName;
            NetworkTriggerDirection = trigger.NetworkDirection;
            RestorePreviousAudioOnDeactivate = restore;
            OnPropertyChanged(nameof(CanRemoveTrigger));
            OnPropertyChanged(nameof(IsStatefulTriggerSelected));
            OnPropertyChanged(nameof(ScheduleTimeZoneDisplayName));
            OnPropertyChanged(nameof(ScheduleTimeZoneDetails));
        }
        finally { _loadingTrigger = false; }
    }

    internal void AddAutomaticTrigger()
    {
        EnsureTriggerEntries();
        if (!CanAddAutomaticTrigger) return;
        BeginTriggerEdit(-1);
    }

    internal void EditAutomaticTrigger(TriggerEntry entry)
    {
        int index = _triggerEntries.IndexOf(entry);
        if (!_isEditingAutomaticTrigger && index >= 0) BeginTriggerEdit(index);
    }

    private void BeginTriggerEdit(int index)
    {
        SaveSelectedTrigger();
        _indexBeforeDraft = _selectedTriggerIndex;
        _restoreBeforeDraft = _restorePreviousAudioOnDeactivate;
        _isEditingAutomaticTrigger = true;
        _selectedTriggerIndex = index;
        _triggerEditorError = null;
        LoadTrigger(index >= 0 ? _triggerEntries[index].Trigger : new RoutineTrigger { TimeZoneId = _scheduleTimeZoneId });
        NotifyTriggerEditorState();
    }

    internal bool ConfirmTriggerEdit()
    {
        if (!_isEditingAutomaticTrigger) return false;
        RoutineTrigger trigger = RoutineTrigger.Capture(BuildCurrentRoutine(), _selectedTriggerIndex >= 0 ? _triggerEntries[_selectedTriggerIndex].Trigger.Id : null).Normalize();
        _triggerEditorError = trigger.Validate();
        if (_triggerEditorError == null && _triggerEntries.Where((_, index) => index != _selectedTriggerIndex).Any(entry => entry.Trigger.Normalize().ConfigurationKey == trigger.ConfigurationKey))
            _triggerEditorError = "This trigger is already configured.";
        OnPropertyChanged(nameof(TriggerEditorError));
        if (_triggerEditorError != null) return false;
        if (_selectedTriggerIndex >= 0) _triggerEntries[_selectedTriggerIndex].Update(trigger);
        else
        {
            _triggerEntries.Add(new(trigger));
            _selectedTriggerIndex = _triggerEntries.Count - 1;
        }
        _isEditingAutomaticTrigger = false;
        RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
        NotifyTriggerEditorState();
        return true;
    }

    internal void CancelTriggerEdit()
    {
        if (!_isEditingAutomaticTrigger) return;
        _isEditingAutomaticTrigger = false;
        _selectedTriggerIndex = _indexBeforeDraft;
        _triggerEditorError = null;
        LoadSelectedTrigger();
        RestorePreviousAudioOnDeactivate = _restoreBeforeDraft;
        NotifyTriggerEditorState();
    }

    internal void RemoveAutomaticTrigger(TriggerEntry entry)
    {
        if (_isEditingAutomaticTrigger) return;
        int index = _triggerEntries.IndexOf(entry);
        if (index < 0) return;
        _selectedTriggerIndex = index;
        RemoveSelectedTrigger();
    }

    internal void RemoveSelectedTrigger()
    {
        EnsureTriggerEntries();
        if (!CanRemoveTrigger) return;
        _loadingTrigger = true;
        try
        {
            _triggerEntries.RemoveAt(_selectedTriggerIndex);
            _selectedTriggerIndex = Math.Min(_selectedTriggerIndex, _triggerEntries.Count - 1);
        }
        finally { _loadingTrigger = false; }
        LoadSelectedTrigger();
        NotifyTriggerEditorState();
    }

    private void NotifyTriggerEditorState()
    {
        OnPropertyChanged(nameof(SelectedTriggerIndex));
        OnPropertyChanged(nameof(HasAutomaticTriggers));
        OnPropertyChanged(nameof(ShowTriggerEmptyState));
        OnPropertyChanged(nameof(CanAddAutomaticTrigger));
        OnPropertyChanged(nameof(CanRemoveTrigger));
        OnPropertyChanged(nameof(IsEditingAutomaticTrigger));
        OnPropertyChanged(nameof(CanSaveRoutine));
        OnPropertyChanged(nameof(TriggerEditorHeading));
        OnPropertyChanged(nameof(TriggerEditorConfirmText));
        OnPropertyChanged(nameof(TriggerEditorError));
        OnPropertyChanged(nameof(IsStatefulTriggerSelected));
    }

    public string? Validate()
    {
        if (_isEditingAutomaticTrigger) return "Finish or cancel the trigger edit before saving the routine.";
        if (ValidateTimeCondition() is { } timeError) return timeError;
        string? error = ValidateCurrentEditor();
        if (error != null) return error;
        AudioRoutine routine = BuildRoutine();
        if (AudioPilot.Services.Routines.RoutineHotkeyGroups.Validate(routine, []) is { } groupError) return groupError;
        if (routine.ValidateCommunicationsTargets() is { } targetError) return targetError;
        if (routine.Conditions.Validate() is { } conditionError) return conditionError;
        return routine.ValidateTriggers();
    }

    public AudioRoutine BuildRoutine()
    {
        EnsureTriggerEntries();
        SaveSelectedTrigger();
        AudioRoutine routine = BuildCurrentRoutine();
        routine.Triggers = [.. _triggerEntries.Where(static entry => entry.Trigger.Kind != RoutineTriggerKind.Hotkey)
            .Select(static entry => entry.Trigger.Copy())];
        routine.RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
        return routine;
    }
}
