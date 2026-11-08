using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private readonly AudioRoutine[] _otherRoutines;
    private string _hotkeyCycleGroup;
    public IReadOnlyList<string> HotkeyCycleGroups => [.. _otherRoutines.Select(static routine => routine.HotkeyCycleGroup)
        .Where(static group => group.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    public bool IsHotkeyCyclingExpanded { get; set; }
    public bool HasHotkeyCycleGroups => _otherRoutines.Any(static routine => routine.HotkeyCycleGroup.Length > 0);
    public string HotkeyCycleGroup
    {
        get => _hotkeyCycleGroup;
        set
        {
            if (_hotkeyCycleGroup == value) return;
            _hotkeyCycleGroup = value ?? string.Empty;
            OnPropertyChanged();
            var member = _otherRoutines.FirstOrDefault(routine => string.Equals(routine.HotkeyCycleGroup, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (member != null && !string.IsNullOrWhiteSpace(member.Hotkey)) EditorHotkey.LoadFromString(member.Hotkey);
        }
    }
}
