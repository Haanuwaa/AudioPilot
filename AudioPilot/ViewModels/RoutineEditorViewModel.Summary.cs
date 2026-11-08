using System.ComponentModel;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    public string CommunicationsSummary => FormatCommunicationsSummary(LocalizationService.English);
    public Func<LocalizationService, string> CommunicationsSummaryPresentation => FormatCommunicationsSummary;

    private string FormatCommunicationsSummary(LocalizationService localization)
    {
        List<string> targets = [];
        if (BuildCommunicationsTarget(true) is { } output) targets.Add(localization.FormatKey("Routine_Output", output.Name));
        if (BuildCommunicationsTarget(false) is { } input) targets.Add(localization.FormatKey("Routine_Input", input.Name));
        return targets.Count == 0 ? localization["RoutineChoice_LeaveUnchanged"] : string.Join("; ", targets);
    }

    public string VolumeTargetsSummary => FormatVolumeTargetsSummary(LocalizationService.English);
    public Func<LocalizationService, string> VolumeTargetsSummaryPresentation => FormatVolumeTargetsSummary;

    private string FormatVolumeTargetsSummary(LocalizationService localization)
    {
        if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out int? output) ||
            !TryParseOptionalVolumePercent(MicVolumePercentText, out int? input)) return localization["Routine_CheckVolumeValues0To100"];
        List<string> changes = [];
        if (output.HasValue) changes.Add(localization.FormatKey("Routine_EditorOutputPercent", output));
        if (OutputMuteAction != RoutineMuteAction.Unchanged) changes.Add(OutputMuteAction == RoutineMuteAction.Mute ? localization["Routine_OutputMuted"] : localization["Routine_OutputUnmuted"]);
        if (input.HasValue) changes.Add(localization.FormatKey("Routine_EditorMicPercent", input));
        if (InputMuteAction != RoutineMuteAction.Unchanged) changes.Add(InputMuteAction == RoutineMuteAction.Mute ? localization["Routine_MicrophoneMuted"] : localization["Routine_MicrophoneUnmuted"]);
        return changes.Count == 0 ? localization["RoutineChoice_LeaveUnchanged"] : string.Join("; ", changes);
    }

    public string HotkeyCyclingSummary => FormatHotkeyCyclingSummary(LocalizationService.English);
    public Func<LocalizationService, string> HotkeyCyclingSummaryPresentation => FormatHotkeyCyclingSummary;
    private string FormatHotkeyCyclingSummary(LocalizationService localization) =>
        string.IsNullOrWhiteSpace(HotkeyCycleGroup) ? localization["Routine_NoCycleGroup"] : localization.FormatKey("Routine_EditorGroup", HotkeyCycleGroup.Trim());

    public string ConditionsSummary => FormatConditionsSummary(LocalizationService.English);
    public Func<LocalizationService, string> ConditionsSummaryPresentation => FormatConditionsSummary;
    private string FormatConditionsSummary(LocalizationService localization) => ValidateTimeCondition() is { } error ? localization.Format(error)
        : RequireDevice && SelectedRequiredDevice == null ? localization["Routine_ChooseTheRequiredAudioDevice"]
        : BuildConditions() is { HasRequirements: true } conditions ? conditions.BuildSummary(ResolvedRequiredApplicationName, localization) : localization["Routine_NoRequirements"];

    private void NotifySectionSummaryChanged(string? propertyName)
    {
        string? summary = propertyName switch
        {
            nameof(SelectedCommunicationsOutputIndex) or nameof(SelectedCommunicationsInputIndex) => nameof(CommunicationsSummary),
            nameof(MasterVolumePercentText) or nameof(MicVolumePercentText) or nameof(OutputMuteAction) or nameof(InputMuteAction) => nameof(VolumeTargetsSummary),
            nameof(HotkeyCycleGroup) => nameof(HotkeyCyclingSummary),
            nameof(RequireTimeWindow) or nameof(ConditionAllDay) or nameof(ConditionStart) or nameof(ConditionEnd)
                or nameof(ConditionSunday) or nameof(ConditionMonday) or nameof(ConditionTuesday) or nameof(ConditionWednesday)
                or nameof(ConditionThursday) or nameof(ConditionFriday) or nameof(ConditionSaturday)
                or nameof(RequireDevice) or nameof(SelectedRequiredDevice) or nameof(RequiredDeviceStateIndex)
                or nameof(RequiredApplicationPath) or nameof(RequiredNetworkName) or nameof(RequiredApplicationStateIndex)
                or nameof(RequiredNetworkStateIndex) => nameof(ConditionsSummary),
            _ => null,
        };
        if (summary != null) OnSummaryChanged(summary);
    }

    private void OnSummaryChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string EditorSummary => FormatEditorSummary(LocalizationService.English);
    public Func<LocalizationService, string> EditorSummaryPresentation => FormatEditorSummary;

    private string FormatEditorSummary(LocalizationService localization)
    {
        AudioRoutine routine = BuildCurrentRoutine();
        routine.Triggers = _triggerEntriesInitialized
            ? [.. _triggerEntries.Select(static entry => entry.Trigger.Copy())]
            : [.. _initialTriggers.Select(static trigger => trigger.Copy())];
        List<string> lines =
        [
            routine.HasExecutionTarget ? localization.FormatKey("Routine_EditorChanges", routine.BuildTargetSummary("; ", localization)) : localization["Routine_ChooseAtLeastOneAudioAction"],
            localization.FormatKey("Routine_EditorRuns", routine.BuildTriggerSummary(true, localization).Replace(" | " + localization.FormatKey("Routine_Or", ""), localization["Routine_EditorOr"]).Replace(" | ", "; ")),
        ];
        if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out _) || !TryParseOptionalVolumePercent(MicVolumePercentText, out _))
            lines.Add(localization["Routine_EnterVolumeLevelsFrom0To100"]);
        if (ValidateTimeCondition() is { } timeError) lines.Add(localization.Format(timeError));
        else if (routine.Conditions.HasRequirements) lines.Add(localization.FormatKey("Routine_EditorOnlyWhen", FormatConditionsSummary(localization)));
        if (RequireDevice && SelectedRequiredDevice == null) lines.Add(localization["Routine_ChooseTheRequiredAudioDeviceText"]);
        if (routine.NotifyOnAutomaticFailure) lines.Add(localization["Routine_NotifyOnceIfAnAutomaticAudioAction"]);
        if (IsEditingAutomaticTrigger) lines.Add(localization["Routine_TheTriggerBeingEditedIsNotIncluded"]);
        return string.Join(Environment.NewLine, lines);
    }

    private void OnSummaryHotkeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotkeyViewModel.DisplayText)) OnPropertyChanged(nameof(EditorSummary));
    }
}
