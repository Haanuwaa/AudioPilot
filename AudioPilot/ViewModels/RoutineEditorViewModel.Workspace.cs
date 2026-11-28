using System.Globalization;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal enum RoutineEditorSection
{
    Actions = 0,
    Triggers = 1,
    Conditions = 2,
}

internal enum RoutineStarterTemplate
{
    GamingHeadset,
    WorkCalls,
    NightVolumeCap,
    PerAppRouting,
}

internal sealed record RoutineAudioSetupSnapshot(
    string? OutputDeviceId,
    string? OutputDeviceName,
    string? OutputDeviceStableId,
    string? InputDeviceId,
    string? InputDeviceName,
    string? InputDeviceStableId,
    string? CommunicationsOutputDeviceId = null,
    string? CommunicationsOutputDeviceName = null,
    string? CommunicationsOutputDeviceStableId = null,
    string? CommunicationsInputDeviceId = null,
    string? CommunicationsInputDeviceName = null,
    string? CommunicationsInputDeviceStableId = null,
    decimal? MasterVolumePercent = null,
    decimal? MicVolumePercent = null,
    RoutineMuteAction OutputMuteAction = RoutineMuteAction.Unchanged,
    RoutineMuteAction InputMuteAction = RoutineMuteAction.Unchanged);

internal sealed record RoutineValidationIssue(
    RoutineEditorSection Section,
    string FieldKey,
    LocalizedText Message)
{
    public Func<LocalizationService, string> MessagePresentation => localization => localization.Format(Message);
    public string Text => LocalizationService.English.Format(Message);
}

internal sealed partial class RoutineEditorViewModel
{
    private RoutineEditorSection _selectedSection = RoutineEditorSection.Actions;
    private readonly Func<RoutineAudioSetupSnapshot?>? _captureCurrentAudioSetup;
    private LocalizedText? _captureSetupFeedback;
    private bool _isCaptureSetupFeedbackError;
    private bool _suppressCaptureFeedbackClear;
    private bool _clearedCommunicationsForAppRouting;
    private bool _starterTemplatesDismissed;
    private bool _hasAttemptedValidation;

    public bool ShowStarterTemplates => !IsEditingExistingRoutine && !_starterTemplatesDismissed;
    public bool CanResetChanges => HasUnsavedChanges || IsEditingAutomaticTrigger;

    internal void MarkValidationAttempted()
    {
        if (_hasAttemptedValidation) return;
        _hasAttemptedValidation = true;
        NotifyWorkspaceStateChanged();
    }

