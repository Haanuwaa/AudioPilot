using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public enum DaySelectionPreset
{
    EveryDay,
    Weekdays,
    Weekends,
}

internal sealed class RoutineTriggerDraftViewModel : INotifyPropertyChanged
{
    internal static readonly IReadOnlyList<string> TriggerModeLabels =
    [
        "Manual",
        "Application",
        "AudioPilot startup",
        "Device change",
        "Scheduled",
        "Network",
        "Steam Big Picture",
        "Device availability",
        "Windows unlock",
        "System resume",
    ];

    internal static readonly IReadOnlyList<string> AutomaticTriggerModeLabels = [.. TriggerModeLabels.Skip(1)];

    internal static readonly List<string> ApplicationTriggerModeLabels =
    [
        "When application launches",
        "When application window is focused",
        "When application exits",
    ];

    internal static readonly List<string> ApplicationTriggerTitleMatchModeLabels =
    [
        "Exact match (e.g., 'My App Window')",
        "Contains (e.g., 'Chrome')",
        "Wildcard (e.g., '* - Google Chrome')",
        "Regex (e.g., '.*Chrome.*')",
    ];

    private static readonly DayOfWeek[] s_allDays =
    [
        DayOfWeek.Sunday,
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
    ];

    private readonly RoutineEditorViewModel _owner;
    private readonly bool _isNewTrigger;
    private string _selectedTriggerMode = TriggerModeLabels[0];
    private string _triggerAppPath = string.Empty;
    private ApplicationTriggerMode _applicationTriggerMode = ApplicationTriggerMode.AppLaunch;
    private string _applicationTriggerTitlePattern = string.Empty;
    private ApplicationTriggerTitleMatchMode _applicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
    private TimeOnly _scheduleTime = new(12, 0);
    private int _scheduleHour = 12;
    private int _scheduleMinute;
    private string _scheduleAmPm = "PM";
    private HashSet<DayOfWeek> _scheduleDays = [];
    private string _scheduleTimeZoneId;
    private bool _notifyBeforeScheduledRun;
    private string _triggerNetworkName = string.Empty;
    private string? _selectedAvailableNetworkName;
    private NetworkTriggerDirection _networkTriggerDirection = NetworkTriggerDirection.Connect;
    private RoutineDeviceReference? _selectedAvailabilityDevice;
    private DeviceAvailabilityTransition _deviceTransition = DeviceAvailabilityTransition.Connected;
    private bool _updatingNetworkChoices;
    private bool _updatingDeviceChoices;
    private LocalizedText? _errorText;

    internal RoutineTriggerDraftViewModel(
        RoutineEditorViewModel owner,
        string scheduleTimeZoneId,
        bool isNewTrigger = false)
    {
        _owner = owner;
        _isNewTrigger = isNewTrigger;
        _scheduleTimeZoneId = string.IsNullOrWhiteSpace(scheduleTimeZoneId) ? TimeZoneInfo.Local.Id : scheduleTimeZoneId;
        UpdateScheduleComponentsFromTime(notify: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsNewTrigger => _isNewTrigger;
    public string TriggerEditorHeading => _isNewTrigger ? "New trigger" : "Edit trigger";
    public string TriggerEditorConfirmText => _isNewTrigger ? "Add trigger" : "Save trigger";

    public static IReadOnlyList<string> AvailableTriggerModes => AutomaticTriggerModeLabels;
    public static IReadOnlyList<string> ApplicationTriggerModeOptions => ApplicationTriggerModeLabels;
    public static IReadOnlyList<string> ApplicationTriggerTitleMatchModeOptions => ApplicationTriggerTitleMatchModeLabels;
    public static IReadOnlyList<DeviceAvailabilityTransition> DeviceTransitions { get; } = Enum.GetValues<DeviceAvailabilityTransition>();

    public static IReadOnlyList<KeyValuePair<NetworkTriggerDirection, string>> NetworkTriggerDirectionOptions =>
    [
        new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Connect, "Connect to specific network"),
        new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Disconnect, "Disconnect from network"),
        new KeyValuePair<NetworkTriggerDirection, string>(NetworkTriggerDirection.Both, "Both connect and disconnect"),
    ];

