using System.Globalization;
using System.Text.Json.Serialization;
using AudioPilot.Localization;

namespace AudioPilot.Models;

/// <summary>One automatic trigger, independent of the routine's actions and manual shortcuts.</summary>
public sealed record RoutineTrigger
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public RoutineTriggerKind Kind { get; init; } = RoutineTriggerKind.Application;
    public RoutineDeviceReference? Device { get; init; }
    public DeviceAvailabilityTransition DeviceTransition { get; init; }
    public string AppPath { get; init; } = string.Empty;
    public ApplicationTriggerMode ApplicationMode { get; init; }
    public string TitlePattern { get; init; } = string.Empty;
    public ApplicationTriggerTitleMatchMode TitleMatchMode { get; init; } = ApplicationTriggerTitleMatchMode.Contains;
    public TimeOnly Time { get; init; } = new(12, 0);
    public HashSet<DayOfWeek> Days { get; init; } = [];
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public bool NotifyBeforeRun { get; init; }
    public string NetworkName { get; init; } = string.Empty;
    public NetworkTriggerDirection NetworkDirection { get; init; }

    [JsonIgnore]
    public bool IsStateful => Kind == RoutineTriggerKind.SteamBigPicture || Kind == RoutineTriggerKind.Application && ApplicationMode != ApplicationTriggerMode.AppExit;

    [JsonIgnore]
    public string Summary => FormatSummary(LocalizationService.English);

    [JsonIgnore]
    public Func<LocalizationService, string> SummaryPresentation => FormatSummary;

    internal string FormatSummary(LocalizationService localization)
    {
        if (Kind == RoutineTriggerKind.Hotkey) return localization["Routine_ManualOnly"];
        if (Kind == RoutineTriggerKind.Application && string.IsNullOrWhiteSpace(AppPath)) return localization["Routine_PickApp"];
        return FormatAutomaticSummary(localization, includeScheduleDetails: true) ?? localization["Routine_RunManually"];
    }

    internal string? FormatAutomaticSummary(LocalizationService localization, bool includeScheduleDetails)
    {
        switch (Kind)
        {
            case RoutineTriggerKind.Application when !string.IsNullOrWhiteSpace(AppPath):
                string mode = ApplicationMode switch
                {
                    ApplicationTriggerMode.AppLaunch => localization["Routine_Launch"],
                    ApplicationTriggerMode.ProcessFocus => localization["Routine_Focus"],
                    ApplicationTriggerMode.AppExit => localization["Routine_Exit"],
                    _ => string.Empty,
                };
                string application = localization.FormatKey("Routine_AppTrigger", mode, RoutineTriggerPathHelper.GetTriggerDisplayName(AppPath, localization));
                if (ApplicationMode != ApplicationTriggerMode.ProcessFocus || string.IsNullOrWhiteSpace(TitlePattern)) return application;
                string match = TitleMatchMode switch
                {
                    ApplicationTriggerTitleMatchMode.Exact => localization["Routine_Exact"],
                    ApplicationTriggerTitleMatchMode.Contains => localization["Routine_Contains"],
                    ApplicationTriggerTitleMatchMode.Wildcard => localization["Routine_Wildcard"],
                    ApplicationTriggerTitleMatchMode.Regex => localization["Routine_Regex"],
                    _ => string.Empty,
                };
                return application + " | " + localization.FormatKey("Routine_Title", match, TitlePattern);
            case RoutineTriggerKind.AudioPilotStartup:
                return localization["Routine_AudioPilotStartup"];
            case RoutineTriggerKind.SteamBigPicture:
                return "Steam Big Picture";
            case RoutineTriggerKind.DeviceChange:
                return localization["Routine_DeviceChange"];
            case RoutineTriggerKind.SessionUnlock:
                return localization["Routine_WindowsUnlock"];
            case RoutineTriggerKind.SystemResume:
                return localization["Routine_SystemResume"];
            case RoutineTriggerKind.DeviceAvailability:
                string transition = DeviceTransition switch
                {
                    DeviceAvailabilityTransition.Connected => localization["Diag_EndpointAvailable"],
                    DeviceAvailabilityTransition.Disconnected => localization["Routine_Unavailable"],
                    _ => localization["Routine_AvailabilityChanges"],
                };
                return localization.FormatKey("Routine_DeviceTrigger", transition, Device?.FormatDisplayName(localization) ?? localization["Routine_NotSelected"]);
            case RoutineTriggerKind.Scheduled:
                string days = Days is { Count: > 0 }
                    ? " " + string.Join(", ", Days.OrderBy(static day => (int)day).Select(day => localization.FormatValue(day)))
                    : string.Empty;
                string schedule = localization.FormatKey("Routine_ScheduledAt", Time.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern), days);
                return includeScheduleDetails ? schedule + $" [{TimeZoneId}]" + (NotifyBeforeRun ? localization["Routine_Reminder"] : string.Empty) : schedule;
            case RoutineTriggerKind.Network when NetworkDirection == NetworkTriggerDirection.Disconnect || !string.IsNullOrWhiteSpace(NetworkName):
                if (string.IsNullOrWhiteSpace(NetworkName)) return localization["Routine_NetworkAllNetworksDisconnected"];
                string direction = NetworkDirection switch
                {
                    NetworkTriggerDirection.Connect => localization["Routine_Connect"],
                    NetworkTriggerDirection.Disconnect => localization["Routine_Disconnect"],
                    NetworkTriggerDirection.Both => localization["Routine_ConnectDisconnect"],
                    _ => string.Empty,
                };
                return localization.FormatKey("Routine_NetworkTrigger", direction, NetworkName);
            default:
                return null;
        }
    }

    public RoutineTrigger Copy() => this with { Days = Days == null ? [] : [.. Days] };

    internal RoutineTrigger Normalize() => this with
    {
        Device = Kind == RoutineTriggerKind.DeviceAvailability ? Device : null,
        AppPath = Kind == RoutineTriggerKind.Application ? RoutineTriggerPathHelper.NormalizeTriggerTarget(AppPath) : string.Empty,
        ApplicationMode = Kind == RoutineTriggerKind.Application ? ApplicationMode : ApplicationTriggerMode.AppLaunch,
        TitlePattern = Kind == RoutineTriggerKind.Application && ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitlePattern?.Trim() ?? string.Empty : string.Empty,
        TitleMatchMode = Kind == RoutineTriggerKind.Application && ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitleMatchMode : ApplicationTriggerTitleMatchMode.Contains,
        Days = Kind == RoutineTriggerKind.Scheduled && Days != null ? [.. Days] : [],
        NotifyBeforeRun = Kind == RoutineTriggerKind.Scheduled && NotifyBeforeRun,
        Time = Kind == RoutineTriggerKind.Scheduled ? Time : new(12, 0),
        TimeZoneId = Kind == RoutineTriggerKind.Scheduled ? NormalizeTimeZoneId(TimeZoneId) : string.Empty,
        NetworkName = Kind == RoutineTriggerKind.Network ? NetworkName ?? string.Empty : string.Empty,
        NetworkDirection = Kind == RoutineTriggerKind.Network ? NetworkDirection : NetworkTriggerDirection.Connect,
    };

    private static string NormalizeTimeZoneId(string? value)
    {
        string id = value?.Trim() ?? string.Empty;
        if (id.Length == 0) return TimeZoneInfo.Local.Id;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id).Id; }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return id; }
    }

    internal static RoutineTrigger Capture(AudioRoutine routine, string? id = null) => new()
    {
        Id = id ?? routine.Triggers.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString("N"),
        Kind = routine.TriggerKind,
        Device = routine.TriggerDevice,
        DeviceTransition = routine.DeviceTransition,
        AppPath = routine.TriggerAppPath,
        ApplicationMode = routine.ApplicationTriggerMode,
        TitlePattern = routine.ApplicationTriggerTitlePattern,
        TitleMatchMode = routine.ApplicationTriggerTitleMatchMode,
        Time = routine.ScheduleTime,
        Days = [.. routine.ScheduleDays],
        TimeZoneId = routine.ScheduleTimeZoneId,
        NotifyBeforeRun = routine.NotifyBeforeScheduledRun,
        NetworkName = routine.TriggerNetworkName,
        NetworkDirection = routine.NetworkTriggerDirection,
    };

    internal string ConfigurationKey => Kind switch
    {
        RoutineTriggerKind.Application => string.Join('\u001f', Kind, RoutineTriggerPathHelper.NormalizeTriggerTarget(AppPath).ToUpperInvariant(),
            ApplicationMode, ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitlePattern : string.Empty,
            ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitleMatchMode : ApplicationTriggerTitleMatchMode.Contains),
        RoutineTriggerKind.Scheduled => string.Join('\u001f', Kind, Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', (Days ?? []).Order()), TimeZoneId),
        RoutineTriggerKind.Network => string.Join('\u001f', Kind, NetworkName, NetworkDirection),
        RoutineTriggerKind.DeviceAvailability => $"{Kind}\u001f{Device?.IdentityKey}\u001f{DeviceTransition}",
        _ => Kind.ToString(),
    };

    internal string? Validate(bool validatePattern = true) => ValidateMessage(validatePattern) is { } message ? LocalizationService.English.Format(message) : null;

    internal LocalizedText? ValidateMessage(bool validatePattern = true)
    {
        if (!Enum.IsDefined(Kind) || Kind == RoutineTriggerKind.Hotkey) return new("Message_ChooseAnAutomaticTriggerType");
        if (!Enum.IsDefined(ApplicationMode) || !Enum.IsDefined(TitleMatchMode) || !Enum.IsDefined(NetworkDirection) ||
            (Days != null && Days.Any(static day => !Enum.IsDefined(day)))) return new("Message_TriggerContainsAnInvalidOption");
        if (Kind == RoutineTriggerKind.DeviceAvailability && (Device is not { IsValid: true } || !Enum.IsDefined(DeviceTransition)))
            return new("Message_ChooseAnAudioDeviceAndAValid");
        if (Kind == RoutineTriggerKind.Application)
        {
            if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(AppPath)) return new("Message_ApplicationTriggerRequiresAFullExePath");
            if (validatePattern && ApplicationMode == ApplicationTriggerMode.ProcessFocus && TitleMatchMode == ApplicationTriggerTitleMatchMode.Regex)
            {
                try { _ = new System.Text.RegularExpressions.Regex(TitlePattern ?? string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { return new("Message_ApplicationTitlePatternIsNotAValid"); }
            }
        }
        if (Kind == RoutineTriggerKind.Network && NetworkDirection != NetworkTriggerDirection.Disconnect && string.IsNullOrWhiteSpace(NetworkName))
            return new("Message_NetworkTriggerRequiresANetworkName");
        if (Kind == RoutineTriggerKind.Scheduled)
        {
            if (Time.Ticks % TimeSpan.TicksPerMinute != 0) return new("Message_ScheduledTimeMustUseWholeMinutes");
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return new("Message_ScheduledTriggerRequiresAValidTimeZone"); }
        }
        return null;
    }
}
