using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using HotkeyIds = AudioPilot.Constants.AppConstants.Hotkeys;

namespace AudioPilot.Services.Configuration;

internal readonly record struct SettingsCommitValidationResult(
    bool HasBlockingIssues,
    IReadOnlyList<string> BlockingMessages)
{
    internal Func<LocalizationService, string>? MessagePresentation { get; init; }
}

/// <summary>
/// Pure settings commit validation, duplicate hotkey detection, and routine cloning helpers
/// shared across GUI ViewModels, headless CLI, and coordinators without WPF coupling.
/// </summary>
internal static class SettingsCommitValidator
{
    internal static List<AudioRoutine> CloneRoutines(IEnumerable<AudioRoutine>? routines)
    {
        if (routines == null)
        {
            return [];
        }

        var cloned = new List<AudioRoutine>();
        int displayOrder = 1;
        foreach (AudioRoutine? routine in routines)
        {
            if (routine == null)
            {
                continue;
            }

            AudioRoutine clone = routine.Clone();
            clone.DisplayOrder = displayOrder++;
            cloned.Add(clone);
        }

        return cloned;
    }

    internal static IEnumerable<(int Id, string Label, string? Hotkey)> GetGlobalBindings(Settings settings, bool switchOnly = false)
    {
        var output = settings.DeviceSwitching.Output;
        var input = settings.DeviceSwitching.Input;
        yield return (HotkeyIds.OutputSwitchHotkeyId, "Output switch", output.HotkeysEnabled ? output.SwitchHotkey : string.Empty);
        yield return (HotkeyIds.OutputReverseSwitchHotkeyId, "Output reverse switch", output.HotkeysEnabled ? output.ReverseSwitchHotkey : string.Empty);
        yield return (HotkeyIds.InputSwitchHotkeyId, "Input switch", input.HotkeysEnabled ? input.SwitchHotkey : string.Empty);
        yield return (HotkeyIds.InputReverseSwitchHotkeyId, "Input reverse switch", input.HotkeysEnabled ? input.ReverseSwitchHotkey : string.Empty);
        if (switchOnly) yield break;
        yield return (HotkeyIds.ToggleAppVisibilityHotkeyId, "Show/hide app", settings.Hotkeys.App.ToggleAppVisibility ?? HotkeyIds.DefaultToggleAppVisibility);
        yield return (HotkeyIds.ShowAudioStatusHotkeyId, "Show audio status", settings.Hotkeys.App.ShowAudioStatus);
        yield return (HotkeyIds.QuickDevicePickerHotkeyId, "Quick device picker", settings.Hotkeys.App.QuickDevicePicker);
        yield return (HotkeyIds.VolumeMixerHotkeyId, "Quick mixer", settings.Hotkeys.App.VolumeMixer);
        yield return (HotkeyIds.MediaShowCurrentTrackId, "Show current track", settings.Hotkeys.Media.ShowCurrentTrack);
        yield return (HotkeyIds.MediaPlayPauseId, "Play/Pause", settings.Hotkeys.Media.PlayPause);
        yield return (HotkeyIds.MediaNextTrackId, "Next track", settings.Hotkeys.Media.NextTrack);
        yield return (HotkeyIds.MediaPrevTrackId, "Previous track", settings.Hotkeys.Media.PreviousTrack);
        yield return (HotkeyIds.MediaSeekForwardId, "Seek forward", settings.Hotkeys.Media.SeekForward);
        yield return (HotkeyIds.MediaSeekBackwardId, "Seek backward", settings.Hotkeys.Media.SeekBackward);
        yield return (HotkeyIds.MuteMicId, "Mute mic", settings.Hotkeys.Mute.Mic);
        yield return (HotkeyIds.MuteSoundId, "Mute sound", settings.Hotkeys.Mute.Sound);
        yield return (HotkeyIds.DeafenId, "Deafen", settings.Hotkeys.Mute.Deafen);
        yield return (HotkeyIds.ListenToInputHotkeyId, "Listen to input", settings.Hotkeys.Listen.ListenToInput);
        yield return (HotkeyIds.MasterVolumeUpHotkeyId, "Master volume up", settings.Hotkeys.Volume.MasterUp);
        yield return (HotkeyIds.MasterVolumeDownHotkeyId, "Master volume down", settings.Hotkeys.Volume.MasterDown);
        yield return (HotkeyIds.MicVolumeUpHotkeyId, "Mic volume up", settings.Hotkeys.Volume.MicUp);
        yield return (HotkeyIds.MicVolumeDownHotkeyId, "Mic volume down", settings.Hotkeys.Volume.MicDown);
        yield return (HotkeyIds.HoldToMuteHotkeyId, "Hold-to-mute", settings.Hotkeys.Mute.HoldToMute);
        yield return (HotkeyIds.PushToTalkHotkeyId, "Push-to-talk", settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty);
        yield return (HotkeyIds.ForegroundMuteHotkeyId, "Foreground app mute", settings.Hotkeys.Volume.ForegroundMute);
        yield return (HotkeyIds.ForegroundVolumeDownHotkeyId, "Foreground app volume down", settings.Hotkeys.Volume.ForegroundDown);
        yield return (HotkeyIds.ForegroundVolumeUpHotkeyId, "Foreground app volume up", settings.Hotkeys.Volume.ForegroundUp);
    }

