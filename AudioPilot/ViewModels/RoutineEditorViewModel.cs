using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal enum RoutineEditorTriggerMode
    {
        Hotkey,
        Application,
        AudioPilotStartup,
        DeviceChange,
        Scheduled,
        Network,
        SteamBigPicture,
    }

    internal sealed partial class RoutineEditorViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly IReadOnlyList<string> TriggerModeLabels = ["Manual", "Application", "AudioPilot startup", "Device change", "Scheduled", "Network", "Steam Big Picture", "Device availability", "Windows unlock", "System resume"];

        private readonly bool _enabled;
        private readonly string _existingId;
        private readonly int _existingDisplayOrder;
        private readonly bool _isEditingExistingRoutine;
        private static readonly IReadOnlyList<string> AutomaticTriggerModeLabels = [.. TriggerModeLabels.Skip(1)];
        private readonly HashSet<string> _reservedHotkeyKeys;
        private readonly IReadOnlyList<string> _minuteOptions;
        private IReadOnlyList<string> _hourOptions = [];
        private IReadOnlyList<string> _amPmOptions = [];
        private readonly IReadOnlyList<string> _applicationTriggerModeOptions = ApplicationTriggerModeLabels;
        private readonly IReadOnlyList<string> _applicationTriggerTitleMatchModeOptions = ApplicationTriggerTitleMatchModeLabels;
        private bool _is24HourFormat;
        private readonly bool _usesSystemTimeFormat;
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _loadAvailableNetworkNamesAsync;

        public IReadOnlyList<string> MinuteOptions => _minuteOptions;

        public bool Is24HourFormat => _is24HourFormat;

        public IReadOnlyList<string> HourOptions => _hourOptions;

        public IReadOnlyList<string> AmPmOptions => _amPmOptions;

        private string _name = string.Empty;
        private int _selectedOutputIndex = -1;
        private int _selectedInputIndex = -1;
        private string _selectedTriggerMode = TriggerModeLabels[0];
        private string _triggerAppPath = string.Empty;
        private bool _switchOutputPerApp;
        private bool _showInTrayMenu;
        private bool _restorePreviousAudioOnDeactivate;
        private string _masterVolumePercentText = string.Empty;
        private string _micVolumePercentText = string.Empty;
        private bool _isVolumeTargetsExpanded;
        private RoutineMuteAction _outputMuteAction;
        private RoutineMuteAction _inputMuteAction;
        private string _resolvedPackagedAppDisplayName = string.Empty;
        private TimeOnly _scheduleTime = new(12, 0);
        private HashSet<DayOfWeek> _scheduleDays = [];
        private string _triggerNetworkName = string.Empty;
        private string? _selectedAvailableNetworkName;
        private NetworkTriggerDirection _networkTriggerDirection = NetworkTriggerDirection.Connect;
        private readonly ObservableCollection<string> _availableNetworkNames = [];
        private bool _isScanningNetworks;
        private bool _networksLoaded;
        private bool _updatingNetworkChoices;
        private bool _pendingNetworkRefresh;
        private bool _pendingNetworkForceRefresh;
        private int _networkRefreshVersion;
        private CancellationTokenSource? _networkRefreshCts;
        private int _disposed;
        private int _scheduleHour = 12;
        private int _scheduleMinute = 0;
        private bool _notifyBeforeScheduledRun;
        private string _scheduleAmPm = "PM";
        private ApplicationTriggerMode _applicationTriggerMode = ApplicationTriggerMode.AppLaunch;
        private string _applicationTriggerTitlePattern = string.Empty;
        private ApplicationTriggerTitleMatchMode _applicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
        private static readonly List<string> ApplicationTriggerModeLabels = ["When application launches", "When application window is focused"];
        private static readonly List<string> ApplicationTriggerTitleMatchModeLabels = ["Exact match (e.g., 'My App Window')", "Contains (e.g., 'Chrome')", "Wildcard (e.g., '* - Google Chrome')", "Regex (e.g., '.*Chrome.*')"];

        public ObservableCollection<string> AvailableNetworkNames => _availableNetworkNames;

        public bool IsScanningNetworks
        {
            get => _isScanningNetworks;
            set
            {
                if (_isScanningNetworks != value)
                {
                    _isScanningNetworks = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand RefreshNetworksCommand { get; }

        private readonly Logger _logger = Logger.Instance;
        private string _scheduleTimeZoneId;

        public async Task RefreshNetworksAsync(bool forceRefresh = false)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_isScanningNetworks)
            {
                _pendingNetworkRefresh = true;
                _pendingNetworkForceRefresh |= forceRefresh;
                _networkRefreshCts?.Cancel();
                return;
            }

            if (_networksLoaded && !forceRefresh)
            {
                return;
            }

            CancellationTokenSource refreshCts = new();
            _networkRefreshCts = refreshCts;
            int refreshVersion = Interlocked.Increment(ref _networkRefreshVersion);
            IsScanningNetworks = true;

            try
            {
                IReadOnlyList<string> orderedNetworks = await _loadAvailableNetworkNamesAsync(refreshCts.Token).ConfigureAwait(true);
                if (refreshCts.IsCancellationRequested || refreshVersion != Volatile.Read(ref _networkRefreshVersion))
                {
                    return;
                }

                _updatingNetworkChoices = true;
                try
                {
                    ReplaceStringCollection(_availableNetworkNames, orderedNetworks);
                    SyncSelectedNetworkFromTriggerName();
                    SyncSelectedRequiredNetworkName();
                    OnPropertyChanged(nameof(TriggerNetworkName));
                    OnPropertyChanged(nameof(RequiredNetworkName));
                }
                finally { _updatingNetworkChoices = false; }
                _networksLoaded = true;
            }
            catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("RoutineEditorViewModel", () => $"network-list-refresh-failed | reason={ex.GetType().Name}", nameof(RefreshNetworksAsync), ex);
            }
            finally
            {
                if (ReferenceEquals(_networkRefreshCts, refreshCts))
                {
                    _networkRefreshCts = null;
                }

                refreshCts.Dispose();
                IsScanningNetworks = false;

                if (_pendingNetworkRefresh && Volatile.Read(ref _disposed) == 0)
                {
                    bool rerunForceRefresh = _pendingNetworkForceRefresh;
                    _pendingNetworkRefresh = false;
                    _pendingNetworkForceRefresh = false;
                    await Task.Yield();
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _ = RefreshNetworksAsync(forceRefresh: rerunForceRefresh);
                    }
                }
            }
        }

        public void RefreshNetworks()
        {
            _ = RefreshNetworksAsync(forceRefresh: true);
        }

        private async Task<IReadOnlyList<string>> LoadAvailableNetworkNamesAsync(CancellationToken cancellationToken)
        {
            HashSet<string> wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(_logger, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<string> allNetworks = [.. wifiNetworks, .. nlmNetworks];
            return [.. allNetworks.OrderBy(static s => s)];
        }

        private void EnsureNetworkListForCurrentSelection()
        {
            if (!IsNetworkTriggerSelected)
            {
                return;
            }

            _ = RefreshNetworksAsync(forceRefresh: ShouldForceRefreshNetworksForCurrentSelection());
        }

        private bool ShouldForceRefreshNetworksForCurrentSelection()
        {
            if (!_networksLoaded)
            {
                return false;
            }

            string networkName = TriggerNetworkName;
            if (string.IsNullOrWhiteSpace(networkName))
            {
                return false;
            }

            return !_availableNetworkNames.Any(existing =>
                string.Equals(existing, networkName, StringComparison.OrdinalIgnoreCase));
        }

        private static void ReplaceStringCollection(ObservableCollection<string> target, IReadOnlyList<string> source)
        {
            int sharedCount = Math.Min(target.Count, source.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                if (!string.Equals(target[index], source[index], StringComparison.Ordinal))
                {
                    target[index] = source[index];
                }
            }

            while (target.Count > source.Count)
            {
                target.RemoveAt(target.Count - 1);
            }

            for (int index = sharedCount; index < source.Count; index++)
            {
                target.Add(source[index]);
            }
        }

        private void SyncSelectedNetworkFromTriggerName()
        {
            string? matchedNetwork = _availableNetworkNames.FirstOrDefault(existing =>
                string.Equals(existing, _triggerNetworkName, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(_selectedAvailableNetworkName, matchedNetwork, StringComparison.Ordinal))
            {
                return;
            }

            _selectedAvailableNetworkName = matchedNetwork;
            OnPropertyChanged(nameof(SelectedAvailableNetworkName));
        }

        private void OnSourceOutputDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshRoutineDeviceChoices();
            RefreshCommunicationsDevices(true);
            _selectedOutputIndex = SyncDeviceCollection(OutputDevices, _sourceOutputDevices, "Leave unchanged", _selectedOutputIndex);
            OnPropertyChanged(nameof(SelectedOutputIndex));
            OnPropertyChanged(nameof(HasAudioTargetSelected));
            OnPropertyChanged(nameof(IsSelectedOutputUnavailable));
        }

        private void OnSourceInputDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshRoutineDeviceChoices();
            RefreshCommunicationsDevices(false);
            _selectedInputIndex = SyncDeviceCollection(InputDevices, _sourceInputDevices, "Leave unchanged", _selectedInputIndex);
            OnPropertyChanged(nameof(SelectedInputIndex));
            OnPropertyChanged(nameof(HasAudioTargetSelected));
            OnPropertyChanged(nameof(IsSelectedInputUnavailable));
        }

        private static int SyncDeviceCollection(ObservableCollection<CycleDevice> target, ObservableCollection<CycleDevice> source, string placeholderName, int selectedIndex)
        {
            CycleDevice? selectedDevice = selectedIndex > 0 && selectedIndex < target.Count ? target[selectedIndex] : null;

            target.Clear();
            target.Add(new CycleDevice { Id = string.Empty, Name = placeholderName });
            foreach (CycleDevice device in source)
            {
                target.Add(new CycleDevice { Id = device.Id, Name = device.Name, StableId = device.StableId });
            }

            return ResolveSelectedIndex(target, selectedDevice?.Id, selectedDevice?.Name, selectedDevice?.StableId);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            EditorHotkey.PropertyChanged -= OnSummaryHotkeyChanged;
            _pendingNetworkRefresh = false;
            _pendingNetworkForceRefresh = false;
            CancellationTokenSource? activeRefresh = Interlocked.Exchange(ref _networkRefreshCts, null);
            activeRefresh?.Cancel();
            _sourceOutputDevices.CollectionChanged -= OnSourceOutputDevicesChanged;
            _sourceInputDevices.CollectionChanged -= OnSourceInputDevicesChanged;
        }

        public RoutineEditorViewModel(
            ObservableCollection<CycleDevice> outputDevices,
            ObservableCollection<CycleDevice> inputDevices,
            AudioRoutine? existingRoutine = null,
            string? suggestedName = null,
            IEnumerable<string>? reservedHotkeyKeys = null,
            string? scheduleTimeZoneId = null,
            bool preloadNetworks = false,
            Func<CancellationToken, Task<IReadOnlyList<string>>>? loadAvailableNetworkNamesAsync = null,
            CultureInfo? displayCulture = null,
            IEnumerable<AudioRoutine>? otherRoutines = null)
        {
            CultureInfo timeCulture = displayCulture ?? CultureInfo.CurrentCulture;
            DateTimeFormatInfo dateTimeFormat = timeCulture.DateTimeFormat;
            _usesSystemTimeFormat = displayCulture == null;
            string shortTimePattern = displayCulture == null
                ? WindowsTimeFormatPreference.GetCurrentTaskbarTimePattern(
                    dateTimeFormat.ShortTimePattern,
                    dateTimeFormat.LongTimePattern)
                : dateTimeFormat.ShortTimePattern;
            ApplyTimeFormat(timeCulture, shortTimePattern);

            _minuteOptions = [.. Enumerable.Range(0, 60).Select(minute => minute.ToString("D2", timeCulture))];
            _sourceOutputDevices = outputDevices;
            _sourceInputDevices = inputDevices;
            _scheduleTimeZoneId = existingRoutine?.TriggerKind == RoutineTriggerKind.Scheduled
                ? existingRoutine.ScheduleTimeZoneId
                : scheduleTimeZoneId ?? TimeZoneInfo.Local.Id;
            _loadAvailableNetworkNamesAsync = loadAvailableNetworkNamesAsync ?? LoadAvailableNetworkNamesAsync;

            OutputDevices.Add(new CycleDevice { Id = string.Empty, Name = "Leave unchanged" });
            foreach (CycleDevice device in outputDevices)
            {
                OutputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name, StableId = device.StableId });
            }

            InputDevices.Add(new CycleDevice { Id = string.Empty, Name = "Leave unchanged" });
            foreach (CycleDevice device in inputDevices)
            {
                InputDevices.Add(new CycleDevice { Id = device.Id, Name = device.Name, StableId = device.StableId });
            }

            InitializeCommunicationsTargets(existingRoutine);
            _sourceOutputDevices.CollectionChanged += OnSourceOutputDevicesChanged;
            _sourceInputDevices.CollectionChanged += OnSourceInputDevicesChanged;

            RefreshNetworksCommand = new RelayCommand(_ => RefreshNetworks());
            if (preloadNetworks)
            {
                _ = RefreshNetworksAsync();
            }

            EditorHotkey = new HotkeyViewModel
            {
                BaseHoverText = "Optional manual shortcut. A manual-only routine needs a hotkey or tray entry. Press a shortcut to assign it, or Delete to clear it.",
            };

            EditorHotkey.PropertyChanged += OnSummaryHotkeyChanged;
            _reservedHotkeyKeys = new HashSet<string>(reservedHotkeyKeys ?? [], StringComparer.OrdinalIgnoreCase);
            _initialTriggers = existingRoutine?.Triggers.Select(static trigger => trigger?.Copy() ?? new RoutineTrigger()).ToList() ?? [];
            _enabled = existingRoutine?.Enabled ?? true;
            _existingId = existingRoutine?.Id ?? Guid.NewGuid().ToString("N");
            _otherRoutines = [.. (otherRoutines ?? []).Where(routine => !string.Equals(routine.Id, _existingId, StringComparison.OrdinalIgnoreCase)).Select(static routine => routine.Clone())];
            _hotkeyCycleGroup = existingRoutine?.HotkeyCycleGroup ?? string.Empty;
            IsHotkeyCyclingExpanded = _hotkeyCycleGroup.Length > 0;
            _existingDisplayOrder = existingRoutine?.DisplayOrder ?? 1;
            _isEditingExistingRoutine = existingRoutine != null;

            if (existingRoutine?.HasHotkeyWarning == true)
            {
                EditorHotkey.SetRegistrationWarning(existingRoutine.HotkeyWarningKind, existingRoutine.HotkeyWarningSummary);
            }

            _selectedAvailabilityDevice = existingRoutine?.TriggerDevice;
            _deviceTransition = existingRoutine?.DeviceTransition ?? DeviceAvailabilityTransition.Connected;
            _selectedRequiredDevice = existingRoutine?.Conditions.Device;
            _requiredDeviceAvailable = existingRoutine?.Conditions.DeviceAvailable ?? true;
            _requireDevice = _selectedRequiredDevice != null;
            _requiredApplicationPath = existingRoutine?.Conditions.RunningAppPath ?? string.Empty;
            _requiredNetworkName = existingRoutine?.Conditions.ConnectedNetwork ?? string.Empty;
            InitializeTimeCondition(existingRoutine?.Conditions.TimeWindow);
            IsConditionsExpanded = existingRoutine?.HasConditions == true;
            RefreshRoutineDeviceChoices();
            if (existingRoutine == null)
            {
                Name = string.IsNullOrWhiteSpace(suggestedName)
                    ? "Routine 1"
                    : suggestedName.Trim();
                SelectedOutputIndex = 0;
                SelectedInputIndex = 0;
                ScheduleTime = new TimeOnly(12, 0);
                ScheduleDays = [];
                UpdateScheduleComponentsFromTime();
                return;
            }

            Name = existingRoutine.Name;
            SelectedOutputIndex = ResolveSelectedIndex(OutputDevices, existingRoutine.OutputDeviceId, existingRoutine.OutputDeviceName, existingRoutine.OutputDeviceStableId);
            SelectedInputIndex = ResolveSelectedIndex(InputDevices, existingRoutine.InputDeviceId, existingRoutine.InputDeviceName, existingRoutine.InputDeviceStableId);
            EditorHotkey.LoadFromString(existingRoutine.Hotkey);
            SelectedTriggerMode = existingRoutine.TriggerKind switch
            {
                RoutineTriggerKind.Application => TriggerModeLabels[1],
                RoutineTriggerKind.AudioPilotStartup => TriggerModeLabels[2],
                RoutineTriggerKind.DeviceChange => TriggerModeLabels[3],
                RoutineTriggerKind.Scheduled => TriggerModeLabels[4],
                RoutineTriggerKind.Network => TriggerModeLabels[5],
                RoutineTriggerKind.SteamBigPicture => TriggerModeLabels[6],
                RoutineTriggerKind.DeviceAvailability => TriggerModeLabels[7],
                RoutineTriggerKind.SessionUnlock => TriggerModeLabels[8],
                RoutineTriggerKind.SystemResume => TriggerModeLabels[9],
                _ when existingRoutine.EnforceTargetsOnDeviceChange => TriggerModeLabels[3],
                _ => TriggerModeLabels[0],
            };
            ScheduleTime = existingRoutine.ScheduleTime;
            ScheduleDays = [.. existingRoutine.ScheduleDays];
            NotifyBeforeScheduledRun = existingRoutine.NotifyBeforeScheduledRun;
            UpdateScheduleComponentsFromTime();
            TriggerNetworkName = existingRoutine.TriggerNetworkName;
            NetworkTriggerDirection = existingRoutine.NetworkTriggerDirection;
            EnsureNetworkListForCurrentSelection();
            TriggerAppPath = existingRoutine.TriggerAppPath;
            SwitchOutputPerApp = existingRoutine.SwitchOutputPerApp;
            TargetAppPath = existingRoutine.TargetAppPath;
            ShowInTrayMenu = existingRoutine.ShowInTrayMenu;
            RestorePreviousAudioOnDeactivate = existingRoutine.RestorePreviousAudioOnDeactivate;
            _applicationTriggerMode = existingRoutine.ApplicationTriggerMode;
            _applicationTriggerTitlePattern = existingRoutine.ApplicationTriggerTitlePattern;
            _applicationTriggerTitleMatchMode = existingRoutine.ApplicationTriggerTitleMatchMode;
            OnPropertyChanged(nameof(SelectedApplicationTriggerMode));
            OnPropertyChanged(nameof(ApplicationTriggerTitlePattern));
            OnPropertyChanged(nameof(SelectedApplicationTriggerTitleMatchMode));
            OnPropertyChanged(nameof(IsAppLaunchModeSelected));
            OnPropertyChanged(nameof(IsProcessFocusModeSelected));
            MasterVolumePercentText = FormatOptionalPercent(existingRoutine.MasterVolumePercent);
            MicVolumePercentText = FormatOptionalPercent(existingRoutine.MicVolumePercent);
            OutputMuteAction = existingRoutine.OutputMuteAction;
            InputMuteAction = existingRoutine.InputMuteAction;
            RefreshVolumeTargetsExpansionState();
        }

        public ObservableCollection<CycleDevice> OutputDevices { get; } = [];

        public ObservableCollection<CycleDevice> InputDevices { get; } = [];

        private readonly ObservableCollection<CycleDevice> _sourceOutputDevices;
        private readonly ObservableCollection<CycleDevice> _sourceInputDevices;

        public HotkeyViewModel EditorHotkey { get; }

        internal void RefreshHotkeyDisplayLabels() => EditorHotkey.RefreshDisplayText();

        public bool IsEditingExistingRoutine => _isEditingExistingRoutine;

        public string PrimaryActionLabel => IsEditingExistingRoutine ? "Update" : "Add";

        public string Name
        {
            get => _name;
            set
            {
                string normalized = NormalizeRoutineName(value);
                if (_name == normalized)
                {
                    return;
                }

                _name = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RoutineNameCharactersRemainingText));
            }
        }

        public string RoutineNameCharactersRemainingText
        {
            get
            {
                int remaining = Math.Max(0, AudioRoutine.MaxNameLength - Name.Length);
                return remaining == 1
                    ? "1 character remaining"
                    : $"{remaining} characters remaining";
            }
        }

        public int SelectedOutputIndex
        {
            get => _selectedOutputIndex;
            set
            {
                if (_selectedOutputIndex == value)
                {
                    return;
                }

                _selectedOutputIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAudioTargetSelected));
                OnPropertyChanged(nameof(IsSelectedOutputUnavailable));
            }
        }

        public int SelectedInputIndex
        {
            get => _selectedInputIndex;
            set
            {
                if (_selectedInputIndex == value)
                {
                    return;
                }

                _selectedInputIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAudioTargetSelected));
                OnPropertyChanged(nameof(IsSelectedInputUnavailable));
            }
        }

        public static IReadOnlyList<string> AvailableTriggerModes => AutomaticTriggerModeLabels;

        public bool IsSelectedOutputUnavailable => SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count &&
            !_sourceOutputDevices.Any(device => string.Equals(device.Id, OutputDevices[SelectedOutputIndex].Id, StringComparison.OrdinalIgnoreCase));

        public bool IsSelectedInputUnavailable => SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count &&
            !_sourceInputDevices.Any(device => string.Equals(device.Id, InputDevices[SelectedInputIndex].Id, StringComparison.OrdinalIgnoreCase));

        public string SelectedTriggerMode
        {
            get => _selectedTriggerMode;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? TriggerModeLabels[0] : value.Trim();
                if (_selectedTriggerMode == normalized)
                {
                    return;
                }

                _selectedTriggerMode = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHotkeyTriggerSelected));
                OnPropertyChanged(nameof(IsApplicationTriggerSelected));
                OnPropertyChanged(nameof(IsAudioPilotStartupTriggerSelected));
                OnPropertyChanged(nameof(IsSteamBigPictureTriggerSelected));
                OnPropertyChanged(nameof(IsDeviceChangeTriggerSelected));
                OnPropertyChanged(nameof(IsDeviceAvailabilityTriggerSelected));
                OnPropertyChanged(nameof(IsSystemEventTriggerSelected));
                OnPropertyChanged(nameof(SystemEventTriggerDescription));
                OnPropertyChanged(nameof(IsScheduledTriggerSelected));
                OnPropertyChanged(nameof(IsNetworkTriggerSelected));

                OnPropertyChanged(nameof(IsStatefulTriggerSelected));
                OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
                OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
                OnPropertyChanged(nameof(ShowApplicationTriggerPathInput));
                OnPropertyChanged(nameof(ApplicationTriggerLifetimeDescription));
                OnPropertyChanged(nameof(IsAppLaunchModeSelected));
                OnPropertyChanged(nameof(IsProcessFocusModeSelected));
                OnPropertyChanged(nameof(ShowApplicationTriggerTitlePatternInput));

                if (!IsEditingAutomaticTrigger && !IsStatefulTriggerSelected)
                {
                    RestorePreviousAudioOnDeactivate = false;
                }

                EnsureNetworkListForCurrentSelection();
            }
        }

        public bool IsHotkeyTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[0], StringComparison.Ordinal);

        public bool IsApplicationTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[1], StringComparison.Ordinal);

        public bool IsAudioPilotStartupTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[2], StringComparison.Ordinal);

        public bool IsDeviceChangeTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[3], StringComparison.Ordinal);

        public bool IsSteamBigPictureTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[6], StringComparison.Ordinal);

        public bool IsScheduledTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[4], StringComparison.Ordinal);

        public bool IsNetworkTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[5], StringComparison.Ordinal);

        public bool IsAppLaunchModeSelected => IsApplicationTriggerSelected && SelectedApplicationTriggerMode == ApplicationTriggerModeLabels[0];

        public bool IsProcessFocusModeSelected => IsApplicationTriggerSelected && SelectedApplicationTriggerMode == ApplicationTriggerModeLabels[1];

        public string ScheduleTimeZoneDisplayName => TimeZoneDisplayFormatter.FormatCompact(
            RoutineScheduleCalculator.ResolveRoutineTimeZone(_scheduleTimeZoneId));

        public string ScheduleTimeZoneDetails => TimeZoneDisplayFormatter.FormatDetails(
            RoutineScheduleCalculator.ResolveRoutineTimeZone(_scheduleTimeZoneId));

        public bool IsStatefulTriggerSelected => IsApplicationTriggerSelected || IsSteamBigPictureTriggerSelected || HasOtherStatefulTriggers;

        public bool HasAudioTargetSelected => SelectedOutputIndex > 0 || SelectedInputIndex > 0;

        public int RoutingScopeIndex
        {
            get => SwitchOutputPerApp ? 1 : 0;
            set => SwitchOutputPerApp = value == 1;
        }

        private string _targetAppPath = string.Empty;
        public string TargetAppPath
        {
            get => _targetAppPath;
            set
            {
                if (_targetAppPath == value) return;
                _targetAppPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<string> ApplicationTriggerModeOptions => _applicationTriggerModeOptions;

        public string SelectedApplicationTriggerMode
        {
            get => ApplicationTriggerModeLabels[(int)_applicationTriggerMode];
            set
            {
                int index = ApplicationTriggerModeLabels.IndexOf(value);
                if (index < 0 || (ApplicationTriggerMode)index == _applicationTriggerMode)
                {
                    return;
                }

                _applicationTriggerMode = (ApplicationTriggerMode)index;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ApplicationTriggerLifetimeDescription));
                OnPropertyChanged(nameof(IsAppLaunchModeSelected));
                OnPropertyChanged(nameof(IsProcessFocusModeSelected));
                OnPropertyChanged(nameof(ShowApplicationTriggerPathInput));
                OnPropertyChanged(nameof(ShowApplicationTriggerTitlePatternInput));
            }
        }

        public ApplicationTriggerMode ApplicationTriggerMode => _applicationTriggerMode;

        public string ApplicationTriggerLifetimeDescription => _applicationTriggerMode == ApplicationTriggerMode.ProcessFocus
            ? "Active while the application has focus."
            : "Active until the application exits.";

        public bool ShowApplicationTriggerPathInput => IsApplicationTriggerSelected;

        public bool ShowApplicationTriggerTitlePatternInput => IsProcessFocusModeSelected;

        public IReadOnlyList<string> ApplicationTriggerTitleMatchModeOptions => _applicationTriggerTitleMatchModeOptions;

        public string SelectedApplicationTriggerTitleMatchMode
        {
            get => ApplicationTriggerTitleMatchModeLabels[(int)_applicationTriggerTitleMatchMode];
            set
            {
                int index = ApplicationTriggerTitleMatchModeLabels.IndexOf(value);
                if (index < 0 || (ApplicationTriggerTitleMatchMode)index == _applicationTriggerTitleMatchMode)
                {
                    return;
                }

                _applicationTriggerTitleMatchMode = (ApplicationTriggerTitleMatchMode)index;
                OnPropertyChanged();
            }
        }

        public ApplicationTriggerTitleMatchMode ApplicationTriggerTitleMatchMode => _applicationTriggerTitleMatchMode;

        public string ApplicationTriggerTitlePattern
        {
            get => _applicationTriggerTitlePattern;
            set
            {
                if (_applicationTriggerTitlePattern == value)
                {
                    return;
                }

                _applicationTriggerTitlePattern = value;
                OnPropertyChanged();
            }
        }

        public bool NotifyBeforeScheduledRun
        {
            get => _notifyBeforeScheduledRun;
            set
            {
                if (_notifyBeforeScheduledRun != value)
                {
                    _notifyBeforeScheduledRun = value;
                    OnPropertyChanged();
                }
            }
        }

        public TimeOnly ScheduleTime
        {
            get => _scheduleTime;
            set
            {
                if (_scheduleTime == value)
                {
                    return;
                }

                _scheduleTime = value;
                UpdateScheduleComponentsFromTime();
                OnPropertyChanged();
            }
        }

        public string ScheduleAmPm
        {
            get => _scheduleAmPm;
            set
            {
                if (_scheduleAmPm == value)
                {
                    return;
                }

                _scheduleAmPm = value;
                UpdateScheduleTimeFromComponents();
                OnPropertyChanged();
            }
        }

        public int ScheduleHourIndex
        {
            get => _is24HourFormat ? _scheduleHour : (_scheduleHour == 12 ? 0 : (_scheduleHour == 0 ? 0 : _scheduleHour));
            set
            {
                if (ScheduleHourIndex == value)
                {
                    return;
                }

                _scheduleHour = _is24HourFormat ? value : (value == 0 ? 12 : value);
                UpdateScheduleTimeFromComponents();
                OnPropertyChanged();
            }
        }

        public int ScheduleMinuteIndex
        {
            get => _scheduleMinute;
            set
            {
                if (_scheduleMinute == value)
                {
                    return;
                }

                _scheduleMinute = value;
                UpdateScheduleTimeFromComponents();
                OnPropertyChanged();
            }
        }

        public int ScheduleAmPmIndex
        {
            get => _scheduleAmPm == "AM" ? 0 : 1;
            set
            {
                if (ScheduleAmPmIndex == value)
                {
                    return;
                }

                _scheduleAmPm = value == 0 ? "AM" : "PM";
                UpdateScheduleTimeFromComponents();
                OnPropertyChanged();
            }
        }

        public HashSet<DayOfWeek> ScheduleDays
        {
            get => _scheduleDays;
            set
            {
                _scheduleDays = value ?? [];
                OnPropertyChanged();
                NotifyAllDaySelectionProperties();
                OnPropertyChanged(nameof(IsDailySchedule));
            }
        }

        private static readonly DayOfWeek[] s_allDays = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

        public bool IsSundaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Sunday);
            set => SetDaySelected(DayOfWeek.Sunday, value);
        }

        public bool IsMondaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Monday);
            set => SetDaySelected(DayOfWeek.Monday, value);
        }

        public bool IsTuesdaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Tuesday);
            set => SetDaySelected(DayOfWeek.Tuesday, value);
        }

        public bool IsWednesdaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Wednesday);
            set => SetDaySelected(DayOfWeek.Wednesday, value);
        }

        public bool IsThursdaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Thursday);
            set => SetDaySelected(DayOfWeek.Thursday, value);
        }

        public bool IsFridaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Friday);
            set => SetDaySelected(DayOfWeek.Friday, value);
        }

        public bool IsSaturdaySelected
        {
            get => _scheduleDays.Contains(DayOfWeek.Saturday);
            set => SetDaySelected(DayOfWeek.Saturday, value);
        }

        private void SetDaySelected(DayOfWeek day, bool value)
        {
            if (value == _scheduleDays.Contains(day))
            {
                return;
            }

            if (value)
            {
                _scheduleDays.Add(day);
            }
            else
            {
                _scheduleDays.Remove(day);
            }

            OnPropertyChanged(nameof(ScheduleDays));
            NotifyAllDaySelectionProperties();
        }

        private void NotifyAllDaySelectionProperties()
        {
            foreach (DayOfWeek day in s_allDays)
            {
                OnPropertyChanged(GetDayPropertyName(day));
            }
        }

        private static string GetDayPropertyName(DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Sunday => nameof(IsSundaySelected),
                DayOfWeek.Monday => nameof(IsMondaySelected),
                DayOfWeek.Tuesday => nameof(IsTuesdaySelected),
                DayOfWeek.Wednesday => nameof(IsWednesdaySelected),
                DayOfWeek.Thursday => nameof(IsThursdaySelected),
                DayOfWeek.Friday => nameof(IsFridaySelected),
                DayOfWeek.Saturday => nameof(IsSaturdaySelected),
                _ => throw new ArgumentOutOfRangeException(nameof(day), day, null)
            };
        }

        public bool IsDailySchedule => _scheduleDays.Count == 0;

        internal void RefreshSystemTimeFormat()
        {
            if (!_usesSystemTimeFormat || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            CultureInfo timeCulture = CultureInfo.CurrentCulture;
            DateTimeFormatInfo dateTimeFormat = timeCulture.DateTimeFormat;
            string timePattern = WindowsTimeFormatPreference.GetCurrentTaskbarTimePattern(
                dateTimeFormat.ShortTimePattern,
                dateTimeFormat.LongTimePattern);
            ApplyTimeFormat(timeCulture, timePattern);
        }

        internal void RefreshTimeFormat(CultureInfo displayCulture, string timePattern)
        {
            ArgumentNullException.ThrowIfNull(displayCulture);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            ApplyTimeFormat(displayCulture, timePattern);
        }

        private void ApplyTimeFormat(CultureInfo timeCulture, string? timePattern)
        {
            (bool is24Hour, bool padsHour) = GetHourFormat(timePattern);
            _is24HourFormat = is24Hour;
            string? hourFormat = padsHour ? "D2" : null;
            _hourOptions = is24Hour
                ? [.. Enumerable.Range(0, 24).Select(hour => hour.ToString(hourFormat, timeCulture))]
                : [
                    12.ToString(hourFormat, timeCulture),
                    .. Enumerable.Range(1, 11).Select(hour => hour.ToString(hourFormat, timeCulture))
                ];

            DateTimeFormatInfo dateTimeFormat = timeCulture.DateTimeFormat;
            _amPmOptions =
            [
                string.IsNullOrWhiteSpace(dateTimeFormat.AMDesignator) ? "AM" : dateTimeFormat.AMDesignator,
                string.IsNullOrWhiteSpace(dateTimeFormat.PMDesignator) ? "PM" : dateTimeFormat.PMDesignator,
            ];

            UpdateScheduleComponentsFromTime();
            OnPropertyChanged(nameof(Is24HourFormat));
            OnPropertyChanged(nameof(HourOptions));
            OnPropertyChanged(nameof(AmPmOptions));
        }

        internal static (bool Is24Hour, bool PadsHour) GetHourFormat(string? timePattern)
        {
            if (string.IsNullOrWhiteSpace(timePattern))
            {
                return (false, false);
            }

            bool inLiteral = false;
            char literalDelimiter = '\0';
            bool escaped = false;
            for (int index = 0; index < timePattern.Length; index++)
            {
                char character = timePattern[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character is '\'' or '"')
                {
                    if (!inLiteral)
                    {
                        inLiteral = true;
                        literalDelimiter = character;
                    }
                    else if (literalDelimiter == character)
                    {
                        inLiteral = false;
                    }

                    continue;
                }

                if (!inLiteral && character is 'H' or 'h')
                {
                    bool padsHour = index + 1 < timePattern.Length && timePattern[index + 1] == character;
                    return (character == 'H', padsHour);
                }
            }

            return (false, false);
        }

        private void UpdateScheduleComponentsFromTime()
        {
            int hour = _scheduleTime.Hour;
            _scheduleMinute = _scheduleTime.Minute;

            if (_is24HourFormat)
            {
                _scheduleHour = hour;
            }
            else
            {
                _scheduleAmPm = hour >= 12 ? "PM" : "AM";
                _scheduleHour = hour == 0 ? 12 : (hour > 12 ? hour - 12 : hour);
            }

            OnPropertyChanged(nameof(ScheduleAmPm));
            OnPropertyChanged(nameof(ScheduleHourIndex));
            OnPropertyChanged(nameof(ScheduleMinuteIndex));
            OnPropertyChanged(nameof(ScheduleAmPmIndex));
        }

        private void UpdateScheduleTimeFromComponents()
        {
            int hour = _scheduleHour;
            if (!_is24HourFormat)
            {
                if (_scheduleAmPm == "PM" && hour != 12)
                {
                    hour += 12;
                }
                else if (_scheduleAmPm == "AM" && hour == 12)
                {
                    hour = 0;
                }
            }

            _scheduleTime = new TimeOnly(hour, _scheduleMinute);
            OnPropertyChanged(nameof(ScheduleTime));
        }

        public string MasterVolumePercentText
        {
            get => _masterVolumePercentText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_masterVolumePercentText == normalized)
                {
                    return;
                }

                _masterVolumePercentText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public string MicVolumePercentText
        {
            get => _micVolumePercentText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_micVolumePercentText == normalized)
                {
                    return;
                }

                _micVolumePercentText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public IReadOnlyList<KeyValuePair<RoutineMuteAction, string>> MuteActions { get; } =
        [new(RoutineMuteAction.Unchanged, "Leave unchanged"), new(RoutineMuteAction.Mute, "Mute"), new(RoutineMuteAction.Unmute, "Unmute")];

        public RoutineMuteAction OutputMuteAction
        {
            get => _outputMuteAction;
            set
            {
                if (_outputMuteAction == value) return;
                _outputMuteAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public RoutineMuteAction InputMuteAction
        {
            get => _inputMuteAction;
            set
            {
                if (_inputMuteAction == value) return;
                _inputMuteAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public bool HasConfiguredVolumeTargets =>
            OutputMuteAction != RoutineMuteAction.Unchanged || InputMuteAction != RoutineMuteAction.Unchanged ||
            !string.IsNullOrWhiteSpace(MasterVolumePercentText) ||
            !string.IsNullOrWhiteSpace(MicVolumePercentText);

        public bool IsVolumeTargetsExpanded
        {
            get => _isVolumeTargetsExpanded;
            set
            {
                if (_isVolumeTargetsExpanded == value)
                {
                    return;
                }

                _isVolumeTargetsExpanded = value;
                OnPropertyChanged();
            }
        }

        public string TriggerAppPath
        {
            get => _triggerAppPath;
            set
            {
                string updatedValue = value ?? string.Empty;
                if (_triggerAppPath == updatedValue)
                {
                    return;
                }

                _triggerAppPath = updatedValue;
                _resolvedPackagedAppDisplayName = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
                OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
            }
        }

        public string TriggerNetworkName
        {
            get => _triggerNetworkName;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_updatingNetworkChoices || _triggerNetworkName == normalized)
                {
                    return;
                }

                _triggerNetworkName = normalized;
                OnPropertyChanged();
                SyncSelectedNetworkFromTriggerName();
            }
        }

        public string? SelectedAvailableNetworkName
        {
            get => _selectedAvailableNetworkName;
            set
            {
                if (_updatingNetworkChoices || string.Equals(_selectedAvailableNetworkName, value, StringComparison.Ordinal))
                {
                    return;
                }

                _selectedAvailableNetworkName = value;
                OnPropertyChanged();

                if (!string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(_triggerNetworkName, value, StringComparison.Ordinal))
                {
                    TriggerNetworkName = value;
                }
            }
        }

        public NetworkTriggerDirection NetworkTriggerDirection
        {
            get => _networkTriggerDirection;
            set
            {
                if (_networkTriggerDirection == value)
                {
                    return;
                }

                _networkTriggerDirection = value;
                OnPropertyChanged();

                if (IsNetworkTriggerSelected)
                {
                    EnsureNetworkListForCurrentSelection();
                }
            }
        }

        public static IReadOnlyList<KeyValuePair<NetworkTriggerDirection, string>> NetworkTriggerDirectionOptions =>
        [
            new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Connect, "Connect to specific network"),
            new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Disconnect, "Disconnect from network"),
            new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Both, "Both connect and disconnect"),
        ];

        public bool HasResolvedTriggerAppTarget =>
            IsApplicationTriggerSelected && !string.IsNullOrWhiteSpace(GetResolvedTriggerAppTargetDisplayName());

        public string ResolvedTriggerAppTargetText
        {
            get
            {
                string displayName = GetResolvedTriggerAppTargetDisplayName();
                return string.IsNullOrWhiteSpace(displayName)
                    ? string.Empty
                    : $"Resolved app: {displayName}";
            }
        }

        public bool SwitchOutputPerApp
        {
            get => _switchOutputPerApp;
            set
            {
                bool normalized = value;
                if (_switchOutputPerApp == normalized)
                {
                    return;
                }

                _switchOutputPerApp = normalized;
                if (normalized && string.IsNullOrWhiteSpace(TargetAppPath)) TargetAppPath = TriggerAppPath;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RoutingScopeIndex));
                OnPropertyChanged(nameof(IsSystemRouting));
            }
        }

        public bool ShowInTrayMenu
        {
            get => _showInTrayMenu;
            set
            {
                if (_showInTrayMenu == value)
                {
                    return;
                }

                _showInTrayMenu = value;
                OnPropertyChanged();
            }
        }

        public bool RestorePreviousAudioOnDeactivate
        {
            get => _restorePreviousAudioOnDeactivate;
            set
            {
                bool normalized = value && IsStatefulTriggerSelected;
                if (_restorePreviousAudioOnDeactivate == normalized)
                {
                    return;
                }

                _restorePreviousAudioOnDeactivate = normalized;
                OnPropertyChanged();
            }
        }

        private string? ValidateCurrentEditor()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "Routine name is required.";
            }

            if (Name.Length > AudioRoutine.MaxNameLength)
            {
                return $"Routine name must be {AudioRoutine.MaxNameLength} characters or fewer.";
            }

            bool hasOutput = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count;
            bool hasInput = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count;
            if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out int? masterVolumePercent) ||
                !TryParseOptionalVolumePercent(MicVolumePercentText, out int? micVolumePercent))
            {
                return "Volume targets must be whole numbers between 0 and 100.";
            }

            if (SwitchOutputPerApp && (!HasAudioTargetSelected || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TargetAppPath)))
            {
                return "Application audio routing requires a target .exe path or packaged app AUMID and at least one output or input device.";
            }

            bool hasVolumeTarget = masterVolumePercent.HasValue || micVolumePercent.HasValue;
            if (!hasOutput && !hasInput && !hasVolumeTarget && SelectedCommunicationsOutputIndex <= 0 && SelectedCommunicationsInputIndex <= 0 && OutputMuteAction == RoutineMuteAction.Unchanged && InputMuteAction == RoutineMuteAction.Unchanged)
            {
                return "Choose an output device, input device, volume target, or mute action.";
            }

            string hotkey = EditorHotkey.ToHotkeyString();
            if (IsHotkeyTriggerSelected && !HasOtherAutomaticTriggers && string.IsNullOrWhiteSpace(hotkey) && !ShowInTrayMenu)
            {
                return "Assign a hotkey or show this routine in the tray menu.";
            }

            if (TryNormalizeHotkey(hotkey, out string normalizedHotkey) && (_reservedHotkeyKeys.Contains(normalizedHotkey) ||
                _otherRoutines.Any(routine => routine.Enabled && string.Equals(AudioPilot.Services.Routines.RoutineHotkeyGroups.NormalizeHotkey(routine.Hotkey), normalizedHotkey, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(HotkeyCycleGroup) || !string.Equals(routine.HotkeyCycleGroup, HotkeyCycleGroup.Trim(), StringComparison.OrdinalIgnoreCase)))))
            {
                return "Routine hotkey must be unique and cannot conflict with another app hotkey.";
            }

            if (IsApplicationTriggerSelected && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TriggerAppPath))
            {
                return "Application trigger requires a full .exe path or packaged app AUMID.";
            }

            if (IsProcessFocusModeSelected && !string.IsNullOrWhiteSpace(ApplicationTriggerTitlePattern) && ApplicationTriggerTitleMatchMode == ApplicationTriggerTitleMatchMode.Regex)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(ApplicationTriggerTitlePattern);
                }
                catch (ArgumentException)
                {
                    return "Title pattern regex is invalid.";
                }
            }

            if (IsNetworkTriggerSelected && NetworkTriggerDirection != NetworkTriggerDirection.Disconnect && string.IsNullOrWhiteSpace(TriggerNetworkName))
            {
                return "Network trigger requires a network name when direction is Connect or Both.";
            }

            if (RestorePreviousAudioOnDeactivate && !IsStatefulTriggerSelected)
            {
                return "Restore-on-deactivate is only available for stateful routines.";
            }

            return null;
        }

        private AudioRoutine BuildCurrentRoutine()
        {
            CycleDevice? output = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count
                ? OutputDevices[SelectedOutputIndex]
                : null;
            CycleDevice? input = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count
                ? InputDevices[SelectedInputIndex]
                : null;

            RoutineTriggerKind triggerKind = SelectedTriggerMode == TriggerModeLabels[8] ? RoutineTriggerKind.SessionUnlock : SelectedTriggerMode == TriggerModeLabels[9] ? RoutineTriggerKind.SystemResume : IsDeviceAvailabilityTriggerSelected ? RoutineTriggerKind.DeviceAvailability : IsApplicationTriggerSelected
                ? RoutineTriggerKind.Application
                : IsAudioPilotStartupTriggerSelected
                    ? RoutineTriggerKind.AudioPilotStartup
                : IsScheduledTriggerSelected
                    ? RoutineTriggerKind.Scheduled
                : IsNetworkTriggerSelected
                    ? RoutineTriggerKind.Network
                : IsDeviceChangeTriggerSelected
                        ? RoutineTriggerKind.DeviceChange
                    : IsSteamBigPictureTriggerSelected
                        ? RoutineTriggerKind.SteamBigPicture
                    : RoutineTriggerKind.Hotkey;

            int? masterVolumePercent = TryParseOptionalVolumePercent(MasterVolumePercentText, out int? parsedMasterVolumePercent)
                ? parsedMasterVolumePercent
                : null;
            int? micVolumePercent = TryParseOptionalVolumePercent(MicVolumePercentText, out int? parsedMicVolumePercent)
                ? parsedMicVolumePercent
                : null;

            return new AudioRoutine
            {
                Id = _existingId,
                Name = Name.Trim(),
                Enabled = _enabled,
                CommunicationsOutput = BuildCommunicationsTarget(true),
                CommunicationsInput = BuildCommunicationsTarget(false),
                OutputDeviceId = output?.Id ?? string.Empty,
                OutputDeviceStableId = output?.StableId,
                OutputDeviceName = output?.Name ?? string.Empty,
                InputDeviceId = input?.Id ?? string.Empty,
                InputDeviceStableId = input?.StableId,
                InputDeviceName = input?.Name ?? string.Empty,
                OutputMuteAction = OutputMuteAction,
                InputMuteAction = InputMuteAction,
                MasterVolumePercent = masterVolumePercent,
                MicVolumePercent = micVolumePercent,
                Hotkey = EditorHotkey.ToHotkeyString(),
                HotkeyCycleGroup = HotkeyCycleGroup,
                TriggerKind = triggerKind,
                TriggerDevice = SelectedAvailabilityDevice,
                DeviceTransition = DeviceTransition,
                Conditions = BuildConditions(),
                TriggerAppPath = IsApplicationTriggerSelected ? RoutineTriggerPathHelper.NormalizeTriggerTarget(TriggerAppPath) : string.Empty,
                SwitchOutputPerApp = SwitchOutputPerApp,
                TargetAppPath = SwitchOutputPerApp ? RoutineTriggerPathHelper.NormalizeTriggerTarget(TargetAppPath) : string.Empty,
                ShowInTrayMenu = ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                EnforceTargetsOnDeviceChange = IsDeviceChangeTriggerSelected,
                ApplicationTriggerMode = IsApplicationTriggerSelected ? _applicationTriggerMode : ApplicationTriggerMode.AppLaunch,
                ApplicationTriggerTitlePattern = IsProcessFocusModeSelected ? _applicationTriggerTitlePattern : string.Empty,
                ApplicationTriggerTitleMatchMode = _applicationTriggerTitleMatchMode,
                DisplayOrder = _existingDisplayOrder,
                ScheduleTime = IsScheduledTriggerSelected ? ScheduleTime : new TimeOnly(12, 0),
                ScheduleDays = IsScheduledTriggerSelected ? [.. ScheduleDays] : [],
                ScheduleTimeZoneId = _scheduleTimeZoneId,
                NotifyBeforeScheduledRun = IsScheduledTriggerSelected && NotifyBeforeScheduledRun,
                TriggerNetworkName = IsNetworkTriggerSelected ? TriggerNetworkName : string.Empty,
                NetworkTriggerDirection = IsNetworkTriggerSelected ? NetworkTriggerDirection : NetworkTriggerDirection.Connect,
            };
        }

        internal void ApplyExecutablePath(string path)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeExecutablePath(path);
        }

        internal void ApplyPackagedAppId(string appUserModelId)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(appUserModelId);
        }

        internal void SetResolvedPackagedAppDisplayName(string? displayName)
        {
            string normalized = displayName?.Trim() ?? string.Empty;
            if (_resolvedPackagedAppDisplayName == normalized)
            {
                return;
            }

            _resolvedPackagedAppDisplayName = normalized;
            OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
            OnPropertyChanged(nameof(ResolvedTriggerAppTargetText));
        }

        private static string NormalizeRoutineName(string? value)
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length <= AudioRoutine.MaxNameLength)
            {
                return normalized;
            }

            return normalized[..AudioRoutine.MaxNameLength];
        }

        private static int ResolveSelectedIndex(ObservableCollection<CycleDevice> devices, string? deviceId, string? deviceName, string? stableId = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(stableId)) return 0;

            for (int index = 1; index < devices.Count; index++)
                if (!string.IsNullOrWhiteSpace(deviceId) && string.Equals(devices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase)) return index;

            if (!string.IsNullOrWhiteSpace(stableId))
            {
                int match = -1;
                for (int index = 1; index < devices.Count; index++)
                {
                    if (!string.Equals(devices[index].StableId, stableId, StringComparison.Ordinal)) continue;
                    if (match >= 0) { match = -1; break; }
                    match = index;
                }
                if (match >= 0) return match;
            }

            devices.Add(new CycleDevice { Id = deviceId ?? string.Empty, StableId = stableId, Name = string.IsNullOrWhiteSpace(deviceName) ? "Unavailable device" : deviceName });
            return devices.Count - 1;
        }

        private static bool TryNormalizeHotkey(string? rawHotkey, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(rawHotkey))
            {
                return false;
            }

            var parser = new HotkeyViewModel();
            if (!parser.LoadFromString(rawHotkey))
            {
                return false;
            }

            normalized = parser.ToHotkeyString();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private static string FormatOptionalPercent(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool TryParseOptionalVolumePercent(string? rawValue, out int? parsed)
        {
            string normalized = rawValue?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                parsed = null;
                return true;
            }

            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 0 or > 100)
            {
                parsed = null;
                return false;
            }

            parsed = value;
            return true;
        }

        private string GetResolvedTriggerAppTargetDisplayName()
        {
            if (!IsApplicationTriggerSelected || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TriggerAppPath))
            {
                return string.Empty;
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(TriggerAppPath) && !string.IsNullOrWhiteSpace(_resolvedPackagedAppDisplayName))
            {
                return _resolvedPackagedAppDisplayName;
            }

            return RoutineTriggerPathHelper.GetTriggerDisplayName(TriggerAppPath);
        }

        private void RefreshVolumeTargetsExpansionState()
        {
            IsVolumeTargetsExpanded = HasConfiguredVolumeTargets;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (propertyName is nameof(SelectedTriggerMode) or nameof(TriggerAppPath) or nameof(ScheduleTime) or nameof(TriggerNetworkName) or nameof(SelectedApplicationTriggerMode) or nameof(ApplicationTriggerTitlePattern)
                or nameof(ScheduleDays) or nameof(NotifyBeforeScheduledRun) or nameof(NetworkTriggerDirection) or nameof(SelectedApplicationTriggerTitleMatchMode) or nameof(SelectedAvailabilityDevice) or nameof(DeviceTransition))
                SaveSelectedTrigger();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName != nameof(EditorSummary))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditorSummary)));
        }
    }
}
