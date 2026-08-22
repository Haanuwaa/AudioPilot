using System.ComponentModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
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
            else if (routine.Conditions.HasRequirements) lines.Add($"Only when: {routine.Conditions.Summary}");
            if (RequireDevice && SelectedRequiredDevice == null) lines.Add("Choose the required audio device.");
            if (IsEditingAutomaticTrigger) lines.Add("The trigger being edited is not included until you confirm it.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private void OnSummaryHotkeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotkeyViewModel.DisplayText)) OnPropertyChanged(nameof(EditorSummary));
    }
}
