using System.Collections.ObjectModel;
using System.ComponentModel;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    internal sealed class TriggerEntry(RoutineTrigger trigger) : INotifyPropertyChanged
    {
        internal RoutineTrigger Trigger { get; private set; } = trigger;
        public string Summary => Trigger.Summary;
        public Func<LocalizationService, string> SummaryPresentation => localization => Trigger.FormatSummary(localization);
        internal void Update(RoutineTrigger value)
        {
            string previousSummary = Trigger.Summary;
            Trigger = value;
            if (!string.Equals(previousSummary, value.Summary, StringComparison.Ordinal))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<RoutineTrigger> _initialTriggers;
    private readonly ObservableCollection<TriggerEntry> _triggerEntries = [];
    private RoutineTriggerDraftViewModel? _activeTriggerDraft;
    private int _selectedTriggerIndex;
    private int _editingTriggerIndex = -1;
    private bool _loadingTrigger;
    private bool _triggerEntriesInitialized;
    private bool _isEditingAutomaticTrigger;

    public RoutineTriggerDraftViewModel? ActiveTriggerDraft => _activeTriggerDraft;
    internal RoutineTriggerDraftViewModel CurrentTriggerDraft => _activeTriggerDraft ?? _selectedTriggerDraft;

    public bool IsEditingAutomaticTrigger => _isEditingAutomaticTrigger;
    public bool CanSaveRoutine => !_isEditingAutomaticTrigger;
    public bool HasAutomaticTriggers => _triggerEntriesInitialized
        ? _triggerEntries.Count > 0
        : _initialTriggers.Count > 0 || !_selectedTriggerDraft.IsHotkeyTriggerSelected;
    public bool ShowTriggerEmptyState => !HasAutomaticTriggers && !_isEditingAutomaticTrigger;
    public string TriggerEditorHeading => _activeTriggerDraft?.TriggerEditorHeading ?? (_selectedTriggerIndex < 0 ? "New trigger" : "Edit trigger");
    public string TriggerEditorConfirmText => _activeTriggerDraft?.TriggerEditorConfirmText ?? (_selectedTriggerIndex < 0 ? "Add trigger" : "Save trigger");
    public string? TriggerEditorError => _activeTriggerDraft?.TriggerEditorError;
    public LocalizedText? TriggerEditorErrorText => _activeTriggerDraft?.TriggerEditorErrorText;

    public ObservableCollection<TriggerEntry> TriggerEntries
    {
        get { EnsureTriggerEntries(); return _triggerEntries; }
    }

    private bool HasOtherStatefulTriggers => !_triggerEntriesInitialized
        ? _initialTriggers?.Skip(1).Any(static trigger => trigger.IsStateful) == true
        : _triggerEntries.Where((_, index) => index != _selectedTriggerIndex).Any(static entry => entry.Trigger.IsStateful);

    public bool CanAddAutomaticTrigger => !_isEditingAutomaticTrigger && TriggerEntries.Count < AudioRoutine.MaxAutomaticTriggers;
    public bool CanRemoveTrigger => !_isEditingAutomaticTrigger && _selectedTriggerIndex >= 0 && _selectedTriggerIndex < _triggerEntries.Count;

    public int SelectedTriggerIndex
    {
        get => _isEditingAutomaticTrigger ? _editingTriggerIndex : _selectedTriggerIndex;
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
        if (_triggerEntries.Count == 0 && !_selectedTriggerDraft.IsHotkeyTriggerSelected)
            _triggerEntries.Add(new(_selectedTriggerDraft.Capture()));
        _selectedTriggerIndex = _triggerEntries.Count > 0 ? 0 : -1;
    }

    private void SaveSelectedTrigger()
    {
        if (_loadingTrigger || _isEditingAutomaticTrigger || _selectedTriggerIndex < 0 || _triggerEntries.Count == 0) return;
        TriggerEntry entry = _triggerEntries[_selectedTriggerIndex];
        entry.Update(_selectedTriggerDraft.Capture(entry.Trigger.Id));
    }

    private void LoadSelectedTrigger() => LoadIntoSelectedDraft(_selectedTriggerIndex >= 0 && _selectedTriggerIndex < _triggerEntries.Count
        ? _triggerEntries[_selectedTriggerIndex].Trigger : new RoutineTrigger { Kind = RoutineTriggerKind.Hotkey, TimeZoneId = _scheduleTimeZoneId });

    private void LoadIntoSelectedDraft(RoutineTrigger trigger)
    {
        _loadingTrigger = true;
        try
        {
            _selectedTriggerDraft.Load(trigger);
            RefreshRoutineDeviceChoices();
            OnPropertyChanged(nameof(CanRemoveTrigger));
            OnPropertyChanged(nameof(IsStatefulTriggerSelected));
            OnPropertyChanged(nameof(ScheduleTimeZoneDisplayName));
            OnPropertyChanged(nameof(ScheduleTimeZoneDetails));
        }
        finally { _loadingTrigger = false; }
    }

    private void OnSelectedTriggerDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isEditingAutomaticTrigger) return;
        if (!_loadingTrigger && IsTriggerConfigurationProperty(e.PropertyName))
        {
            SaveSelectedTrigger();
        }

        if (!_loadingTrigger && e.PropertyName is nameof(RoutineTriggerDraftViewModel.IsStateful) && !IsStatefulTriggerSelected)
        {
            RestorePreviousAudioOnDeactivate = false;
        }

        string? forwarded = e.PropertyName == nameof(RoutineTriggerDraftViewModel.IsStateful)
            ? nameof(IsStatefulTriggerSelected)
            : e.PropertyName;
        if (_loadingTrigger)
        {
            if (forwarded != null) RaiseRawPropertyChanged(forwarded);
            return;
        }

        OnPropertyChanged(forwarded);
    }

    private void OnActiveTriggerDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward draft changes to listeners on the parent VM (such as unit tests and app-path preload)
        // without mutating committed triggers or recomputing the parent routine summary on every keystroke.
        if (e.PropertyName is nameof(RoutineTriggerDraftViewModel.TriggerEditorError) or nameof(RoutineTriggerDraftViewModel.TriggerEditorErrorText))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
            return;
        }

        if (e.PropertyName == nameof(RoutineTriggerDraftViewModel.IsStateful))
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
    }

    private static bool IsTriggerConfigurationProperty(string? propertyName) => propertyName is
        nameof(SelectedTriggerMode)
        or nameof(TriggerAppPath)
        or nameof(ScheduleTime)
        or nameof(TriggerNetworkName)
        or nameof(SelectedApplicationTriggerMode)
        or nameof(ApplicationTriggerTitlePattern)
        or nameof(ScheduleDays)
        or nameof(NotifyBeforeScheduledRun)
        or nameof(NetworkTriggerDirection)
        or nameof(SelectedApplicationTriggerTitleMatchMode)
        or nameof(SelectedAvailabilityDevice)
        or nameof(DeviceTransition);

    private void DetachActiveTriggerDraft()
    {
        _activeTriggerDraft?.PropertyChanged -= OnActiveTriggerDraftPropertyChanged;
        _activeTriggerDraft = null;
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
        _editingTriggerIndex = index;
        _isEditingAutomaticTrigger = true;
        RoutineTrigger seed = index >= 0 ? _triggerEntries[index].Trigger : new RoutineTrigger { TimeZoneId = _scheduleTimeZoneId };
        var draft = new RoutineTriggerDraftViewModel(this, seed.TimeZoneId, isNewTrigger: index < 0);
        draft.Load(seed);
        draft.NotifyNetworkDiscoveryUpdated();
        _activeTriggerDraft = draft;
        RefreshRoutineDeviceChoices();
        draft.PropertyChanged += OnActiveTriggerDraftPropertyChanged;
        NotifyTriggerEditorState();
    }

    internal bool ConfirmTriggerEdit()
    {
        if (!_isEditingAutomaticTrigger || _activeTriggerDraft == null) return false;
        RoutineTrigger trigger = _activeTriggerDraft.Capture(_editingTriggerIndex >= 0 ? _triggerEntries[_editingTriggerIndex].Trigger.Id : null).Normalize();
        LocalizedText? error = trigger.ValidateMessage();
        if (error == null && _triggerEntries.Where((_, index) => index != _editingTriggerIndex).Any(entry => entry.Trigger.Normalize().ConfigurationKey == trigger.ConfigurationKey))
            error = new("Message_ThisTriggerIsAlreadyConfigured");
        _activeTriggerDraft.SetError(error);
        if (error != null) return false;

        if (_editingTriggerIndex >= 0)
        {
            _triggerEntries[_editingTriggerIndex].Update(trigger);
            _selectedTriggerIndex = _editingTriggerIndex;
        }
        else
        {
            _triggerEntries.Add(new(trigger));
            _selectedTriggerIndex = _triggerEntries.Count - 1;
        }

        DetachActiveTriggerDraft();
        _isEditingAutomaticTrigger = false;
        _editingTriggerIndex = -1;
        LoadSelectedTrigger();
        RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
        NotifyTriggerEditorState();
        return true;
    }

    internal void CancelTriggerEdit()
    {
        if (!_isEditingAutomaticTrigger) return;
        DetachActiveTriggerDraft();
        _isEditingAutomaticTrigger = false;
        _editingTriggerIndex = -1;
        RefreshRoutineDeviceChoices();
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
        if (!IsStatefulTriggerSelected) RestorePreviousAudioOnDeactivate = false;
        NotifyTriggerEditorState();
    }

    private void NotifyTriggerEditorState()
    {
        RaiseRawPropertyChanged(nameof(ActiveTriggerDraft));
        RaiseRawPropertyChanged(nameof(SelectedTriggerIndex));
        RaiseRawPropertyChanged(nameof(HasAutomaticTriggers));
        RaiseRawPropertyChanged(nameof(ShowTriggerEmptyState));
        RaiseRawPropertyChanged(nameof(CanAddAutomaticTrigger));
        RaiseRawPropertyChanged(nameof(CanRemoveTrigger));
        RaiseRawPropertyChanged(nameof(IsEditingAutomaticTrigger));
        RaiseRawPropertyChanged(nameof(CanSaveRoutine));
        RaiseRawPropertyChanged(nameof(TriggerEditorHeading));
        RaiseRawPropertyChanged(nameof(TriggerEditorConfirmText));
        RaiseRawPropertyChanged(nameof(TriggerEditorError));
        RaiseRawPropertyChanged(nameof(TriggerEditorErrorText));
        RaiseRawPropertyChanged(nameof(IsStatefulTriggerSelected));
        OnSummaryChanged(nameof(EditorSummary));
        NotifyWorkspaceStateChanged();
    }

    private void RaiseRawPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string? Validate() => ValidateMessage() is { } message ? LocalizationService.English.Format(message) : null;

    internal LocalizedText? ValidateMessage()
    {
        if (_isEditingAutomaticTrigger) return new("Message_FinishOrCancelTheTriggerEditBefore");
        if (ValidateTimeCondition() is { } timeError) return timeError;
        LocalizedText? error = ValidateCurrentEditor();
        if (error != null) return error;
        AudioRoutine routine = BuildRoutine();
        if (AudioPilot.Services.Routines.RoutineHotkeyGroups.ValidateMessage(routine, []) is { } groupError) return groupError;
        if (routine.ValidateCommunicationsTargetsMessage() is { } targetError) return targetError;
        if (routine.Conditions.ValidateMessage() is { } conditionError) return conditionError;
        return routine.ValidateTriggersMessage();
    }

    public AudioRoutine BuildRoutine()
    {
        EnsureTriggerEntries();
        SaveSelectedTrigger();
        AudioRoutine routine = BuildCurrentRoutine(_selectedTriggerDraft);
        routine.Triggers = [.. _triggerEntries.Where(static entry => entry.Trigger.Kind != RoutineTriggerKind.Hotkey)
            .Select(static entry => entry.Trigger.Copy())];
        routine.RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
        return routine;
    }
}