    internal static SettingsCommitValidationResult Validate(Settings candidate)
    {
        var blocking = new List<string>();
        var displayIssues = new List<SettingsDiagnostic>();

        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
            candidate,
            activeOutputDevices: null,
            activeInputDevices: null);

        foreach (SettingsDiagnostic warning in diagnostics.Warnings)
        {
            if (warning.Code.StartsWith("invalid-hotkey-", StringComparison.OrdinalIgnoreCase)
                || warning.Code.StartsWith("reserved-hotkey-", StringComparison.OrdinalIgnoreCase)
                || warning.Code.StartsWith("invalid-routine-", StringComparison.OrdinalIgnoreCase)
                || warning.Code.StartsWith("reserved-routine-hotkey-", StringComparison.OrdinalIgnoreCase))
            {
                blocking.Add($"- {warning.Message}");
                displayIssues.Add(warning);
            }
        }

        foreach (string duplicate in BuildDuplicateHotkeyConflicts(candidate))
        {
            blocking.Add($"- Duplicate hotkey: {duplicate}");
            displayIssues.Add(new("duplicate-hotkey", $"Duplicate hotkey: {duplicate}", string.Empty)
            {
                UiMessage = new("Validation_DuplicateShortcut", duplicate),
            });
        }

        return new SettingsCommitValidationResult(
            HasBlockingIssues: blocking.Count > 0,
            BlockingMessages: blocking)
        {
            MessagePresentation = language => string.Join("\n", displayIssues.Select(issue => "- " +
                (issue.UiMessage == null ? AppMessages.Translate(issue.Message, language) : language.Format(issue.UiMessage)))),
        };
    }

    internal static bool TryValidateConfigMutation(Settings previous, Settings candidate, out string? error)
    {
        return TryValidateIntroducedBlockingIssues(previous, candidate, "Invalid config change", out error);
    }

    internal static bool TryValidateRoutineStateMutation(Settings previous, Settings candidate, out string? error)
    {
        return TryValidateIntroducedBlockingIssues(previous, candidate, "Cannot enable routine", out error);
    }

    private static bool TryValidateIntroducedBlockingIssues(Settings previous, Settings candidate, string header, out string? error)
    {
        SettingsCommitValidationResult candidateValidation = Validate(candidate);
        if (!candidateValidation.HasBlockingIssues)
        {
            error = null;
            return true;
        }

        SettingsCommitValidationResult previousValidation = Validate(previous);
        var previousNonDuplicateMessages = new HashSet<string>(
            previousValidation.BlockingMessages.Where(static message => !message.StartsWith("- Duplicate hotkey: ", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        var introducedIssues = new List<string>();
        foreach (string message in candidateValidation.BlockingMessages)
        {
            if (message.StartsWith("- Duplicate hotkey: ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!previousNonDuplicateMessages.Contains(message))
            {
                introducedIssues.Add(message);
            }
        }

        Dictionary<string, List<string>> previousDuplicates = BuildDuplicateHotkeyLabels(previous);
        Dictionary<string, List<string>> candidateDuplicates = BuildDuplicateHotkeyLabels(candidate);
        foreach (string hotkey in candidateDuplicates.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            List<string> candidateLabels = candidateDuplicates[hotkey];
            bool isIntroduced = !previousDuplicates.TryGetValue(hotkey, out List<string>? previousLabels)
                || candidateLabels.Any(label => !previousLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
            if (isIntroduced)
            {
                introducedIssues.Add($"- Duplicate hotkey: {hotkey} ({string.Join(", ", candidateLabels)})");
            }
        }

        if (introducedIssues.Count == 0)
        {
            error = null;
            return true;
        }

        error = $"{header}:{Environment.NewLine}{string.Join(Environment.NewLine, introducedIssues)}";
        return false;
    }

    internal static List<string> BuildDuplicateHotkeyConflicts(Settings settings) => [.. BuildDuplicateHotkeyLabels(settings)
        .Select(entry => $"{entry.Key} ({string.Join(", ", entry.Value)})")
        .Order(StringComparer.OrdinalIgnoreCase)];

    internal static Dictionary<string, List<string>> BuildDuplicateHotkeyLabels(Settings settings)
    {
        List<(string Label, string Hotkey)> assignments = [];

        void Add(string label, string? rawHotkey)
        {
            if (!TryNormalizeHotkey(rawHotkey, out string normalized) || string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            assignments.Add((label, normalized));
        }

        foreach (var (_, label, hotkey) in GetGlobalBindings(settings))
        {
            Add(label, hotkey);
        }

        List<(string Label, string Hotkey)> routineAssignments = [];
        foreach (AudioRoutine routine in RoutineHotkeyGroups.Representatives(settings.Routines.Items))
        {
            if (!TryNormalizeHotkey(routine.Hotkey, out string normalized) || string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(routine.Name)
                ? "Routine"
                : $"Routine: {routine.Name}";
            routineAssignments.Add((label, normalized));
        }

        var labelsByHotkey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < assignments.Count; index++)
        {
            var (label, hotkey) = assignments[index];
            if (!labelsByHotkey.TryGetValue(hotkey, out var labels))
            {
                labels = [];
                labelsByHotkey[hotkey] = labels;
            }

            labels.Add(label);
        }

        foreach (var (_, hotkey) in routineAssignments)
        {
            if (!labelsByHotkey.TryGetValue(hotkey, out var labels))
            {
                labels = [];
                labelsByHotkey[hotkey] = labels;
            }
        }

        var conflicts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in labelsByHotkey)
        {
            bool hasBuiltInConflict = entry.Value.Count > 1;
            int routineConflictCount = routineAssignments.Count(routine => string.Equals(routine.Hotkey, entry.Key, StringComparison.OrdinalIgnoreCase));
            bool hasRoutineConflict = routineConflictCount > 0;
            bool hasDuplicateRoutineConflict = routineConflictCount > 1;
            if (!hasBuiltInConflict && !hasDuplicateRoutineConflict && !(entry.Value.Count > 0 && hasRoutineConflict))
            {
                continue;
            }

            List<string> labels = [.. entry.Value];
            labels.AddRange(routineAssignments
                .Where(routine => string.Equals(routine.Hotkey, entry.Key, StringComparison.OrdinalIgnoreCase))
                .Select(static routine => routine.Label));

            if (labels.Count <= 1)
            {
                continue;
            }

            conflicts.Add(entry.Key, labels);
        }

        return conflicts;
    }

    internal static bool TryNormalizeHotkey(string? rawHotkey, out string normalized) =>
        HotkeySyntax.TryNormalize(rawHotkey, out normalized, canonicalModifierOrder: true);
}
