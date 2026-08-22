using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineStatefulCoordinatorTests
{
    [Fact]
    public void CreateSession_BuildsStableSessionIdentity()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            TriggerKind = RoutineTriggerKind.Application,
            RestorePreviousAudioOnDeactivate = true,
        };

        AppViewModel.RoutineStatefulSession session = AppRoutineStatefulCoordinator.CreateSession(
            routine,
            rootProcessId: 321,
            activationSequence: 7,
            restoreSnapshot: new AppViewModel.RoutineAudioRestoreSnapshot("out-1", "Speakers", "in-1", "Mic"));

        Assert.Equal("application-launch:routine-1:321", session.SessionKey);
        Assert.Equal("routine-1", session.RoutineId);
        Assert.Equal("Desk", session.RoutineName);
        Assert.Equal(7, session.ActivationSequence);
        Assert.True(session.RestorePreviousAudioOnDeactivate);
        Assert.Equal(321, session.RootProcessId);
        Assert.True(session.RestoreSnapshot.HasValue);
    }

    [Fact]
    public void GetEndedAppStartSessionKeys_ReturnsNewestEndedSessionsFirst()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> sessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-a:100"] = new("application-launch:routine-a:100", "routine-a", "A", RoutineTriggerKind.Application, 1, true, null, 100),
            ["application-launch:routine-b:200"] = new("application-launch:routine-b:200", "routine-b", "B", RoutineTriggerKind.Application, 3, true, null, 200),
            ["steam-big-picture:routine-c"] = new("steam-big-picture:routine-c", "routine-c", "C", RoutineTriggerKind.SteamBigPicture, 2, true, null, null),
        };
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
        ];

        List<string> ended = AppRoutineStatefulCoordinator.GetEndedAppStartSessionKeys(sessions, snapshots);

        Assert.Equal(["application-launch:routine-b:200"], ended);
    }

    [Fact]
    public void DeactivateSession_RemovesTargetAndRequestsRestoreOnlyForLatestRestorableSession()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> sessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-old:100"] = new("application-launch:routine-old:100", "routine-old", "Old", RoutineTriggerKind.Application, 1, true, null, 100),
            ["application-launch:routine-new:200"] = new("application-launch:routine-new:200", "routine-new", "New", RoutineTriggerKind.Application, 2, true, null, 200),
        };

        RoutineStatefulSessionDeactivationResult oldResult = AppRoutineStatefulCoordinator.DeactivateSession(sessions, "application-launch:routine-old:100");
        RoutineStatefulSessionDeactivationResult newResult = AppRoutineStatefulCoordinator.DeactivateSession(sessions, "application-launch:routine-new:200");

        Assert.NotNull(oldResult.Session);
        Assert.False(oldResult.ShouldRestore);
        Assert.NotNull(newResult.Session);
        Assert.True(newResult.ShouldRestore);
        Assert.Empty(sessions);
    }

    [Fact]
    public void DeactivateSession_UsesBatchRestoreSnapshot_ForNewestFirstTeardown()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> sessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-old:100"] = new("application-launch:routine-old:100", "routine-old", "Old", RoutineTriggerKind.Application, 1, true, null, 100),
            ["application-launch:routine-new:200"] = new("application-launch:routine-new:200", "routine-new", "New", RoutineTriggerKind.Application, 2, true, null, 200),
        };

        long latestActivationSequence = AppRoutineStatefulCoordinator.GetLatestActivationSequence(sessions.Values);

        RoutineStatefulSessionDeactivationResult newResult = AppRoutineStatefulCoordinator.DeactivateSession(
            sessions,
            "application-launch:routine-new:200",
            latestActivationSequence);
        RoutineStatefulSessionDeactivationResult oldResult = AppRoutineStatefulCoordinator.DeactivateSession(
            sessions,
            "application-launch:routine-old:100",
            latestActivationSequence);

        Assert.NotNull(newResult.Session);
        Assert.True(newResult.ShouldRestore);
        Assert.NotNull(oldResult.Session);
        Assert.False(oldResult.ShouldRestore);
        Assert.Empty(sessions);
    }

    [Theory]
    [InlineData(true, false, 1, false, false, true, true)]
    [InlineData(true, false, 0, true, false, true, true)]
    [InlineData(true, false, 1, false, true, true, false)]
    [InlineData(false, false, 1, true, true, false, false)]
    [InlineData(true, true, 1, true, true, false, false)]
    public void ResolveSteamBigPictureMonitorDecision_ReturnsExpectedValues(
        bool monitoringEnabled,
        bool isCleaningUp,
        int watchedRoutineCount,
        bool hasActiveSteamSessions,
        bool monitorRunning,
        bool expectedShouldMonitor,
        bool expectedStartMonitor)
    {
        SteamBigPictureMonitorDecision decision = AppRoutineStatefulCoordinator.ResolveSteamBigPictureMonitorDecision(
            monitoringEnabled,
            isCleaningUp,
            watchedRoutineCount,
            hasActiveSteamSessions,
            monitorRunning);

        Assert.Equal(expectedShouldMonitor, decision.ShouldMonitor);
        Assert.Equal(expectedStartMonitor, decision.StartMonitor);
    }

    [Fact]
    public void GetInvalidRoutineStatefulSessionKeys_ReturnsSessionsNoLongerTracked()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> activeSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-a:100"] = new(
                "application-launch:routine-a:100",
                "routine-a",
                "Routine A",
                RoutineTriggerKind.Application,
                activationSequence: 1,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null,
                rootProcessId: 100),
            ["steam-big-picture:routine-b"] = new(
                "steam-big-picture:routine-b",
                "routine-b",
                "Routine B",
                RoutineTriggerKind.SteamBigPicture,
                activationSequence: 3,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null),
            ["application-launch:routine-c:200"] = new(
                "application-launch:routine-c:200",
                "routine-c",
                "Routine C",
                RoutineTriggerKind.Application,
                activationSequence: 2,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null,
                rootProcessId: 200),
        };

        List<AudioRoutine> watchedAppStartRoutines =
        [
            new()
            {
                Id = "routine-c",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
            }
        ];
        List<AudioRoutine> watchedSteamBigPictureRoutines = [];

        List<string> result = AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(
            activeSessions,
            watchedAppStartRoutines,
            watchedSteamBigPictureRoutines);

        Assert.Equal(["steam-big-picture:routine-b", "application-launch:routine-a:100"], result);
    }

    [Fact]
    public void GetInvalidRoutineStatefulSessionKeys_KeepsStillWatchedSessions()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> activeSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-a:100"] = new("application-launch:routine-a:100", "routine-a", "Routine A", RoutineTriggerKind.Application, 1, true, null, 100),
            ["steam-big-picture:routine-b"] = new("steam-big-picture:routine-b", "routine-b", "Routine B", RoutineTriggerKind.SteamBigPicture, 2, true, null, null),
        };

        List<AudioRoutine> watchedAppStartRoutines =
        [
            new()
            {
                Id = "routine-a",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
            }
        ];
        List<AudioRoutine> watchedSteamBigPictureRoutines =
        [
            new()
            {
                Id = "routine-b",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-2",
            }
        ];

        List<string> result = AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(
            activeSessions,
            watchedAppStartRoutines,
            watchedSteamBigPictureRoutines);

        Assert.Empty(result);
    }

    [Fact]
    public void GetInvalidRoutineStatefulSessionKeys_InvalidatesSessionWhenRoutineConfigurationChanges()
    {
        AudioRoutine originalRoutine = new()
        {
            Id = "routine-a",
            Name = "Focused app",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            TriggerAppPath = @"C:\Apps\Target.exe",
            OutputDeviceId = "out-1",
            RestorePreviousAudioOnDeactivate = true,
        };
        AppViewModel.RoutineStatefulSession session = AppRoutineStatefulCoordinator.CreateSession(
            originalRoutine,
            rootProcessId: 100,
            activationSequence: 1,
            restoreSnapshot: null);
        Dictionary<string, AppViewModel.RoutineStatefulSession> activeSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            [session.SessionKey] = session,
        };
        AudioRoutine changedRoutine = originalRoutine.Clone();
        changedRoutine.OutputDeviceId = "out-2";

        List<string> result = AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(
            activeSessions,
            [changedRoutine],
            []);

        Assert.Equal([session.SessionKey], result);
    }
}
