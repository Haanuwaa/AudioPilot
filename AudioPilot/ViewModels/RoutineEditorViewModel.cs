using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal readonly record struct NetworkDiscoveryResult(IReadOnlyList<string> Names, string? Warning = null);

    internal sealed partial class RoutineEditorViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly IReadOnlyList<string> TriggerModeLabels = RoutineTriggerDraftViewModel.TriggerModeLabels;
        private static readonly IReadOnlyList<string> AutomaticTriggerModeLabels = RoutineTriggerDraftViewModel.AutomaticTriggerModeLabels;

        private bool _enabled;
        private readonly string _existingId;
        private readonly int _existingDisplayOrder;
        private readonly bool _isEditingExistingRoutine;
        private readonly AudioRoutine? _initialRoutine;
        private readonly string _initialSuggestedName;
        private readonly HashSet<string> _reservedHotkeyKeys;
        private readonly IReadOnlyList<string> _minuteOptions;
        private IReadOnlyList<string> _hourOptions = [];
        private IReadOnlyList<string> _amPmOptions = [];
        private bool _is24HourFormat;
        private readonly bool _usesSystemTimeFormat;
        private readonly Func<CancellationToken, Task<NetworkDiscoveryResult>> _loadAvailableNetworkNamesAsync;
        private readonly RoutineTriggerDraftViewModel _selectedTriggerDraft;
        private readonly (string Name, bool Enabled, string Definition, string Triggers, string Extras) _initialSnapshot;

        public IReadOnlyList<string> MinuteOptions => _minuteOptions;

        public bool Is24HourFormat => _is24HourFormat;

        public IReadOnlyList<string> HourOptions => _hourOptions;

        public IReadOnlyList<string> AmPmOptions => _amPmOptions;

        private string _name = string.Empty;
        private int _selectedOutputIndex = -1;
        private int _selectedInputIndex = -1;
        private bool _routeToApplication;
        private bool _showInTrayMenu;
        private bool _playSoundAfterApplying;
        private bool _restorePreviousAudioOnDeactivate;
        private string _masterVolumePercentText = string.Empty;
        private string _micVolumePercentText = string.Empty;
        private string _appVolumePercentText = string.Empty;
        private string _triggerDelaySecondsText = string.Empty;
        private bool _isVolumeTargetsExpanded;
        private RoutineMuteAction _outputMuteAction;
        private RoutineMuteAction _inputMuteAction;
        private RoutineMuteAction _appMuteAction;
        private RoutineInputMonitoringAction _inputMonitoringAction;
        private readonly ObservableCollection<string> _availableNetworkNames = [];
        private bool _isScanningNetworks;
        private bool _networksLoaded;
        private bool _updatingNetworkChoices;
        private bool _pendingNetworkRefresh;
        private bool _pendingNetworkForceRefresh;
        private int _networkRefreshVersion;
        private CancellationTokenSource? _networkRefreshCts;
        private int _disposed;

        public ObservableCollection<string> AvailableNetworkNames => _availableNetworkNames;
        public string NetworkDiscoveryWarning { get; private set; } = string.Empty;
        internal bool IsUpdatingNetworkChoices => _updatingNetworkChoices;

        public bool IsScanningNetworks
        {
            get => _isScanningNetworks;
            set
            {
                if (_isScanningNetworks != value)
                {
                    _isScanningNetworks = value;
                    OnPropertyChanged();
                    _selectedTriggerDraft?.NotifyScanningNetworksChanged();
                    _activeTriggerDraft?.NotifyScanningNetworksChanged();
                }
            }
        }

        public ICommand RefreshNetworksCommand { get; }

        private readonly Logger _logger = Logger.Instance;
        private readonly string _scheduleTimeZoneId;

        public async Task RefreshNetworksAsync(bool forceRefresh = false)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_isScanningNetworks)
            {
                if (!forceRefresh)
                {
                    return;
                }
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
                NetworkDiscoveryResult discovery = await _loadAvailableNetworkNamesAsync(refreshCts.Token).ConfigureAwait(true);
                if (refreshCts.IsCancellationRequested || refreshVersion != Volatile.Read(ref _networkRefreshVersion))
                {
                    return;
                }

                _updatingNetworkChoices = true;
                try
                {
                    ReplaceStringCollection(_availableNetworkNames, discovery.Names);
                    NetworkDiscoveryWarning = discovery.Warning ?? string.Empty;
                    OnPropertyChanged(nameof(NetworkDiscoveryWarning));
                    _selectedTriggerDraft.NotifyNetworkDiscoveryUpdated();
                    _activeTriggerDraft?.NotifyNetworkDiscoveryUpdated();
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
                if (!refreshCts.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    NetworkDiscoveryWarning = "Network discovery was unavailable. You can still type a network name.";
                    OnPropertyChanged(nameof(NetworkDiscoveryWarning));
                    _selectedTriggerDraft.NotifyNetworkDiscoveryUpdated();
                    _activeTriggerDraft?.NotifyNetworkDiscoveryUpdated();
                }
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

        private async Task<NetworkDiscoveryResult> LoadAvailableNetworkNamesAsync(CancellationToken cancellationToken)
        {
            var wifiNetworks = await NativeWifiScanner.GetAvailableSsidsAsync(_logger, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<string> nlmNetworks = Coordinators.NetworkTriggerCoordinator.GetAvailableNetworkNames();
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<string> allNetworks = [.. wifiNetworks.Ssids, .. nlmNetworks];
            return new([.. allNetworks.OrderBy(static s => s)], wifiNetworks.Warning);
        }

        internal void EnsureNetworkListForTrigger(string networkName)
        {
            _ = RefreshNetworksAsync(forceRefresh: ShouldForceRefreshNetworksFor(networkName));
        }

        private bool ShouldForceRefreshNetworksFor(string networkName)
        {
            if (!_networksLoaded || string.IsNullOrWhiteSpace(networkName))
            {
                return false;
            }

            return !_availableNetworkNames.Any(existing =>
                string.Equals(existing, networkName, StringComparison.Ordinal));
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

            DetachActiveTriggerDraft();
            _selectedTriggerDraft.PropertyChanged -= OnSelectedTriggerDraftPropertyChanged;
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
            Func<CancellationToken, Task<NetworkDiscoveryResult>>? loadAvailableNetworkNamesAsync = null,
            CultureInfo? displayCulture = null,
            IEnumerable<AudioRoutine>? otherRoutines = null,
            Func<RoutineAudioSetupSnapshot?>? captureCurrentAudioSetup = null)
        {
            _captureCurrentAudioSetup = captureCurrentAudioSetup;
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
            _selectedTriggerDraft = new RoutineTriggerDraftViewModel(this, _scheduleTimeZoneId);
            _selectedTriggerDraft.PropertyChanged += OnSelectedTriggerDraftPropertyChanged;

            if (preloadNetworks)
            {
                _ = RefreshNetworksAsync();
            }

            EditorHotkey = new HotkeyViewModel
            {
                BaseHoverText = "Optional manual shortcut. You can also use Run now or the CLI. Press a shortcut to assign it, or Delete to clear it. Example: Ctrl + Alt + R.",
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
            _initialRoutine = existingRoutine?.Clone();
            _initialSuggestedName = string.IsNullOrWhiteSpace(suggestedName) ? "Routine 1" : suggestedName.Trim();

            if (existingRoutine?.HasHotkeyWarning == true)
            {
                EditorHotkey.SetRegistrationWarning(existingRoutine.HotkeyWarningKind, existingRoutine.HotkeyWarningSummary);
            }

            _selectedRequiredDevice = existingRoutine?.Conditions.Device;
            _requiredDeviceAvailable = existingRoutine?.Conditions.DeviceAvailable ?? true;
            _requiredDeviceDefault = existingRoutine?.Conditions.DeviceMustBeDefault ?? false;
            _requiredApplicationRunning = existingRoutine?.Conditions.ApplicationMustBeRunning ?? true;
            _requiredNetworkConnected = existingRoutine?.Conditions.NetworkMustBeConnected ?? true;
            _requireDevice = _selectedRequiredDevice != null;
            _requiredApplicationPath = existingRoutine?.Conditions.RunningAppPath ?? string.Empty;
            _requiredNetworkName = existingRoutine?.Conditions.ConnectedNetwork ?? string.Empty;
            InitializeTimeCondition(existingRoutine?.Conditions.TimeWindow);
            IsConditionsExpanded = existingRoutine?.HasConditions == true;

            if (existingRoutine == null)
            {
                Name = string.IsNullOrWhiteSpace(suggestedName)
                    ? "Routine 1"
                    : suggestedName.Trim();
                SelectedOutputIndex = 0;
                SelectedInputIndex = 0;
                RefreshRoutineDeviceChoices();
                _initialSnapshot = CaptureSnapshotFingerprint();
                return;
            }

            Name = existingRoutine.Name;
            SelectedOutputIndex = ResolveSelectedIndex(OutputDevices, existingRoutine.OutputDeviceId, existingRoutine.OutputDeviceName, existingRoutine.OutputDeviceStableId);
            SelectedInputIndex = ResolveSelectedIndex(InputDevices, existingRoutine.InputDeviceId, existingRoutine.InputDeviceName, existingRoutine.InputDeviceStableId);
            EditorHotkey.LoadFromString(existingRoutine.Hotkey);

            _loadingTrigger = true;
            try
            {
                _selectedTriggerDraft.Load(new RoutineTrigger
                {
                    Kind = existingRoutine.TriggerKind,
                    Device = existingRoutine.TriggerDevice,
                    DeviceTransition = existingRoutine.DeviceTransition,
                    AppPath = existingRoutine.TriggerAppPath,
                    ApplicationMode = existingRoutine.ApplicationTriggerMode,
                    TitlePattern = existingRoutine.ApplicationTriggerTitlePattern,
                    TitleMatchMode = existingRoutine.ApplicationTriggerTitleMatchMode,
                    Time = existingRoutine.ScheduleTime,
                    Days = [.. existingRoutine.ScheduleDays],
                    TimeZoneId = _scheduleTimeZoneId,
                    NotifyBeforeRun = existingRoutine.NotifyBeforeScheduledRun,
                    NetworkName = existingRoutine.TriggerNetworkName,
                    NetworkDirection = existingRoutine.NetworkTriggerDirection,
                });
            }
            finally
            {
                _loadingTrigger = false;
            }

            RefreshRoutineDeviceChoices();
            if (_selectedTriggerDraft.IsNetworkTriggerSelected)
            {
                EnsureNetworkListForTrigger(_selectedTriggerDraft.TriggerNetworkName);
            }

            RouteToApplication = existingRoutine.RouteToApplication;
            TargetAppPath = existingRoutine.TargetAppPath;
            ShowInTrayMenu = existingRoutine.ShowInTrayMenu;
            PlaySoundAfterApplying = existingRoutine.PlaySoundAfterApplying;
            RestorePreviousAudioOnDeactivate = existingRoutine.RestorePreviousAudioOnDeactivate;
            MasterVolumePercentText = FormatOptionalPercent(existingRoutine.MasterVolumePercent);
            MicVolumePercentText = FormatOptionalPercent(existingRoutine.MicVolumePercent);
            AppVolumePercentText = FormatOptionalPercent(existingRoutine.AppVolumePercent);
            AppMuteAction = existingRoutine.AppMuteAction;
            TriggerDelaySecondsText = existingRoutine.TriggerDelaySeconds > 0
                ? existingRoutine.TriggerDelaySeconds.ToString(CultureInfo.CurrentCulture)
                : string.Empty;
            OutputMuteAction = existingRoutine.OutputMuteAction;
            InputMuteAction = existingRoutine.InputMuteAction;
            InputMonitoringAction = existingRoutine.InputMonitoringAction;
            RefreshVolumeTargetsExpansionState();
            _initialSnapshot = CaptureSnapshotFingerprint();
        }

        public ObservableCollection<CycleDevice> OutputDevices { get; } = [];

        public ObservableCollection<CycleDevice> InputDevices { get; } = [];

        private readonly ObservableCollection<CycleDevice> _sourceOutputDevices;
        private readonly ObservableCollection<CycleDevice> _sourceInputDevices;

        public HotkeyViewModel EditorHotkey { get; }

        internal void RefreshHotkeyDisplayLabels() => EditorHotkey.RefreshDisplayText();

        public bool IsEditingExistingRoutine => _isEditingExistingRoutine;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnPropertyChanged();
            }
        }

        public string PrimaryActionLabel => IsEditingExistingRoutine ? "Update routine" : "Add routine";

        public bool HasUnsavedChanges => CaptureSnapshotFingerprint() != _initialSnapshot;

        internal (string Name, bool Enabled, string Definition, string Triggers, string Extras) CaptureSnapshotFingerprint()
        {
            AudioRoutine routine = BuildCurrentRoutineForSummaryOrSnapshot();
            string extras = string.Join(
                '\u001f',
                routine.Hotkey,
                routine.HotkeyCycleGroup,
                routine.ShowInTrayMenu,
                routine.PlaySoundAfterApplying,
                _masterVolumePercentText,
                _micVolumePercentText,
                _appVolumePercentText,
                _triggerDelaySecondsText);
            return (routine.Name, routine.Enabled, AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine), routine.TriggerFingerprint, extras);
        }

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

        public LocalizedText RoutineNameCharactersRemainingText
        {
            get
            {
                int remaining = Math.Max(0, AudioRoutine.MaxNameLength - Name.Length);
                return remaining == 1
                    ? new("Routine_OneCharacterRemaining")
                    : new("Routine_CharactersRemaining", remaining);
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
            get => CurrentTriggerDraft.SelectedTriggerMode;
            set => CurrentTriggerDraft.SelectedTriggerMode = value;
        }

        public bool IsHotkeyTriggerSelected => CurrentTriggerDraft.IsHotkeyTriggerSelected;
        public bool IsApplicationTriggerSelected => CurrentTriggerDraft.IsApplicationTriggerSelected;
        public bool IsAudioPilotStartupTriggerSelected => CurrentTriggerDraft.IsAudioPilotStartupTriggerSelected;
        public bool IsDeviceChangeTriggerSelected => CurrentTriggerDraft.IsDeviceChangeTriggerSelected;
        public bool IsSteamBigPictureTriggerSelected => CurrentTriggerDraft.IsSteamBigPictureTriggerSelected;
        public bool IsScheduledTriggerSelected => CurrentTriggerDraft.IsScheduledTriggerSelected;
        public bool IsNetworkTriggerSelected => CurrentTriggerDraft.IsNetworkTriggerSelected;
        public bool IsDeviceAvailabilityTriggerSelected => CurrentTriggerDraft.IsDeviceAvailabilityTriggerSelected;
        public bool IsSystemEventTriggerSelected => CurrentTriggerDraft.IsSystemEventTriggerSelected;
        public string SystemEventTriggerDescription => CurrentTriggerDraft.SystemEventTriggerDescription;
        public bool IsAppLaunchModeSelected => CurrentTriggerDraft.IsAppLaunchModeSelected;
        public bool IsProcessFocusModeSelected => CurrentTriggerDraft.IsProcessFocusModeSelected;
        public string ScheduleTimeZoneDisplayName => CurrentTriggerDraft.ScheduleTimeZoneDisplayName;
        public string ScheduleTimeZoneDetails => CurrentTriggerDraft.ScheduleTimeZoneDetails;

        public bool IsStatefulTriggerSelected => _isEditingAutomaticTrigger
            ? _triggerEntries.Any(static entry => entry.Trigger.IsStateful)
            : _selectedTriggerDraft.IsStateful || HasOtherStatefulTriggers;

        public bool HasAudioTargetSelected => SelectedOutputIndex > 0 || SelectedInputIndex > 0;

        public int RoutingScopeIndex
        {
            get => RouteToApplication ? 1 : 0;
            set => RouteToApplication = value == 1;
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
                OnPropertyChanged(nameof(ResolvedRoutingApplicationName));
                OnPropertyChanged(nameof(HasResolvedRoutingApplication));
            }
        }

        public static IReadOnlyList<string> ApplicationTriggerModeOptions => RoutineTriggerDraftViewModel.ApplicationTriggerModeLabels;

        public string SelectedApplicationTriggerMode
        {
            get => CurrentTriggerDraft.SelectedApplicationTriggerMode;
            set => CurrentTriggerDraft.SelectedApplicationTriggerMode = value;
        }

        public ApplicationTriggerMode ApplicationTriggerMode => CurrentTriggerDraft.ApplicationTriggerMode;
        public string ApplicationTriggerLifetimeDescription => CurrentTriggerDraft.ApplicationTriggerLifetimeDescription;
        public bool ShowApplicationTriggerPathInput => CurrentTriggerDraft.ShowApplicationTriggerPathInput;
        public bool ShowApplicationTriggerTitlePatternInput => CurrentTriggerDraft.ShowApplicationTriggerTitlePatternInput;
        public static IReadOnlyList<string> ApplicationTriggerTitleMatchModeOptions => RoutineTriggerDraftViewModel.ApplicationTriggerTitleMatchModeLabels;

        public string SelectedApplicationTriggerTitleMatchMode
        {
            get => CurrentTriggerDraft.SelectedApplicationTriggerTitleMatchMode;
            set => CurrentTriggerDraft.SelectedApplicationTriggerTitleMatchMode = value;
        }

        public ApplicationTriggerTitleMatchMode ApplicationTriggerTitleMatchMode => CurrentTriggerDraft.ApplicationTriggerTitleMatchMode;

        public string ApplicationTriggerTitlePattern
        {
            get => CurrentTriggerDraft.ApplicationTriggerTitlePattern;
            set => CurrentTriggerDraft.ApplicationTriggerTitlePattern = value;
        }

        public bool NotifyBeforeScheduledRun
        {
            get => CurrentTriggerDraft.NotifyBeforeScheduledRun;
            set => CurrentTriggerDraft.NotifyBeforeScheduledRun = value;
        }

        public TimeOnly ScheduleTime
        {
            get => CurrentTriggerDraft.ScheduleTime;
            set => CurrentTriggerDraft.ScheduleTime = value;
        }

        public string ScheduleAmPm
        {
            get => CurrentTriggerDraft.ScheduleAmPm;
            set => CurrentTriggerDraft.ScheduleAmPm = value;
        }

        public int ScheduleHourIndex
        {
            get => CurrentTriggerDraft.ScheduleHourIndex;
            set => CurrentTriggerDraft.ScheduleHourIndex = value;
        }

        public int ScheduleMinuteIndex
        {
            get => CurrentTriggerDraft.ScheduleMinuteIndex;
            set => CurrentTriggerDraft.ScheduleMinuteIndex = value;
        }

        public int ScheduleAmPmIndex
        {
            get => CurrentTriggerDraft.ScheduleAmPmIndex;
            set => CurrentTriggerDraft.ScheduleAmPmIndex = value;
        }

        public HashSet<DayOfWeek> ScheduleDays
        {
            get => CurrentTriggerDraft.ScheduleDays;
            set => CurrentTriggerDraft.ScheduleDays = value;
        }

        public bool IsSundaySelected { get => CurrentTriggerDraft.IsSundaySelected; set => CurrentTriggerDraft.IsSundaySelected = value; }
        public bool IsMondaySelected { get => CurrentTriggerDraft.IsMondaySelected; set => CurrentTriggerDraft.IsMondaySelected = value; }
        public bool IsTuesdaySelected { get => CurrentTriggerDraft.IsTuesdaySelected; set => CurrentTriggerDraft.IsTuesdaySelected = value; }
        public bool IsWednesdaySelected { get => CurrentTriggerDraft.IsWednesdaySelected; set => CurrentTriggerDraft.IsWednesdaySelected = value; }
        public bool IsThursdaySelected { get => CurrentTriggerDraft.IsThursdaySelected; set => CurrentTriggerDraft.IsThursdaySelected = value; }
        public bool IsFridaySelected { get => CurrentTriggerDraft.IsFridaySelected; set => CurrentTriggerDraft.IsFridaySelected = value; }
        public bool IsSaturdaySelected { get => CurrentTriggerDraft.IsSaturdaySelected; set => CurrentTriggerDraft.IsSaturdaySelected = value; }
        public bool IsDailySchedule => CurrentTriggerDraft.IsDailySchedule;

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

            OnPropertyChanged(nameof(Is24HourFormat));
            OnPropertyChanged(nameof(HourOptions));
            OnPropertyChanged(nameof(AmPmOptions));
            _selectedTriggerDraft?.NotifyTimeFormatChanged();
            _activeTriggerDraft?.NotifyTimeFormatChanged();
            NotifyConditionTimeFormatChanged();
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
                RaiseRawPropertyChanged(nameof(SetMasterVolumeEnabled));
                RaiseRawPropertyChanged(nameof(MasterVolumeSliderValue));
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
                RaiseRawPropertyChanged(nameof(SetMicVolumeEnabled));
                RaiseRawPropertyChanged(nameof(MicVolumeSliderValue));
            }
        }

        public string AppVolumePercentText
        {
            get => _appVolumePercentText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_appVolumePercentText == normalized)
                {
                    return;
                }

                _appVolumePercentText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
                RaiseRawPropertyChanged(nameof(SetAppVolumeEnabled));
                RaiseRawPropertyChanged(nameof(AppVolumeSliderValue));
            }
        }

        public string TriggerDelaySecondsText
        {
            get => _triggerDelaySecondsText;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (_triggerDelaySecondsText == normalized)
                {
                    return;
                }

                _triggerDelaySecondsText = normalized;
                OnPropertyChanged();
                RaiseRawPropertyChanged(nameof(TriggerDelaySeconds));
            }
        }

        public int TriggerDelaySeconds
        {
            get => TryParseTriggerDelaySeconds(_triggerDelaySecondsText, out int parsed) ? parsed : 0;
            set
            {
                int clamped = Math.Clamp(value, 0, AudioRoutine.MaxTriggerDelaySeconds);
                TriggerDelaySecondsText = clamped > 0 ? clamped.ToString(CultureInfo.CurrentCulture) : string.Empty;
            }
        }

        public RoutineMuteAction AppMuteAction
        {
            get => _appMuteAction;
            set
            {
                if (_appMuteAction == value) return;
                _appMuteAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public IReadOnlyList<KeyValuePair<RoutineMuteAction, string>> MuteActions { get; } =
        [new(RoutineMuteAction.Unchanged, "Leave unchanged"), new(RoutineMuteAction.Mute, "Mute"), new(RoutineMuteAction.Unmute, "Unmute")];

        public IReadOnlyList<KeyValuePair<RoutineInputMonitoringAction, string>> InputMonitoringActions { get; } =
        [new(RoutineInputMonitoringAction.Unchanged, "Leave unchanged"), new(RoutineInputMonitoringAction.Enable, "Enable"), new(RoutineInputMonitoringAction.Disable, "Disable")];

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

        public RoutineInputMonitoringAction InputMonitoringAction
        {
            get => _inputMonitoringAction;
            set
            {
                if (_inputMonitoringAction == value) return;
                _inputMonitoringAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
            }
        }

        public bool HasConfiguredVolumeTargets =>
            OutputMuteAction != RoutineMuteAction.Unchanged || InputMuteAction != RoutineMuteAction.Unchanged ||
            InputMonitoringAction != RoutineInputMonitoringAction.Unchanged ||
            !string.IsNullOrWhiteSpace(MasterVolumePercentText) ||
            !string.IsNullOrWhiteSpace(MicVolumePercentText) ||
            (RouteToApplication && (!string.IsNullOrWhiteSpace(AppVolumePercentText) || AppMuteAction != RoutineMuteAction.Unchanged));

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
            get => CurrentTriggerDraft.TriggerAppPath;
            set => CurrentTriggerDraft.TriggerAppPath = value;
        }

        public string TriggerNetworkName
        {
            get => CurrentTriggerDraft.TriggerNetworkName;
            set => CurrentTriggerDraft.TriggerNetworkName = value;
        }

        public string? SelectedAvailableNetworkName
        {
            get => CurrentTriggerDraft.SelectedAvailableNetworkName;
            set => CurrentTriggerDraft.SelectedAvailableNetworkName = value;
        }

        public NetworkTriggerDirection NetworkTriggerDirection
        {
            get => CurrentTriggerDraft.NetworkTriggerDirection;
            set => CurrentTriggerDraft.NetworkTriggerDirection = value;
        }

        public static IReadOnlyList<KeyValuePair<NetworkTriggerDirection, string>> NetworkTriggerDirectionOptions =>
            RoutineTriggerDraftViewModel.NetworkTriggerDirectionOptions;

        public bool RouteToApplication
        {
            get => _routeToApplication;
            set
            {
                bool normalized = value;
                if (_routeToApplication == normalized)
                {
                    return;
                }

                _routeToApplication = normalized;
                if (normalized && string.IsNullOrWhiteSpace(TargetAppPath)) TargetAppPath = TriggerAppPath;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RoutingScopeIndex));
                OnPropertyChanged(nameof(IsSystemRouting));
                OnPropertyChanged(nameof(ResolvedRoutingApplicationName));
                OnPropertyChanged(nameof(HasResolvedRoutingApplication));
                OnPropertyChanged(nameof(HasConfiguredVolumeTargets));
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

        public bool PlaySoundAfterApplying
        {
            get => _playSoundAfterApplying;
            set { if (_playSoundAfterApplying == value) return; _playSoundAfterApplying = value; OnPropertyChanged(); }
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

        private LocalizedText? ValidateCurrentEditor()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return new("Message_RoutineNameIsRequired");
            }

            if (Name.Length > AudioRoutine.MaxNameLength)
            {
                return new("Validation_RoutineNameLength", AudioRoutine.MaxNameLength);
            }

            bool hasOutput = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count;
            bool hasInput = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count;
            bool validAppVolume = !RouteToApplication || TryParseOptionalVolumePercent(AppVolumePercentText, out _);
            decimal? appVolumePercent = RouteToApplication && TryParseOptionalVolumePercent(AppVolumePercentText, out decimal? parsedAppVolume)
                ? parsedAppVolume : null;
            if (!TryParseOptionalVolumePercent(MasterVolumePercentText, out decimal? masterVolumePercent) ||
                !TryParseOptionalVolumePercent(MicVolumePercentText, out decimal? micVolumePercent) ||
                !validAppVolume)
            {
                return new("Validation_RoutineVolumeLevels");
            }

            if (HasAutomaticTriggers && !TryParseTriggerDelaySeconds(TriggerDelaySecondsText, out _))
            {
                return new("Validation_RoutineTriggerDelayRange");
            }

            bool hasAppAudioTarget = RouteToApplication && (appVolumePercent.HasValue || AppMuteAction != RoutineMuteAction.Unchanged);
            if (RouteToApplication && ((!HasAudioTargetSelected && !hasAppAudioTarget) || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TargetAppPath)))
            {
                return new("Message_ApplicationAudioRoutingRequiresATargetExe");
            }

            bool hasVolumeTarget = masterVolumePercent.HasValue || micVolumePercent.HasValue || (RouteToApplication && appVolumePercent.HasValue);
            if (!hasOutput && !hasInput && !hasVolumeTarget && SelectedCommunicationsOutputIndex <= 0 && SelectedCommunicationsInputIndex <= 0 && OutputMuteAction == RoutineMuteAction.Unchanged && InputMuteAction == RoutineMuteAction.Unchanged && InputMonitoringAction == RoutineInputMonitoringAction.Unchanged && !hasAppAudioTarget)
            {
                return new("Message_ChooseAnOutputDeviceInputDeviceVolume");
            }

            string hotkey = EditorHotkey.ToHotkeyString();
            if (HotkeyParsingService.TryNormalizeHotkey(hotkey, out string normalizedHotkey) && (_reservedHotkeyKeys.Contains(normalizedHotkey) ||
                _otherRoutines.Any(routine => routine.Enabled && string.Equals(AudioPilot.Services.Routines.RoutineHotkeyGroups.NormalizeHotkey(routine.Hotkey), normalizedHotkey, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(HotkeyCycleGroup) || !string.Equals(routine.HotkeyCycleGroup, HotkeyCycleGroup.Trim(), StringComparison.OrdinalIgnoreCase)))))
            {
                return new("Message_RoutineHotkeyMustBeUniqueAndCannot");
            }

            if (_selectedTriggerDraft.ValidateCurrentFields() is { } triggerFieldError)
            {
                return triggerFieldError;
            }

            if (RestorePreviousAudioOnDeactivate && !IsStatefulTriggerSelected)
            {
                return new("Message_RestoreOnDeactivateIsOnlyAvailableFor");
            }

            return null;
        }

        private AudioRoutine BuildCurrentRoutine(RoutineTriggerDraftViewModel? triggerDraft = null)
        {
            RoutineTriggerDraftViewModel draft = triggerDraft ?? _selectedTriggerDraft;
            CycleDevice? output = SelectedOutputIndex > 0 && SelectedOutputIndex < OutputDevices.Count
                ? OutputDevices[SelectedOutputIndex]
                : null;
            CycleDevice? input = SelectedInputIndex > 0 && SelectedInputIndex < InputDevices.Count
                ? InputDevices[SelectedInputIndex]
                : null;

            decimal? masterVolumePercent = TryParseOptionalVolumePercent(MasterVolumePercentText, out decimal? parsedMasterVolumePercent)
                ? parsedMasterVolumePercent
                : null;
            decimal? micVolumePercent = TryParseOptionalVolumePercent(MicVolumePercentText, out decimal? parsedMicVolumePercent)
                ? parsedMicVolumePercent
                : null;
            decimal? appVolumePercent = RouteToApplication && TryParseOptionalVolumePercent(AppVolumePercentText, out decimal? parsedAppVolumePercent)
                ? parsedAppVolumePercent
                : null;
            int triggerDelaySeconds = HasAutomaticTriggers && TryParseTriggerDelaySeconds(TriggerDelaySecondsText, out int parsedDelaySeconds)
                ? parsedDelaySeconds
                : 0;

            RoutineTrigger captured = draft.Capture();
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
                InputMonitoringAction = InputMonitoringAction,
                MasterVolumePercent = masterVolumePercent,
                MicVolumePercent = micVolumePercent,
                AppVolumePercent = appVolumePercent,
                AppMuteAction = RouteToApplication ? AppMuteAction : RoutineMuteAction.Unchanged,
                Hotkey = EditorHotkey.ToHotkeyString(),
                HotkeyCycleGroup = HotkeyCycleGroup,
                TriggerDelaySeconds = triggerDelaySeconds,
                TriggerKind = captured.Kind,
                TriggerDevice = captured.Device,
                DeviceTransition = captured.DeviceTransition,
                Conditions = BuildConditions(),
                TriggerAppPath = captured.AppPath,
                RouteToApplication = RouteToApplication,
                TargetAppPath = RouteToApplication ? RoutineTriggerPathHelper.NormalizeTriggerTarget(TargetAppPath) : string.Empty,
                ShowInTrayMenu = ShowInTrayMenu,
                PlaySoundAfterApplying = PlaySoundAfterApplying,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                ApplicationTriggerMode = captured.ApplicationMode,
                ApplicationTriggerTitlePattern = captured.TitlePattern,
                ApplicationTriggerTitleMatchMode = captured.TitleMatchMode,
                DisplayOrder = _existingDisplayOrder,
                ScheduleTime = captured.Time,
                ScheduleDays = [.. captured.Days],
                ScheduleTimeZoneId = captured.TimeZoneId,
                NotifyBeforeScheduledRun = captured.NotifyBeforeRun,
                TriggerNetworkName = captured.NetworkName,
                NetworkTriggerDirection = captured.NetworkDirection,
            };
        }

        private AudioRoutine BuildCurrentRoutineForSummaryOrSnapshot()
        {
            AudioRoutine routine = BuildCurrentRoutine(_selectedTriggerDraft);
            if (_triggerEntriesInitialized)
            {
                routine.Triggers = [.. _triggerEntries.Where(static entry => entry.Trigger.Kind != RoutineTriggerKind.Hotkey)
                    .Select(static entry => entry.Trigger.Copy())];
            }
            else if (_initialTriggers.Count > 0)
            {
                routine.Triggers = [.. _initialTriggers.Select(static trigger => trigger.Copy())];
            }
            else if (!_selectedTriggerDraft.IsHotkeyTriggerSelected)
            {
                routine.Triggers = [_selectedTriggerDraft.Capture()];
            }
            else
            {
                routine.Triggers = [];
            }

            routine.RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
            return routine;
        }

        internal void ApplyExecutablePath(string path)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeExecutablePath(path);
        }

        internal void ApplyPackagedAppId(string appUserModelId)
        {
            TriggerAppPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(appUserModelId);
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

        private static string FormatOptionalPercent(decimal? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.#", CultureInfo.CurrentCulture)
                : string.Empty;
        }

        private static bool TryParseTriggerDelaySeconds(string? rawValue, out int seconds)
        {
            string normalized = rawValue?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                seconds = 0;
                return true;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) &&
                parsed >= 0 &&
                parsed <= AudioRoutine.MaxTriggerDelaySeconds)
            {
                seconds = parsed;
                return true;
            }

            seconds = 0;
            return false;
        }

        private static bool TryParseOptionalVolumePercent(string? rawValue, out decimal? parsed)
        {
            string normalized = rawValue?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                parsed = null;
                return true;
            }

            if (!VolumePercent.TryParseLevel(normalized, out decimal value))
            {
                parsed = null;
                return false;
            }

            parsed = value;
            return true;
        }

        private void RefreshVolumeTargetsExpansionState()
        {
            IsVolumeTargetsExpanded = HasConfiguredVolumeTargets;
        }

        internal static bool AffectsReadinessOrSummary(string? propertyName) => propertyName is not (
            null
            or nameof(EditorSummary)
            or nameof(CommunicationsSummary)
            or nameof(VolumeTargetsSummary)
            or nameof(HotkeyCyclingSummary)
            or nameof(ConditionsSummary)
            or nameof(RoutineNameCharactersRemainingText)
            or nameof(IsScanningNetworks)
            or nameof(NetworkDiscoveryWarning)
            or nameof(AvailableNetworkNames)
            or nameof(RoutineDeviceChoices)
            or nameof(IsVolumeTargetsExpanded)
            or nameof(IsConditionsExpanded)
            or nameof(IsCommunicationsExpanded)
            or nameof(IsHotkeyCyclingExpanded)
            or nameof(HourOptions)
            or nameof(MinuteOptions)
            or nameof(AmPmOptions)
            or nameof(Is24HourFormat)
            or nameof(ActiveTriggerDraft)
            or nameof(TriggerEditorHeading)
            or nameof(TriggerEditorConfirmText)
            or nameof(TriggerEditorError)
            or nameof(TriggerEditorErrorText)
            or nameof(SelectedSection)
            or nameof(IsActionsSectionSelected)
            or nameof(IsTriggersSectionSelected)
            or nameof(IsConditionsSectionSelected)
            or nameof(ShowStarterTemplates)
            or nameof(CaptureSetupFeedback)
            or nameof(HasCaptureSetupFeedback)
            or nameof(IsCaptureSetupFeedbackError)
            or nameof(ClearedCommunicationsForAppRouting)
            or nameof(SetMasterVolumeEnabled)
            or nameof(MasterVolumeSliderValue)
            or nameof(SetMicVolumeEnabled)
            or nameof(MicVolumeSliderValue)
            or nameof(SetAppVolumeEnabled)
            or nameof(AppVolumeSliderValue)
            or nameof(ConfiguredActionsCount)
            or nameof(ConfiguredTriggersCount)
            or nameof(ConfiguredConditionsCount)
            or nameof(ValidationIssues)
            or nameof(HasValidationIssues)
            or nameof(HasActionsValidationIssue)
            or nameof(HasTriggersValidationIssue)
            or nameof(HasConditionsValidationIssue)
            or nameof(HotkeyConflictMessage)
            or nameof(HasHotkeyConflict)
            or nameof(HotkeyCycleGroupMembers)
            or nameof(HasHotkeyCycleGroupMembers)
            or nameof(HotkeyCycleGroupPreviewPresentation)
            or nameof(PreviewWhenPresentation)
            or nameof(PreviewOnlyIfPresentation)
            or nameof(PreviewThenPresentation)
            or nameof(HasConfiguredVolumeTargets)
            or nameof(HasAudioTargetSelected)
            or nameof(RoutingScopeIndex)
            or nameof(IsSystemRouting)
            or nameof(ResolvedRoutingApplicationName)
            or nameof(HasResolvedRoutingApplication)
            or nameof(ResolvedRequiredApplicationName)
            or nameof(HasResolvedRequiredApplication)
            or nameof(HasUnsavedChanges)
            or nameof(CanResetChanges));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (propertyName is nameof(SelectedOutputIndex) or nameof(SelectedInputIndex) or nameof(MasterVolumePercentText)
                or nameof(MicVolumePercentText) or nameof(AppVolumePercentText) or nameof(OutputMuteAction)
                or nameof(InputMuteAction) or nameof(AppMuteAction) or nameof(InputMonitoringAction)
                or nameof(SelectedCommunicationsOutputIndex) or nameof(SelectedCommunicationsInputIndex) or nameof(RouteToApplication))
            {
                ClearCaptureSetupFeedback();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            NotifySectionSummaryChanged(propertyName);
            if (AffectsReadinessOrSummary(propertyName))
            {
                OnSummaryChanged(nameof(EditorSummary));
                NotifyWorkspaceStateChanged();
            }
        }
    }
}