    public IReadOnlyList<string> HourOptions => _owner.HourOptions;
    public IReadOnlyList<string> MinuteOptions => _owner.MinuteOptions;
    public IReadOnlyList<string> AmPmOptions => _owner.AmPmOptions;
    public bool Is24HourFormat => _owner.Is24HourFormat;
    public ObservableCollection<string> AvailableNetworkNames => _owner.AvailableNetworkNames;
    public bool IsScanningNetworks => _owner.IsScanningNetworks;
    public string NetworkDiscoveryWarning => _owner.NetworkDiscoveryWarning;
    public ICommand RefreshNetworksCommand => _owner.RefreshNetworksCommand;
    public IReadOnlyList<RoutineDeviceReference> RoutineDeviceChoices => _owner.RoutineDeviceChoices;

    public string? TriggerEditorError => _errorText is { } message ? LocalizationService.English.Format(message) : null;
    public LocalizedText? TriggerEditorErrorText => _errorText;

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
            OnPropertyChanged(nameof(IsDeviceChangeTriggerSelected));
            OnPropertyChanged(nameof(IsSteamBigPictureTriggerSelected));
            OnPropertyChanged(nameof(IsDeviceAvailabilityTriggerSelected));
            OnPropertyChanged(nameof(IsSystemEventTriggerSelected));
            OnPropertyChanged(nameof(SystemEventTriggerDescription));
            OnPropertyChanged(nameof(IsScheduledTriggerSelected));
            OnPropertyChanged(nameof(IsNetworkTriggerSelected));
            OnPropertyChanged(nameof(IsStateful));
            OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
            OnPropertyChanged(nameof(ResolvedTriggerAppTargetDisplayName));
            OnPropertyChanged(nameof(ShowApplicationTriggerPathInput));
            OnPropertyChanged(nameof(ApplicationTriggerLifetimeDescription));
            OnPropertyChanged(nameof(IsAppLaunchModeSelected));
            OnPropertyChanged(nameof(IsProcessFocusModeSelected));
            OnPropertyChanged(nameof(ShowApplicationTriggerTitlePatternInput));

