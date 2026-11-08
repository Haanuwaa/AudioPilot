using System.Collections;
using System.ComponentModel;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private static readonly Dictionary<string, LocalizedText> SettingsFieldHelp = new(StringComparer.Ordinal)
    {
        [nameof(SettingsOverlayDurationSecondsDraft)] = new("Message_OverlayDurationHelp"),
        [nameof(SettingsSeekStepSecondsDraft)] = new("Message_SeekStepHelp"),
        [nameof(SettingsMasterVolumeStepPercentDraft)] = new("Message_MasterVolumeStepHelp"),
        [nameof(SettingsMicVolumeStepPercentDraft)] = new("Message_MicVolumeStepHelp"),
        [nameof(SettingsForegroundVolumeStepPercentDraft)] = new("Message_AppVolumeStepHelp"),
    };
    private readonly Dictionary<string, string> _settingsFieldErrors = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, LocalizedText> _settingsFieldGuidance = new(SettingsFieldHelp, StringComparer.Ordinal);

    public bool HasErrors => _settingsFieldErrors.Count != 0;
    public IReadOnlyDictionary<string, LocalizedText> SettingsFieldGuidance => _settingsFieldGuidance;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName)
    {
        string[] errors = propertyName != null && _settingsFieldErrors.ContainsKey(propertyName)
            ? [Localization.Format(_settingsFieldGuidance[propertyName])] : [];
        return errors;
    }

    private void NotifySettingsErrorLanguageChanged()
    {
        foreach (string name in _settingsFieldErrors.Keys.ToArray())
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
    }

    private void RefreshSettingsFieldValidation()
    {
        bool hadErrors = HasErrors;
        bool changed = false;
        Set(nameof(SettingsOverlayDurationSecondsDraft), TryGetSettingsOverlayDurationSeconds(out _), new("Message_OverlayDurationRange"));
        Set(nameof(SettingsSeekStepSecondsDraft), MediaSeekStep.TryParse(SettingsSeekStepSecondsDraft, out _), new("Validation_SeekStep"));
        Set(nameof(SettingsForegroundVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsForegroundVolumeStepPercentDraft, out _), new("Message_WholePercentRange"));
        Set(nameof(SettingsMasterVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsMasterVolumeStepPercentDraft, out _)
            || (!HasConfiguredHotkeyDraft(SettingsMasterVolumeUpHotkeyDraft) && !HasConfiguredHotkeyDraft(SettingsMasterVolumeDownHotkeyDraft)), new("Message_WholePercentRange"));
        Set(nameof(SettingsMicVolumeStepPercentDraft), TryGetVolumeStepPercent(SettingsMicVolumeStepPercentDraft, out _)
            || (!HasConfiguredHotkeyDraft(SettingsMicVolumeUpHotkeyDraft) && !HasConfiguredHotkeyDraft(SettingsMicVolumeDownHotkeyDraft)), new("Message_WholePercentRange"));
        if (changed) OnPropertyChanged(nameof(SettingsFieldGuidance));
        if (hadErrors != HasErrors) OnPropertyChanged(nameof(HasErrors));

        void Set(string name, bool valid, LocalizedText message)
        {
            string? next = valid ? null : LocalizationService.English.Format(message);
            if (_settingsFieldErrors.GetValueOrDefault(name) == next) return;
            if (next == null) _settingsFieldErrors.Remove(name);
            else _settingsFieldErrors[name] = next;
            _settingsFieldGuidance[name] = valid ? SettingsFieldHelp[name] : message;
            changed = true;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
        }
    }
}
