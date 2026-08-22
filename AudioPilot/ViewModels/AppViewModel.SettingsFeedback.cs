using System.Windows.Threading;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public enum TabSaveState { None, Unsaved, Saved }

[Flags]
internal enum SettingsFeedbackTabs { None = 0, Output = 1, Input = 2, Routines = 4, Settings = 8 }

public partial class AppViewModel
{
    private const string UnsavedSettingsFeedback = "Unsaved changes.";
    private static readonly SettingsFeedbackTabs[] FeedbackTabs = [SettingsFeedbackTabs.Output, SettingsFeedbackTabs.Input, SettingsFeedbackTabs.Routines, SettingsFeedbackTabs.Settings];
    private readonly Dictionary<SettingsFeedbackTabs, string> _pendingFeedbackDetails = [];
    private string _settingsFeedbackText = string.Empty;
    private SettingsFeedbackTabs _unsavedFeedbackTabs;
    private SettingsFeedbackTabs _savedFeedbackTabs;
    public TabSaveState OutputTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Output);
    public TabSaveState InputTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Input);
    public TabSaveState RoutinesTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Routines);
    public TabSaveState SettingsTabSaveState => GetTabSaveState(SettingsFeedbackTabs.Settings);
    public string OutputTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Output);
    public string InputTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Input);
    public string RoutinesTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Routines);
    public string SettingsTabFeedbackText => GetTabFeedbackText(SettingsFeedbackTabs.Settings);
    private DispatcherOperation? _settingsFeedbackWork;
    private DispatcherTimer? _settingsFeedbackTimer;
    private bool HasSaveConfirmation => _savedFeedbackTabs != SettingsFeedbackTabs.None;

    private TabSaveState GetTabSaveState(SettingsFeedbackTabs tab) => (_unsavedFeedbackTabs & tab) != 0
        ? TabSaveState.Unsaved : (_savedFeedbackTabs & tab) != 0 ? TabSaveState.Saved : TabSaveState.None;

    private string GetTabFeedbackText(SettingsFeedbackTabs tab) => GetTabSaveState(tab) switch
    {
        TabSaveState.Saved => _settingsFeedbackText,
        TabSaveState.Unsaved => _pendingFeedbackDetails.GetValueOrDefault(tab, string.Empty),
        _ => string.Empty,
    };

    private SettingsFeedbackTabs GetUnsavedFeedbackTabs()
    {
        SettingsFeedbackTabs tabs = GetEditedDeviceSettingsTabs(includeStartup: false);
        Settings? baseline = GetCachedSettingsSnapshot();
        if (baseline == null || EnumerateSettingsDraftChanges(baseline, includeToolbarMirrors: false).Any()) tabs |= SettingsFeedbackTabs.Settings;
        if (HasRoutineEdits()) tabs |= SettingsFeedbackTabs.Routines;
        return tabs;
    }

    private void SetSettingsFeedback(string text, SettingsFeedbackTabs unsaved = SettingsFeedbackTabs.None, SettingsFeedbackTabs saved = SettingsFeedbackTabs.None)
    {
        _settingsFeedbackTimer?.Stop();
        bool detailsChanged = RefreshSettingsFeedbackDetails(unsaved);
        if (!string.Equals(_settingsFeedbackText, text, StringComparison.Ordinal) || _unsavedFeedbackTabs != unsaved || _savedFeedbackTabs != saved || detailsChanged)
        {
            _settingsFeedbackText = text;
            _unsavedFeedbackTabs = unsaved;
            _savedFeedbackTabs = saved;
            OnPropertyChanged(nameof(OutputTabSaveState));
            OnPropertyChanged(nameof(InputTabSaveState));
            OnPropertyChanged(nameof(RoutinesTabSaveState));
            OnPropertyChanged(nameof(SettingsTabSaveState));
            OnPropertyChanged(nameof(OutputTabFeedbackText));
            OnPropertyChanged(nameof(InputTabFeedbackText));
            OnPropertyChanged(nameof(RoutinesTabFeedbackText));
            OnPropertyChanged(nameof(SettingsTabFeedbackText));
        }
        if (!HasSaveConfirmation) return;
        if (_settingsFeedbackTimer == null)
        {
            _settingsFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(4) };
            _settingsFeedbackTimer.Tick += SaveConfirmationTimer_Tick;
        }
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
                    SettingsFeedbackTabs.Output => EnumerateDeviceSettingChanges(baseline, output: true, includeStartup: false),
                    SettingsFeedbackTabs.Input => EnumerateDeviceSettingChanges(baseline, output: false, includeStartup: false),
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
        if (!_isCleaningUp && HasSaveConfirmation) SetSettingsFeedback(string.Empty);
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
            if (IsPersistedAutoSaveEnabled())
            {
                if (!HasSaveConfirmation || GetUnsavedFeedbackTabs() != SettingsFeedbackTabs.None) SetSettingsFeedback(string.Empty);
                return;
            }
            SettingsFeedbackTabs unsaved = GetUnsavedFeedbackTabs();
            if (unsaved != SettingsFeedbackTabs.None) SetSettingsFeedback(UnsavedSettingsFeedback, unsaved: unsaved);
            else if (_settingsFeedbackText == UnsavedSettingsFeedback) SetSettingsFeedback(string.Empty);
        }, DispatcherPriority.Background);
    }

    private async Task<AppDialogResult> ShowSettingsSaveFeedbackAsync(string message, SettingsFeedbackTabs savedTabs)
    {
        try
        {
            await InvokeOnDispatcherAsync(() =>
            {
                if (_isCleaningUp) return;
                SettingsFeedbackTabs unsaved = GetUnsavedFeedbackTabs();
                if (unsaved != SettingsFeedbackTabs.None)
                {
                    if (IsPersistedAutoSaveEnabled()) SetSettingsFeedback(string.Empty);
                    else SetSettingsFeedback(UnsavedSettingsFeedback, unsaved: unsaved);
                }
                else SetSettingsFeedback(message, saved: savedTabs);
            });
        }
        catch (OperationCanceledException) when (_isCleaningUp || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
        {
        }
        return AppDialogResult.Acknowledged;
    }
}
