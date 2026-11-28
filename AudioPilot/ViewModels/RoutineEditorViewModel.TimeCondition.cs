using System.Globalization;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private bool _requireTimeWindow;
    private bool _conditionAllDay;
    private string _conditionStart = "09:00";
    private string _conditionEnd = "17:00";
    private TimeOnly _conditionStartTime = new(9, 0);
    private TimeOnly _conditionEndTime = new(17, 0);
    private int _conditionStartHour = 9;
    private int _conditionStartMinute;
    private string _conditionStartAmPm = "AM";
    private int _conditionEndHour = 5;
    private int _conditionEndMinute;
    private string _conditionEndAmPm = "PM";
    private string _conditionTimeZoneId = TimeZoneInfo.Local.Id;
    private readonly HashSet<DayOfWeek> _conditionDays = [];

    public bool RequireTimeWindow { get => _requireTimeWindow; set { if (_requireTimeWindow == value) return; _requireTimeWindow = value; OnPropertyChanged(); } }
    public bool ConditionAllDay { get => _conditionAllDay; set { if (_conditionAllDay == value) return; _conditionAllDay = value; OnPropertyChanged(); } }

    public string ConditionStart
    {
        get => _conditionStart;
        set
        {
            string normalized = value ?? string.Empty;
            if (_conditionStart == normalized) return;
            _conditionStart = normalized;
            if (TryParseConditionTime(_conditionStart, out TimeOnly parsed))
            {
                _conditionStartTime = parsed;
                SyncConditionTimeComponents(isStart: true, notify: true);
            }
            OnPropertyChanged();
        }
    }

    public string ConditionEnd
    {
        get => _conditionEnd;
        set
        {
            string normalized = value ?? string.Empty;
            if (_conditionEnd == normalized) return;
            _conditionEnd = normalized;
            if (TryParseConditionTime(_conditionEnd, out TimeOnly parsed))
            {
                _conditionEndTime = parsed;
                SyncConditionTimeComponents(isStart: false, notify: true);
            }
            OnPropertyChanged();
        }
    }

    public int ConditionStartHourIndex
    {
        get => _is24HourFormat ? _conditionStartHour : (_conditionStartHour == 12 ? 0 : (_conditionStartHour == 0 ? 0 : _conditionStartHour));
        set
        {
            int maxExclusive = _is24HourFormat ? 24 : 12;
            if (ConditionStartHourIndex == value || value < 0 || value >= maxExclusive) return;
            _conditionStartHour = _is24HourFormat ? value : (value == 0 ? 12 : value);
            UpdateConditionTimeFromComponents(isStart: true);
            OnPropertyChanged();
        }
    }

    public int ConditionStartMinuteIndex
    {
        get => _conditionStartMinute;
        set
        {
            if (_conditionStartMinute == value || value < 0 || value > 59) return;
            _conditionStartMinute = value;
            UpdateConditionTimeFromComponents(isStart: true);
            OnPropertyChanged();
        }
    }

    public int ConditionStartAmPmIndex
    {
        get => _conditionStartAmPm == "AM" ? 0 : 1;
        set
        {
            if (ConditionStartAmPmIndex == value || value < 0 || value > 1) return;
            _conditionStartAmPm = value == 0 ? "AM" : "PM";
            UpdateConditionTimeFromComponents(isStart: true);
            OnPropertyChanged();
        }
    }

    public int ConditionEndHourIndex
    {
        get => _is24HourFormat ? _conditionEndHour : (_conditionEndHour == 12 ? 0 : (_conditionEndHour == 0 ? 0 : _conditionEndHour));
        set
        {
            int maxExclusive = _is24HourFormat ? 24 : 12;
            if (ConditionEndHourIndex == value || value < 0 || value >= maxExclusive) return;
            _conditionEndHour = _is24HourFormat ? value : (value == 0 ? 12 : value);
            UpdateConditionTimeFromComponents(isStart: false);
            OnPropertyChanged();
        }
    }

    public int ConditionEndMinuteIndex
    {
        get => _conditionEndMinute;
        set
        {
            if (_conditionEndMinute == value || value < 0 || value > 59) return;
            _conditionEndMinute = value;
            UpdateConditionTimeFromComponents(isStart: false);
            OnPropertyChanged();
        }
    }

    public int ConditionEndAmPmIndex
    {
        get => _conditionEndAmPm == "AM" ? 0 : 1;
        set
        {
            if (ConditionEndAmPmIndex == value || value < 0 || value > 1) return;
            _conditionEndAmPm = value == 0 ? "AM" : "PM";
            UpdateConditionTimeFromComponents(isStart: false);
            OnPropertyChanged();
        }
    }

    public LocalizedText ConditionTimeZone => new("Routine_TimeZone", _conditionTimeZoneId);
    public string ConditionTimeZoneDetails => TimeZoneDisplayFormatter.FormatRoutineZone(_conditionTimeZoneId, details: true);

    public bool ConditionSunday { get => _conditionDays.Contains(DayOfWeek.Sunday); set => SetConditionDay(DayOfWeek.Sunday, value, nameof(ConditionSunday)); }
    public bool ConditionMonday { get => _conditionDays.Contains(DayOfWeek.Monday); set => SetConditionDay(DayOfWeek.Monday, value, nameof(ConditionMonday)); }
    public bool ConditionTuesday { get => _conditionDays.Contains(DayOfWeek.Tuesday); set => SetConditionDay(DayOfWeek.Tuesday, value, nameof(ConditionTuesday)); }
    public bool ConditionWednesday { get => _conditionDays.Contains(DayOfWeek.Wednesday); set => SetConditionDay(DayOfWeek.Wednesday, value, nameof(ConditionWednesday)); }
    public bool ConditionThursday { get => _conditionDays.Contains(DayOfWeek.Thursday); set => SetConditionDay(DayOfWeek.Thursday, value, nameof(ConditionThursday)); }
    public bool ConditionFriday { get => _conditionDays.Contains(DayOfWeek.Friday); set => SetConditionDay(DayOfWeek.Friday, value, nameof(ConditionFriday)); }
    public bool ConditionSaturday { get => _conditionDays.Contains(DayOfWeek.Saturday); set => SetConditionDay(DayOfWeek.Saturday, value, nameof(ConditionSaturday)); }

    public void ApplyConditionDayPreset(DaySelectionPreset preset)
    {
        _conditionDays.Clear();
        if (preset == DaySelectionPreset.Weekdays)
        {
            _conditionDays.UnionWith([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        }
        else if (preset == DaySelectionPreset.Weekends)
        {
            _conditionDays.UnionWith([DayOfWeek.Saturday, DayOfWeek.Sunday]);
        }

        RaiseRawPropertyChanged(nameof(ConditionSunday));
        RaiseRawPropertyChanged(nameof(ConditionMonday));
        RaiseRawPropertyChanged(nameof(ConditionTuesday));
        RaiseRawPropertyChanged(nameof(ConditionWednesday));
        RaiseRawPropertyChanged(nameof(ConditionThursday));
        RaiseRawPropertyChanged(nameof(ConditionFriday));
        OnPropertyChanged(nameof(ConditionSaturday));
    }

    private void SetConditionDay(DayOfWeek day, bool value, string propertyName)
    {
        if (value == _conditionDays.Contains(day)) return;
        if (value) _conditionDays.Add(day); else _conditionDays.Remove(day);
        OnPropertyChanged(propertyName);
    }

    private void InitializeTimeCondition(RoutineTimeWindow? window)
    {
        _requireTimeWindow = window != null;
        _conditionTimeZoneId = window?.TimeZoneId ?? _scheduleTimeZoneId;
        _conditionDays.Clear();
        if (window != null)
        {
            _conditionAllDay = window.AllDay;
            _conditionStartTime = window.Start;
            _conditionEndTime = window.End;
            _conditionStart = window.Start.ToString("t", CultureInfo.CurrentCulture);
            _conditionEnd = window.End.ToString("t", CultureInfo.CurrentCulture);
            if (!window.Days.IsDefault) _conditionDays.UnionWith(window.Days);
        }
        else
        {
            _conditionAllDay = false;
            _conditionStartTime = new TimeOnly(9, 0);
            _conditionEndTime = new TimeOnly(17, 0);
            _conditionStart = "09:00";
            _conditionEnd = "17:00";
        }

        SyncConditionTimeComponents(isStart: true, notify: false);
        SyncConditionTimeComponents(isStart: false, notify: false);
    }

    private void NotifyConditionTimeFormatChanged()
    {
        SyncConditionTimeComponents(isStart: true, notify: true);
        SyncConditionTimeComponents(isStart: false, notify: true);
    }

    private void SyncConditionTimeComponents(bool isStart, bool notify)
    {
        TimeOnly time = isStart ? _conditionStartTime : _conditionEndTime;
        int hour = time.Hour;
        int minute = time.Minute;
        int displayHour;
        string amPm;

        if (_is24HourFormat)
        {
            displayHour = hour;
            amPm = hour >= 12 ? "PM" : "AM";
        }
        else
        {
            amPm = hour >= 12 ? "PM" : "AM";
            displayHour = hour == 0 ? 12 : (hour > 12 ? hour - 12 : hour);
        }

        if (isStart)
        {
            _conditionStartHour = displayHour;
            _conditionStartMinute = minute;
            _conditionStartAmPm = amPm;
            if (notify)
            {
                PropertyChanged?.Invoke(this, new(nameof(ConditionStartHourIndex)));
                PropertyChanged?.Invoke(this, new(nameof(ConditionStartMinuteIndex)));
                PropertyChanged?.Invoke(this, new(nameof(ConditionStartAmPmIndex)));
            }
        }
        else
        {
            _conditionEndHour = displayHour;
            _conditionEndMinute = minute;
            _conditionEndAmPm = amPm;
            if (notify)
            {
                PropertyChanged?.Invoke(this, new(nameof(ConditionEndHourIndex)));
                PropertyChanged?.Invoke(this, new(nameof(ConditionEndMinuteIndex)));
                PropertyChanged?.Invoke(this, new(nameof(ConditionEndAmPmIndex)));
            }
        }
    }

    private void UpdateConditionTimeFromComponents(bool isStart)
    {
        int hour = isStart ? _conditionStartHour : _conditionEndHour;
        int minute = isStart ? _conditionStartMinute : _conditionEndMinute;
        string amPm = isStart ? _conditionStartAmPm : _conditionEndAmPm;

        if (!_is24HourFormat)
        {
            if (amPm == "PM" && hour != 12) hour += 12;
            else if (amPm == "AM" && hour == 12) hour = 0;
        }

        TimeOnly updated = new(hour, minute);
        string formatted = _is24HourFormat
            ? updated.ToString("HH:mm", CultureInfo.InvariantCulture)
            : updated.ToString("t", CultureInfo.CurrentCulture);

        if (isStart)
        {
            _conditionStartTime = updated;
            _conditionStart = formatted;
            OnPropertyChanged(nameof(ConditionStart));
        }
        else
        {
            _conditionEndTime = updated;
            _conditionEnd = formatted;
            OnPropertyChanged(nameof(ConditionEnd));
        }
    }

    private LocalizedText? ValidateTimeCondition()
    {
        if (!RequireTimeWindow) return null;
        if (!ConditionAllDay && (!TryParseConditionTime(ConditionStart, out _) || !TryParseConditionTime(ConditionEnd, out _)))
            return new("Message_EnterValidStartAndEndTimesSuch");
        return BuildTimeWindow().ValidateMessage();
    }

    private static bool TryParseConditionTime(string text, out TimeOnly time) =>
        TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time) ||
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private RoutineTimeWindow BuildTimeWindow() => new()
    {
        Start = TryParseConditionTime(ConditionStart, out var start) ? start : _conditionStartTime,
        End = TryParseConditionTime(ConditionEnd, out var end) ? end : _conditionEndTime,
        AllDay = ConditionAllDay,
        Days = [.. _conditionDays.Order()],
        TimeZoneId = _conditionTimeZoneId,
    };
}
