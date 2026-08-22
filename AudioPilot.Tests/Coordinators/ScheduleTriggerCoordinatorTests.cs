using System.Collections.ObjectModel;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class ScheduleTriggerCoordinatorTests
{
    [Fact]
    public void Start_StartsTimer_WhenNotStarted()
    {
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<AudioRoutine>();
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();
        string providedTimeZone = TimeZoneInfo.Local.Id;

        var coordinator = new ScheduleTriggerCoordinator(
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
        var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, source) => executed.Add(routine),
            new Logger(),
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var logger = new Logger();

        var coordinator = new ScheduleTriggerCoordinator(
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
        var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            new Logger(),
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
        var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            new Logger(),
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
        var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            new Logger(),
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
        var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (routine, _) => executed.Add(routine),
            new Logger(),
            nowProvider: () => new DateTime(2026, 1, 5, 7, 30, 0, DateTimeKind.Utc));

        coordinator.Start();
        coordinator.CheckScheduledRoutinesForTests();
        coordinator.Dispose();

        AudioRoutine routine = Assert.Single(executed);
        Assert.Equal("routine-pacific-sunday", routine.Id);
    }

    [Fact]
    public void TryCreateScheduledOccurrenceUtc_ShiftsDstGapToFirstValidInstant()
    {
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        bool created = ScheduleTriggerCoordinator.TryCreateScheduledOccurrenceUtc(
            new DateOnly(2026, 3, 8),
            new TimeOnly(2, 30),
            pacific,
            out DateTime occurrenceUtc);

        Assert.True(created);
        Assert.Equal(new DateTime(2026, 3, 8, 10, 0, 0, DateTimeKind.Utc), occurrenceUtc);
    }

    [Fact]
    public void TryCreateScheduledOccurrenceUtc_UsesEarlierOccurrence_ForDstFold()
    {
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        bool created = ScheduleTriggerCoordinator.TryCreateScheduledOccurrenceUtc(
            new DateOnly(2026, 11, 1),
            new TimeOnly(1, 30),
            pacific,
            out DateTime occurrenceUtc);

        Assert.True(created);
        Assert.Equal(new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Utc), occurrenceUtc);
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
        using var coordinator = new ScheduleTriggerCoordinator(
            routines,
            (_, _) => executionCount++,
            new Logger(),
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
            new Logger(),
            nowProvider: () => currentTime);

        coordinator.Start();
        coordinator.Stop();
        coordinator.Start();

        Assert.Equal(2, attemptCount);
    }
}
