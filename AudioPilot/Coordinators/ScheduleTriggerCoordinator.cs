using System.Collections.ObjectModel;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed class ScheduleTriggerCoordinator(
        ObservableCollection<AudioRoutine> routines,
        Action<AudioRoutine, string> executeRoutine,
        Logger logger,
        Func<DateTime>? nowProvider = null,
        Func<IReadOnlyList<AudioRoutine>>? routineSnapshotProvider = null) : IDisposable
    {
        private Timer? _timer;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DateTime> _lastOccurrenceByRoutineId = [];
        private DateTime? _lastCheckUtc;
        private bool _disposed;
        private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.Now);
        private readonly Func<IReadOnlyList<AudioRoutine>> _routineSnapshotProvider = routineSnapshotProvider ?? (() => [.. routines]);

        public void Start()
        {
            DateTime catchUpStartUtc;
            DateTime nowUtc;

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_timer != null)
                {
                    return;
                }

                DateTime now = _nowProvider();
                nowUtc = NormalizeToUtc(now);
                catchUpStartUtc = TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;

                DateTime nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
                TimeSpan initialDelay = nextMinute - now;

                _timer = new Timer(
                    CheckScheduledRoutines,
                    null,
                    initialDelay,
                    TimeSpan.FromMinutes(1));

                logger.Info("ScheduleTriggerCoordinator", () => "Scheduler started");
            }

            CheckScheduledRoutinesCore(catchUpStartUtc, nowUtc, includeWindowStart: true);
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                    logger.Info("ScheduleTriggerCoordinator", () => "Scheduler stopped");
                }
            }
        }

        private void CheckScheduledRoutines(object? state)
        {
            DateTime nowUtc;
            DateTime windowStartUtc;

            lock (_lock)
            {
                if (_disposed || _timer == null)
                {
                    return;
                }

                nowUtc = NormalizeToUtc(_nowProvider());
                windowStartUtc = _lastCheckUtc ?? TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;
            }

            CheckScheduledRoutinesCore(windowStartUtc, nowUtc, includeWindowStart: false);
        }

        private void CheckScheduledRoutinesCore(DateTime windowStartUtc, DateTime nowUtc, bool includeWindowStart)
        {
            if (!IsRunning())
            {
                return;
            }

            IReadOnlyList<AudioRoutine> routinesCopy = GetRoutineSnapshot();
            var routinesToExecute = new List<(AudioRoutine Routine, DateTime OccurrenceUtc)>();

            foreach (AudioRoutine routine in routinesCopy)
            {
                if (!routine.Enabled || routine.TriggerKind != RoutineTriggerKind.Scheduled)
                {
                    continue;
                }

                TimeZoneInfo routineTimeZone = ResolveRoutineTimeZone(routine.ScheduleTimeZoneId);
                if (!TryGetScheduledOccurrenceInWindow(
                        routine,
                        routineTimeZone,
                        windowStartUtc,
                        nowUtc,
                        includeWindowStart,
                        out DateTime occurrenceUtc))
                {
                    continue;
                }

                lock (_lock)
                {
                    if (_disposed || _timer == null)
                    {
                        return;
                    }

                    if (_lastOccurrenceByRoutineId.TryGetValue(routine.Id, out DateTime lastOccurrence) &&
                        occurrenceUtc <= lastOccurrence)
                    {
                        continue;
                    }

                    _lastOccurrenceByRoutineId[routine.Id] = occurrenceUtc;
                }

                logger.Info("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger | routineName={LogPrivacy.Label(routine.Name)}");
                routinesToExecute.Add((routine, occurrenceUtc));
            }

            foreach ((AudioRoutine routine, DateTime occurrenceUtc) in routinesToExecute)
            {
                try
                {
                    if (!IsRunning())
                    {
                        return;
                    }

                    executeRoutine(routine, "Scheduled trigger");
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        if (_lastOccurrenceByRoutineId.TryGetValue(routine.Id, out DateTime reservedOccurrence) &&
                            reservedOccurrence == occurrenceUtc)
                        {
                            _lastOccurrenceByRoutineId.Remove(routine.Id);
                        }
                    }

                    logger.Error("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger-failed | routineName={LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}");
                }
            }
        }

        private bool IsRunning()
        {
            lock (_lock)
            {
                return !_disposed && _timer != null;
            }
        }

        private IReadOnlyList<AudioRoutine> GetRoutineSnapshot()
        {
            try
            {
                return _routineSnapshotProvider();
            }
            catch (Exception ex)
            {
                logger.Warning(
                    "ScheduleTriggerCoordinator",
                    () => $"scheduled-routine-snapshot-failed | reason={ex.GetType().Name}",
                    nameof(GetRoutineSnapshot),
                    ex);
                return [];
            }
        }

        internal static TimeZoneInfo ResolveRoutineTimeZone(string? timeZoneId)
        {
            try
            {
                return string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Local
                    : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                return TimeZoneInfo.Local;
            }
        }

        internal static DateTime NormalizeToUtc(DateTime now)
        {
            if (now.Kind == DateTimeKind.Utc)
            {
                return now;
            }

            DateTime localNow = now.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(now, DateTimeKind.Local)
                : now;

            return localNow.ToUniversalTime();
        }

        internal static DateTime TruncateToMinute(DateTime utcTime)
        {
            DateTime normalizedUtc = NormalizeToUtc(utcTime);
            return new DateTime(normalizedUtc.Year, normalizedUtc.Month, normalizedUtc.Day, normalizedUtc.Hour, normalizedUtc.Minute, 0, DateTimeKind.Utc);
        }

        internal static bool TryGetScheduledOccurrenceInWindow(
            AudioRoutine routine,
            TimeZoneInfo routineTimeZone,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            bool includeWindowStart,
            out DateTime occurrenceUtc)
        {
            occurrenceUtc = default;
            DateTime normalizedStartUtc = NormalizeToUtc(windowStartUtc);
            DateTime normalizedEndUtc = NormalizeToUtc(windowEndUtc);

            if (normalizedEndUtc < normalizedStartUtc)
            {
                return false;
            }

            DateOnly startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedStartUtc, routineTimeZone));
            DateOnly endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedEndUtc, routineTimeZone));

            for (DateOnly localDate = startDate; localDate <= endDate; localDate = localDate.AddDays(1))
            {
                if (!OccursOnLocalDate(routine, localDate.DayOfWeek))
                {
                    continue;
                }

                if (!TryCreateScheduledOccurrenceUtc(localDate, routine.ScheduleTime, routineTimeZone, out DateTime scheduledUtc))
                {
                    continue;
                }

                if (scheduledUtc > normalizedEndUtc)
                {
                    continue;
                }

                if (includeWindowStart)
                {
                    if (scheduledUtc >= normalizedStartUtc)
                    {
                        occurrenceUtc = scheduledUtc;
                    }

                    continue;
                }

                if (scheduledUtc > normalizedStartUtc)
                {
                    occurrenceUtc = scheduledUtc;
                }
            }

            return occurrenceUtc != default;
        }

        internal static bool TryCreateScheduledOccurrenceUtc(
            DateOnly localDate,
            TimeOnly scheduledTime,
            TimeZoneInfo routineTimeZone,
            out DateTime scheduledUtc)
        {
            DateTime localDateTime = localDate.ToDateTime(scheduledTime, DateTimeKind.Unspecified);
            if (routineTimeZone.IsInvalidTime(localDateTime))
            {
                DateTime firstValidLocalTime = localDateTime;
                for (int minute = 0; minute < 180 && routineTimeZone.IsInvalidTime(firstValidLocalTime); minute++)
                {
                    firstValidLocalTime = firstValidLocalTime.AddMinutes(1);
                }

                if (routineTimeZone.IsInvalidTime(firstValidLocalTime))
                {
                    scheduledUtc = default;
                    return false;
                }

                localDateTime = firstValidLocalTime;
            }

            TimeSpan offset;
            if (routineTimeZone.IsAmbiguousTime(localDateTime))
            {
                offset = routineTimeZone.GetAmbiguousTimeOffsets(localDateTime).Max();
            }
            else
            {
                offset = routineTimeZone.GetUtcOffset(localDateTime);
            }

            scheduledUtc = new DateTimeOffset(localDateTime, offset).UtcDateTime;
            return true;
        }

        private static bool OccursOnLocalDate(AudioRoutine routine, DayOfWeek localDay)
        {
            return routine.ScheduleDays.Count == 0 || routine.ScheduleDays.Contains(localDay);
        }

        internal void CheckScheduledRoutinesForTests()
        {
            CheckScheduledRoutines(null);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Stop();
                _lastOccurrenceByRoutineId.Clear();
                _lastCheckUtc = null;
            }
        }
    }
}
