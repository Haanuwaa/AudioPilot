using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Localization;

namespace AudioPilot.ViewModels;

internal sealed class RunningAppPickerViewModel : INotifyPropertyChanged
{
    private List<RunningAppIdentity> _allApps = [];
    private string _searchText = string.Empty;
    private RunningAppIdentity? _selectedApp;
    private string _confirmedTargetPath = string.Empty;
    private string _confirmedDisplayName = string.Empty;
    private bool _confirmedIsPackagedApp;

    public RunningAppPickerViewModel(IEnumerable<RunningAppIdentity> apps)
    {
        ReplaceApps(apps);
    }

    public IReadOnlyList<RunningAppIdentity> FilteredApps { get; private set; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            string text = value ?? string.Empty;
            if (_searchText == text)
            {
                return;
            }

            string previousQuery = _searchText.Trim();
            _searchText = text;
            OnPropertyChanged();
            if (!string.Equals(previousQuery, text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                ApplyFilter(SelectedTargetPath);
            }
        }
    }

    public RunningAppIdentity? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (_selectedApp == value)
            {
                return;
            }

            _selectedApp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmSelection));
        }
    }

    public bool CanConfirmSelection => SelectedApp is { } app &&
        !string.IsNullOrWhiteSpace(app.TargetPath) && FilteredApps.Contains(app);

    public bool HasNoFilteredApps => FilteredApps.Count == 0;

    public LocalizedText EmptyStateText => _allApps.Count == 0
        ? new("Routine_NoRunningAppsDetected")
        : new("Routine_NoRunningAppsMatchSearch");

    public LocalizedText FilteredAppCountText => FilteredApps.Count == 1
        ? new("Routine_OneRunningApp")
        : new("Routine_ManyRunningApps", FilteredApps.Count);

    public string SelectedTargetPath => SelectedApp?.TargetPath ?? string.Empty;
    internal string ConfirmedTargetPath => _confirmedTargetPath;
    internal string ConfirmedDisplayName => _confirmedDisplayName;
    internal bool ConfirmedIsPackagedApp => _confirmedIsPackagedApp;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ReplaceApps(IEnumerable<RunningAppIdentity> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        string preferredTargetPath = SelectedTargetPath;
        _allApps = [.. apps.Where(static app => !string.IsNullOrWhiteSpace(app.TargetPath))];
        ApplyFilter(preferredTargetPath);
    }

    internal void TrySelectTargetPath(string? targetPath)
    {
        RunningAppIdentity? match = FindFilteredApp(targetPath);
        if (match.HasValue)
        {
            SelectedApp = match.Value;
        }
    }

    internal bool ConfirmSelection()
    {
        if (!CanConfirmSelection || !SelectedApp.HasValue)
        {
            return false;
        }

        _confirmedTargetPath = SelectedApp.Value.TargetPath;
        _confirmedDisplayName = SelectedApp.Value.DisplayName;
        _confirmedIsPackagedApp = SelectedApp.Value.IsPackagedApp;
        return true;
    }

    private void ApplyFilter(string? preferredTargetPath)
    {
        int previousCount = FilteredApps.Count;
        string normalizedSearch = SearchText.Trim();
        List<RunningAppIdentity> nextItems = normalizedSearch.Length == 0
            ? _allApps
            : [.. _allApps.Where(app =>
                app.DisplayName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                app.TargetPath.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(app.WindowTitle) && app.WindowTitle.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)))];

        if (!FilteredApps.SequenceEqual(nextItems))
        {
            FilteredApps = nextItems;
            OnPropertyChanged(nameof(FilteredApps));
        }

        if (previousCount != FilteredApps.Count)
        {
            OnPropertyChanged(nameof(HasNoFilteredApps));
            OnPropertyChanged(nameof(FilteredAppCountText));
        }

        OnPropertyChanged(nameof(EmptyStateText));
        SelectedApp = FindFilteredApp(preferredTargetPath);
        OnPropertyChanged(nameof(CanConfirmSelection));
    }

    private RunningAppIdentity? FindFilteredApp(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        string normalized = RoutineTriggerPathHelper.NormalizeTriggerTarget(targetPath);
        foreach (RunningAppIdentity app in FilteredApps)
        {
            if (string.Equals(app.TargetPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return app;
            }
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