    internal void ResetToInitialState()
    {
        if (_isEditingAutomaticTrigger) CancelTriggerEdit();
        _hasAttemptedValidation = false;
        _starterTemplatesDismissed = false;
        ClearedCommunicationsForAppRouting = false;
        ClearCaptureSetupFeedback();

        AudioRoutine? initial = _initialRoutine;
        Name = initial != null ? initial.Name : _initialSuggestedName;
        Enabled = initial?.Enabled ?? true;
        SelectedOutputIndex = initial != null
            ? ResolveSelectedIndex(OutputDevices, initial.OutputDeviceId, initial.OutputDeviceName, initial.OutputDeviceStableId)
            : 0;
        SelectedInputIndex = initial != null
            ? ResolveSelectedIndex(InputDevices, initial.InputDeviceId, initial.InputDeviceName, initial.InputDeviceStableId)
            : 0;

        SelectedCommunicationsOutputIndex = initial?.CommunicationsOutput is { } commsOut
            ? ResolveSelectedIndex(CommunicationsOutputDevices, commsOut.Id, commsOut.Name, commsOut.StableId)
            : 0;
        SelectedCommunicationsInputIndex = initial?.CommunicationsInput is { } commsIn
            ? ResolveSelectedIndex(CommunicationsInputDevices, commsIn.Id, commsIn.Name, commsIn.StableId)
            : 0;
        IsCommunicationsExpanded = initial?.HasCommunicationsTarget == true;
        RaiseRawPropertyChanged(nameof(IsCommunicationsExpanded));

        EditorHotkey.LoadFromString(initial?.Hotkey ?? string.Empty);
        if (initial?.HasHotkeyWarning == true)
        {
            EditorHotkey.SetRegistrationWarning(initial.HotkeyWarningKind, initial.HotkeyWarningSummary);
        }

        _hotkeyCycleGroup = initial?.HotkeyCycleGroup ?? string.Empty;
        RaiseRawPropertyChanged(nameof(HotkeyCycleGroup));
        OnSummaryChanged(nameof(HotkeyCyclingSummary));
        IsHotkeyCyclingExpanded = _hotkeyCycleGroup.Length > 0;
        RaiseRawPropertyChanged(nameof(IsHotkeyCyclingExpanded));

        _loadingTrigger = true;
        try
        {
            if (_triggerEntriesInitialized)
            {
                _triggerEntries.Clear();
                foreach (RoutineTrigger trigger in _initialTriggers)
                {
                    _triggerEntries.Add(new(trigger.Copy()));
                }

                _selectedTriggerIndex = _triggerEntries.Count > 0 ? 0 : -1;
            }

            if (initial != null)
            {
                _selectedTriggerDraft.Load(new RoutineTrigger
                {
                    Kind = initial.TriggerKind,
                    Device = initial.TriggerDevice,
                    DeviceTransition = initial.DeviceTransition,
                    AppPath = initial.TriggerAppPath,
                    ApplicationMode = initial.ApplicationTriggerMode,
                    TitlePattern = initial.ApplicationTriggerTitlePattern,
                    TitleMatchMode = initial.ApplicationTriggerTitleMatchMode,
                    Time = initial.ScheduleTime,
                    Days = [.. initial.ScheduleDays],
                    TimeZoneId = _scheduleTimeZoneId,
                    NotifyBeforeRun = initial.NotifyBeforeScheduledRun,
                    NetworkName = initial.TriggerNetworkName,
                    NetworkDirection = initial.NetworkTriggerDirection,
                });
            }
            else
            {
                _selectedTriggerDraft.Load(new RoutineTrigger { Kind = RoutineTriggerKind.Hotkey, TimeZoneId = _scheduleTimeZoneId });
            }
        }
        finally
        {
            _loadingTrigger = false;
        }

        RouteToApplication = initial?.RouteToApplication ?? false;
        TargetAppPath = initial?.TargetAppPath ?? string.Empty;
        ShowInTrayMenu = initial?.ShowInTrayMenu ?? false;
        PlaySoundAfterApplying = initial?.PlaySoundAfterApplying ?? false;
        RestorePreviousAudioOnDeactivate = initial?.RestorePreviousAudioOnDeactivate ?? false;
        MasterVolumePercentText = FormatOptionalPercent(initial?.MasterVolumePercent);
        MicVolumePercentText = FormatOptionalPercent(initial?.MicVolumePercent);
        AppVolumePercentText = FormatOptionalPercent(initial?.AppVolumePercent);
        AppMuteAction = initial?.AppMuteAction ?? RoutineMuteAction.Unchanged;
        TriggerDelaySecondsText = initial is { TriggerDelaySeconds: > 0 }
            ? initial.TriggerDelaySeconds.ToString(CultureInfo.CurrentCulture)
            : string.Empty;
        OutputMuteAction = initial?.OutputMuteAction ?? RoutineMuteAction.Unchanged;
        InputMuteAction = initial?.InputMuteAction ?? RoutineMuteAction.Unchanged;
        InputMonitoringAction = initial?.InputMonitoringAction ?? RoutineInputMonitoringAction.Unchanged;
        RefreshVolumeTargetsExpansionState();

        _selectedRequiredDevice = initial?.Conditions.Device;
        _requiredDeviceAvailable = initial?.Conditions.DeviceAvailable ?? true;
        _requiredDeviceDefault = initial?.Conditions.DeviceMustBeDefault ?? false;
        _requiredApplicationRunning = initial?.Conditions.ApplicationMustBeRunning ?? true;
        _requiredNetworkConnected = initial?.Conditions.NetworkMustBeConnected ?? true;
        _requireDevice = _selectedRequiredDevice != null;
        RequiredApplicationPath = initial?.Conditions.RunningAppPath ?? string.Empty;
        RequiredNetworkName = initial?.Conditions.ConnectedNetwork ?? string.Empty;
        InitializeTimeCondition(initial?.Conditions.TimeWindow);
        IsConditionsExpanded = initial?.HasConditions == true;

        RefreshRoutineDeviceChoices();
        RaiseRawPropertyChanged(nameof(ShowStarterTemplates));
        RaiseRawPropertyChanged(nameof(RequireDevice));
        RaiseRawPropertyChanged(nameof(SelectedRequiredDevice));
        RaiseRawPropertyChanged(nameof(RequiredDeviceStateIndex));
        RaiseRawPropertyChanged(nameof(RequiredApplicationStateIndex));
        RaiseRawPropertyChanged(nameof(RequiredNetworkStateIndex));
        RaiseRawPropertyChanged(nameof(RequireTimeWindow));
        RaiseRawPropertyChanged(nameof(ConditionAllDay));
        RaiseRawPropertyChanged(nameof(ConditionStart));
        RaiseRawPropertyChanged(nameof(ConditionEnd));
        RaiseRawPropertyChanged(nameof(ConditionStartHourIndex));
        RaiseRawPropertyChanged(nameof(ConditionStartMinuteIndex));
        RaiseRawPropertyChanged(nameof(ConditionStartAmPmIndex));
        RaiseRawPropertyChanged(nameof(ConditionEndHourIndex));
        RaiseRawPropertyChanged(nameof(ConditionEndMinuteIndex));
        RaiseRawPropertyChanged(nameof(ConditionEndAmPmIndex));
        RaiseRawPropertyChanged(nameof(ConditionSunday));
        RaiseRawPropertyChanged(nameof(ConditionMonday));
        RaiseRawPropertyChanged(nameof(ConditionTuesday));
        RaiseRawPropertyChanged(nameof(ConditionWednesday));
        RaiseRawPropertyChanged(nameof(ConditionThursday));
        RaiseRawPropertyChanged(nameof(ConditionFriday));
        RaiseRawPropertyChanged(nameof(ConditionSaturday));
        RaiseRawPropertyChanged(nameof(IsConditionsExpanded));
        OnSummaryChanged(nameof(ConditionsSummary));
        NotifyTriggerEditorState();
    }

