using System.Windows.Threading;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public enum TabSaveState { None, Unsaved, Saved, NotSaved }

[Flags]
internal enum SettingsFeedbackTabs { None = 0, Output = 1, Input = 2, Routines = 4, Settings = 8 }

public partial class AppViewModel
{
    private const string UnsavedSettingsFeedback = "Unsaved changes.";
    private static readonly TimeSpan SaveConfirmationDuration = TimeSpan.FromSeconds(4);
    private static readonly SettingsFeedbackTabs[] FeedbackTabs = [SettingsFeedbackTabs.Output, SettingsFeedbackTabs.Input, SettingsFeedbackTabs.Routines, SettingsFeedbackTabs.Settings];
    private readonly Dictionary<SettingsFeedbackTabs, string> _pendingFeedbackDetails = [];
    private readonly Dictionary<SettingsFeedbackTabs, (long SavedAt, string Message)> _saveConfirmations = [];
    private readonly Dictionary<SettingsFeedbackTabs, (string Message, Func<LocalizationService, string> Presentation, bool WasDirty, long Revision)> _saveProblems = [];
    private readonly TimeProvider _settingsFeedbackTimeProvider;
    private long? _settingsFeedbackTimerSavedAt;
    private SettingsFeedbackTabs _unsavedFeedbackTabs;
    private SettingsFeedbackTabs _savedFeedbackTabs;
    private SettingsFeedbackTabs _notSavedFeedbackTabs;
    private SettingsFeedbackTabs _retainedApplyFeedbackTabs;
    private SettingsFeedbackTabs _themeFeedbackTab = SettingsFeedbackTabs.Output;
    private long _themeEditRevision;
    public TabSaveState OutputTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Output);
    public TabSaveState InputTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Input);
    public TabSaveState RoutinesTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Routines);
    public TabSaveState SettingsTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Settings);
    public string OutputTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Output);
    public string InputTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Input);
    public string RoutinesTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Routines);
    public string SettingsTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Settings);
    public Func<LocalizationService, string> OutputTabFeedbackPresentation => language => PresentTabFeedback(SettingsFeedbackTabs.Output, language);
    public Func<LocalizationService, string> InputTabFeedbackPresentation => language => PresentTabFeedback(SettingsFeedbackTabs.Input, language);
    public Func<LocalizationService, string> RoutinesTabFeedbackPresentation => language => PresentTabFeedback(SettingsFeedbackTabs.Routines, language);
    public Func<LocalizationService, string> SettingsTabFeedbackPresentation => language => PresentTabFeedback(SettingsFeedbackTabs.Settings, language);
    private DispatcherOperation? _settingsFeedbackWork;
    private DispatcherTimer? _settingsFeedbackTimer;
    private bool HasSaveConfirmation => _savedFeedbackTabs != SettingsFeedbackTabs.None;

    private TabSaveState GetTabSaveState(SettingsFeedbackTabs tab) => (_notSavedFeedbackTabs & tab) != 0
        ? TabSaveState.NotSaved : (_unsavedFeedbackTabs & tab) != 0
        ? TabSaveState.Unsaved : (_savedFeedbackTabs & tab) != 0 ? TabSaveState.Saved : TabSaveState.None;

    private string GetTabFeedbackText(SettingsFeedbackTabs tab) => GetTabSaveState(tab) switch
    {
        TabSaveState.NotSaved => _saveProblems[tab].Message,
        TabSaveState.Saved => _saveConfirmations[tab].Message,
        TabSaveState.Unsaved => _pendingFeedbackDetails.GetValueOrDefault(tab, string.Empty),
        _ => string.Empty,
    };

    private string PresentTabFeedback(SettingsFeedbackTabs tab, LocalizationService language) =>
        GetTabSaveState(tab) == TabSaveState.NotSaved ? _saveProblems[tab].Presentation(language)
            : AppMessages.Translate(GetTabFeedbackText(tab), language);

    private SettingsFeedbackTabs GetUnsavedFeedbackTabs()
    {
        SettingsFeedbackTabs tabs = GetEditedDeviceSettingsTabs(includeStartup: false, includeTheme: false);
        Settings? baseline = GetCachedSettingsSnapshot();
        if (baseline != null && Theme != baseline.Theme) tabs |= _themeFeedbackTab;
        if (baseline == null || EnumerateSettingsDraftChanges(baseline, includeToolbarMirrors: false).Any()) tabs |= SettingsFeedbackTabs.Settings;
        if (HasRoutineEdits()) tabs |= SettingsFeedbackTabs.Routines;
        return tabs;
    }

    private void SetSettingsFeedback(SettingsFeedbackTabs pending, SettingsFeedbackTabs saved = SettingsFeedbackTabs.None, string? message = null, bool problemsChanged = false)
    {
        long now = _settingsFeedbackTimeProvider.GetTimestamp();
        bool messagesChanged = false;
        SettingsFeedbackTabs confirmed = SettingsFeedbackTabs.None;
        SettingsFeedbackTabs notSaved = SettingsFeedbackTabs.None;
        foreach (SettingsFeedbackTabs tab in FeedbackTabs)
        {
            if (_saveProblems.TryGetValue(tab, out var problem) && problem.WasDirty && (pending & tab) == 0)
                problemsChanged |= _saveProblems.Remove(tab);
            if (_saveProblems.ContainsKey(tab)) notSaved |= tab;
            if (_saveConfirmations.TryGetValue(tab, out var previous)
                && (((pending | notSaved) & tab) != 0 || _settingsFeedbackTimeProvider.GetElapsedTime(previous.SavedAt, now) >= SaveConfirmationDuration))
                messagesChanged |= _saveConfirmations.Remove(tab);
            if ((saved & tab) != 0 && ((pending | notSaved) & tab) == 0)
            {
                string text = message ?? string.Empty;
                messagesChanged |= !_saveConfirmations.TryGetValue(tab, out var current) || current.Message != text;
                _saveConfirmations[tab] = (now, text);
            }
            if (_saveConfirmations.ContainsKey(tab)) confirmed |= tab;
        }
        SettingsFeedbackTabs unsaved = pending;
        if (IsPersistedAutoSaveEnabled())
            unsaved = SettingsAutoSaveEnabledDraft ? SettingsFeedbackTabs.None : pending & SettingsFeedbackTabs.Settings;
        unsaved |= _unsavedFeedbackTabs & _retainedApplyFeedbackTabs & ~(confirmed | notSaved);
        bool detailsChanged = RefreshSettingsFeedbackDetails(unsaved);
        if (_unsavedFeedbackTabs != unsaved || _savedFeedbackTabs != confirmed || _notSavedFeedbackTabs != notSaved || detailsChanged || messagesChanged || problemsChanged)
        {
            _notSavedFeedbackTabs = notSaved;
            _unsavedFeedbackTabs = unsaved;
            _savedFeedbackTabs = confirmed;
            OnPropertyChanged(nameof(OutputTabSaveState));
            OnPropertyChanged(nameof(InputTabSaveState));
            OnPropertyChanged(nameof(RoutinesTabSaveState));
            OnPropertyChanged(nameof(SettingsTabSaveState));
            OnPropertyChanged(nameof(OutputTabFeedbackText));
            OnPropertyChanged(nameof(InputTabFeedbackText));
            OnPropertyChanged(nameof(RoutinesTabFeedbackText));
            OnPropertyChanged(nameof(SettingsTabFeedbackText));
            OnPropertyChanged(nameof(OutputTabFeedbackPresentation));
            OnPropertyChanged(nameof(InputTabFeedbackPresentation));
            OnPropertyChanged(nameof(RoutinesTabFeedbackPresentation));
            OnPropertyChanged(nameof(SettingsTabFeedbackPresentation));
        }
        UpdateSettingsFeedbackTimer(now);
    }

    private void UpdateSettingsFeedbackTimer(long now)
    {
        long? nextSavedAt = null;
        foreach (var (savedAt, _) in _saveConfirmations.Values)
            if (!nextSavedAt.HasValue || savedAt < nextSavedAt.Value) nextSavedAt = savedAt;
        if (nextSavedAt == null)
        {
            _settingsFeedbackTimer?.Stop();
            _settingsFeedbackTimerSavedAt = null;
            return;
        }
        if (nextSavedAt == _settingsFeedbackTimerSavedAt && _settingsFeedbackTimer is { IsEnabled: true }) return;
        if (_settingsFeedbackTimer == null)
        {
            _settingsFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _settingsFeedbackTimer.Tick += SaveConfirmationTimer_Tick;
        }
        _settingsFeedbackTimer.Stop();
        TimeSpan remaining = SaveConfirmationDuration - _settingsFeedbackTimeProvider.GetElapsedTime(nextSavedAt.Value, now);
        _settingsFeedbackTimer.Interval = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
        _settingsFeedbackTimerSavedAt = nextSavedAt;
        _settingsFeedbackTimer.Start();
    }

    private bool RefreshSettingsFeedbackDetails(SettingsFeedbackTabs unsaved)
    {
        Settings? baseline = unsaved == SettingsFeedbackTabs.None ? null : GetCachedSettingsSnapshot();
        bool changed = false;
        foreach (SettingsFeedbackTabs tab in FeedbackTabs)
        {
            string next = string.Empty;
            if ((unsaved & tab) != 0)
            {
                IEnumerable<string> changes = baseline == null ? [] : tab switch
                {
                    SettingsFeedbackTabs.Output => EnumerateDeviceSettingChanges(baseline, output: true, includeStartup: false, includeTheme: _themeFeedbackTab == tab),
                    SettingsFeedbackTabs.Input => EnumerateDeviceSettingChanges(baseline, output: false, includeStartup: false, includeTheme: _themeFeedbackTab == tab),
                    SettingsFeedbackTabs.Routines => EnumerateRoutineChanges(baseline),
                    _ => EnumerateSettingsDraftChanges(baseline, includeToolbarMirrors: false),
                };
                string action = tab switch
                {
                    SettingsFeedbackTabs.Settings => "Apply settings",
                    SettingsFeedbackTabs.Routines => "Save routines",
                    _ => "Save settings",
                };
                var labels = new List<string>(3);
                int count = 0;
                foreach (string label in changes)
                {
                    if (count++ < 3) labels.Add(label);
                }
                string summary = count == 0 ? UnsavedSettingsFeedback
                    : $"Unsaved: {string.Join(", ", labels)}{(count > 3 ? $", and {count - 3} more" : string.Empty)}.";
                next = $"{summary}\nClick {action} to save.";
            }
            if (_pendingFeedbackDetails.GetValueOrDefault(tab, string.Empty) == next) continue;
            if (next.Length == 0) _pendingFeedbackDetails.Remove(tab);
            else _pendingFeedbackDetails[tab] = next;
            changed = true;
        }
        return changed;
    }

    private void SaveConfirmationTimer_Tick(object? sender, EventArgs e)
    {
        _settingsFeedbackTimer?.Stop();
        _settingsFeedbackTimerSavedAt = null;
        if (!_isCleaningUp && HasSaveConfirmation) SetSettingsFeedback(GetUnsavedFeedbackTabs());
    }

    private void QueueSettingsFeedbackRefresh()
    {
        if (_isInitializing || _isCleaningUp || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) return;
        if (!_dispatcher.CheckAccess())
        {
            _ = InvokeOnDispatcherAsync(QueueSettingsFeedbackRefresh);
            return;
        }
        if (_settingsFeedbackWork is { Status: DispatcherOperationStatus.Pending }) return;
        _settingsFeedbackWork = _dispatcher.BeginInvoke(() =>
        {
            _settingsFeedbackWork = null;
            if (_isInitializing || _isCleaningUp) return;
            RefreshSettingsFieldValidation();
            if (IsPersistedAutoSaveEnabled() && SettingsAutoSaveEnabledDraft && !HasSaveConfirmation && _saveProblems.Count == 0)
            {
                if (_unsavedFeedbackTabs != SettingsFeedbackTabs.None) SetSettingsFeedback(SettingsFeedbackTabs.None);
                return;
            }
            SetSettingsFeedback(GetUnsavedFeedbackTabs());
        }, DispatcherPriority.Background);
    }

    private Task ReportSettingsSaveProblemAsync(SettingsFeedbackTabs scope, string reason, bool validation = true,
        Func<LocalizationService, string>? reasonPresentation = null)
        => InvokeOnDispatcherAsync(() =>
        {
            if (_isCleaningUp || IsRuntimeShutdownRequested) return;
            SettingsFeedbackTabs pending = GetUnsavedFeedbackTabs();
            SettingsFeedbackTabs affected = pending & scope;
            if (affected == SettingsFeedbackTabs.None)
                affected = scope == (SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input)
                    ? SelectedSettingsTabIndex == 1 ? SettingsFeedbackTabs.Input : SettingsFeedbackTabs.Output : scope;
            string detail = string.Join("\n", reason.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
            foreach (SettingsFeedbackTabs tab in FeedbackTabs)
            {
                if ((affected & tab) == 0) continue;
                string action = tab == SettingsFeedbackTabs.Settings ? "Apply settings" : tab == SettingsFeedbackTabs.Routines ? "Save routines" : "Save settings";
                string text = validation ? $"Changes weren't saved.\n{detail}\nFix the issue, then click {action}."
                    : $"Changes weren't saved. Your edits are still here.\nTry {action} again. If it continues, check folder access and free space.";
                var actionText = new LocalizedText(tab == SettingsFeedbackTabs.Settings ? "Common_Apply"
                    : tab == SettingsFeedbackTabs.Routines ? "Ui_SaveRoutines" : "Ui_SaveSettings");
                string Present(LocalizationService language) => validation
                    ? language["Message_ChangesWerenTSaved"] + "\n"
                        + string.Join("\n", (reasonPresentation?.Invoke(language) ?? AppMessages.Translate(reason, language))
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3))
                        + "\n" + language.FormatKey("Message_FixTheIssueThenClick0", actionText)
                    : language["Message_ChangesWerenTSavedYourEditsAre"] + "\n"
                        + language.FormatKey("Message_Try0AgainIfItContinuesCheck", actionText);
                _saveProblems[tab] = (text, Present, (pending & tab) != 0, Volatile.Read(ref _autoSaveDirtyRevision));
            }
            RefreshSettingsFieldValidation();
            SetSettingsFeedback(pending, problemsChanged: true);
        });

    private void ClearSettingsSaveProblems(SettingsFeedbackTabs scope, long revision)
    {
        bool changed = false;
        foreach (SettingsFeedbackTabs tab in FeedbackTabs)
            if ((scope & tab) != 0 && _saveProblems.TryGetValue(tab, out var problem) && problem.Revision <= revision)
                changed |= _saveProblems.Remove(tab);
        if (changed) SetSettingsFeedback(GetUnsavedFeedbackTabs(), problemsChanged: true);
    }

    private async Task<AppDialogResult> ShowSettingsSaveFeedbackAsync(string message, SettingsFeedbackTabs savedTabs)
    {
        try
        {
            await InvokeOnDispatcherAsync(() =>
            {
                if (_isCleaningUp) return;
                SetSettingsFeedback(GetUnsavedFeedbackTabs(), savedTabs, message);
            });
        }
        catch (OperationCanceledException) when (_isCleaningUp || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
        {
        }
        return AppDialogResult.Acknowledged;
    }
}
