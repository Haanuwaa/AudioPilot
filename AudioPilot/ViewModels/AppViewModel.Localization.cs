using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private readonly LocalizationService? _localization;
    private string _settingsLanguageDraft = "System";
    private long _languageEditRevision;

    public LocalizationService Localization => _localization ?? LocalizationService.Default;

    public string SettingsLanguageDraft
    {
        get => _settingsLanguageDraft;
        set
        {
            string normalized = LocalizationService.NormalizeLanguage(value);
            if (_settingsLanguageDraft == normalized && Localization.Language == normalized) return;
            _settingsLanguageDraft = normalized;
            Interlocked.Increment(ref _languageEditRevision);
            Localization.SetLanguage(normalized);
            NotifySettingsErrorLanguageChanged();
            OnPropertyChanged(nameof(SettingsLanguageDraft));
        }
    }

    private void LoadLanguage(string? language)
    {
        string normalized = LocalizationService.NormalizeLanguage(language);
        if (_settingsLanguageDraft == normalized && Localization.Language == normalized) return;
        _settingsLanguageDraft = normalized;
        Localization.SetLanguage(_settingsLanguageDraft);
        NotifySettingsErrorLanguageChanged();
        OnPropertyChanged(nameof(SettingsLanguageDraft));
    }

    private void PublishCliLanguage(Settings settings, long editRevision)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => PublishCliLanguage(settings, editRevision));
            return;
        }

        bool preserveDraft = CurrentSettings is { } previous
            && SettingsLanguageDraft != LocalizationService.NormalizeLanguage(previous.App.Language);
        lock (_settingsLock)
        {
            _cachedSettings = settings;
        }

        UpdateLastSettingsWriteTime();
        if (!preserveDraft && Volatile.Read(ref _languageEditRevision) == editRevision && !_isCleaningUp && !IsRuntimeShutdownRequested)
        {
            using var suppression = SuppressAutoSave();
            LoadLanguage(settings.App.Language);
        }

        QueueSettingsFeedbackRefresh();
    }
}
