using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineAppStartCoordinatorTests
{
    [Theory]
    [InlineData(true, false, 1, 0, false, (int)RoutineAppStartMonitorAction.Start)]
    [InlineData(true, false, 0, 1, false, (int)RoutineAppStartMonitorAction.Start)]
    [InlineData(true, false, 0, 0, true, (int)RoutineAppStartMonitorAction.Start)]
    [InlineData(true, false, 1, 0, false, (int)RoutineAppStartMonitorAction.None, true)]
    [InlineData(false, false, 1, 1, true, (int)RoutineAppStartMonitorAction.Stop, true)]
    [InlineData(true, true, 1, 1, true, (int)RoutineAppStartMonitorAction.Stop, true)]
    [InlineData(false, false, 0, 0, false, (int)RoutineAppStartMonitorAction.None)]
    public void ResolveMonitorDecision_ReturnsExpectedAction(
        bool monitoringEnabled,
        bool isCleaningUp,
        int watchedRoutineCount,
        int activeLeaseCount,
        bool hasActiveAppStartStatefulSessions,
        int expectedAction,
        bool isRunning = false)
    {
        RoutineAppStartMonitorDecision decision = AppRoutineAppStartCoordinator.ResolveMonitorDecision(
            monitoringEnabled,
            isCleaningUp,
            watchedRoutineCount,
            activeLeaseCount,
            hasActiveAppStartStatefulSessions,
            isRunning);

        Assert.Equal((RoutineAppStartMonitorAction)expectedAction, decision.Action);
    }

    [Fact]
    public void PrepareLeaseRefresh_ReconcilesLeasesAndReturnsClones()
    {
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["routine-1:100"] = new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
        ];

        RoutineAppOutputLeaseRefreshPreparation result = AppRoutineAppStartCoordinator.PrepareLeaseRefresh(currentLeases, watchedRoutines, snapshots);

        Assert.Equal(1, result.PreviousLeaseCount);
        AppViewModel.RoutineAppOutputLease reconciled = Assert.Single(result.ReconciledLeases.Values);
        AppViewModel.RoutineAppOutputLease active = Assert.Single(result.ActiveLeases);
        Assert.Equal("out-2", reconciled.OutputDeviceId);
        Assert.Equal("Headset", active.OutputDeviceName);
        Assert.NotSame(reconciled, active);
        Assert.Empty(result.RemovedLeases);
    }

    [Fact]
    public void PrepareLeaseRefresh_WithSnapshotSet_ReconcilesLeasesAndReturnsClones()
    {
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["routine-1:100"] = new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        AppViewModel.RoutineAppStartSnapshotSet snapshotSet = AppViewModel.CreateRoutineAppStartSnapshotSet(
        [
            new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1),
        ]);

        RoutineAppOutputLeaseRefreshPreparation result = AppRoutineAppStartCoordinator.PrepareLeaseRefresh(currentLeases, watchedRoutines, snapshotSet);

        Assert.Equal(1, result.PreviousLeaseCount);
        AppViewModel.RoutineAppOutputLease reconciled = Assert.Single(result.ReconciledLeases.Values);
        AppViewModel.RoutineAppOutputLease active = Assert.Single(result.ActiveLeases);
        Assert.Equal("out-2", reconciled.OutputDeviceId);
        Assert.Equal("Headset", active.OutputDeviceName);
        Assert.NotSame(reconciled, active);
        Assert.Empty(result.RemovedLeases);
    }

    [Fact]
    public async Task PrepareStartedProcessWorkloadAsync_ReturnsWorkloadWhenSnapshotMatchesWatchedRoutine()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        ];

        RoutineAppStartProcessWorkload? result = await AppRoutineAppStartCoordinator.PrepareStartedProcessWorkloadAsync(
            100,
            watchedRoutines,
            activeLeaseCount: 2,
            static processId => new RoutineProcessSnapshot(processId, @"C:\Apps\Spotify\Spotify.exe"),
            CancellationToken.None);

        Assert.True(result.HasValue);
        Assert.Equal(100, result.Value.Snapshot.ProcessId);
        Assert.True(result.Value.RequiresProcessSnapshotCapture);
        AppViewModel.RoutineAppStartMatch match = Assert.Single(result.Value.Matches);
        Assert.Equal("routine-1", match.Routine.Id);
    }

    [Fact]
    public void PlanStartedMatchExecutions_MarksExistingLeaseMatchesAsSkipped()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        AudioRoutine secondRoutine = new()
        {
            Id = "routine-2",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-2",
            OutputDeviceName = "Headset",
        };
        List<AppViewModel.RoutineAppStartMatch> matches =
        [
            new(routine, 101),
            new(secondRoutine, 200),
        ];
        RoutineProcessSnapshot processSnapshot = new(101, @"C:\Apps\Spotify\Spotify.exe", 100);
        List<AppViewModel.RoutineAppOutputLease> activeLeases =
        [
            new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        ];
        List<RoutineProcessSnapshot> processSnapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            processSnapshot,
        ];

        IReadOnlyList<RoutineAppStartMatchExecutionPlan> result = AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
            matches,
            processSnapshot,
            activeLeases,
            processSnapshots);

        Assert.Equal(RoutineAppStartMatchExecutionAction.SkipExistingActiveLease, result[0].Action);
        Assert.Equal(RoutineAppStartMatchExecutionAction.Execute, result[1].Action);
    }

    [Fact]
    public void PlanStartedMatchExecutions_WithSnapshotSet_MarksExistingLeaseMatchesAsSkipped()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        List<AppViewModel.RoutineAppStartMatch> matches =
        [
            new(routine, 101),
        ];
        RoutineProcessSnapshot processSnapshot = new(101, @"C:\Apps\Spotify\Spotify.exe", 100);
        List<AppViewModel.RoutineAppOutputLease> activeLeases =
        [
            new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        ];
        AppViewModel.RoutineAppStartSnapshotSet snapshotSet = AppViewModel.CreateRoutineAppStartSnapshotSet(
        [
            new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            processSnapshot,
        ]);

        IReadOnlyList<RoutineAppStartMatchExecutionPlan> result = AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
            matches,
            processSnapshot,
            activeLeases,
            snapshotSet);

        Assert.Equal(RoutineAppStartMatchExecutionAction.SkipExistingActiveLease, result[0].Action);
    }

    [Fact]
    public async Task ExecuteLeaseApplicationsAsync_AppliesTargetsAndShowsOverlayOnceWhenLeaseCompletes()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineAppStartCoordinatorTests), "routine-app-start.log");
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers",
            "in-1",
            "Mic");

        List<(string leaseKey, uint processId, bool output)> markedApplications = [];
        List<string> overlayShown = [];
        List<string> completedLeases = [];
        List<(string? outputDeviceName, string? inputDeviceName)> overlays = [];

        await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
            [lease],
            [new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1)],
            [],
            static (rootProcessId, _, _, _) => [unchecked((uint)rootProcessId)],
            static (_, _) => Task.FromResult(true),
            static (_, _) => Task.FromResult(true),
            (currentLease, outputDeviceName, inputDeviceName) =>
            {
                overlays.Add((outputDeviceName, inputDeviceName));
                return Task.CompletedTask;
            },
            (currentLease, processId, output) => markedApplications.Add((currentLease.LeaseKey, processId, output)),
            currentLease => overlayShown.Add(currentLease.LeaseKey),
            currentLease => completedLeases.Add(currentLease.LeaseKey),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(2, markedApplications.Count);
        Assert.Contains(markedApplications, entry => entry.output && entry.processId == 100u);
        Assert.Contains(markedApplications, entry => !entry.output && entry.processId == 100u);
        Assert.Single(overlayShown);
        Assert.Single(completedLeases);
        var overlay = Assert.Single(overlays);
        Assert.Equal("Speakers", overlay.outputDeviceName);
        Assert.Equal("Mic", overlay.inputDeviceName);
        Assert.True(lease.CompletionOverlayShown);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("processId=id[len=3 hash=", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("processId=100", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteLeaseApplicationsAsync_WhenOneSideAlreadyApplied_OnlyAppliesRemainingSide_AndThenShowsOverlay()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineAppStartCoordinatorTests), "routine-app-start-partial.log");
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers",
            "in-1",
            "Mic");
        lease.AppliedOutputProcessIds.Add(100u);

        int outputCalls = 0;
        int inputCalls = 0;
        List<(string leaseKey, uint processId, bool output)> markedApplications = [];
        List<string> overlayShown = [];
        List<string> completedLeases = [];
        List<(string? outputDeviceName, string? inputDeviceName)> overlays = [];

        await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
            [lease],
            [new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1)],
            [],
            static (rootProcessId, _, _, _) => [unchecked((uint)rootProcessId)],
            (_, _) =>
            {
                outputCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                inputCalls++;
                return Task.FromResult(true);
            },
            (currentLease, outputDeviceName, inputDeviceName) =>
            {
                overlays.Add((outputDeviceName, inputDeviceName));
                return Task.CompletedTask;
            },
            (currentLease, processId, output) => markedApplications.Add((currentLease.LeaseKey, processId, output)),
            currentLease => overlayShown.Add(currentLease.LeaseKey),
            currentLease => completedLeases.Add(currentLease.LeaseKey),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(0, outputCalls);
        Assert.Equal(1, inputCalls);
        Assert.Single(markedApplications);
        Assert.Contains(markedApplications, entry => !entry.output && entry.processId == 100u);
        Assert.Single(overlayShown);
        Assert.Single(completedLeases);
        var overlay = Assert.Single(overlays);
        Assert.Null(overlay.outputDeviceName);
        Assert.Equal("Mic", overlay.inputDeviceName);
        Assert.True(lease.CompletionOverlayShown);
    }

    [Fact]
    public async Task ExecuteLeaseApplicationsAsync_WhenProcessAlreadyFullyApplied_DoesNotReplayOrShowOverlay()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineAppStartCoordinatorTests), "routine-app-start-no-replay.log");
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers",
            "in-1",
            "Mic");
        lease.AppliedOutputProcessIds.Add(100u);
        lease.AppliedInputProcessIds.Add(100u);

        bool outputInvoked = false;
        bool inputInvoked = false;
        List<string> overlayShown = [];
        List<string> completedLeases = [];
        List<(string? outputDeviceName, string? inputDeviceName)> overlays = [];

        await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
            [lease],
            [new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1)],
            [],
            static (rootProcessId, _, _, _) => [unchecked((uint)rootProcessId)],
            (_, _) =>
            {
                outputInvoked = true;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                inputInvoked = true;
                return Task.FromResult(true);
            },
            (currentLease, outputDeviceName, inputDeviceName) =>
            {
                overlays.Add((outputDeviceName, inputDeviceName));
                return Task.CompletedTask;
            },
            static (_, _, _) => { },
            currentLease => overlayShown.Add(currentLease.LeaseKey),
            currentLease => completedLeases.Add(currentLease.LeaseKey),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.False(outputInvoked);
        Assert.False(inputInvoked);
        Assert.Empty(overlayShown);
        Assert.Empty(completedLeases);
        Assert.Empty(overlays);
        Assert.False(lease.CompletionOverlayShown);
    }

    [Theory]
    [InlineData("out-1", "in-1", false, false, true)]
    [InlineData("out-1", "in-1", true, false, true)]
    [InlineData("out-1", "in-1", true, true, false)]
    [InlineData("out-1", "", true, false, false)]
    [InlineData("", "in-1", false, true, false)]
    public void HasPendingLeaseApplications_ReturnsExpectedValue(
        string outputDeviceId,
        string inputDeviceId,
        bool outputApplied,
        bool inputApplied,
        bool expected)
    {
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            outputDeviceId,
            outputDeviceId.Length == 0 ? string.Empty : "Speakers",
            inputDeviceId,
            inputDeviceId.Length == 0 ? string.Empty : "Mic");

        if (outputApplied)
        {
            lease.AppliedOutputProcessIds.Add(100u);
        }

        if (inputApplied)
        {
            lease.AppliedInputProcessIds.Add(100u);
        }

        bool actual = AppRoutineAppStartCoordinator.HasPendingLeaseApplications(lease);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExecuteLeaseApplicationsAsync_ThrowsOperationCanceled_WhenTokenAlreadyCanceled()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineAppStartCoordinatorTests), "routine-app-start-cancelled.log");
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
                [lease],
                [new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1)],
                [],
                static (rootProcessId, _, _, _) => [unchecked((uint)rootProcessId)],
                static (_, _) => Task.FromResult(true),
                static (_, _) => Task.FromResult(true),
                static (_, _, _) => Task.CompletedTask,
                static (_, _, _) => { },
                static _ => { },
                static _ => { },
                loggerScope.Logger,
                cts.Token));
    }

    [Fact]
    public async Task ExecuteLeaseApplicationsAsync_StopsBeforeNextCandidate_WhenCancellationIsRequestedMidLoop()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineAppStartCoordinatorTests), "routine-app-start-mid-loop-cancel.log");
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers");
        List<uint> appliedProcessIds = [];
        bool overlayShown = false;
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
                [lease],
                [
                    new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1),
                    new RoutineProcessSnapshot(101, @"C:\Apps\Spotify\Spotify.exe", 100),
                ],
                [],
                static (_, _, _, _) => [100u, 101u],
                (_, processId) =>
                {
                    appliedProcessIds.Add(processId);
                    cts.Cancel();
                    return Task.FromResult(true);
                },
                static (_, _) => Task.FromResult(false),
                (_, _, _) =>
                {
                    overlayShown = true;
                    return Task.CompletedTask;
                },
                static (_, _, _) => { },
                static _ => { },
                static _ => { },
                loggerScope.Logger,
                cts.Token));

        Assert.Equal([100u], appliedProcessIds);
        Assert.False(overlayShown);
        Assert.DoesNotContain(101u, lease.AppliedOutputProcessIds);
    }

    [Fact]
    public void DoesLiveLeaseMatchExpectedSnapshot_ReturnsFalse_WhenTargetsChanged()
    {
        AppViewModel.RoutineAppOutputLease currentLease = new(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-new",
            "Headset",
            "in-1",
            "Mic");
        AppViewModel.RoutineAppOutputLease expectedLease = new(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-old",
            "Speakers",
            "in-1",
            "Mic");

        bool matches = AppRoutineAppStartCoordinator.DoesLiveLeaseMatchExpectedSnapshot(currentLease, expectedLease);

        Assert.False(matches);
    }

    [Fact]
    public void DoesLiveLeaseMatchExpectedSnapshot_ReturnsTrue_WhenLeaseStillMatches()
    {
        AppViewModel.RoutineAppOutputLease currentLease = new(
            "routine-1:100",
            "routine-1",
            "Desk",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers",
            "in-1",
            "Mic");
        AppViewModel.RoutineAppOutputLease expectedLease = currentLease.Clone();

        bool matches = AppRoutineAppStartCoordinator.DoesLiveLeaseMatchExpectedSnapshot(currentLease, expectedLease);

        Assert.True(matches);
    }

    [Fact]
    public void PrepareLeaseRefresh_RemovesLease_WhenTrackedRootProcessNoLongerExists()
    {
        DateTime nowUtc = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
        var lease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        {
            CreatedUtc = nowUtc - TimeSpan.FromSeconds(10),
        };
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [lease.LeaseKey] = lease
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        List<RoutineProcessSnapshot> snapshots = [];

        RoutineAppOutputLeaseRefreshPreparation result = AppRoutineAppStartCoordinator.PrepareLeaseRefresh(currentLeases, watchedRoutines, snapshots, nowUtc);

        Assert.Equal(1, result.PreviousLeaseCount);
        Assert.Empty(result.ReconciledLeases);
        Assert.Empty(result.ActiveLeases);
        AppViewModel.RoutineAppOutputLease removedLease = Assert.Single(result.RemovedLeases);
        Assert.Equal("routine-1:100", removedLease.LeaseKey);
    }
}