            if (IsNetworkTriggerSelected)
            {
                _owner.EnsureNetworkListForTrigger(TriggerNetworkName);
            }
        }
    }

    public bool IsHotkeyTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[0], StringComparison.Ordinal);
    public bool IsApplicationTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[1], StringComparison.Ordinal);
    public bool IsAudioPilotStartupTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[2], StringComparison.Ordinal);
    public bool IsDeviceChangeTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[3], StringComparison.Ordinal);
    public bool IsScheduledTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[4], StringComparison.Ordinal);
    public bool IsNetworkTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[5], StringComparison.Ordinal);
    public bool IsSteamBigPictureTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[6], StringComparison.Ordinal);
    public bool IsDeviceAvailabilityTriggerSelected => string.Equals(SelectedTriggerMode, TriggerModeLabels[7], StringComparison.Ordinal);
    public bool IsSystemEventTriggerSelected => SelectedTriggerMode == TriggerModeLabels[8] || SelectedTriggerMode == TriggerModeLabels[9];

    public string SystemEventTriggerDescription => SelectedTriggerMode == TriggerModeLabels[8]
        ? "Runs when you unlock Windows. Unlock and resume in the same wake cycle run this routine once."
        : "Runs after Windows resumes and audio recovery finishes. Unavailable targets are given time to reconnect. This does not wake the computer.";

    public bool IsAppLaunchModeSelected => IsApplicationTriggerSelected && _applicationTriggerMode == ApplicationTriggerMode.AppLaunch;
    public bool IsProcessFocusModeSelected => IsApplicationTriggerSelected && _applicationTriggerMode == ApplicationTriggerMode.ProcessFocus;
    public bool IsStateful => (IsApplicationTriggerSelected && _applicationTriggerMode != ApplicationTriggerMode.AppExit) || IsSteamBigPictureTriggerSelected;
    public bool ShowApplicationTriggerPathInput => IsApplicationTriggerSelected;
    public bool ShowApplicationTriggerTitlePatternInput => IsProcessFocusModeSelected;

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
            OnPropertyChanged(nameof(ResolvedTriggerAppTargetDisplayName));
        }
    }

    public string ResolvedTriggerAppTargetDisplayName => IsApplicationTriggerSelected ? _owner.ResolveApplicationDisplayName(TriggerAppPath) : string.Empty;
    public bool HasResolvedTriggerAppTarget => !string.IsNullOrWhiteSpace(ResolvedTriggerAppTargetDisplayName);

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
            OnPropertyChanged(nameof(IsStateful));
            OnPropertyChanged(nameof(ShowApplicationTriggerPathInput));
            OnPropertyChanged(nameof(ShowApplicationTriggerTitlePatternInput));
        }
    }

    public ApplicationTriggerMode ApplicationTriggerMode => _applicationTriggerMode;

    public string ApplicationTriggerLifetimeDescription => _applicationTriggerMode switch
    {
        ApplicationTriggerMode.ProcessFocus => "Active while the application has focus.",
        ApplicationTriggerMode.AppExit => "Runs once after the last matching instance exits. Applications already absent at startup do not trigger it.",
        _ => "Active until the application exits.",
    };

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

            _applicationTriggerTitlePattern = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public RoutineDeviceReference? SelectedAvailabilityDevice
    {
        get => _selectedAvailabilityDevice;
        set
        {
            if (_updatingDeviceChoices || _selectedAvailabilityDevice == value)
            {
                return;
            }

            _selectedAvailabilityDevice = value;
            OnPropertyChanged();
        }
    }

    public DeviceAvailabilityTransition DeviceTransition
    {
        get => _deviceTransition;
        set
        {
            if (_deviceTransition == value)
            {
                return;
            }

            _deviceTransition = value;
            OnPropertyChanged();
        }
    }

    public string ScheduleTimeZoneId
    {
        get => _scheduleTimeZoneId;
        internal set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? TimeZoneInfo.Local.Id : value;
            if (string.Equals(_scheduleTimeZoneId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _scheduleTimeZoneId = normalized;
            OnPropertyChanged(nameof(ScheduleTimeZoneDisplayName));
            OnPropertyChanged(nameof(ScheduleTimeZoneDetails));
        }
    }

    public string ScheduleTimeZoneDisplayName => TimeZoneDisplayFormatter.FormatRoutineZone(_scheduleTimeZoneId);
    public string ScheduleTimeZoneDetails => TimeZoneDisplayFormatter.FormatRoutineZone(_scheduleTimeZoneId, details: true);

    public bool NotifyBeforeScheduledRun
    {
        get => _notifyBeforeScheduledRun;
        set
        {
            if (_notifyBeforeScheduledRun == value)
            {
                return;
            }

            _notifyBeforeScheduledRun = value;
            OnPropertyChanged();
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
            UpdateScheduleComponentsFromTime(notify: true);
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
        get => Is24HourFormat ? _scheduleHour : (_scheduleHour == 12 ? 0 : (_scheduleHour == 0 ? 0 : _scheduleHour));
        set
        {
            int maxExclusive = Is24HourFormat ? 24 : 12;
            if (ScheduleHourIndex == value || value < 0 || value >= maxExclusive)
            {
                return;
            }

            _scheduleHour = Is24HourFormat ? value : (value == 0 ? 12 : value);
            UpdateScheduleTimeFromComponents();
            OnPropertyChanged();
        }
    }

    public int ScheduleMinuteIndex
    {
        get => _scheduleMinute;
        set
        {
            if (_scheduleMinute == value || value < 0 || value > 59)
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
            if (ScheduleAmPmIndex == value || value < 0 || value > 1)
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

    public bool IsDailySchedule => _scheduleDays.Count == 0;
    public bool IsSundaySelected { get => _scheduleDays.Contains(DayOfWeek.Sunday); set => SetDaySelected(DayOfWeek.Sunday, value); }
    public bool IsMondaySelected { get => _scheduleDays.Contains(DayOfWeek.Monday); set => SetDaySelected(DayOfWeek.Monday, value); }
    public bool IsTuesdaySelected { get => _scheduleDays.Contains(DayOfWeek.Tuesday); set => SetDaySelected(DayOfWeek.Tuesday, value); }
    public bool IsWednesdaySelected { get => _scheduleDays.Contains(DayOfWeek.Wednesday); set => SetDaySelected(DayOfWeek.Wednesday, value); }
    public bool IsThursdaySelected { get => _scheduleDays.Contains(DayOfWeek.Thursday); set => SetDaySelected(DayOfWeek.Thursday, value); }
    public bool IsFridaySelected { get => _scheduleDays.Contains(DayOfWeek.Friday); set => SetDaySelected(DayOfWeek.Friday, value); }
    public bool IsSaturdaySelected { get => _scheduleDays.Contains(DayOfWeek.Saturday); set => SetDaySelected(DayOfWeek.Saturday, value); }

    public void ApplyScheduleDayPreset(DaySelectionPreset preset)
    {
        ScheduleDays = preset switch
        {
            DaySelectionPreset.Weekdays => [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            DaySelectionPreset.Weekends => [DayOfWeek.Saturday, DayOfWeek.Sunday],
            _ => [],
        };
    }

    public string TriggerNetworkName
    {
        get => _triggerNetworkName;
        set
        {
            string normalized = value ?? string.Empty;
            if (_updatingNetworkChoices || _owner.IsUpdatingNetworkChoices || _triggerNetworkName == normalized)
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
            if (_updatingNetworkChoices || _owner.IsUpdatingNetworkChoices || string.Equals(_selectedAvailableNetworkName, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedAvailableNetworkName = value;
            OnPropertyChanged();

            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(_triggerNetworkName, value, StringComparison.Ordinal))
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
                _owner.EnsureNetworkListForTrigger(TriggerNetworkName);
            }
        }
    }

    public RoutineTriggerKind TriggerKind => SelectedTriggerMode == TriggerModeLabels[8] ? RoutineTriggerKind.SessionUnlock
        : SelectedTriggerMode == TriggerModeLabels[9] ? RoutineTriggerKind.SystemResume
        : IsDeviceAvailabilityTriggerSelected ? RoutineTriggerKind.DeviceAvailability
        : IsApplicationTriggerSelected ? RoutineTriggerKind.Application
        : IsAudioPilotStartupTriggerSelected ? RoutineTriggerKind.AudioPilotStartup
        : IsScheduledTriggerSelected ? RoutineTriggerKind.Scheduled
        : IsNetworkTriggerSelected ? RoutineTriggerKind.Network
        : IsDeviceChangeTriggerSelected ? RoutineTriggerKind.DeviceChange
        : IsSteamBigPictureTriggerSelected ? RoutineTriggerKind.SteamBigPicture
        : RoutineTriggerKind.Hotkey;

    internal void Load(RoutineTrigger trigger)
    {
        SelectedTriggerMode = MapKindToLabel(trigger.Kind);
        SelectedAvailabilityDevice = trigger.Device;
        DeviceTransition = trigger.DeviceTransition;
        TriggerAppPath = trigger.AppPath;
        SelectedApplicationTriggerMode = ApplicationTriggerModeLabels[Enum.IsDefined(trigger.ApplicationMode) ? (int)trigger.ApplicationMode : 0];
        SelectedApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchModeLabels[Enum.IsDefined(trigger.TitleMatchMode) ? (int)trigger.TitleMatchMode : 0];
        ApplicationTriggerTitlePattern = trigger.TitlePattern;
        ScheduleTime = trigger.Time;
        ScheduleDays = trigger.Days == null ? [] : [.. trigger.Days];
        ScheduleTimeZoneId = trigger.TimeZoneId;
        NotifyBeforeScheduledRun = trigger.NotifyBeforeRun;
        TriggerNetworkName = trigger.NetworkName;
        NetworkTriggerDirection = trigger.NetworkDirection;
        SetError(null);
    }

    internal RoutineTrigger Capture(string? existingId = null) => new()
    {
        Id = string.IsNullOrWhiteSpace(existingId) ? Guid.NewGuid().ToString("N") : existingId,
        Kind = TriggerKind,
        Device = IsDeviceAvailabilityTriggerSelected ? SelectedAvailabilityDevice : null,
        DeviceTransition = IsDeviceAvailabilityTriggerSelected ? DeviceTransition : DeviceAvailabilityTransition.Connected,
        AppPath = IsApplicationTriggerSelected ? RoutineTriggerPathHelper.NormalizeTriggerTarget(TriggerAppPath) : string.Empty,
        ApplicationMode = IsApplicationTriggerSelected ? _applicationTriggerMode : ApplicationTriggerMode.AppLaunch,
        TitlePattern = IsProcessFocusModeSelected ? _applicationTriggerTitlePattern : string.Empty,
        TitleMatchMode = _applicationTriggerTitleMatchMode,
        Time = IsScheduledTriggerSelected ? ScheduleTime : new TimeOnly(12, 0),
        Days = IsScheduledTriggerSelected ? [.. ScheduleDays] : [],
        TimeZoneId = _scheduleTimeZoneId,
        NotifyBeforeRun = IsScheduledTriggerSelected && NotifyBeforeScheduledRun,
        NetworkName = IsNetworkTriggerSelected ? TriggerNetworkName : string.Empty,
        NetworkDirection = IsNetworkTriggerSelected ? NetworkTriggerDirection : NetworkTriggerDirection.Connect,
    };

    internal LocalizedText? ValidateCurrentFields()
    {
        if (IsApplicationTriggerSelected && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(TriggerAppPath))
        {
            return new("Message_ApplicationTriggerRequiresAFullExePath");
        }

        if (IsProcessFocusModeSelected && !string.IsNullOrWhiteSpace(ApplicationTriggerTitlePattern) && ApplicationTriggerTitleMatchMode == ApplicationTriggerTitleMatchMode.Regex)
        {
            try
            {
                _ = new Regex(ApplicationTriggerTitlePattern);
            }
            catch (ArgumentException)
            {
                return new("Message_TitlePatternRegexIsInvalid");
            }
        }

        if (IsNetworkTriggerSelected && NetworkTriggerDirection != NetworkTriggerDirection.Disconnect && string.IsNullOrWhiteSpace(TriggerNetworkName))
        {
            return new("Message_NetworkTriggerRequiresANetworkNameWhen");
        }

        return null;
    }

    internal void SetError(LocalizedText? error)
    {
        _errorText = error;
        OnPropertyChanged(nameof(TriggerEditorError));
        OnPropertyChanged(nameof(TriggerEditorErrorText));
    }

    internal void NotifyTimeFormatChanged()
    {
        UpdateScheduleComponentsFromTime(notify: true);
        OnPropertyChanged(nameof(Is24HourFormat));
        OnPropertyChanged(nameof(HourOptions));
        OnPropertyChanged(nameof(AmPmOptions));
    }

    internal void NotifyResolvedApplicationNamesChanged()
    {
        OnPropertyChanged(nameof(ResolvedTriggerAppTargetDisplayName));
        OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
    }

    internal void NotifyNetworkDiscoveryUpdated()
    {
        _updatingNetworkChoices = true;
        try
        {
            SyncSelectedNetworkFromTriggerName();
            OnPropertyChanged(nameof(NetworkDiscoveryWarning));
            OnPropertyChanged(nameof(TriggerNetworkName));
        }
        finally
        {
            _updatingNetworkChoices = false;
        }
    }

    internal void NotifyScanningNetworksChanged() => OnPropertyChanged(nameof(IsScanningNetworks));

    internal void NotifyDeviceChoicesUpdated()
    {
        _updatingDeviceChoices = true;
        try
        {
            OnPropertyChanged(nameof(RoutineDeviceChoices));
            OnPropertyChanged(nameof(SelectedAvailabilityDevice));
        }
        finally
        {
            _updatingDeviceChoices = false;
        }
    }

    internal static string MapKindToLabel(RoutineTriggerKind kind) => kind switch
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
        _ => TriggerModeLabels[0],
    };

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
        OnPropertyChanged(nameof(IsDailySchedule));
    }

    private void NotifyAllDaySelectionProperties()
    {
        foreach (DayOfWeek day in s_allDays)
        {
            OnPropertyChanged(GetDayPropertyName(day));
        }
    }

    private static string GetDayPropertyName(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => nameof(IsSundaySelected),
        DayOfWeek.Monday => nameof(IsMondaySelected),
        DayOfWeek.Tuesday => nameof(IsTuesdaySelected),
        DayOfWeek.Wednesday => nameof(IsWednesdaySelected),
        DayOfWeek.Thursday => nameof(IsThursdaySelected),
        DayOfWeek.Friday => nameof(IsFridaySelected),
        DayOfWeek.Saturday => nameof(IsSaturdaySelected),
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };

    private void UpdateScheduleComponentsFromTime(bool notify)
    {
        int hour = _scheduleTime.Hour;
        _scheduleMinute = _scheduleTime.Minute;

        if (Is24HourFormat)
        {
            _scheduleHour = hour;
        }
        else
        {
            _scheduleAmPm = hour >= 12 ? "PM" : "AM";
            _scheduleHour = hour == 0 ? 12 : (hour > 12 ? hour - 12 : hour);
        }

        if (!notify)
        {
            return;
        }

        OnPropertyChanged(nameof(ScheduleAmPm));
        OnPropertyChanged(nameof(ScheduleHourIndex));
        OnPropertyChanged(nameof(ScheduleMinuteIndex));
        OnPropertyChanged(nameof(ScheduleAmPmIndex));
    }

    private void UpdateScheduleTimeFromComponents()
    {
        int hour = _scheduleHour;
        if (!Is24HourFormat)
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

    private void SyncSelectedNetworkFromTriggerName()
    {
        string? matchedNetwork = AvailableNetworkNames.FirstOrDefault(existing =>
            string.Equals(existing, _triggerNetworkName, StringComparison.Ordinal));

        if (string.Equals(_selectedAvailableNetworkName, matchedNetwork, StringComparison.Ordinal))
        {
            return;
        }

        _selectedAvailableNetworkName = matchedNetwork;
        OnPropertyChanged(nameof(SelectedAvailableNetworkName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
