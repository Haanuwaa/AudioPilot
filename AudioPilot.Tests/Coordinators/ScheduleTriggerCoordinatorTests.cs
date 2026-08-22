using System.Collections.ObjectModel;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class ScheduleTriggerCoordinatorTests
{
    [Fact]
    public void Reminders_BatchOptedInRoutinesOnceWithoutExecutingEarly()
    {
        DateTime now = new(2026, 1, 5, 8, 58, 30, DateTimeKind.Utc);
        var first = ReminderRoutine("first");
        var second = ReminderRoutine("second");
        var optedOut = ReminderRoutine("quiet");
        optedOut.NotifyBeforeScheduledRun = false;
        var batches = new List<IReadOnlyList<ScheduledRoutineReminder>>();
        int executions = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator([first, second, optedOut], (_, _) => executions++, logger,
            nowProvider: () => now, notifyUpcomingRoutines: batches.Add);

        coordinator.Start();
        Assert.Empty(batches);
        now = now.AddSeconds(30);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Stop();
        coordinator.Start();
        Assert.Equal(["first", "second"], Assert.Single(batches).Select(static reminder => reminder.Routine.Id));
        Assert.Equal(0, executions);

        now = now.AddMinutes(1);
        coordinator.CheckScheduledRoutinesForTests();
        now = now.AddSeconds(-30);
        coordinator.CheckScheduledRoutinesForTests();
        Assert.Single(batches);
        Assert.Equal(3, executions);
    }

    [Fact]
    public void Reminders_DoNotCatchUpAfterSleepPastTheOccurrence()
    {
        DateTime now = new(2026, 1, 5, 8, 58, 30, DateTimeKind.Utc);
        int notifications = 0;
        int executions = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator([ReminderRoutine("sleep")], (_, _) => executions++, logger,
            nowProvider: () => now, notifyUpcomingRoutines: _ => notifications++);
        coordinator.Start();
        now = now.AddMinutes(3);
        coordinator.CheckScheduledRoutinesForTests();
        Assert.Equal(0, notifications);
        Assert.Equal(1, executions);
    }

    [Theory]
    [InlineData("UTC", "2026-01-05T23:59:00Z", 0, 0, true)]
    [InlineData("Eastern Standard Time", "2026-03-08T06:59:00Z", 2, 30, true)]
    [InlineData("Eastern Standard Time", "2026-11-01T04:59:00Z", 1, 0, true)]
    [InlineData("Eastern Standard Time", "2026-11-01T05:59:00Z", 1, 0, false)]
    public void Reminders_UseScheduledDateAndDaylightSavingOccurrence(string zone, string utc, int hour, int minute, bool expected)
    {
        DateTime now = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        AudioRoutine routine = ReminderRoutine("zone");
        routine.ScheduleTimeZoneId = zone;
        routine.ScheduleTime = new TimeOnly(hour, minute);
        routine.ScheduleDays = [zone == "UTC" ? DayOfWeek.Tuesday : DayOfWeek.Sunday];

        Assert.Equal(expected, ScheduleTriggerCoordinator.TryGetReminderOccurrence(routine, now, out DateTime occurrence));
        if (expected)
        {
            Assert.Equal(now.AddMinutes(1), occurrence);
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("opted-out")]
    [InlineData("rescheduled")]
    [InlineData("deleted")]
    [InlineData("deadline")]
    [InlineData("trigger-changed")]
    [InlineData("no-target")]
    public void QueuedReminders_RevalidateSavedStateBeforeShowing(string change)
    {
        DateTime now = new(2026, 1, 5, 8, 59, 0, DateTimeKind.Utc);
        AudioRoutine original = ReminderRoutine("edited");
        var queued = new ScheduledRoutineReminder[] { new(original, now.AddMinutes(1)) };
        AudioRoutine current = original.Clone();
        Assert.Single(ScheduleTriggerCoordinator.GetCurrentReminders(queued, [current], now));

        switch (change)
        {
            case "disabled": current.Enabled = false; break;
            case "opted-out": current.NotifyBeforeScheduledRun = false; break;
            case "rescheduled": current.ScheduleTime = new TimeOnly(10, 0); break;
            case "deleted": current.Id = "replacement"; break;
            case "deadline": now = now.AddMinutes(1); break;
            case "trigger-changed": current.TriggerKind = RoutineTriggerKind.Hotkey; break;
            case "no-target": current.OutputDeviceId = string.Empty; break;
        }

        Assert.Empty(ScheduleTriggerCoordinator.GetCurrentReminders(queued, [current], now));
    }

    [Fact]
    public void ReminderFailure_DoesNotPreventExecutionOrFloodRetries()
    {
        DateTime now = new(2026, 1, 5, 8, 59, 0, DateTimeKind.Utc);
        int notifications = 0;
        int executions = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator([ReminderRoutine("failure")], (_, _) => executions++, logger,
            nowProvider: () => now, notifyUpcomingRoutines: _ =>
            {
                notifications++;
                throw new InvalidOperationException("Notification unavailable");
            });
        coordinator.Start();
        coordinator.CheckScheduledRoutinesForTests();
        now = now.AddMinutes(1);
        coordinator.CheckScheduledRoutinesForTests();
        Assert.Equal(1, notifications);
        Assert.Equal(1, executions);
    }

    [Fact]
    public void SnapshotFailure_DoesNotEraseReminderDeduplication()
    {
        DateTime now = new(2026, 1, 5, 8, 59, 0, DateTimeKind.Utc);
        bool failSnapshot = false;
        AudioRoutine routine = ReminderRoutine("snapshot");
        int notifications = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator([], (_, _) => { }, logger,
            nowProvider: () => now,
            routineSnapshotProvider: () => failSnapshot ? throw new InvalidOperationException("Unavailable") : [routine],
            notifyUpcomingRoutines: _ => notifications++);
        coordinator.Start();
        failSnapshot = true;
        coordinator.CheckScheduledRoutinesForTests();
        failSnapshot = false;
        coordinator.CheckScheduledRoutinesForTests();
        Assert.Equal(1, notifications);
    }

    private static AudioRoutine ReminderRoutine(string id) => new()
    {
        Id = id,
        Name = id,
        TriggerKind = RoutineTriggerKind.Scheduled,
        ScheduleTime = new TimeOnly(9, 0),
        ScheduleTimeZoneId = "UTC",
        NotifyBeforeScheduledRun = true,
        OutputDeviceId = "out-1",
    };

    [Fact]
    public void Start_StartsTimer_WhenNotStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        coordinator.Start();

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Stop_StopsTimer_WhenStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        coordinator.Start();
        coordinator.Stop();

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Start_DoesNotStartTimer_WhenAlreadyStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        coordinator.Start();
        coordinator.Start();

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Dispose_StopsTimer_WhenStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        coordinator.Start();
        coordinator.Dispose();

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenNotStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        var exception = Record.Exception(() => coordinator.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Constructor_AcceptsTimeZoneProvider()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        using var logger = Logger.CreateInMemoryForTests();
        string providedTimeZone = TimeZoneInfo.Local.Id;

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executedRoutines.Add(routine),
            logger);

        Assert.NotNull(coordinator);
    }

    [Fact]
    public void CheckScheduledRoutines_ExecutesRoutine_WhenTimeMatches()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-1",
                Name = "Morning Routine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        currentTime = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-1", routine.Id);
    }

    [Fact]
    public void CheckScheduledRoutines_DoesNotExecute_WhenTimeDoesNotMatch()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-1",
                Name = "Morning Routine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        currentTime = new DateTime(2026, 1, 5, 7, 59, 45, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        Assert.Empty(executed);
    }

    [Fact]
    public void CheckScheduledRoutines_RespectsDayFilter_WhenDaysSpecified()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-1",
                Name = "Weekday Routine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleDays = [DayOfWeek.Tuesday],
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        currentTime = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        Assert.Empty(executed);
    }

    [Fact]
    public void CheckScheduledRoutines_DoesNotExecute_WhenRoutineDisabled()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-1",
                Name = "Morning Routine",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        currentTime = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        Assert.Empty(executed);
    }

    [Fact]
    public void CheckScheduledRoutines_HandlesInvalidTimeZoneId()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-1",
                Name = "Morning Routine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = "Invalid/Timezone",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();

        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        currentTime = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-1", routine.Id);
    }

    [Fact]
    public void Start_ExecutesCurrentMinuteScheduledRoutineImmediately()
    {
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-start-catchup",
                Name = "Start Catchup",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            logger,
            nowProvider: () => new DateTime(2026, 1, 5, 8, 0, 30, DateTimeKind.Local));

        coordinator.Start();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-start-catchup", routine.Id);
    }

    [Fact]
    public void CheckScheduledRoutines_ExecutesMissedRoutineAfterDelayedTick()
    {
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-delayed-tick",
                Name = "Delayed Tick",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        DateTime currentTime = new(2026, 1, 5, 7, 59, 0, DateTimeKind.Local);
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        executed.Clear();
        currentTime = new DateTime(2026, 1, 5, 8, 1, 5, DateTimeKind.Local);

        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-delayed-tick", routine.Id);
    }

    [Fact]
    public void CheckScheduledRoutines_MatchesScheduleInRoutineTimeZone()
    {
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-pacific",
                Name = "Pacific Morning",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleTimeZoneId = "Pacific Standard Time",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            logger,
            nowProvider: () => new DateTime(2026, 1, 5, 17, 0, 0, DateTimeKind.Utc));

        coordinator.Start();
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-pacific", routine.Id);
    }

    [Fact]
    public void CheckScheduledRoutines_RespectsRoutineTimeZoneDayFilter()
    {
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-pacific-sunday",
                Name = "Pacific Sunday",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(23, 30),
                ScheduleDays = [DayOfWeek.Sunday],
                ScheduleTimeZoneId = "Pacific Standard Time",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        };
        List<AudioRoutine> executed = [];
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            logger,
            nowProvider: () => new DateTime(2026, 1, 5, 7, 30, 0, DateTimeKind.Utc));

        coordinator.Start();
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-pacific-sunday", routine.Id);
    }

    [Fact]
    public void CheckScheduledRoutines_DoesNotRepeatOccurrenceAfterClockMovesBackward()
    {
        DateTime currentTime = new(2026, 1, 5, 7, 59, 30, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-clock-rollback",
                Name = "Clock rollback",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
            }
        };
        int executionCount = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (_, _) => executionCount++,
            logger,
            nowProvider: () => currentTime);
        coordinator.Start();

        currentTime = new DateTime(2026, 1, 5, 8, 0, 5, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        currentTime = new DateTime(2026, 1, 5, 7, 59, 40, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();
        currentTime = new DateTime(2026, 1, 5, 8, 0, 20, DateTimeKind.Local);
        coordinator.CheckScheduledRoutinesForTests();

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void CheckScheduledRoutines_ReleasesOccurrenceReservation_WhenDispatchThrows()
    {
        DateTime currentTime = new(2026, 1, 5, 8, 0, 5, DateTimeKind.Local);
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-retry",
                Name = "Retry dispatch",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(8, 0),
                ScheduleTimeZoneId = TimeZoneInfo.Local.Id,
                OutputDeviceId = "out-1",
            }
        };
        int attemptCount = 0;
        using var logger = Logger.CreateInMemoryForTests();
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (_, _) =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    throw new InvalidOperationException("dispatch failed");
                }
            },
            logger,
            nowProvider: () => currentTime);

        coordinator.Start();
        coordinator.Stop();
        coordinator.Start();

        Assert.Equal(2, attemptCount);
    }
}
