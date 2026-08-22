using System.Text.Json.Serialization;
using AudioPilot.Helpers;

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
    public bool IsStateful => Kind is RoutineTriggerKind.Application or RoutineTriggerKind.SteamBigPicture;

    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (Kind == RoutineTriggerKind.Hotkey) return "Manual only (no automatic trigger)";
            if (Kind == RoutineTriggerKind.Application && string.IsNullOrWhiteSpace(AppPath)) return "Application: choose an application";
            var routine = new AudioRoutine();
            ApplyTo(routine);
            return routine.RoutineDetailsTriggerSummary;
        }
    }

    public RoutineTrigger Copy() => this with { Days = Days == null ? [] : [.. Days] };

    internal RoutineTrigger Normalize() => this with
    {
        AppPath = Kind == RoutineTriggerKind.Application ? RoutineTriggerPathHelper.NormalizeTriggerTarget(AppPath) : string.Empty,
        ApplicationMode = Kind == RoutineTriggerKind.Application ? ApplicationMode : ApplicationTriggerMode.AppLaunch,
        TitlePattern = Kind == RoutineTriggerKind.Application && ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitlePattern?.Trim() ?? string.Empty : string.Empty,
        TitleMatchMode = Kind == RoutineTriggerKind.Application && ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitleMatchMode : ApplicationTriggerTitleMatchMode.Contains,
        Days = Kind == RoutineTriggerKind.Scheduled && Days != null ? [.. Days] : [],
        NotifyBeforeRun = Kind == RoutineTriggerKind.Scheduled && NotifyBeforeRun,
        Time = Kind == RoutineTriggerKind.Scheduled ? Time : new(12, 0),
        TimeZoneId = NormalizeTimeZoneId(TimeZoneId),
        NetworkName = Kind == RoutineTriggerKind.Network ? NetworkName?.Trim() ?? string.Empty : string.Empty,
        NetworkDirection = Kind == RoutineTriggerKind.Network ? NetworkDirection : NetworkTriggerDirection.Connect,
    };

    private static string NormalizeTimeZoneId(string? value)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(value?.Trim() ?? string.Empty).Id; }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local.Id; }
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

    internal void ApplyTo(AudioRoutine routine) => routine.Triggers = Kind == RoutineTriggerKind.Hotkey ? [] : [Copy()];

    internal string ConfigurationKey => Kind switch
    {
        RoutineTriggerKind.Application => string.Join('\u001f', Kind, RoutineTriggerPathHelper.NormalizeTriggerTarget(AppPath).ToUpperInvariant(),
            ApplicationMode, ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitlePattern : string.Empty,
            ApplicationMode == ApplicationTriggerMode.ProcessFocus ? TitleMatchMode : ApplicationTriggerTitleMatchMode.Contains),
        RoutineTriggerKind.Scheduled => string.Join('\u001f', Kind, Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', (Days ?? []).Order()), TimeZoneId),
        RoutineTriggerKind.Network => string.Join('\u001f', Kind, NetworkName?.Trim().ToUpperInvariant(), NetworkDirection),
        RoutineTriggerKind.DeviceAvailability => $"{Kind}\u001f{Device?.IdentityKey}\u001f{DeviceTransition}",
        _ => Kind.ToString(),
    };

    internal string? Validate(bool validatePattern = true)
    {
        if (!Enum.IsDefined(Kind) || Kind == RoutineTriggerKind.Hotkey) return "Choose an automatic trigger type.";
        if (!Enum.IsDefined(ApplicationMode) || !Enum.IsDefined(TitleMatchMode) || !Enum.IsDefined(NetworkDirection) ||
            (Days != null && Days.Any(static day => !Enum.IsDefined(day)))) return "Trigger contains an invalid option.";
        if (Kind == RoutineTriggerKind.DeviceAvailability && (Device is not { IsValid: true } || !Enum.IsDefined(DeviceTransition)))
            return "Choose an audio device and a valid availability transition.";
        if (Kind == RoutineTriggerKind.Application)
        {
            if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(AppPath)) return "Application trigger requires a full .exe path or packaged app AUMID.";
            if (validatePattern && ApplicationMode == ApplicationTriggerMode.ProcessFocus && TitleMatchMode == ApplicationTriggerTitleMatchMode.Regex)
            {
                try { _ = new System.Text.RegularExpressions.Regex(TitlePattern ?? string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException) { return "Application title pattern is not a valid regular expression."; }
            }
        }
        if (Kind == RoutineTriggerKind.Network && NetworkDirection != NetworkTriggerDirection.Disconnect && string.IsNullOrWhiteSpace(NetworkName))
            return "Network trigger requires a network name.";
        if (Kind == RoutineTriggerKind.Scheduled)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return "Scheduled trigger requires a valid time zone."; }
        }
        return null;
    }
}