    public void DismissStarterTemplates()
    {
        if (_starterTemplatesDismissed) return;
        _starterTemplatesDismissed = true;
        RaiseRawPropertyChanged(nameof(ShowStarterTemplates));
    }

    public void ApplyStarterTemplate(RoutineStarterTemplate template, LocalizationService? localization = null)
    {
        if (_isEditingAutomaticTrigger) CancelTriggerEdit();
        ClearCaptureSetupFeedback();
        DismissStarterTemplates();
        LocalizationService loc = localization ?? LocalizationService.English;
        switch (template)
        {
            case RoutineStarterTemplate.GamingHeadset:
                Name = loc["Routine_TemplateGaming"];
                SwitchToSystemRouting();
                MasterVolumePercentText = "65";
                MicVolumePercentText = "85";
                OutputMuteAction = RoutineMuteAction.Unmute;
                InputMuteAction = RoutineMuteAction.Unmute;
                IsVolumeTargetsExpanded = true;
                if (TriggerEntries.Count == 0)
                {
                    AddAutomaticTrigger();
                    ActiveTriggerDraft!.SelectedTriggerMode = "Steam Big Picture";
                    _ = ConfirmTriggerEdit();
                }
                RestorePreviousAudioOnDeactivate = true;
                SelectedSection = RoutineEditorSection.Actions;
                break;

            case RoutineStarterTemplate.WorkCalls:
                Name = loc["Routine_TemplateWork"];
                SwitchToSystemRouting();
                OutputMuteAction = RoutineMuteAction.Unmute;
                InputMuteAction = RoutineMuteAction.Unmute;
                InputMonitoringAction = RoutineInputMonitoringAction.Disable;
                ShowInTrayMenu = true;
                IsCommunicationsExpanded = true;
                RaiseRawPropertyChanged(nameof(IsCommunicationsExpanded));
                IsVolumeTargetsExpanded = true;
                SelectedSection = RoutineEditorSection.Actions;
                break;

            case RoutineStarterTemplate.NightVolumeCap:
                Name = loc["Routine_TemplateNight"];
                SwitchToSystemRouting();
                MasterVolumePercentText = "25";
                IsVolumeTargetsExpanded = true;
                RequireTimeWindow = true;
                ConditionAllDay = false;
                ConditionStart = "22:00";
                ConditionEnd = "07:00";
                IsConditionsExpanded = true;
                RaiseRawPropertyChanged(nameof(IsConditionsExpanded));
                SelectedSection = RoutineEditorSection.Conditions;
                break;

            case RoutineStarterTemplate.PerAppRouting:
                Name = loc["Routine_TemplatePerApp"];
                SwitchToApplicationRouting();
                IsVolumeTargetsExpanded = true;
                SelectedSection = RoutineEditorSection.Actions;
                break;
        }
    }

