using System.Globalization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private bool _requireTimeWindow;
    private bool _conditionAllDay;
    private string _conditionStart = "09:00";
    private string _conditionEnd = "17:00";
    private string _conditionTimeZoneId = TimeZoneInfo.Local.Id;
    private readonly HashSet<DayOfWeek> _conditionDays = [];
    public bool RequireTimeWindow { get => _requireTimeWindow; set { _requireTimeWindow = value; OnPropertyChanged(); } }
    public bool ConditionAllDay { get => _conditionAllDay; set { _conditionAllDay = value; OnPropertyChanged(); } }
    public string ConditionStart { get => _conditionStart; set { _conditionStart = value; OnPropertyChanged(); } }
    public string ConditionEnd { get => _conditionEnd; set { _conditionEnd = value; OnPropertyChanged(); } }
    public string ConditionTimeZone => $"Time zone: {_conditionTimeZoneId}";
    public bool ConditionSunday { get => _conditionDays.Contains(DayOfWeek.Sunday); set { if (value) _conditionDays.Add(DayOfWeek.Sunday); else _conditionDays.Remove(DayOfWeek.Sunday); OnPropertyChanged(); } }
    public bool ConditionMonday { get => _conditionDays.Contains(DayOfWeek.Monday); set { if (value) _conditionDays.Add(DayOfWeek.Monday); else _conditionDays.Remove(DayOfWeek.Monday); OnPropertyChanged(); } }
    public bool ConditionTuesday { get => _conditionDays.Contains(DayOfWeek.Tuesday); set { if (value) _conditionDays.Add(DayOfWeek.Tuesday); else _conditionDays.Remove(DayOfWeek.Tuesday); OnPropertyChanged(); } }
    public bool ConditionWednesday { get => _conditionDays.Contains(DayOfWeek.Wednesday); set { if (value) _conditionDays.Add(DayOfWeek.Wednesday); else _conditionDays.Remove(DayOfWeek.Wednesday); OnPropertyChanged(); } }
    public bool ConditionThursday { get => _conditionDays.Contains(DayOfWeek.Thursday); set { if (value) _conditionDays.Add(DayOfWeek.Thursday); else _conditionDays.Remove(DayOfWeek.Thursday); OnPropertyChanged(); } }
    public bool ConditionFriday { get => _conditionDays.Contains(DayOfWeek.Friday); set { if (value) _conditionDays.Add(DayOfWeek.Friday); else _conditionDays.Remove(DayOfWeek.Friday); OnPropertyChanged(); } }
    public bool ConditionSaturday { get => _conditionDays.Contains(DayOfWeek.Saturday); set { if (value) _conditionDays.Add(DayOfWeek.Saturday); else _conditionDays.Remove(DayOfWeek.Saturday); OnPropertyChanged(); } }

    private void InitializeTimeCondition(RoutineTimeWindow? window)
    {
        _requireTimeWindow = window != null;
        _conditionTimeZoneId = window?.TimeZoneId ?? _scheduleTimeZoneId;
        if (window == null) return;
        _conditionAllDay = window.AllDay;
        _conditionStart = window.Start.ToString("t", CultureInfo.CurrentCulture);
        _conditionEnd = window.End.ToString("t", CultureInfo.CurrentCulture);
        if (!window.Days.IsDefault) _conditionDays.UnionWith(window.Days);
    }

    private string? ValidateTimeCondition()
    {
        if (!RequireTimeWindow) return null;
        if (!ConditionAllDay && (!TryParseConditionTime(ConditionStart, out _) || !TryParseConditionTime(ConditionEnd, out _)))
            return "Enter valid start and end times, such as 09:00 or 9:00 AM.";
        return BuildTimeWindow().Validate();
    }

    private static bool TryParseConditionTime(string text, out TimeOnly time) =>
        TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time) ||
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private RoutineTimeWindow BuildTimeWindow() => new()
    {
        Start = TryParseConditionTime(ConditionStart, out var start) ? start : new(9, 0),
        End = TryParseConditionTime(ConditionEnd, out var end) ? end : new(17, 0),
        AllDay = ConditionAllDay,
        Days = [.. _conditionDays.Order()],
        TimeZoneId = _conditionTimeZoneId,
    };
}
