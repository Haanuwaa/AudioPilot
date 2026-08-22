using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Services.Routines;

/// <summary>Defines explicit hotkey-sharing groups for registration, validation, and ordered cycling.</summary>
internal static class RoutineHotkeyGroups
{
    internal static string BindingKey(AudioRoutine routine) => string.IsNullOrWhiteSpace(routine.HotkeyCycleGroup)
        ? "routine:" + routine.Id : "group:" + routine.HotkeyCycleGroup;

    internal static string NormalizeHotkey(string hotkey)
    {
        var parser = new HotkeyViewModel();
        return parser.LoadFromString(hotkey) ? parser.ToHotkeyString() : string.Empty;
    }

    internal static string? Validate(AudioRoutine routine, IEnumerable<AudioRoutine> routines)
    {
        if (string.IsNullOrEmpty(routine.HotkeyCycleGroup)) return null;
        if (routine.HotkeyCycleGroup.Length > 40) return "Hotkey cycle group names must be 40 characters or fewer.";
        string hotkey = NormalizeHotkey(routine.Hotkey);
        if (hotkey.Length == 0) return "A hotkey cycle group requires a valid hotkey.";
        if (routines.Any(other => !string.Equals(other.Id, routine.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(other.HotkeyCycleGroup, routine.HotkeyCycleGroup, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(NormalizeHotkey(other.Hotkey), hotkey, StringComparison.OrdinalIgnoreCase)))
            return "All routines in a hotkey cycle group must use the same hotkey.";
        return null;
    }

    internal static void UpdateMemberHotkeys(AudioRoutine edited, IEnumerable<AudioRoutine> routines)
    {
        if (string.IsNullOrEmpty(edited.HotkeyCycleGroup)) return;
        foreach (AudioRoutine member in routines)
            if (string.Equals(member.HotkeyCycleGroup, edited.HotkeyCycleGroup, StringComparison.OrdinalIgnoreCase)) member.Hotkey = edited.Hotkey;
    }

    internal static IEnumerable<AudioRoutine> Representatives(IEnumerable<AudioRoutine> routines) => routines
        .Where(static routine => routine.Enabled).GroupBy(static routine => (Binding: BindingKey(routine).ToUpperInvariant(), Hotkey: NormalizeHotkey(routine.Hotkey).ToUpperInvariant()))
        .Select(static group => group.First());
}