    public RoutineEditorSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value) return;
            _selectedSection = value;
            if (value == RoutineEditorSection.Conditions && !IsConditionsExpanded)
            {
                IsConditionsExpanded = true;
                RaiseRawPropertyChanged(nameof(IsConditionsExpanded));
            }

            RaiseRawPropertyChanged(nameof(SelectedSection));
            RaiseRawPropertyChanged(nameof(IsActionsSectionSelected));
            RaiseRawPropertyChanged(nameof(IsTriggersSectionSelected));
            RaiseRawPropertyChanged(nameof(IsConditionsSectionSelected));
            if (_isEditingAutomaticTrigger || (RequireDevice && SelectedRequiredDevice == null))
            {
                RaiseRawPropertyChanged(nameof(ValidationIssues));
                RaiseRawPropertyChanged(nameof(HasValidationIssues));
                RaiseRawPropertyChanged(nameof(HasTriggersValidationIssue));
                RaiseRawPropertyChanged(nameof(HasConditionsValidationIssue));
            }
        }
    }

    public bool IsActionsSectionSelected
    {
        get => _selectedSection == RoutineEditorSection.Actions;
        set { if (value) SelectedSection = RoutineEditorSection.Actions; }
    }

    public bool IsTriggersSectionSelected
    {
        get => _selectedSection == RoutineEditorSection.Triggers;
        set { if (value) SelectedSection = RoutineEditorSection.Triggers; }
    }

    public bool IsConditionsSectionSelected
    {
        get => _selectedSection == RoutineEditorSection.Conditions;
        set { if (value) SelectedSection = RoutineEditorSection.Conditions; }
    }

    public bool CanCaptureCurrentAudioSetup => _captureCurrentAudioSetup != null;

    public LocalizedText? CaptureSetupFeedback => _captureSetupFeedback;
    public bool HasCaptureSetupFeedback => _captureSetupFeedback != null;
    public bool IsCaptureSetupFeedbackError => _isCaptureSetupFeedbackError;

    private void ClearCaptureSetupFeedback()
    {
        if (_suppressCaptureFeedbackClear || _captureSetupFeedback == null) return;
        _captureSetupFeedback = null;
        _isCaptureSetupFeedbackError = false;
        RaiseRawPropertyChanged(nameof(CaptureSetupFeedback));
        RaiseRawPropertyChanged(nameof(HasCaptureSetupFeedback));
        RaiseRawPropertyChanged(nameof(IsCaptureSetupFeedbackError));
    }

    public bool ClearedCommunicationsForAppRouting
    {
        get => _clearedCommunicationsForAppRouting;
        private set
        {
            if (_clearedCommunicationsForAppRouting == value) return;
            _clearedCommunicationsForAppRouting = value;
            RaiseRawPropertyChanged(nameof(ClearedCommunicationsForAppRouting));
        }
    }

    public bool SetMasterVolumeEnabled
    {
        get => !string.IsNullOrWhiteSpace(MasterVolumePercentText);
        set
        {
            if (value == SetMasterVolumeEnabled) return;
            MasterVolumePercentText = value ? "50" : string.Empty;
        }
    }

    public double MasterVolumeSliderValue
    {
        get => TryParseOptionalVolumePercent(MasterVolumePercentText, out decimal? parsed) && parsed.HasValue
            ? (double)Math.Clamp(parsed.Value, 0m, 100m)
            : 50d;
        set
        {
            int rounded = Math.Clamp((int)Math.Round(value), 0, 100);
            string formatted = rounded.ToString(CultureInfo.CurrentCulture);
            if (string.Equals(MasterVolumePercentText, formatted, StringComparison.Ordinal)) return;
            MasterVolumePercentText = formatted;
        }
    }

    public bool SetMicVolumeEnabled
    {
        get => !string.IsNullOrWhiteSpace(MicVolumePercentText);
        set
        {
            if (value == SetMicVolumeEnabled) return;
            MicVolumePercentText = value ? "50" : string.Empty;
        }
    }

    public double MicVolumeSliderValue
    {
        get => TryParseOptionalVolumePercent(MicVolumePercentText, out decimal? parsed) && parsed.HasValue
            ? (double)Math.Clamp(parsed.Value, 0m, 100m)
            : 50d;
        set
        {
            int rounded = Math.Clamp((int)Math.Round(value), 0, 100);
            string formatted = rounded.ToString(CultureInfo.CurrentCulture);
            if (string.Equals(MicVolumePercentText, formatted, StringComparison.Ordinal)) return;
            MicVolumePercentText = formatted;
        }
    }

    public bool SetAppVolumeEnabled
    {
        get => !string.IsNullOrWhiteSpace(AppVolumePercentText);
        set
        {
            if (value == SetAppVolumeEnabled) return;
            AppVolumePercentText = value ? "50" : string.Empty;
        }
    }

    public double AppVolumeSliderValue
    {
        get => TryParseOptionalVolumePercent(AppVolumePercentText, out decimal? parsed) && parsed.HasValue
            ? (double)Math.Clamp(parsed.Value, 0m, 100m)
            : 50d;
        set
        {
            int rounded = Math.Clamp((int)Math.Round(value), 0, 100);
            string formatted = rounded.ToString(CultureInfo.CurrentCulture);
            if (string.Equals(AppVolumePercentText, formatted, StringComparison.Ordinal)) return;
            AppVolumePercentText = formatted;
        }
    }

    public int ConfiguredActionsCount
    {
        get
        {
            int count = 0;
            if (SelectedOutputIndex > 0) count++;
            if (SelectedInputIndex > 0) count++;
            if (SelectedCommunicationsOutputIndex > 0) count++;
            if (SelectedCommunicationsInputIndex > 0) count++;
            if (RouteToApplication && !string.IsNullOrWhiteSpace(AppVolumePercentText)) count++;
            if (RouteToApplication && AppMuteAction != RoutineMuteAction.Unchanged) count++;
            if (!string.IsNullOrWhiteSpace(MasterVolumePercentText)) count++;
            if (!string.IsNullOrWhiteSpace(MicVolumePercentText)) count++;
            if (OutputMuteAction != RoutineMuteAction.Unchanged) count++;
            if (InputMuteAction != RoutineMuteAction.Unchanged) count++;
            if (InputMonitoringAction != RoutineInputMonitoringAction.Unchanged) count++;
            return count;
        }
    }

    public int ConfiguredTriggersCount
    {
        get
        {
            int count = _triggerEntriesInitialized ? _triggerEntries.Count : _initialTriggers.Count;
            if (!string.IsNullOrWhiteSpace(EditorHotkey.ToHotkeyString())) count++;
            if (ShowInTrayMenu) count++;
            return count;
        }
    }

    public int ConfiguredConditionsCount
    {
        get
        {
            int count = 0;
            if (RequireTimeWindow) count++;
            if (RequireDevice) count++;
            if (!string.IsNullOrWhiteSpace(RequiredApplicationPath)) count++;
            if (!string.IsNullOrWhiteSpace(RequiredNetworkName)) count++;
            return count;
        }
    }

    public IReadOnlyList<RoutineValidationIssue> ValidationIssues => CollectValidationIssues();
    public bool HasValidationIssues => ValidationIssues.Count > 0;
    public bool HasActionsValidationIssue => ValidationIssues.Any(static issue => issue.Section == RoutineEditorSection.Actions);
    public bool HasTriggersValidationIssue => ValidationIssues.Any(static issue => issue.Section == RoutineEditorSection.Triggers);
    public bool HasConditionsValidationIssue => ValidationIssues.Any(static issue => issue.Section == RoutineEditorSection.Conditions);

    public LocalizedText? HotkeyConflictMessage
    {
        get
        {
            string hotkey = EditorHotkey.ToHotkeyString();
            if (!HotkeyParsingService.TryNormalizeHotkey(hotkey, out string normalizedHotkey)) return null;
            if (_reservedHotkeyKeys.Contains(normalizedHotkey) ||
                _otherRoutines.Any(routine => routine.Enabled && string.Equals(AudioPilot.Services.Routines.RoutineHotkeyGroups.NormalizeHotkey(routine.Hotkey), normalizedHotkey, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(HotkeyCycleGroup) || !string.Equals(routine.HotkeyCycleGroup, HotkeyCycleGroup.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                return new("Message_RoutineHotkeyMustBeUniqueAndCannot");
            }

            return null;
        }
    }

    public bool HasHotkeyConflict => HotkeyConflictMessage != null;

    public IReadOnlyList<string> HotkeyCycleGroupMembers
    {
        get
        {
            string group = HotkeyCycleGroup.Trim();
            if (group.Length == 0) return [];
            return [.. _otherRoutines
                .Where(routine => string.Equals(routine.HotkeyCycleGroup, group, StringComparison.OrdinalIgnoreCase))
                .Select(static routine => routine.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))];
        }
    }

    public bool HasHotkeyCycleGroupMembers => HotkeyCycleGroupMembers.Count > 0;

    public Func<LocalizationService, string> HotkeyCycleGroupPreviewPresentation => localization =>
    {
        IReadOnlyList<string> members = HotkeyCycleGroupMembers;
        if (members.Count == 0) return string.Empty;
        string joined = string.Join(", ", members);
        return localization.FormatKey("Routine_CycleGroupSharedWith", joined);
    };

    public Func<LocalizationService, string> PreviewWhenPresentation => FormatPreviewWhen;
    public Func<LocalizationService, string> PreviewOnlyIfPresentation => FormatPreviewOnlyIf;
    public Func<LocalizationService, string> PreviewThenPresentation => FormatPreviewThen;

    public bool SwitchToApplicationRouting()
    {
        if (!CanRoutePerApp)
        {
            SelectedCommunicationsOutputIndex = 0;
            SelectedCommunicationsInputIndex = 0;
            ClearedCommunicationsForAppRouting = true;
        }
        else
        {
            ClearedCommunicationsForAppRouting = false;
        }

        RouteToApplication = true;
        return true;
    }

    public void SwitchToSystemRouting()
    {
        ClearedCommunicationsForAppRouting = false;
        RouteToApplication = false;
    }

    public bool CaptureCurrentAudioSetup(bool includeVolumeAndMute = true)
    {
        if (_captureCurrentAudioSetup == null) return false;
        RoutineAudioSetupSnapshot? snapshot = _captureCurrentAudioSetup();
        bool hasAnyValue = snapshot != null && (
            !string.IsNullOrWhiteSpace(snapshot.OutputDeviceId) ||
            !string.IsNullOrWhiteSpace(snapshot.InputDeviceId) ||
            (includeVolumeAndMute && (snapshot.MasterVolumePercent.HasValue || snapshot.MicVolumePercent.HasValue ||
                snapshot.OutputMuteAction != RoutineMuteAction.Unchanged || snapshot.InputMuteAction != RoutineMuteAction.Unchanged)));
        if (!hasAnyValue || snapshot == null)
        {
            _captureSetupFeedback = new("Routine_CaptureSetupUnavailable");
            _isCaptureSetupFeedbackError = true;
            RaiseRawPropertyChanged(nameof(CaptureSetupFeedback));
            RaiseRawPropertyChanged(nameof(HasCaptureSetupFeedback));
            RaiseRawPropertyChanged(nameof(IsCaptureSetupFeedbackError));
            return false;
        }

        _suppressCaptureFeedbackClear = true;
        try
        {
            ClearedCommunicationsForAppRouting = false;
            if (!string.IsNullOrWhiteSpace(snapshot.OutputDeviceId))
            {
                SelectedOutputIndex = ResolveSelectedIndex(OutputDevices, snapshot.OutputDeviceId, snapshot.OutputDeviceName, snapshot.OutputDeviceStableId);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.InputDeviceId))
            {
                SelectedInputIndex = ResolveSelectedIndex(InputDevices, snapshot.InputDeviceId, snapshot.InputDeviceName, snapshot.InputDeviceStableId);
            }

            if (IsSystemRouting)
            {
                bool distinctCommsOut = !string.IsNullOrWhiteSpace(snapshot.CommunicationsOutputDeviceId)
                    && !string.Equals(snapshot.CommunicationsOutputDeviceId, snapshot.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
                bool distinctCommsIn = !string.IsNullOrWhiteSpace(snapshot.CommunicationsInputDeviceId)
                    && !string.Equals(snapshot.CommunicationsInputDeviceId, snapshot.InputDeviceId, StringComparison.OrdinalIgnoreCase);

                SelectedCommunicationsOutputIndex = distinctCommsOut
                    ? ResolveSelectedIndex(CommunicationsOutputDevices, snapshot.CommunicationsOutputDeviceId, snapshot.CommunicationsOutputDeviceName, snapshot.CommunicationsOutputDeviceStableId)
                    : 0;
                SelectedCommunicationsInputIndex = distinctCommsIn
                    ? ResolveSelectedIndex(CommunicationsInputDevices, snapshot.CommunicationsInputDeviceId, snapshot.CommunicationsInputDeviceName, snapshot.CommunicationsInputDeviceStableId)
                    : 0;
                if (distinctCommsOut || distinctCommsIn)
                {
                    IsCommunicationsExpanded = true;
                    RaiseRawPropertyChanged(nameof(IsCommunicationsExpanded));
                }
            }

            if (includeVolumeAndMute)
            {
                if (snapshot.MasterVolumePercent.HasValue)
                {
                    MasterVolumePercentText = FormatOptionalPercent(snapshot.MasterVolumePercent);
                }

                if (snapshot.MicVolumePercent.HasValue)
                {
                    MicVolumePercentText = FormatOptionalPercent(snapshot.MicVolumePercent);
                }

                OutputMuteAction = snapshot.OutputMuteAction;
                InputMuteAction = snapshot.InputMuteAction;
                if (HasConfiguredVolumeTargets)
                {
                    IsVolumeTargetsExpanded = true;
                }
            }
        }
        finally
        {
            _suppressCaptureFeedbackClear = false;
        }

        _captureSetupFeedback = new("Routine_CapturedCurrentAudioSetup");
        _isCaptureSetupFeedbackError = false;
        RaiseRawPropertyChanged(nameof(CaptureSetupFeedback));
        RaiseRawPropertyChanged(nameof(HasCaptureSetupFeedback));
        RaiseRawPropertyChanged(nameof(IsCaptureSetupFeedbackError));
        return true;
    }

    private List<RoutineValidationIssue> CollectValidationIssues()
    {
        List<RoutineValidationIssue> issues = [];

        if (string.IsNullOrWhiteSpace(Name))
            issues.Add(new(RoutineEditorSection.Actions, "RoutineName", new("Message_RoutineNameIsRequired")));
        else if (Name.Length > AudioRoutine.MaxNameLength)
            issues.Add(new(RoutineEditorSection.Actions, "RoutineName", new("Validation_RoutineNameLength", AudioRoutine.MaxNameLength)));

        bool validMaster = TryParseOptionalVolumePercent(MasterVolumePercentText, out decimal? masterVolumePercent);
        bool validMic = TryParseOptionalVolumePercent(MicVolumePercentText, out decimal? micVolumePercent);
        bool validApp = !RouteToApplication || TryParseOptionalVolumePercent(AppVolumePercentText, out _);
        decimal? appVolumePercent = RouteToApplication && TryParseOptionalVolumePercent(AppVolumePercentText, out decimal? parsedApp) ? parsedApp : null;
        if (!validMaster)
            issues.Add(new(RoutineEditorSection.Actions, "MasterVolume", new("Validation_RoutineVolumeLevels")));
        else if (!validMic)
            issues.Add(new(RoutineEditorSection.Actions, "MicVolume", new("Validation_RoutineVolumeLevels")));
        else if (!validApp)
            issues.Add(new(RoutineEditorSection.Actions, "AppVolume", new("Validation_RoutineVolumeLevels")));

        bool hasAppAudioTarget = RouteToApplication && (appVolumePercent.HasValue || AppMuteAction != RoutineMuteAction.Unchanged);
        bool hasInvalidTargetAppPath = !string.IsNullOrWhiteSpace(TargetAppPath) && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TargetAppPath);
        bool missingPerAppConfiguration = (!HasAudioTargetSelected && !hasAppAudioTarget) || string.IsNullOrWhiteSpace(TargetAppPath);
        if (RouteToApplication && (hasInvalidTargetAppPath || (_hasAttemptedValidation && missingPerAppConfiguration)))
            issues.Add(new(RoutineEditorSection.Actions, "TargetApp", new("Message_ApplicationAudioRoutingRequiresATargetExe")));

        bool hasOutput = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count;
        bool hasInput = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count;
        bool hasVolumeTarget = masterVolumePercent.HasValue || micVolumePercent.HasValue || (RouteToApplication && appVolumePercent.HasValue);
        bool suppressInitialMissingActionWarning = !IsEditingExistingRoutine && !_hasAttemptedValidation;
        if (!suppressInitialMissingActionWarning && !hasOutput && !hasInput && !hasVolumeTarget && SelectedCommunicationsOutputIndex <= 0 && SelectedCommunicationsInputIndex <= 0 && OutputMuteAction == RoutineMuteAction.Unchanged && InputMuteAction == RoutineMuteAction.Unchanged && InputMonitoringAction == RoutineInputMonitoringAction.Unchanged && !hasAppAudioTarget)
            issues.Add(new(RoutineEditorSection.Actions, "OutputDevice", new("Message_ChooseAnOutputDeviceInputDeviceVolume")));

        AudioRoutine current = BuildCurrentRoutineForSummaryOrSnapshot();
        if (current.ValidateCommunicationsTargetsMessage() is { } commsError)
            issues.Add(new(RoutineEditorSection.Actions, "Communications", commsError));

        if (HotkeyConflictMessage is { } hotkeyConflict)
            issues.Add(new(RoutineEditorSection.Triggers, "Hotkey", hotkeyConflict));

        if (AudioPilot.Services.Routines.RoutineHotkeyGroups.ValidateMessage(current, []) is { } groupError)
            issues.Add(new(RoutineEditorSection.Triggers, "HotkeyCycleGroup", groupError));

        if (HasAutomaticTriggers && !TryParseTriggerDelaySeconds(TriggerDelaySecondsText, out _))
            issues.Add(new(RoutineEditorSection.Triggers, "TriggerDelay", new("Validation_RoutineTriggerDelayRange")));

        if (_isEditingAutomaticTrigger)
        {
            if (_hasAttemptedValidation || SelectedSection != RoutineEditorSection.Triggers)
                issues.Add(new(RoutineEditorSection.Triggers, "TriggerEditor", new("Message_FinishOrCancelTheTriggerEditBefore")));
        }
        else if (current.ValidateTriggersMessage() is { } triggerError)
        {
            issues.Add(new(RoutineEditorSection.Triggers, "TriggerEditor", triggerError));
        }

        if (ValidateTimeCondition() is { } timeError)
            issues.Add(new(RoutineEditorSection.Conditions, "TimeCondition", timeError));

        if (RequireDevice && SelectedRequiredDevice == null && (_hasAttemptedValidation || SelectedSection != RoutineEditorSection.Conditions))
            issues.Add(new(RoutineEditorSection.Conditions, "RequiredDevice", new("Routine_ChooseTheRequiredAudioDeviceText")));

        if (!string.IsNullOrWhiteSpace(RequiredApplicationPath) && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(RequiredApplicationPath))
            issues.Add(new(RoutineEditorSection.Conditions, "RequiredApplication", new("Validation_RequiredApplicationPath")));

        return issues;
    }

    private string FormatPreviewWhen(LocalizationService localization)
    {
        AudioRoutine routine = BuildCurrentRoutineForSummaryOrSnapshot();
        string runs = routine.BuildTriggerSummary(true, localization)
            .Replace(" | " + localization.FormatKey("Routine_Or", ""), Environment.NewLine + "• ")
            .Replace(" | ", Environment.NewLine + "• ");
        return $"• {runs}";
    }

    private string FormatPreviewOnlyIf(LocalizationService localization)
    {
        if (ValidateTimeCondition() is { } error) return $"• {localization.Format(error)}";
        if (RequireDevice && SelectedRequiredDevice == null) return $"• {localization["Routine_ChooseTheRequiredAudioDevice"]}";
        RoutineConditions conditions = BuildConditions();
        if (!conditions.HasRequirements) return localization["Routine_NoRequirements"];
        return "• " + conditions.BuildSummary(ResolvedRequiredApplicationName, localization).Replace("; ", Environment.NewLine + "• ");
    }

    private string FormatPreviewThen(LocalizationService localization)
    {
        AudioRoutine routine = BuildCurrentRoutineForSummaryOrSnapshot();
        if (!routine.HasExecutionTarget) return localization["Routine_ChooseAtLeastOneAudioAction"];
        List<string> items = [.. routine.BuildTargetSummary("\n", localization).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        if (PlaySoundAfterApplying) items.Add(localization["Routine_PlaySoundAfterApplying"]);
        if (RestorePreviousAudioOnDeactivate && IsStatefulTriggerSelected) items.Add(localization["Ui_RestorePreviousAudioWhenThisRoutineDeactivates"]);
        return string.Join(Environment.NewLine, items.Select(static item => $"• {item}"));
    }

    private void NotifyWorkspaceStateChanged()
    {
        RaiseRawPropertyChanged(nameof(ConfiguredActionsCount));
        RaiseRawPropertyChanged(nameof(ConfiguredTriggersCount));
        RaiseRawPropertyChanged(nameof(ConfiguredConditionsCount));
        RaiseRawPropertyChanged(nameof(ValidationIssues));
        RaiseRawPropertyChanged(nameof(HasValidationIssues));
        RaiseRawPropertyChanged(nameof(HasActionsValidationIssue));
        RaiseRawPropertyChanged(nameof(HasTriggersValidationIssue));
        RaiseRawPropertyChanged(nameof(HasConditionsValidationIssue));
        RaiseRawPropertyChanged(nameof(HotkeyConflictMessage));
        RaiseRawPropertyChanged(nameof(HasHotkeyConflict));
        RaiseRawPropertyChanged(nameof(HotkeyCycleGroupMembers));
        RaiseRawPropertyChanged(nameof(HasHotkeyCycleGroupMembers));
        RaiseRawPropertyChanged(nameof(HotkeyCycleGroupPreviewPresentation));
        RaiseRawPropertyChanged(nameof(PreviewWhenPresentation));
        RaiseRawPropertyChanged(nameof(PreviewOnlyIfPresentation));
        RaiseRawPropertyChanged(nameof(PreviewThenPresentation));
        RaiseRawPropertyChanged(nameof(HasUnsavedChanges));
        RaiseRawPropertyChanged(nameof(CanResetChanges));
    }
}
