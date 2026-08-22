using System.Collections;
using System.ComponentModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private static readonly Dictionary<string, string> SettingsFieldHelp = new(StringComparer.Ordinal)
    {
        [nameof(SettingsOverlayDurationSecondsDraft)] = "How long the overlay stays visible in seconds (0.5 to 10). Enable overlays to change this setting.",
        [nameof(SettingsSeekStepSecondsDraft)] = MediaSeekStep.InputHelp + " The player must support seeking.",
        [nameof(SettingsMasterVolumeStepPercentDraft)] = "Sets how much each master volume hotkey press changes the default output volume. Use 1 to 100.",
        [nameof(SettingsMicVolumeStepPercentDraft)] = "Sets how much each mic volume hotkey press changes the default microphone volume. Use 1 to 100.",
        [nameof(SettingsForegroundVolumeStepPercentDraft)] = "Changes each matching audio session by this many percentage points. Use 1 to 100. Changing volume leaves mute unchanged.",
    };
    private readonly Dictionary<string, string> _settingsFieldErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _settingsFieldGuidance = new(SettingsFieldHelp, StringComparer.Ordinal);

    public bool HasErrors => _settingsFieldErrors.Count != 0;
    public IReadOnlyDictionary<string, string> SettingsFieldGuidance => _settingsFieldGuidance;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName)
    {
        string[] errors = propertyName != null && _settingsFieldErrors.TryGetValue(propertyName, out string? error) ? [error] : [];
        return errors;
    }

    private void RefreshSettingsFieldValidation()
    {
        bool hadErrors = HasErrors;
        bool changed = false;
        Set(nameof(SettingsOverlayDurationSecondsDraft), TryGetSettingsOverlayDurationSeconds(out _), "Enter a duration from 0.5 to 10 seconds.");
        Set(nameof(SettingsSeekStepSecondsDraft), MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out _), MediaSeekStep.InputHelp);
        Set(nameof(SettingsForegroundVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsForegroundVolumeStepPercentDraft, out _), "Enter a whole percentage from 1 to 100.");
        Set(nameof(SettingsMasterVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsMasterVolumeStepPercentDraft, out _)
            || (!HasConfiguredHotkeyDraft(SettingsMasterVolumeUpHotkeyDraft) && !HasConfiguredHotkeyDraft(SettingsMasterVolumeDownHotkeyDraft)), "Enter a whole percentage from 1 to 100.");
        Set(nameof(SettingsMicVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsMicVolumeStepPercentDraft, out _)
            || (!HasConfiguredHotkeyDraft(SettingsMicVolumeUpHotkeyDraft) && !HasConfiguredHotkeyDraft(SettingsMicVolumeDownHotkeyDraft)), "Enter a whole percentage from 1 to 100.");
        if (changed) OnPropertyChanged(nameof(SettingsFieldGuidance));
        if (hadErrors != HasErrors) OnPropertyChanged(nameof(HasErrors));

        void Set(string name, bool valid, string message)
        {
            string? next = valid ? null : message;
            if (_settingsFieldErrors.GetValueOrDefault(name) == next) return;
            if (next == null) _settingsFieldErrors.Remove(name);
            else _settingsFieldErrors[name] = next;
            _settingsFieldGuidance[name] = next ?? SettingsFieldHelp[name];
            changed = true;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
        }
    }
}
