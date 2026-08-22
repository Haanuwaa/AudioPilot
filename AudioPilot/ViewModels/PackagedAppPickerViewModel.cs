using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPilot.ViewModels
{
    internal sealed class PackagedAppPickerViewModel : INotifyPropertyChanged
    {
        private readonly List<AudioDeviceHelper.PackagedAppIdentity> _allApps = [];
        private string _searchText = string.Empty;
        private AudioDeviceHelper.PackagedAppIdentity? _selectedApp;
        private string _confirmedAppUserModelId = string.Empty;
        private string _confirmedDisplayName = string.Empty;

        public PackagedAppPickerViewModel(IEnumerable<AudioDeviceHelper.PackagedAppIdentity> apps)
        {
            ReplaceApps(apps, selectFirstFilteredApp: false);
        }

        public ObservableCollection<AudioDeviceHelper.PackagedAppIdentity> FilteredApps { get; } = [];

        public string SearchText
        {
            get => _searchText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_searchText == normalized)
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged();
                ApplyFilter(selectFirstFilteredApp: false);
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

        public bool CanConfirmSelection => SelectedApp.HasValue;

        public bool HasFilteredApps => FilteredApps.Count > 0;

        public bool HasNoFilteredApps => FilteredApps.Count == 0;

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
            ReplaceApps(apps, selectFirstFilteredApp: false);
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

        private void ReplaceApps(IEnumerable<AudioDeviceHelper.PackagedAppIdentity> apps, bool selectFirstFilteredApp)
        {
            string preferredSelectedAppUserModelId = SelectedAppUserModelId;

            _allApps.Clear();
            _allApps.AddRange(apps);

            ApplyFilter(preferredSelectedAppUserModelId, selectFirstFilteredApp);
        }

        private void ApplyFilter(bool selectFirstFilteredApp)
        {
            ApplyFilter(SelectedAppUserModelId, selectFirstFilteredApp);
        }

        private void ApplyFilter(string? preferredSelectedAppUserModelId, bool selectFirstFilteredApp)
        {
            string normalizedSearch = SearchText;
            IEnumerable<AudioDeviceHelper.PackagedAppIdentity> nextItems = string.IsNullOrWhiteSpace(normalizedSearch)
                ? _allApps
                : _allApps.Where(app =>
                    app.DisplayName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                    app.AppUserModelId.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));

            FilteredApps.Clear();
            foreach (AudioDeviceHelper.PackagedAppIdentity app in nextItems)
            {
                FilteredApps.Add(app);
            }

            OnPropertyChanged(nameof(HasFilteredApps));
            OnPropertyChanged(nameof(HasNoFilteredApps));
            OnPropertyChanged(nameof(FilteredAppCountText));

            AudioDeviceHelper.PackagedAppIdentity? nextSelection = FindFilteredApp(preferredSelectedAppUserModelId);
            if (!nextSelection.HasValue && selectFirstFilteredApp)
            {
                nextSelection = FilteredApps.FirstOrDefault();
            }

            SelectedApp = nextSelection;
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
