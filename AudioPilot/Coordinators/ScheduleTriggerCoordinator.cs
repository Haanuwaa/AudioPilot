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
        Action<IReadOnlyList<ScheduledRoutineReminder>>? notifyUpcomingRoutines = null,
        TimeSpan? snapshotRetryDelay = null) : IDisposable
    {
        private Timer? _timer;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DateTime> _lastOccurrenceByRoutineId = [];
        private readonly Dictionary<string, DateTime> _lastReminderByRoutineId = [];
        private long _generation;
        private DateTime? _lastCheckUtc;
        private bool _includeWindowStart;
        private bool _checking;
        private int _snapshotRetries;
        private bool _disposed;
        private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.Now);
        private readonly Func<IReadOnlyList<AudioRoutine>> _routineSnapshotProvider = routineSnapshotProvider ?? (() => [.. routines]);

        public void Start()
        {
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
                _lastCheckUtc = RoutineScheduleCalculator.TruncateToMinute(nowUtc);
                _includeWindowStart = true;
                _snapshotRetries = 0;
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

            CheckScheduledRoutines(generation);
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

        internal void DiscardPastOccurrences()
        {
            lock (_lock)
            {
                if (_disposed || _timer == null) return;
                DateTime now = RoutineScheduleCalculator.NormalizeToUtc(_nowProvider());
                _lastCheckUtc = now;
                _includeWindowStart = false;
                _timer.Dispose();
                _timer = new Timer(CheckScheduledRoutines, ++_generation,
                    RoutineScheduleCalculator.TruncateToMinute(now).AddMinutes(1) - now, TimeSpan.FromMinutes(1));
            }
        }

        private void CheckScheduledRoutines(object? state)
        {
            DateTime nowUtc;
            DateTime windowStartUtc;
            long generation;
            bool includeWindowStart;

            lock (_lock)
            {
                generation = state is long callbackGeneration ? callbackGeneration : _generation;
                if (_disposed || _timer == null || generation != _generation || _checking)
                {
                    return;
                }

                nowUtc = RoutineScheduleCalculator.NormalizeToUtc(_nowProvider());
                windowStartUtc = _lastCheckUtc ?? RoutineScheduleCalculator.TruncateToMinute(nowUtc);
                includeWindowStart = _includeWindowStart;
                _checking = true;
            }

            try
            {
                IReadOnlyList<AudioRoutine>? snapshot = GetRoutineSnapshot();
                if (snapshot == null)
                {
                    lock (_lock)
                        if (IsRunning(generation) && _snapshotRetries++ < 3)
                            _timer!.Change(snapshotRetryDelay ?? TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
                    return;
                }
                CheckScheduledRoutinesCore(snapshot, windowStartUtc, nowUtc, includeWindowStart, generation);
                lock (_lock)
                {
                    if (!IsRunning(generation)) return;
                    _lastCheckUtc = nowUtc;
                    _includeWindowStart = false;
                    _snapshotRetries = 0;
                    _timer!.Change(RoutineScheduleCalculator.TruncateToMinute(nowUtc).AddMinutes(1) - nowUtc, TimeSpan.FromMinutes(1));
                }
            }
            finally { lock (_lock) _checking = false; }
        }

        private void CheckScheduledRoutinesCore(IReadOnlyList<AudioRoutine> routinesCopy, DateTime windowStartUtc, DateTime nowUtc, bool includeWindowStart, long generation)
        {
            if (!IsRunning(generation))
            {
                return;
            }

            var routinesToExecute = new List<(AudioRoutine Routine, DateTime OccurrenceUtc)>();
            var reminders = new List<ScheduledRoutineReminder>();
            HashSet<string> currentIds = routinesCopy.Select(static routine => routine.RuntimeTriggerKey).ToHashSet(StringComparer.Ordinal);
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

                        bool alreadyExecuted = _lastOccurrenceByRoutineId.TryGetValue(routine.RuntimeTriggerKey, out DateTime executedUtc) && upcomingUtc <= executedUtc;
                        bool alreadyReminded = _lastReminderByRoutineId.TryGetValue(routine.RuntimeTriggerKey, out DateTime remindedUtc) && upcomingUtc == remindedUtc;
                        if (!alreadyExecuted && !alreadyReminded)
                        {
                            _lastReminderByRoutineId[routine.RuntimeTriggerKey] = upcomingUtc;
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

                    if (_lastOccurrenceByRoutineId.TryGetValue(routine.RuntimeTriggerKey, out DateTime lastOccurrence) &&
                        occurrenceUtc <= lastOccurrence)
                    {
                        continue;
                    }

                    _lastOccurrenceByRoutineId[routine.RuntimeTriggerKey] = occurrenceUtc;
                }

                logger.Info("ScheduleTriggerCoordinator", () => $"scheduled-routine-trigger | routineName={LogPrivacy.Label(routine.Name)}");
                routinesToExecute.Add((routine, occurrenceUtc));
            }

            foreach (var group in routinesToExecute.GroupBy(static execution => (execution.Routine.Id, execution.OccurrenceUtc)))
            {
                (AudioRoutine routine, DateTime occurrenceUtc) = group.First();
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
                        foreach (var (Routine, OccurrenceUtc) in group)
                            if (_lastOccurrenceByRoutineId.TryGetValue(Routine.RuntimeTriggerKey, out DateTime reservedOccurrence) && reservedOccurrence == occurrenceUtc)
                                _lastOccurrenceByRoutineId.Remove(Routine.RuntimeTriggerKey);
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
                expectedOccurrences[reminder.Routine.RuntimeTriggerKey] = reminder.OccurrenceUtc;
            }

            return [.. currentRoutines.SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.Scheduled)).Where(routine => expectedOccurrences.TryGetValue(routine.RuntimeTriggerKey, out DateTime expectedUtc)
                && TryGetReminderOccurrence(routine, nowUtc, out DateTime occurrenceUtc)
                && occurrenceUtc == expectedUtc)];
        }

        private IReadOnlyList<AudioRoutine>? GetRoutineSnapshot()
        {
            try
            {
                // Retain disabled schedules in the history keys so re-enabling cannot replay an occurrence.
                return [.. _routineSnapshotProvider().SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.Scheduled))];
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
