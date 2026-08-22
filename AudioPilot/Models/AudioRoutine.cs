using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AudioPilot.Helpers;
using AudioPilot.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AudioPilot.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RoutineTriggerKind
    {
        Hotkey,
        Application,
        AudioPilotStartup,
        SteamBigPicture,
        DeviceChange,
        Scheduled,
        Network,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ApplicationTriggerMode
    {
        AppLaunch,
        ProcessFocus,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ApplicationTriggerTitleMatchMode
    {
        Exact,
        Contains,
        Wildcard,
        Regex,
    }

    [JsonConverter(typeof(StringEnumConverter))]
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

    public sealed class AudioRoutine : INotifyPropertyChanged
    {
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
        private RoutineTriggerKind _triggerKind;
        private string _triggerAppPath = string.Empty;
        private bool _switchOutputPerApp;
        private bool _showInTrayMenu;
        private bool _restorePreviousAudioOnDeactivate;
        private bool _enforceTargetsOnDeviceChange;
        private bool _hasConflict;
        private string _conflictSummary = string.Empty;
        private HotkeyWarningKind _hotkeyWarningKind;
        private string _hotkeyWarningSummary = string.Empty;
        private DateTimeOffset? _lastRunUtc;
        private RoutineLastRunState _lastRunState;
        private string _lastRunDetail = string.Empty;
        private TimeOnly _scheduleTime = new(12, 0);
        private HashSet<DayOfWeek> _scheduleDays = [];
        private string _scheduleTimeZoneId = TimeZoneInfo.Local.Id;
        private string _triggerNetworkName = string.Empty;
        private NetworkTriggerDirection _networkTriggerDirection = NetworkTriggerDirection.Connect;
        private ApplicationTriggerMode _applicationTriggerMode = ApplicationTriggerMode.AppLaunch;
        private string _applicationTriggerTitlePattern = string.Empty;
        private ApplicationTriggerTitleMatchMode _applicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;

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

        public RoutineTriggerKind TriggerKind
        {
            get => _triggerKind;
            set
            {
                if (!SetField(ref _triggerKind, value))
                {
                    return;
                }

                NormalizeTriggerDependentState();

                if (_triggerKind is not RoutineTriggerKind.Application and not RoutineTriggerKind.SteamBigPicture)
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

        private void NormalizeTriggerDependentState()
        {
            if (_triggerKind != RoutineTriggerKind.Application && !string.IsNullOrWhiteSpace(_triggerAppPath))
            {
                _triggerAppPath = string.Empty;
                OnPropertyChanged(nameof(TriggerAppPath));
            }

            if (_triggerKind != RoutineTriggerKind.Application && _switchOutputPerApp)
            {
                _switchOutputPerApp = false;
                OnPropertyChanged(nameof(SwitchOutputPerApp));
            }

            if (_triggerKind != RoutineTriggerKind.Application)
            {
                if (_applicationTriggerMode != ApplicationTriggerMode.AppLaunch)
                {
                    _applicationTriggerMode = ApplicationTriggerMode.AppLaunch;
                    OnPropertyChanged(nameof(ApplicationTriggerMode));
                }

                if (!string.IsNullOrWhiteSpace(_applicationTriggerTitlePattern))
                {
                    _applicationTriggerTitlePattern = string.Empty;
                    OnPropertyChanged(nameof(ApplicationTriggerTitlePattern));
                }

                if (_applicationTriggerTitleMatchMode != ApplicationTriggerTitleMatchMode.Contains)
                {
                    _applicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
                    OnPropertyChanged(nameof(ApplicationTriggerTitleMatchMode));
                }
            }

            if (_triggerKind != RoutineTriggerKind.DeviceChange && _enforceTargetsOnDeviceChange)
            {
                _enforceTargetsOnDeviceChange = false;
                OnPropertyChanged(nameof(EnforceTargetsOnDeviceChange));
            }

            if (_triggerKind != RoutineTriggerKind.Scheduled)
            {
                if (_scheduleDays.Count > 0)
                {
                    _scheduleDays.Clear();
                    OnPropertyChanged(nameof(ScheduleDays));
                }

                if (_scheduleTime != new TimeOnly(12, 0))
                {
                    _scheduleTime = new TimeOnly(12, 0);
                    OnPropertyChanged(nameof(ScheduleTime));
                }
            }

            if (_triggerKind != RoutineTriggerKind.Network && !string.IsNullOrWhiteSpace(_triggerNetworkName))
            {
                _triggerNetworkName = string.Empty;
                OnPropertyChanged(nameof(TriggerNetworkName));
            }

            if (_triggerKind != RoutineTriggerKind.Hotkey)
            {
                if (_showInTrayMenu)
                {
                    _showInTrayMenu = false;
                    OnPropertyChanged(nameof(ShowInTrayMenu));
                    OnPropertyChanged(nameof(HasTrayMenuTrigger));
                }

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

        public string TriggerAppPath
        {
            get => _triggerAppPath;
            set
            {
                string normalized = TriggerKind == RoutineTriggerKind.Application
                    ? RoutineTriggerPathHelper.NormalizeTriggerTarget(value)
                    : string.Empty;
                if (!SetField(ref _triggerAppPath, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public bool SwitchOutputPerApp
        {
            get => _switchOutputPerApp;
            set
            {
                bool normalized = value && TriggerKind == RoutineTriggerKind.Application;
                if (!SetField(ref _switchOutputPerApp, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public ApplicationTriggerMode ApplicationTriggerMode
        {
            get => _applicationTriggerMode;
            set
            {
                if (!SetField(ref _applicationTriggerMode, value))
                {
                    return;
                }

                if (_triggerKind == RoutineTriggerKind.Application && value != ApplicationTriggerMode.ProcessFocus)
                {
                    if (!string.IsNullOrWhiteSpace(_applicationTriggerTitlePattern))
                    {
                        _applicationTriggerTitlePattern = string.Empty;
                        OnPropertyChanged(nameof(ApplicationTriggerTitlePattern));
                    }

                    if (_applicationTriggerTitleMatchMode != ApplicationTriggerTitleMatchMode.Contains)
                    {
                        _applicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
                        OnPropertyChanged(nameof(ApplicationTriggerTitleMatchMode));
                    }
                }

                OnPropertyChanged(nameof(IsProcessFocusMode));
                OnTriggerSummariesChanged();
            }
        }

        [JsonIgnore]
        public bool IsProcessFocusMode => ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus;

        public string ApplicationTriggerTitlePattern
        {
            get => _applicationTriggerTitlePattern;
            set
            {
                string normalized = _triggerKind == RoutineTriggerKind.Application && _applicationTriggerMode == ApplicationTriggerMode.ProcessFocus
                    ? value?.Trim() ?? string.Empty
                    : string.Empty;
                if (!SetField(ref _applicationTriggerTitlePattern, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public ApplicationTriggerTitleMatchMode ApplicationTriggerTitleMatchMode
        {
            get => _applicationTriggerTitleMatchMode;
            set
            {
                ApplicationTriggerTitleMatchMode normalized = _triggerKind == RoutineTriggerKind.Application && _applicationTriggerMode == ApplicationTriggerMode.ProcessFocus
                    ? value
                    : ApplicationTriggerTitleMatchMode.Contains;
                if (!SetField(ref _applicationTriggerTitleMatchMode, normalized))
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
                bool normalized = value && IsStatefulTrigger;
                if (!SetField(ref _restorePreviousAudioOnDeactivate, normalized))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public bool EnforceTargetsOnDeviceChange
        {
            get => _enforceTargetsOnDeviceChange;
            set
            {
                bool normalized = value;
                if (!SetField(ref _enforceTargetsOnDeviceChange, normalized))
                {
                    return;
                }
            }
        }

        public bool ShowInTrayMenu
        {
            get => _showInTrayMenu;
            set
            {
                bool normalized = TriggerKind == RoutineTriggerKind.Hotkey && value;
                if (!SetField(ref _showInTrayMenu, normalized))
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
        public bool HasExecutionTarget => HasOutputTarget || HasInputTarget || HasVolumeTarget;

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

        public TimeOnly ScheduleTime
        {
            get => _scheduleTime;
            set
            {
                if (!SetField(ref _scheduleTime, value))
                {
                    return;
                }

                OnTriggerSummariesChanged();
            }
        }

        public HashSet<DayOfWeek> ScheduleDays
        {
            get => _scheduleDays;
            set
            {
                HashSet<DayOfWeek> normalized = value ?? [];
                if (_scheduleDays.SetEquals(normalized))
                {
                    return;
                }

                _scheduleDays = normalized;
                OnTriggerSummariesChanged();
            }
        }

        public string ScheduleTimeZoneId
        {
            get => _scheduleTimeZoneId;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? TimeZoneInfo.Local.Id : value.Trim();
                if (string.Equals(_scheduleTimeZoneId, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _scheduleTimeZoneId = normalized;
                OnTriggerSummariesChanged();
            }
        }

        public string TriggerNetworkName
        {
            get => _triggerNetworkName;
            set
            {
                string normalized = TriggerKind == RoutineTriggerKind.Network
                    ? value?.Trim() ?? string.Empty
                    : string.Empty;
                if (!SetField(ref _triggerNetworkName, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasNetworkTrigger));
                OnTriggerSummariesChanged();
            }
        }

        public NetworkTriggerDirection NetworkTriggerDirection
        {
            get => _networkTriggerDirection;
            set
            {
                NetworkTriggerDirection normalized = TriggerKind == RoutineTriggerKind.Network
                    ? value
                    : NetworkTriggerDirection.Connect;
                if (!SetField(ref _networkTriggerDirection, normalized))
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
        private bool HasRestorePreviousAudioOption => RestorePreviousAudioOnDeactivate && IsStatefulTrigger;

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
                triggers.Add($"Scheduled: {ScheduleTime.ToString(timeFormat)}{dayText}");
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

                if (NetworkTriggerDirection == NetworkTriggerDirection.Disconnect)
                {
                    triggers.Add($"Network: {directionText}");
                }
                else
                {
                    triggers.Add($"Network: {directionText} to {TriggerNetworkName}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(Hotkey))
            {
                triggers.Add($"Hotkey: {Hotkey}");
            }

            if (includeRoutineOptions && RestorePreviousAudioOnDeactivate && IsStatefulTrigger)
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
        public string TargetKindBadgeText => (HasOutputTarget, HasInputTarget, HasVolumeTarget) switch
        {
            (true, true, _) => "Out + In",
            (true, false, _) => "Out",
            (false, true, _) => "In",
            (false, false, true) => "Vol",
            _ => string.Empty,
        };

        [JsonIgnore]
        public string TargetSummary
        {
            get
            {
                var parts = new List<string>();

                if (HasOutputTarget)
                {
                    parts.Add($"Output: {OutputDeviceName}");
                }

                if (HasInputTarget)
                {
                    parts.Add($"Input: {InputDeviceName}");
                }

                if (HasMasterVolumeTarget)
                {
                    parts.Add($"Master: {MasterVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
                }

                if (HasMicVolumeTarget)
                {
                    parts.Add($"Microphone: {MicVolumePercent.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}%");
                }

                return parts.Count > 0
                    ? string.Join(" | ", parts)
                    : string.Empty;
            }
        }

        public AudioRoutine Clone()
        {
            return new AudioRoutine
            {
                Id = Id,
                Name = Name,
                Enabled = Enabled,
                OutputDeviceId = OutputDeviceId,
                OutputDeviceName = OutputDeviceName,
                InputDeviceId = InputDeviceId,
                InputDeviceName = InputDeviceName,
                MasterVolumePercent = MasterVolumePercent,
                MicVolumePercent = MicVolumePercent,
                Hotkey = Hotkey,
                TriggerKind = TriggerKind,
                TriggerAppPath = TriggerAppPath,
                SwitchOutputPerApp = SwitchOutputPerApp,
                ApplicationTriggerMode = ApplicationTriggerMode,
                ApplicationTriggerTitlePattern = ApplicationTriggerTitlePattern,
                ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode,
                ShowInTrayMenu = ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate,
                EnforceTargetsOnDeviceChange = EnforceTargetsOnDeviceChange,
                DisplayOrder = DisplayOrder,
                HotkeyWarningKind = HotkeyWarningKind,
                HotkeyWarningSummary = HotkeyWarningSummary,
                ScheduleTime = ScheduleTime,
                ScheduleDays = [.. ScheduleDays],
                ScheduleTimeZoneId = ScheduleTimeZoneId,
                TriggerNetworkName = TriggerNetworkName,
                NetworkTriggerDirection = NetworkTriggerDirection,
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
