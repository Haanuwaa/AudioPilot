using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPilot.ViewModels
{
    internal sealed class PackagedAppPickerViewModel : INotifyPropertyChanged
    {
        private List<AudioDeviceHelper.PackagedAppIdentity> _allApps = [];
        private string _searchText = string.Empty;
        private AudioDeviceHelper.PackagedAppIdentity? _selectedApp;
        private string _confirmedAppUserModelId = string.Empty;
        private string _confirmedDisplayName = string.Empty;

        public PackagedAppPickerViewModel(IEnumerable<AudioDeviceHelper.PackagedAppIdentity> apps)
        {
            ReplaceApps(apps);
        }

        public ObservableCollection<AudioDeviceHelper.PackagedAppIdentity> FilteredApps { get; } = [];

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
                    ApplyFilter(SelectedAppUserModelId);
                }
            }
        }

        public AudioDeviceHelper.PackagedAppIdentity? SelectedApp
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
            !string.IsNullOrWhiteSpace(app.AppUserModelId) && FilteredApps.Contains(app);

        public bool HasNoFilteredApps => FilteredApps.Count == 0;

        public string EmptyStateText => _allApps.Count == 0
            ? "No packaged apps were detected. Try Refresh after Store updates finish, or enter the app ID in the routine editor."
            : "No apps match your search. Try a different name or app ID.";

        public string FilteredAppCountText
        {
            get
            {
                int count = FilteredApps.Count;
                return count == 1 ? "1 app" : $"{count} apps";
            }
        }

        public string SelectedAppUserModelId => SelectedApp?.AppUserModelId ?? string.Empty;

        internal string ConfirmedAppUserModelId => _confirmedAppUserModelId;

        internal string ConfirmedDisplayName => _confirmedDisplayName;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void ReplaceApps(IEnumerable<AudioDeviceHelper.PackagedAppIdentity> apps)
        {
            ArgumentNullException.ThrowIfNull(apps);
            string preferredSelectedAppUserModelId = SelectedAppUserModelId;
            _allApps = [.. apps.Where(static app => !string.IsNullOrWhiteSpace(app.AppUserModelId))];
            ApplyFilter(preferredSelectedAppUserModelId);
        }

        internal void TrySelectAppUserModelId(string? appUserModelId)
        {
            AudioDeviceHelper.PackagedAppIdentity? match = FindFilteredApp(appUserModelId);
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

            _confirmedAppUserModelId = SelectedApp.Value.AppUserModelId;
            _confirmedDisplayName = SelectedApp.Value.DisplayName;
            return true;
        }

        private void ApplyFilter(string? preferredSelectedAppUserModelId)
        {
            int previousCount = FilteredApps.Count;
            string normalizedSearch = SearchText.Trim();
            List<AudioDeviceHelper.PackagedAppIdentity> nextItems = normalizedSearch.Length == 0
                ? _allApps
                : [.. _allApps.Where(app =>
                    (app.DisplayName ?? string.Empty).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                    app.AppUserModelId.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))];

            if (nextItems.Count == 0 && FilteredApps.Count > 0)
            {
                FilteredApps.Clear();
            }
            int sharedCount = Math.Min(FilteredApps.Count, nextItems.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                if (FilteredApps[index] != nextItems[index])
                {
                    FilteredApps[index] = nextItems[index];
                }
            }
            while (FilteredApps.Count > nextItems.Count)
            {
                FilteredApps.RemoveAt(FilteredApps.Count - 1);
            }
            for (int index = sharedCount; index < nextItems.Count; index++)
            {
                FilteredApps.Add(nextItems[index]);
            }

            if (previousCount != FilteredApps.Count)
            {
                OnPropertyChanged(nameof(HasNoFilteredApps));
                OnPropertyChanged(nameof(FilteredAppCountText));
            }
            OnPropertyChanged(nameof(EmptyStateText));
            SelectedApp = FindFilteredApp(preferredSelectedAppUserModelId);
            OnPropertyChanged(nameof(CanConfirmSelection));
        }

        private AudioDeviceHelper.PackagedAppIdentity? FindFilteredApp(string? appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return null;
            }

            foreach (AudioDeviceHelper.PackagedAppIdentity app in FilteredApps)
            {
                if (string.Equals(app.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
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
}
