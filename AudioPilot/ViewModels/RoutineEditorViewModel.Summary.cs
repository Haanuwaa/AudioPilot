using System.ComponentModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    public string CommunicationsSummary
    {
        get
        {
            List<string> targets = [];
            if (BuildCommunicationsTarget(true) is { } output) targets.Add($"Output: {output.Name}");
            if (BuildCommunicationsTarget(false) is { } input) targets.Add($"Input: {input.Name}");
            return targets.Count == 0 ? "Leave unchanged" : string.Join("; ", targets);
        }
    }

    public string VolumeTargetsSummary
    {
        get
        {
            if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out int? output) ||
                !TryParseOptionalVolumePercent(MicVolumePercentText, out int? input)) return "Check volume values (0 to 100)";
            List<string> changes = [];
            if (output.HasValue) changes.Add($"Output {output}%");
            if (OutputMuteAction != RoutineMuteAction.Unchanged) changes.Add(OutputMuteAction == RoutineMuteAction.Mute ? "Output muted" : "Output unmuted");
            if (input.HasValue) changes.Add($"Microphone {input}%");
            if (InputMuteAction != RoutineMuteAction.Unchanged) changes.Add(InputMuteAction == RoutineMuteAction.Mute ? "Microphone muted" : "Microphone unmuted");
            return changes.Count == 0 ? "Leave unchanged" : string.Join("; ", changes);
        }
    }

    public string HotkeyCyclingSummary => string.IsNullOrWhiteSpace(HotkeyCycleGroup) ? "No cycle group" : $"Group: {HotkeyCycleGroup.Trim()}";

    public string ConditionsSummary => ValidateTimeCondition() is { } error ? error
        : RequireDevice && SelectedRequiredDevice == null ? "Choose the required audio device"
        : BuildConditions() is { HasRequirements: true } conditions ? conditions.BuildSummary(ResolvedRequiredApplicationName) : "No requirements";

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
        if (summary != null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(summary));
    }

    public string EditorSummary
    {
        get
        {
            AudioRoutine routine = BuildCurrentRoutine();
            routine.Triggers = _triggerEntriesInitialized
                ? [.. _triggerEntries.Select(static entry => entry.Trigger.Copy())]
                : [.. _initialTriggers.Select(static trigger => trigger.Copy())];
            List<string> lines =
            [
                routine.HasExecutionTarget ? $"Changes: {routine.TargetSummary.Replace(" | ", "; ")}" : "Choose at least one audio action.",
                routine.Triggers.Count > 0 || !string.IsNullOrWhiteSpace(routine.Hotkey) || routine.ShowInTrayMenu
                    ? $"Runs: {routine.TriggerSummary.Replace(" | OR ", " or ").Replace(" | ", "; ")}" : "Add a hotkey, tray entry, or automatic trigger.",
            ];
            if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out _) || !TryParseOptionalVolumePercent(MicVolumePercentText, out _))
                lines.Add("Enter volume levels from 0 to 100, or leave them blank.");
            if (ValidateTimeCondition() is { } timeError) lines.Add(timeError);
            else if (routine.Conditions.HasRequirements) lines.Add($"Only when: {ConditionsSummary}");
            if (RequireDevice && SelectedRequiredDevice == null) lines.Add("Choose the required audio device.");
            if (routine.NotifyOnAutomaticFailure) lines.Add("Notify once if an automatic audio action fails, until recovery.");
            if (IsEditingAutomaticTrigger) lines.Add("The trigger being edited is not included until you confirm it.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private void OnSummaryHotkeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotkeyViewModel.DisplayText)) OnPropertyChanged(nameof(EditorSummary));
    }
}
