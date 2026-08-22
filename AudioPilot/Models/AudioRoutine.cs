using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AudioPilot.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter<RoutineTriggerKind>))]
    public enum RoutineTriggerKind
    {
        Hotkey,
        Application,
        AudioPilotStartup,
        SteamBigPicture,
        DeviceChange,
        Scheduled,
        Network,
        DeviceAvailability,
        SessionUnlock,
        SystemResume,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ApplicationTriggerMode>))]
    public enum ApplicationTriggerMode
    {
        AppLaunch,
        ProcessFocus,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ApplicationTriggerTitleMatchMode>))]
    public enum ApplicationTriggerTitleMatchMode
    {
        Exact,
        Contains,
        Wildcard,
        Regex,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<NetworkTriggerDirection>))]
    public enum NetworkTriggerDirection
    {
        Connect,
        Disconnect,
        Both,
    }

    public enum RoutineLastRunState
    {
        Never,
        Succeeded,
        Failed,
        WaitingForApp,
        Skipped,
    }

    public sealed partial class AudioRoutine : INotifyPropertyChanged, IJsonOnDeserializing, IJsonOnDeserialized
    {
        public const int MaxNameLength = 64;

        private bool _isDeserializing;
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private bool _enabled = true;
        private int _displayOrder = 1;
        private string _outputDeviceId = string.Empty;
        private string _outputDeviceName = string.Empty;
        private string _inputDeviceId = string.Empty;
        private string _inputDeviceName = string.Empty;
        private int? _masterVolumePercent;
        private int? _micVolumePercent;
        private string _hotkey = string.Empty;
        private string _targetAppPath = string.Empty;
        private bool _switchOutputPerApp;
        private bool _showInTrayMenu;
        private bool _restorePreviousAudioOnDeactivate;

        private bool _hasConflict;
        private string _conflictSummary = string.Empty;
        private HotkeyWarningKind _hotkeyWarningKind;
        private string _hotkeyWarningSummary = string.Empty;
        private DateTimeOffset? _lastRunUtc;
        private RoutineLastRunState _lastRunState;
        private string _lastRunDetail = string.Empty;

        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (!SetField(ref _name, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (!SetField(ref _enabled, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(HasLastRunBadge));
            }
        }

        [JsonIgnore]
        public int DisplayOrder
        {
            get => _displayOrder;
            set
            {
                if (!SetField(ref _displayOrder, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(DisplayName));
            }
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputDeviceStableId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InputDeviceStableId { get; set; }

        public string OutputDeviceId
        {
            get => _outputDeviceId;
            set
            {
                if (!SetField(ref _outputDeviceId, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasOutputTarget));
                OnPropertyChanged(nameof(HasExecutionTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
                OnTriggerSummariesChanged();
            }
        }

        public string OutputDeviceName
        {
            get => _outputDeviceName;
            set
            {
                if (!SetField(ref _outputDeviceName, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public string InputDeviceId
        {
            get => _inputDeviceId;
            set
            {
                if (!SetField(ref _inputDeviceId, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasInputTarget));
                OnPropertyChanged(nameof(HasExecutionTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
                OnTriggerSummariesChanged();
            }
        }

        public string InputDeviceName
        {
            get => _inputDeviceName;
            set
            {
                if (!SetField(ref _inputDeviceName, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public int? MasterVolumePercent
        {
            get => _masterVolumePercent;
            set
            {
                int? normalized = value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;
                if (!SetField(ref _masterVolumePercent, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasMasterVolumeTarget));
                OnPropertyChanged(nameof(HasVolumeTarget));
                OnPropertyChanged(nameof(HasExecutionTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        public int? MicVolumePercent
        {
            get => _micVolumePercent;
            set
            {
                int? normalized = value.HasValue ? Math.Clamp(value.Value, 0, 100) : null;
                if (!SetField(ref _micVolumePercent, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasMicVolumeTarget));
                OnPropertyChanged(nameof(HasVolumeTarget));
                OnPropertyChanged(nameof(HasExecutionTarget));
                OnPropertyChanged(nameof(TargetKindBadgeText));
                OnPropertyChanged(nameof(TargetSummary));
            }
        }

        private string _hotkeyCycleGroup = string.Empty;
        public string HotkeyCycleGroup
        {
            get => _hotkeyCycleGroup;
            set { if (SetField(ref _hotkeyCycleGroup, value?.Trim() ?? string.Empty)) OnTriggerSummariesChanged(); }
        }

        public string Hotkey
        {
            get => _hotkey;
            set
            {
                if (!SetField(ref _hotkey, value))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public RoutineTriggerKind TriggerKind
        {
            get => CurrentTrigger.Kind;
            set
            {
                if (!SetTriggerField(CurrentTrigger.Kind, newValue => CurrentTrigger = CurrentTrigger with { Kind = newValue }, value))
                {
                    return;
                }

                NormalizeTriggerDependentState();

                if (!_isDeserializing && !HasStatefulTriggers)
                {
                    if (_restorePreviousAudioOnDeactivate)
                    {
                        _restorePreviousAudioOnDeactivate = false;
                        OnPropertyChanged(nameof(RestorePreviousAudioOnDeactivate));
                    }
                }

                OnPropertyChanged(nameof(UsesApplicationTrigger));
                OnPropertyChanged(nameof(HasApplicationTrigger));
                OnPropertyChanged(nameof(HasAudioPilotStartupTrigger));
                OnPropertyChanged(nameof(HasSteamBigPictureTrigger));
                OnPropertyChanged(nameof(HasDeviceChangeTrigger));
                OnPropertyChanged(nameof(HasNetworkTrigger));
                OnPropertyChanged(nameof(IsStatefulTrigger));
                OnTriggerSummariesChanged();
            }
        }

        /// <summary>
        /// Defers trigger-dependent normalization until every JSON property has been assigned.
        /// </summary>
        void IJsonOnDeserializing.OnDeserializing() => _isDeserializing = true;

        void IJsonOnDeserialized.OnDeserialized()
        {
            _isDeserializing = false;
            NormalizeTriggerDependentState();
            ApplicationTriggerTitlePattern = CurrentTrigger.TitlePattern;
            ApplicationTriggerTitleMatchMode = CurrentTrigger.TitleMatchMode;
            RestorePreviousAudioOnDeactivate = _restorePreviousAudioOnDeactivate;
            NetworkTriggerDirection = CurrentTrigger.NetworkDirection;
        }

        private void NormalizeTriggerDependentState()
        {
            if (_isDeserializing)
            {
                return;
            }

            PreserveApplicationTarget();

            if (CurrentTrigger.Kind != RoutineTriggerKind.Application && !string.IsNullOrWhiteSpace(CurrentTrigger.AppPath))
            {
                CurrentTrigger = CurrentTrigger with { AppPath = string.Empty };
                OnPropertyChanged(nameof(TriggerAppPath));
            }

            if (CurrentTrigger.Kind != RoutineTriggerKind.Application)
            {
                if (CurrentTrigger.ApplicationMode != ApplicationTriggerMode.AppLaunch)
                {
                    CurrentTrigger = CurrentTrigger with { ApplicationMode = ApplicationTriggerMode.AppLaunch };
                    OnPropertyChanged(nameof(ApplicationTriggerMode));
                }

                if (!string.IsNullOrWhiteSpace(CurrentTrigger.TitlePattern))
                {
                    CurrentTrigger = CurrentTrigger with { TitlePattern = string.Empty };
                    OnPropertyChanged(nameof(ApplicationTriggerTitlePattern));
                }

                if (CurrentTrigger.TitleMatchMode != ApplicationTriggerTitleMatchMode.Contains)
                {
                    CurrentTrigger = CurrentTrigger with { TitleMatchMode = ApplicationTriggerTitleMatchMode.Contains };
                    OnPropertyChanged(nameof(ApplicationTriggerTitleMatchMode));
                }
            }


            if (CurrentTrigger.Kind != RoutineTriggerKind.Scheduled)
            {
                NotifyBeforeScheduledRun = false;
                if (CurrentTrigger.Days.Count > 0)
                {
                    CurrentTrigger.Days.Clear();
                    OnPropertyChanged(nameof(ScheduleDays));
                }

                if (CurrentTrigger.Time != new TimeOnly(12, 0))
                {
                    CurrentTrigger = CurrentTrigger with { Time = new TimeOnly(12, 0) };
                    OnPropertyChanged(nameof(ScheduleTime));
                }
            }

            if (CurrentTrigger.Kind != RoutineTriggerKind.Network && !string.IsNullOrWhiteSpace(CurrentTrigger.NetworkName))
            {
                CurrentTrigger = CurrentTrigger with { NetworkName = string.Empty };
                OnPropertyChanged(nameof(TriggerNetworkName));
            }
        }

        [JsonIgnore]
        public bool UsesApplicationTrigger
        {
            get => TriggerKind == RoutineTriggerKind.Application;
            set
            {
                if (value)
                {
                    TriggerKind = RoutineTriggerKind.Application;
                    return;
                }

                if (TriggerKind == RoutineTriggerKind.Application)
                {
                    TriggerKind = RoutineTriggerKind.Hotkey;
                }
            }
        }

        [JsonIgnore]
        public string TriggerAppPath
        {
            get => CurrentTrigger.AppPath;
            set
            {
                string normalized = _isDeserializing || TriggerKind == RoutineTriggerKind.Application
                    ? RoutineTriggerPathHelper.NormalizeTriggerTarget(value)
                    : string.Empty;
                if (!SetTriggerField(CurrentTrigger.AppPath, newValue => CurrentTrigger = CurrentTrigger with { AppPath = newValue }, normalized))
                {
                    return;
                }

                PreserveApplicationTarget();
                OnTriggerSummariesChanged();
            }
        }

        public string TargetAppPath
        {
            get => _targetAppPath;
            set
            {
                if (!SetField(ref _targetAppPath, RoutineTriggerPathHelper.NormalizeTriggerTarget(value))) return;
                OnPropertyChanged(nameof(TargetSummary));
                OnTriggerSummariesChanged();
            }
        }

        private void PreserveApplicationTarget()
        {
            if (!_isDeserializing && _switchOutputPerApp && string.IsNullOrWhiteSpace(_targetAppPath) && !string.IsNullOrWhiteSpace(CurrentTrigger.AppPath))
                TargetAppPath = CurrentTrigger.AppPath;
        }

        public bool SwitchOutputPerApp
        {
            get => _switchOutputPerApp;
            set
            {
                if (!SetField(ref _switchOutputPerApp, value))
                {
                    return;
                }

                PreserveApplicationTarget();
                OnPropertyChanged(nameof(TargetSummary));
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public ApplicationTriggerMode ApplicationTriggerMode
        {
            get => CurrentTrigger.ApplicationMode;
            set
            {
                if (!SetTriggerField(CurrentTrigger.ApplicationMode, newValue => CurrentTrigger = CurrentTrigger with { ApplicationMode = newValue }, value))
                {
                    return;
                }

                if (!_isDeserializing && CurrentTrigger.Kind == RoutineTriggerKind.Application && value != ApplicationTriggerMode.ProcessFocus)
                {
                    if (!string.IsNullOrWhiteSpace(CurrentTrigger.TitlePattern))
                    {
                        CurrentTrigger = CurrentTrigger with { TitlePattern = string.Empty };
                        OnPropertyChanged(nameof(ApplicationTriggerTitlePattern));
                    }

                    if (CurrentTrigger.TitleMatchMode != ApplicationTriggerTitleMatchMode.Contains)
                    {
                        CurrentTrigger = CurrentTrigger with { TitleMatchMode = ApplicationTriggerTitleMatchMode.Contains };
                        OnPropertyChanged(nameof(ApplicationTriggerTitleMatchMode));
                    }
                }

                OnPropertyChanged(nameof(IsProcessFocusMode));
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public bool IsProcessFocusMode => ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus;

        [JsonIgnore]
        public string ApplicationTriggerTitlePattern
        {
            get => CurrentTrigger.TitlePattern;
            set
            {
                string normalized = _isDeserializing || (CurrentTrigger.Kind == RoutineTriggerKind.Application && CurrentTrigger.ApplicationMode == ApplicationTriggerMode.ProcessFocus)
                    ? value?.Trim() ?? string.Empty
                    : string.Empty;
                if (!SetTriggerField(CurrentTrigger.TitlePattern, newValue => CurrentTrigger = CurrentTrigger with { TitlePattern = newValue }, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public ApplicationTriggerTitleMatchMode ApplicationTriggerTitleMatchMode
        {
            get => CurrentTrigger.TitleMatchMode;
            set
            {
                ApplicationTriggerTitleMatchMode normalized = _isDeserializing || (CurrentTrigger.Kind == RoutineTriggerKind.Application && CurrentTrigger.ApplicationMode == ApplicationTriggerMode.ProcessFocus)
                    ? value
                    : ApplicationTriggerTitleMatchMode.Contains;
                if (!SetTriggerField(CurrentTrigger.TitleMatchMode, newValue => CurrentTrigger = CurrentTrigger with { TitleMatchMode = newValue }, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public bool RestorePreviousAudioOnDeactivate
        {
            get => _restorePreviousAudioOnDeactivate;
            set
            {
                bool normalized = value && (_isDeserializing || HasStatefulTriggers);
                if (!SetField(ref _restorePreviousAudioOnDeactivate, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public bool EnforceTargetsOnDeviceChange
        {
            get => TriggerKind == RoutineTriggerKind.DeviceChange;
            set { if (value) TriggerKind = RoutineTriggerKind.DeviceChange; }
        }

        public bool ShowInTrayMenu
        {
            get => _showInTrayMenu;
            set
            {
                if (!SetField(ref _showInTrayMenu, value))
                {
                    return;
                }

                OnTriggerSummariesChanged();
                OnPropertyChanged(nameof(HasTrayMenuTrigger));
            }
        }

        [JsonIgnore]
        public bool HasOutputTarget => !string.IsNullOrWhiteSpace(OutputDeviceId);

        [JsonIgnore]
        public bool HasInputTarget => !string.IsNullOrWhiteSpace(InputDeviceId);

        [JsonIgnore]
        public bool HasMasterVolumeTarget => MasterVolumePercent.HasValue;

        [JsonIgnore]
        public bool HasMicVolumeTarget => MicVolumePercent.HasValue;

        [JsonIgnore]
        public bool HasVolumeTarget => HasMasterVolumeTarget || HasMicVolumeTarget;

        [JsonIgnore]
        public bool HasExecutionTarget => HasOutputTarget || HasInputTarget || HasVolumeTarget || HasMuteTarget || HasCommunicationsTarget;

        [JsonIgnore]
        public bool HasApplicationTrigger =>
            TriggerKind == RoutineTriggerKind.Application &&
            !string.IsNullOrWhiteSpace(TriggerAppPath);

        [JsonIgnore]
        public bool HasAudioPilotStartupTrigger => TriggerKind == RoutineTriggerKind.AudioPilotStartup;

        [JsonIgnore]
        public bool HasSteamBigPictureTrigger => TriggerKind == RoutineTriggerKind.SteamBigPicture;

        [JsonIgnore]
        public bool HasDeviceChangeTrigger => TriggerKind == RoutineTriggerKind.DeviceChange;

        [JsonIgnore]
        public bool HasScheduledTrigger => TriggerKind == RoutineTriggerKind.Scheduled;

        [JsonIgnore]
        public bool HasNetworkTrigger =>
            TriggerKind == RoutineTriggerKind.Network &&
            (NetworkTriggerDirection == NetworkTriggerDirection.Disconnect || !string.IsNullOrWhiteSpace(TriggerNetworkName));

        [JsonIgnore]
        public bool IsStatefulTrigger => TriggerKind is RoutineTriggerKind.Application or RoutineTriggerKind.SteamBigPicture;

        [JsonIgnore]
        public bool HasTrayMenuTrigger => ShowInTrayMenu;

        [JsonIgnore]
        public string DisplayName => $"{DisplayOrder}. {Name}";

        [JsonIgnore]
        public string StatusLabel => Enabled ? "Enabled" : "Disabled";

        [JsonIgnore]
        public bool HasConflict
        {
            get => _hasConflict;
            set => SetField(ref _hasConflict, value);
        }

        [JsonIgnore]
        public string ConflictSummary
        {
            get => _conflictSummary;
            set => SetField(ref _conflictSummary, value ?? string.Empty);
        }

        [JsonIgnore]
        public HotkeyWarningKind HotkeyWarningKind
        {
            get => _hotkeyWarningKind;
            set
            {
                if (!SetField(ref _hotkeyWarningKind, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasHotkeyWarning));
            }
        }

        [JsonIgnore]
        public bool HasHotkeyWarning => HotkeyWarningKind != HotkeyWarningKind.None;

        [JsonIgnore]
        public string HotkeyWarningSummary
        {
            get => _hotkeyWarningSummary;
            set => SetField(ref _hotkeyWarningSummary, value ?? string.Empty);
        }

        [JsonIgnore]
        public DateTimeOffset? LastRunUtc
        {
            get => _lastRunUtc;
            set
            {
                if (!SetField(ref _lastRunUtc, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
            }
        }

        [JsonIgnore]
        public RoutineLastRunState LastRunState
        {
            get => _lastRunState;
            set
            {
                if (!SetField(ref _lastRunState, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
                OnPropertyChanged(nameof(HasLastRunBadge));
                OnPropertyChanged(nameof(LastRunBadgeText));
            }
        }

        [JsonIgnore]
        public string LastRunDetail
        {
            get => _lastRunDetail;
            set
            {
                string normalized = value ?? string.Empty;
                if (!SetField(ref _lastRunDetail, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(LastRunStatusText));
            }
        }

        [JsonIgnore]
        public bool NotifyBeforeScheduledRun
        {
            get => CurrentTrigger.NotifyBeforeRun;
            set => SetTriggerField(CurrentTrigger.NotifyBeforeRun, newValue => CurrentTrigger = CurrentTrigger with { NotifyBeforeRun = newValue }, value);
        }

        [JsonIgnore]
        public TimeOnly ScheduleTime
        {
            get => CurrentTrigger.Time;
            set
            {
                if (!SetTriggerField(CurrentTrigger.Time, newValue => CurrentTrigger = CurrentTrigger with { Time = newValue }, value))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public HashSet<DayOfWeek> ScheduleDays
        {
            get => CurrentTrigger.Days;
            set
            {
                HashSet<DayOfWeek> normalized = value ?? [];
                if (CurrentTrigger.Days.SetEquals(normalized))
                {
                    return;
                }

                CurrentTrigger = CurrentTrigger with { Days = normalized };
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public string ScheduleTimeZoneId
        {
            get => CurrentTrigger.TimeZoneId;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? TimeZoneInfo.Local.Id : value.Trim();
                if (string.Equals(CurrentTrigger.TimeZoneId, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                CurrentTrigger = CurrentTrigger with { TimeZoneId = normalized };
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public string TriggerNetworkName
        {
            get => CurrentTrigger.NetworkName;
            set
            {
                string normalized = _isDeserializing || TriggerKind == RoutineTriggerKind.Network
                    ? value?.Trim() ?? string.Empty
                    : string.Empty;
                if (!SetTriggerField(CurrentTrigger.NetworkName, newValue => CurrentTrigger = CurrentTrigger with { NetworkName = newValue }, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasNetworkTrigger));
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public NetworkTriggerDirection NetworkTriggerDirection
        {
            get => CurrentTrigger.NetworkDirection;
            set
            {
                NetworkTriggerDirection normalized = _isDeserializing || TriggerKind == RoutineTriggerKind.Network
                    ? value
                    : NetworkTriggerDirection.Connect;
                if (!SetTriggerField(CurrentTrigger.NetworkDirection, newValue => CurrentTrigger = CurrentTrigger with { NetworkDirection = newValue }, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public string LastRunStatusText => BuildLastRunStatusText(DateTimeOffset.UtcNow);

        [JsonIgnore]
        public bool HasLastRunBadge => Enabled && LastRunState != RoutineLastRunState.Never;

        [JsonIgnore]
        public string LastRunBadgeText => LastRunState switch
        {
            RoutineLastRunState.Succeeded => "Ran",
            RoutineLastRunState.Failed => "Failed",
            RoutineLastRunState.WaitingForApp => "Waiting",
            RoutineLastRunState.Skipped => "Skipped",
            _ => string.Empty,
        };

        [JsonIgnore]
        public string ScheduleTimeZoneDisplay
        {
            get
            {
                if (!HasScheduledTrigger)
                {
                    return string.Empty;
                }

                try
                {
                    return TimeZoneDisplayFormatter.FormatCompact(
                        TimeZoneInfo.FindSystemTimeZoneById(ScheduleTimeZoneId));
                }
                catch (TimeZoneNotFoundException)
                {
                    return TimeZoneDisplayFormatter.FormatCompact(TimeZoneInfo.Local);
                }
                catch (InvalidTimeZoneException)
                {
                    return TimeZoneDisplayFormatter.FormatCompact(TimeZoneInfo.Local);
                }
            }
        }

        [JsonIgnore]
        public string ScheduleTimeZoneDetails
        {
            get
            {
                if (!HasScheduledTrigger)
                {
                    return string.Empty;
                }

                try
                {
                    return TimeZoneDisplayFormatter.FormatDetails(
                        TimeZoneInfo.FindSystemTimeZoneById(ScheduleTimeZoneId));
                }
                catch (TimeZoneNotFoundException)
                {
                    return TimeZoneDisplayFormatter.FormatDetails(TimeZoneInfo.Local);
                }
                catch (InvalidTimeZoneException)
                {
                    return TimeZoneDisplayFormatter.FormatDetails(TimeZoneInfo.Local);
                }
            }
        }

        [JsonIgnore]
        public string TriggerSummary
            => BuildTriggerSummary(includeRoutineOptions: true);

        [JsonIgnore]
        public string RoutineDetailsTriggerSummary
            => BuildTriggerSummary(includeRoutineOptions: false);

        [JsonIgnore]
        public bool HasRoutineDetailsOptions => HasApplicationAudioOnlyOption || HasRestorePreviousAudioOption;

        [JsonIgnore]
        public string RoutineDetailsOptionsSummary
        {
            get
            {
                var options = new List<string>();

                if (HasApplicationAudioOnlyOption)
                {
                    options.Add("Application audio only");
                }

                if (HasRestorePreviousAudioOption)
                {
                    options.Add("Restore previous audio on deactivate");
                }

                return string.Join(" | ", options);
            }
        }

        [JsonIgnore]
        private bool HasApplicationAudioOnlyOption => SwitchOutputPerApp && (HasOutputTarget || HasInputTarget);

        [JsonIgnore]
        private bool HasRestorePreviousAudioOption => RestorePreviousAudioOnDeactivate && HasStatefulTriggers;

        private string BuildTriggerSummary(bool includeRoutineOptions)
        {
            var triggers = new List<string>();

            if (HasApplicationTrigger)
            {
                string modeText = ApplicationTriggerMode switch
                {
                    ApplicationTriggerMode.AppLaunch => "launch",
                    ApplicationTriggerMode.ProcessFocus => "focus",
                    _ => ""
                };
                triggers.Add($"Application {modeText}: {RoutineTriggerPathHelper.GetTriggerDisplayName(TriggerAppPath)}");

                if (ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus && !string.IsNullOrWhiteSpace(ApplicationTriggerTitlePattern))
                {
                    string matchModeText = ApplicationTriggerTitleMatchMode switch
                    {
                        ApplicationTriggerTitleMatchMode.Exact => "exact",
                        ApplicationTriggerTitleMatchMode.Contains => "contains",
                        ApplicationTriggerTitleMatchMode.Wildcard => "wildcard",
                        ApplicationTriggerTitleMatchMode.Regex => "regex",
                        _ => ""
                    };
                    triggers.Add($"Title ({matchModeText}): {ApplicationTriggerTitlePattern}");
                }

                if (includeRoutineOptions && SwitchOutputPerApp && (HasOutputTarget || HasInputTarget))
                {
                    triggers.Add("Application audio only");
                }
            }
            else if (HasAudioPilotStartupTrigger)
            {
                triggers.Add("AudioPilot startup");
            }
            else if (HasSteamBigPictureTrigger)
            {
                triggers.Add("Steam Big Picture");
            }
            else if (TriggerKind == RoutineTriggerKind.DeviceAvailability)
            {
                string transition = DeviceTransition switch { DeviceAvailabilityTransition.Connected => "available", DeviceAvailabilityTransition.Disconnected => "unavailable", _ => "availability changes" };
                triggers.Add($"Device {transition}: {TriggerDevice?.DisplayName ?? "not selected"}");
            }
            else if (TriggerKind is RoutineTriggerKind.SessionUnlock or RoutineTriggerKind.SystemResume)
            {
                triggers.Add(TriggerKind == RoutineTriggerKind.SessionUnlock ? "Windows unlock" : "System resume");
            }
            else if (HasDeviceChangeTrigger)
            {
                triggers.Add("Device change");
            }
            else if (HasScheduledTrigger)
            {
                string dayText = ScheduleDays.Count > 0
                    ? $" {string.Join(", ", ScheduleDays.OrderBy(static day => (int)day))}"
                    : "";
                string timeFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
                string schedule = $"Scheduled: {ScheduleTime.ToString(timeFormat)}{dayText}";
                if (!includeRoutineOptions)
                    schedule += $" [{ScheduleTimeZoneId}]" + (NotifyBeforeScheduledRun ? " (reminder)" : string.Empty);
                triggers.Add(schedule);
            }
            else if (HasNetworkTrigger)
            {
                string directionText = NetworkTriggerDirection switch
                {
                    NetworkTriggerDirection.Connect => "Connect",
                    NetworkTriggerDirection.Disconnect => "Disconnect",
                    NetworkTriggerDirection.Both => "Connect/disconnect",
                    _ => string.Empty,
                };

                if (string.IsNullOrWhiteSpace(TriggerNetworkName))
                {
                    triggers.Add("Network: All networks disconnected");
                }
                else
                {
                    triggers.Add($"Network: {directionText} — {TriggerNetworkName}");
                }
            }
            foreach (RoutineTrigger extra in Triggers.Skip(1))
            {
                if (extra != null) triggers.Add(triggers.Count == 0 ? extra.Summary : $"OR {extra.Summary}");
            }
            if (!string.IsNullOrWhiteSpace(Hotkey))
            {
                triggers.Add(string.IsNullOrEmpty(HotkeyCycleGroup) ? $"Hotkey: {Hotkey}" : $"Cycle group: {HotkeyCycleGroup} ({Hotkey})");
            }

            if (includeRoutineOptions && RestorePreviousAudioOnDeactivate && HasStatefulTriggers)
            {
                triggers.Add("Restore on exit");
            }

            if (HasTrayMenuTrigger)
            {
                triggers.Add("Tray menu");
            }

            return triggers.Count > 0
                ? string.Join(" | ", triggers)
                : "No triggers configured";
        }

        [JsonIgnore]
        public string TargetKindBadgeText => HasCommunicationsTarget ? "Devices" : (HasOutputTarget, HasInputTarget, HasVolumeTarget || HasMuteTarget) switch
        {
            (true, true, _) => "Out + In",
            (true, false, _) => "Out",
            (false, true, _) => "In",
            (false, false, true) => HasVolumeTarget ? "Vol" : "Mute",
            _ => string.Empty,
        };

        [JsonIgnore]
        public string TargetSummary => BuildTargetSummary(" | ");

        [JsonIgnore]
        public string RoutineDetailsTargetSummary => BuildTargetSummary(Environment.NewLine);

        private string BuildTargetSummary(string separator)
        {
            var parts = new List<string>();
            if (SwitchOutputPerApp) parts.Add($"App: {RoutineTriggerPathHelper.GetTriggerDisplayName(TargetAppPath)}");

            if (HasOutputTarget)
            {
                parts.Add($"Output: {OutputDeviceName}");
            }

            if (HasInputTarget)
            {
                parts.Add($"Input: {InputDeviceName}");
            }

            if (CommunicationsOutput != null) parts.Add($"Communications output: {CommunicationsOutput.Name}");
            if (CommunicationsInput != null) parts.Add($"Communications microphone: {CommunicationsInput.Name}");

            if (HasMasterVolumeTarget)
            {
                parts.Add($"Master: {MasterVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
            }

            if (HasMicVolumeTarget)
            {
                parts.Add($"Microphone: {MicVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
            }

            if (OutputMuteAction != RoutineMuteAction.Unchanged) parts.Add($"Output: {OutputMuteAction}");
            if (InputMuteAction != RoutineMuteAction.Unchanged) parts.Add($"Microphone: {InputMuteAction}");
            return parts.Count > 0
                ? string.Join(separator, parts)
                : string.Empty;
        }

        public AudioRoutine Clone(bool includeTriggers = true)
        {
            return new AudioRoutine
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                OutputDeviceId = OutputDeviceId,
                CommunicationsOutput = CommunicationsOutput,
                CommunicationsInput = CommunicationsInput,
                OutputDeviceStableId = OutputDeviceStableId,
                OutputDeviceName = OutputDeviceName,
                InputDeviceId = InputDeviceId,
                InputDeviceStableId = InputDeviceStableId,
                InputDeviceName = InputDeviceName,
                MasterVolumePercent = MasterVolumePercent,
                MicVolumePercent = MicVolumePercent,
                OutputMuteAction = OutputMuteAction,
                InputMuteAction = InputMuteAction,
                Hotkey = Hotkey,
                HotkeyCycleGroup = HotkeyCycleGroup,
                Triggers = includeTriggers ? [.. Triggers.Select(static trigger => trigger?.Copy()!)] : [],
                TriggerIdentity = TriggerIdentity,
                Conditions = Conditions,
                SwitchOutputPerApp = SwitchOutputPerApp,
                TargetAppPath = TargetAppPath,
                ShowInTrayMenu = ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                DisplayOrder = DisplayOrder,
                HotkeyWarningKind = HotkeyWarningKind,
                HotkeyWarningSummary = HotkeyWarningSummary,
            };
        }

        public void RefreshLastRunStatusText()
        {
            OnPropertyChanged(nameof(LastRunStatusText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == nameof(TargetSummary))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RoutineDetailsTargetSummary)));
        }

        private void OnTriggerSummariesChanged()
        {
            OnPropertyChanged(nameof(TriggerSummary));
            OnPropertyChanged(nameof(RoutineDetailsTriggerSummary));
            OnRoutineDetailsOptionsChanged();
        }

        private void OnRoutineDetailsOptionsChanged()
        {
            OnPropertyChanged(nameof(HasRoutineDetailsOptions));
            OnPropertyChanged(nameof(RoutineDetailsOptionsSummary));
        }

        private string BuildLastRunStatusText(DateTimeOffset now)
        {
            return LastRunState switch
            {
                RoutineLastRunState.WaitingForApp => "Last run: Waiting for app audio",
                RoutineLastRunState.Never => "Last run: Never",
                _ => BuildCompletedLastRunStatusText(now),
            };
        }

        private string BuildCompletedLastRunStatusText(DateTimeOffset now)
        {
            string relativeTime = LastRunUtc.HasValue
                ? FormatRelativeTime(now - LastRunUtc.Value)
                : "recently";
            string statusSuffix = LastRunState switch
            {
                RoutineLastRunState.Succeeded => string.Empty,
                RoutineLastRunState.Failed => "Failed",
                RoutineLastRunState.Skipped => string.IsNullOrWhiteSpace(LastRunDetail) ? "Skipped" : LastRunDetail,
                _ => string.IsNullOrWhiteSpace(LastRunDetail) ? string.Empty : LastRunDetail,
            };

            return string.IsNullOrWhiteSpace(statusSuffix)
                ? $"Last run: {relativeTime}"
                : $"Last run: {relativeTime}, {statusSuffix}";
        }

        private static string FormatRelativeTime(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed < TimeSpan.FromSeconds(5))
            {
                return "just now";
            }

            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s ago";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m ago";
            }

            if (elapsed < TimeSpan.FromDays(1))
            {
                return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalHours)).ToString(CultureInfo.InvariantCulture)}h ago";
            }

            return $"{Math.Max(1, (int)Math.Floor(elapsed.TotalDays)).ToString(CultureInfo.InvariantCulture)}d ago";
        }
    }
}
