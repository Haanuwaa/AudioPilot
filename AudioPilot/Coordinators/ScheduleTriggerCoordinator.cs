using System.Collections.ObjectModel;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ScheduledRoutineReminder(AudioRoutine Routine, DateTime OccurrenceUtc);

    internal sealed class ScheduleTriggerCoordinator(
        ObservableCollection<AudioRoutine> routines,
        Action<AudioRoutine, string> executeRoutine,
        Logger logger,
        Func<DateTime>? nowProvider = null,
        Func<IReadOnlyList<AudioRoutine>>? routineSnapshotProvider = null,
        Action<IReadOnlyList<ScheduledRoutineReminder>>? notifyUpcomingRoutines = null) : IDisposable
    {
        private Timer? _timer;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DateTime> _lastOccurrenceByRoutineId = [];
        private readonly Dictionary<string, DateTime> _lastReminderByRoutineId = [];
        private long _generation;
        private DateTime? _lastCheckUtc;
        private bool _disposed;
        private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.Now);
        private readonly Func<IReadOnlyList<AudioRoutine>> _routineSnapshotProvider = routineSnapshotProvider ?? (() => [.. routines]);

        public void Start()
        {
            DateTime catchUpStartUtc;
            DateTime nowUtc;
            long generation;

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
                nowUtc = RoutineScheduleCalculator.NormalizeToUtc(now);
                catchUpStartUtc = RoutineScheduleCalculator.TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;
                generation = ++_generation;

                DateTime nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
                TimeSpan initialDelay = nextMinute - now;

                _timer = new Timer(
                    CheckScheduledRoutines,
                    generation,
                    initialDelay,
                    TimeSpan.FromMinutes(1));

                logger.Info("ScheduleTriggerCoordinator", () => "Scheduler started");
            }

            CheckScheduledRoutinesCore(catchUpStartUtc, nowUtc, includeWindowStart: true, generation);
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _generation++;
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
            long generation;

            lock (_lock)
            {
                generation = state is long callbackGeneration ? callbackGeneration : _generation;
                if (_disposed || _timer == null || generation != _generation)
                {
                    return;
                }

                nowUtc = RoutineScheduleCalculator.NormalizeToUtc(_nowProvider());
                windowStartUtc = _lastCheckUtc ?? RoutineScheduleCalculator.TruncateToMinute(nowUtc);
                _lastCheckUtc = nowUtc;
            }

            CheckScheduledRoutinesCore(windowStartUtc, nowUtc, includeWindowStart: false, generation);
        }

        private void CheckScheduledRoutinesCore(DateTime windowStartUtc, DateTime nowUtc, bool includeWindowStart, long generation)
        {
            if (!IsRunning(generation))
            {
                return;
            }

            IReadOnlyList<AudioRoutine>? routinesCopy = GetRoutineSnapshot();
            if (routinesCopy == null)
            {
                return;
            }
            var routinesToExecute = new List<(AudioRoutine Routine, DateTime OccurrenceUtc)>();
            var reminders = new List<ScheduledRoutineReminder>();
            HashSet<string> currentIds = routinesCopy.Select(static routine => routine.Id).ToHashSet(StringComparer.Ordinal);
            lock (_lock)
            {
                if (_disposed || _timer == null || generation != _generation)
                {
                    return;
                }
                foreach (string id in _lastReminderByRoutineId.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                {
                    _lastReminderByRoutineId.Remove(id);
                }
                foreach (string id in _lastOccurrenceByRoutineId.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                {
                    _lastOccurrenceByRoutineId.Remove(id);
                }
            }

            foreach (AudioRoutine routine in routinesCopy)
            {
                if (!routine.Enabled || routine.TriggerKind != RoutineTriggerKind.Scheduled)
                {
                    continue;
                }

                TimeZoneInfo routineTimeZone = RoutineScheduleCalculator.ResolveRoutineTimeZone(routine.ScheduleTimeZoneId);
                if (notifyUpcomingRoutines != null && TryGetReminderOccurrence(routine, nowUtc, out DateTime upcomingUtc))
                {
                    lock (_lock)
                    {
                        if (_disposed || _timer == null || generation != _generation)
                        {
                            return;
                        }

                        bool alreadyExecuted = _lastOccurrenceByRoutineId.TryGetValue(routine.Id, out DateTime executedUtc) && upcomingUtc <= executedUtc;
                        bool alreadyReminded = _lastReminderByRoutineId.TryGetValue(routine.Id, out DateTime remindedUtc) && upcomingUtc == remindedUtc;
                        if (!alreadyExecuted && !alreadyReminded)
                        {
                            _lastReminderByRoutineId[routine.Id] = upcomingUtc;
                            reminders.Add(new ScheduledRoutineReminder(routine, upcomingUtc));
                        }
                    }
                }
                if (!RoutineScheduleCalculator.TryGetScheduledOccurrenceInWindow(
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
                    if (_disposed || _timer == null || generation != _generation)
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
                    if (!IsRunning(generation))
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

                    logger.Error("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger-failed | routineName={LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}", exception: ex);
                }
            }

            if (reminders.Count > 0 && IsRunning(generation))
            {
                try
                {
                    notifyUpcomingRoutines!(reminders);
                }
                catch (Exception ex)
                {
                    logger.Warning("ScheduleTriggerCoordinator", "scheduled-routine-reminder-failed", nameof(CheckScheduledRoutinesCore), ex);
                }
            }
        }

        private bool IsRunning(long generation)
        {
            lock (_lock)
            {
                return !_disposed && _timer != null && generation == _generation;
            }
        }

        /// <summary>Finds an opted-in future occurrence using the same time-zone and daylight-saving rules as execution.</summary>
        internal static bool TryGetReminderOccurrence(AudioRoutine routine, DateTime nowUtc, out DateTime occurrenceUtc)
        {
            occurrenceUtc = default;
            DateTime normalizedUtc = RoutineScheduleCalculator.NormalizeToUtc(nowUtc);
            return routine.Enabled && routine.HasScheduledTrigger && routine.NotifyBeforeScheduledRun && routine.HasExecutionTarget
                && RoutineScheduleCalculator.TryGetScheduledOccurrenceInWindow(
                    routine, RoutineScheduleCalculator.ResolveRoutineTimeZone(routine.ScheduleTimeZoneId), normalizedUtc, normalizedUtc.AddMinutes(1),
                    includeWindowStart: false, out occurrenceUtc);
        }

        /// <summary>Rechecks queued reminders against saved settings so edits and dispatcher delays cannot produce stale notices.</summary>
        internal static IReadOnlyList<AudioRoutine> GetCurrentReminders(
            IReadOnlyList<ScheduledRoutineReminder> reminders, IReadOnlyList<AudioRoutine> currentRoutines, DateTime nowUtc)
        {
            var expectedOccurrences = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (ScheduledRoutineReminder reminder in reminders)
            {
                expectedOccurrences[reminder.Routine.Id] = reminder.OccurrenceUtc;
            }

            return [.. currentRoutines.Where(routine => expectedOccurrences.TryGetValue(routine.Id, out DateTime expectedUtc)
                && TryGetReminderOccurrence(routine, nowUtc, out DateTime occurrenceUtc)
                && occurrenceUtc == expectedUtc)];
        }

        private IReadOnlyList<AudioRoutine>? GetRoutineSnapshot()
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
                return null;
            }
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
                _lastReminderByRoutineId.Clear();
                _lastCheckUtc = null;
            }
        }
    }
}
