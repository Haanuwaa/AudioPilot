using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class ApplicationTriggerCoordinatorTests
{
    [Fact]
    public async Task OverlappingFocusMatches_StayRegisteredOnceAcrossTitleAndWindowChanges()
    {
        var monitor = new FakeWindowFocusMonitor();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var routine = CreateFocusRoutine("multi");
        routine.Triggers = [.. routine.Triggers, new() { Id = "video", Kind = RoutineTriggerKind.Application, AppPath = routine.TriggerAppPath,
            ApplicationMode = ApplicationTriggerMode.ProcessFocus, TitlePattern = "Video" }];
        int ended = 0;
        using var coordinator = new ApplicationTriggerCoordinator([routine], (trigger, _) =>
        {
            events.Enqueue("start:" + trigger.RuntimeTriggerKey);
            if (trigger.TriggerIdentity == "video") ready.TrySetResult();
            return Task.CompletedTask;
        }, Logger.Instance, monitor, deactivateRoutine: (trigger, _) =>
        {
            events.Enqueue("end:" + trigger.RuntimeTriggerKey);
            if (++ended == 2) complete.TrySetResult();
            return Task.CompletedTask;
        });
        coordinator.Start();
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", routine.TriggerAppPath, "Video 1"));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", routine.TriggerAppPath, "Video 2", isTitleChange: true));
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", routine.TriggerAppPath, "Video 3"));
        monitor.RaiseFocused(new WindowFocusEventArgs(50, "Other", @"C:\Apps\Other.exe", "Other"));
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([$"start:multi/{routine.Triggers[0].Id}", "start:multi/video", "end:multi/video", $"end:multi/{routine.Triggers[0].Id}"], [.. events]);
    }

    [Fact]
    public async Task FocusHandoff_JoinsNewTriggerBeforeEndingOldTriggerOfSameRoutine()
    {
        var monitor = new FakeWindowFocusMonitor();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var routine = CreateFocusRoutine("multi");
        routine.ApplicationTriggerTitlePattern = "Music";
        routine.ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
        routine.Triggers = [.. routine.Triggers, new() { Id = "video", Kind = RoutineTriggerKind.Application, AppPath = routine.TriggerAppPath, ApplicationMode = ApplicationTriggerMode.ProcessFocus, TitlePattern = "Video", TitleMatchMode = ApplicationTriggerTitleMatchMode.Contains }];
        using var coordinator = new ApplicationTriggerCoordinator([routine], (trigger, _) =>
        {
            events.Enqueue("start:" + trigger.RuntimeTriggerKey);
            first.TrySetResult();
            return Task.CompletedTask;
        }, Logger.Instance, monitor, deactivateRoutine: (trigger, _) =>
        {
            events.Enqueue("end:" + trigger.RuntimeTriggerKey);
            ended.TrySetResult();
            return Task.CompletedTask;
        });
        coordinator.Start();
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", routine.TriggerAppPath, "Music"));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", routine.TriggerAppPath, "Video", isTitleChange: true));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([$"start:multi/{routine.Triggers[0].Id}", "start:multi/video", $"end:multi/{routine.Triggers[0].Id}"], [.. events]);
    }

    [Theory]
    [InlineData(@"C:\Apps\Target.exe", true)]
    [InlineData(@"C:\Other\Target.exe", false)]
    [InlineData("", true)]
    public async Task WindowFocus_UsesNameFallbackOnlyWhenExecutablePathIsUnavailable(string executablePath, bool matchesExactTarget)
    {
        var monitor = new FakeWindowFocusMonitor();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = new System.Collections.Concurrent.ConcurrentQueue<string>();
        AudioRoutine nameOnly = CreateFocusRoutine("name-only");
        nameOnly.TriggerAppPath = @"C:\Apps\Target.exe";
        using var coordinator = new ApplicationTriggerCoordinator(
            [CreateFocusRoutine("exact-path"), nameOnly],
            (routine, _) =>
            {
                executions.Enqueue(routine.Id);
                if (routine.Id == "name-only") { completed.TrySetResult(); }
                return Task.CompletedTask;
            }, Logger.Instance, monitor);

        coordinator.Start();
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", executablePath, "Window"));
        if (matchesExactTarget) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        else await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(matchesExactTarget ? ["exact-path", "name-only"] : [], executions.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowFocus_WhenMonitoringChangesDuringExecution_DrainsNewGenerationAndSkipsStaleRoutines(bool restart)
    {
        var monitor = new FakeWindowFocusMonitor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = new System.Collections.Concurrent.ConcurrentQueue<string>();
        AudioRoutine[] routines = [CreateFocusRoutine("first"), CreateFocusRoutine("stale")];
        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            async (routine, _) =>
            {
                executions.Enqueue(routine.Id);
                if (routine.Id == "first")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                else if (routine.Id == "current")
                {
                    completed.TrySetResult();
                }
            },
            Logger.Instance, monitor, routineSnapshotProvider: () => Volatile.Read(ref routines));

        coordinator.Start();
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", @"C:\Apps\Target.exe", "Initial"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Volatile.Write(ref routines, [CreateFocusRoutine("current")]);
            if (restart)
            {
                coordinator.Stop();
                coordinator.Start();
            }
            else
            {
                coordinator.RefreshRoutines();
            }

            monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", @"C:\Apps\Target.exe", "Current"));
        }
        finally
        {
            release.TrySetResult();
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["first", "current"], [.. executions]);
    }

    [Fact]
    public async Task TitleChanges_RetainMatchingActivationAndReevaluateChangedTitle()
    {
        var monitor = new FakeWindowFocusMonitor();
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var reevaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshots = 0;
        AudioRoutine music = CreateFocusRoutine("music");
        music.ApplicationTriggerTitlePattern = "Music";
        music.ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
        AudioRoutine other = CreateFocusRoutine("other");
        other.ApplicationTriggerTitlePattern = "Other";
        other.ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains;
        using var coordinator = new ApplicationTriggerCoordinator([music, other], (routine, _) =>
        {
            events.Enqueue("start:" + routine.Id);
            if (routine.Id == "music") initial.TrySetResult();
            else completed.TrySetResult();
            return Task.CompletedTask;
        }, Logger.Instance, monitor, routineSnapshotProvider: () =>
        {
            if (Interlocked.Increment(ref snapshots) >= 3) reevaluated.TrySetResult();
            return [music, other];
        }, deactivateRoutine: (routine, _) =>
        {
            events.Enqueue("end:" + routine.Id);
            return Task.CompletedTask;
        });
        coordinator.Start();
        monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Music - One", windowHandle: 1));
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Music - Two", isTitleChange: true, windowHandle: 1));
        await reevaluated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Other", isTitleChange: true, windowHandle: 1));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["start:music", "end:music", "start:other"], [.. events]);
    }

    [Fact]
    public async Task TitleChangeBurst_CoalescesWhileActivationIsBusy()
    {
        var monitor = new FakeWindowFocusMonitor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        AudioRoutine first = CreateFocusRoutine("first");
        first.ApplicationTriggerTitlePattern = "Initial";
        first.ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Exact;
        AudioRoutine stale = first.Clone();
        stale.Id = "stale";
        stale.ApplicationTriggerTitlePattern = "Intermediate";
        AudioRoutine final = first.Clone();
        final.Id = "final";
        final.ApplicationTriggerTitlePattern = "Final";
        using var coordinator = new ApplicationTriggerCoordinator([first, stale, final], async (routine, _) =>
        {
            events.Enqueue(routine.Id);
            if (routine.Id == "first")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            if (routine.Id == "final") completed.TrySetResult();
        }, Logger.Instance, monitor);
        coordinator.Start();
        monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Initial", windowHandle: 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (int i = 0; i < 100; i++)
                monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Intermediate" + i, isTitleChange: true, windowHandle: 1));
            monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Intermediate", isTitleChange: true, windowHandle: 1));
            monitor.RaiseFocused(new(42, "Target", @"C:\Apps\Target.exe", "Final", isTitleChange: true, windowHandle: 1));
        }
        finally { release.TrySetResult(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["first", "final"], [.. events]);
    }

    [Fact]
    public async Task Dispose_DuringFocusExecution_PreventsRemainingActionsAndMonitorRestart()
    {
        var monitor = new FakeWindowFocusMonitor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        using var coordinator = new ApplicationTriggerCoordinator(
            [CreateFocusRoutine("first"), CreateFocusRoutine("stale")],
            async (_, _) =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }, Logger.Instance, monitor);

        coordinator.Start();
        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", @"C:\Apps\Target.exe", "Initial"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            coordinator.Dispose();
            coordinator.RefreshRoutines();
        }
        finally
        {
            release.TrySetResult();
        }

        await TestExecutionGuards.WaitUntilAsync(
            () => !TestPrivateAccess.GetField<bool>(coordinator, "_focusWorkerRunning"),
            "Focus worker did not finish after disposal.");
        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.Equal(1, monitor.StartCallCount);
    }

    [Theory]
    [InlineData(@"^\d+$", @"^\D+$", "123")]
    [InlineData(@"^\w+$", @"^\W+$", "word")]
    [InlineData(@"^\s+$", @"^\S+$", " ")]
    public void MatchesTitlePattern_RegexCache_PreservesCaseSensitiveEscapeSyntax(string matchingPattern, string oppositePattern, string title)
    {
        using var coordinator = new ApplicationTriggerCoordinator([], (_, _) => Task.CompletedTask, Logger.Instance, new FakeWindowFocusMonitor());
        AudioRoutine routine = CreateFocusRoutine("regex");
        routine.ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex;
        routine.ApplicationTriggerTitlePattern = matchingPattern;
        Assert.True(coordinator.MatchesTitlePatternForTests(routine, title));
        routine.ApplicationTriggerTitlePattern = oppositePattern;
        Assert.False(coordinator.MatchesTitlePatternForTests(routine, title));
        routine.ApplicationTriggerTitlePattern = matchingPattern;
        Assert.True(coordinator.MatchesTitlePatternForTests(routine, title));
    }

    private static AudioRoutine CreateFocusRoutine(string id) => new()
    {
        Id = id,
        Enabled = true,
        TriggerKind = RoutineTriggerKind.Application,
        ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
        TriggerAppPath = @"C:\Apps\Target.exe",
        OutputDeviceId = "output",
    };

    [Fact]
    public void Start_DoesNotStartWithoutProcessFocusRoutines()
    {
        var monitor = new FakeWindowFocusMonitor();
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Launch",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.AppLaunch,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(routines, (_, _) => Task.CompletedTask, Logger.Instance, monitor);

        coordinator.Start();

        Assert.Equal(0, monitor.StartCallCount);
    }

    [Fact]
    public async Task WindowFocus_MatchesExecutablePath_AndSkipsDuplicateSameFocusEvent()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executions = new List<(string RoutineId, int ProcessId)>();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\ExampleUser\AppData\Roaming\Spotify\Spotify.exe",
                ApplicationTriggerTitlePattern = "playlist",
                ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                lock (executions)
                {
                    executions.Add((routine.Id, processId));
                }

                executed.TrySetResult();
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();
        Assert.Equal(1, monitor.StartCallCount);

        monitor.RaiseFocused(new WindowFocusEventArgs(
            4242,
            string.Empty,
            @"C:\Users\ExampleUser\AppData\Roaming\Spotify\Spotify.exe",
            "Spotify Premium - playlist"));

        await executed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        monitor.RaiseFocused(new WindowFocusEventArgs(
            4242,
            "Spotify",
            @"C:\Users\ExampleUser\AppData\Roaming\Spotify\Spotify.exe",
            "Spotify Premium - playlist"));

        lock (executions)
        {
            Assert.Equal(("routine-1", 4242), Assert.Single(executions));
        }
    }

    [Fact]
    public async Task WindowFocus_WhenProcessFocusReturnsToSameProcess_TriggersAgain()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executions = new List<(string RoutineId, int ProcessId)>();
        var executedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Discord Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9235\Discord.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                lock (executions)
                {
                    executions.Add((routine.Id, processId));
                    if (executions.Count == 2)
                    {
                        executedTwice.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Friends - Discord"));

        await WaitForExecutionCountAsync(executions, 1);
        monitor.RaiseFocused(new WindowFocusEventArgs(
            23136,
            "explorer",
            @"C:\Windows\explorer.exe",
            string.Empty));

        monitor.RaiseFocused(new WindowFocusEventArgs(
            28776,
            "Discord",
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Friends - Discord"));

        await executedTwice.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        lock (executions)
        {
            Assert.Equal([("routine-1", 28776), ("routine-1", 28776)], executions);
        }
    }

    [Fact]
    public async Task WindowFocus_WhenFocusLeavesMatchedWindow_DeactivatesBeforeNextActivation()
    {
        var monitor = new FakeWindowFocusMonitor();
        var transitions = new List<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Focused app",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            TriggerAppPath = @"C:\Apps\Target.exe",
            OutputDeviceId = "out-1",
        };

        using var coordinator = new ApplicationTriggerCoordinator(
            [routine],
            (matchedRoutine, processId) =>
            {
                lock (transitions)
                {
                    transitions.Add($"activate:{matchedRoutine.Id}:{processId}");
                }

                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor,
            (matchedRoutine, processId) =>
            {
                lock (transitions)
                {
                    transitions.Add($"deactivate:{matchedRoutine.Id}:{processId}");
                }

                completed.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(42, "Target", @"C:\Apps\Target.exe", "Target"));
        monitor.RaiseFocused(new WindowFocusEventArgs(84, "explorer", @"C:\Windows\explorer.exe", string.Empty));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        lock (transitions)
        {
            Assert.Equal(["activate:routine-1:42", "deactivate:routine-1:42"], transitions);
        }
    }

    [Fact]
    public async Task WindowFocus_MatchesPackagedAppByExecutablePath()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Spotify Store",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            3131,
            "Spotify",
            @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.0.0.0_x64__zpdnekdrzrea0\Spotify.exe",
            "Spotify"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(("routine-1", 3131), result);
    }

    [Fact]
    public async Task WindowFocus_MatchesSteamWebHelper_WhenTargetIsSteamExe()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Steam Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Program Files (x86)\Steam\steam.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            6672,
            "steamwebhelper",
            @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
            "Steam"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(("routine-1", 6672), result);
    }

    [Fact]
    public async Task WindowFocus_MatchesSquirrelAppExe_WhenTargetIsUpdateExe()
    {
        var monitor = new FakeWindowFocusMonitor();
        var executed = new TaskCompletionSource<(string RoutineId, int ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Discord Focus",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers"
            }
        ];

        using var coordinator = new ApplicationTriggerCoordinator(
            routines,
            (routine, processId) =>
            {
                executed.TrySetResult((routine.Id, processId));
                return Task.CompletedTask;
            },
            Logger.Instance,
            monitor);

        coordinator.Start();

        monitor.RaiseFocused(new WindowFocusEventArgs(
            3528,
            "Discord",
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9236\Discord.exe",
            "@ExampleUser - Discord"));

        (string RoutineId, int ProcessId) result = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(("routine-1", 3528), result);
    }

    [Fact]
    public void MatchesTitlePattern_PathologicalRegex_ReturnsFalse()
    {
        using var coordinator = new ApplicationTriggerCoordinator(
            [],
            (_, _) => Task.CompletedTask,
            Logger.Instance,
            new FakeWindowFocusMonitor());
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            ApplicationTriggerTitlePattern = "(a+)+$",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
        };
        string windowTitle = new string('a', 20_000) + "!";

        bool matched = coordinator.MatchesTitlePatternForTests(routine, windowTitle);

        Assert.False(matched);
    }

    [Fact]
    public void WindowFocus_RacingWithDispose_DoesNotReadDisposedShutdownTokenSource()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var monitor = new FakeWindowFocusMonitor();
            var routine = new AudioRoutine
            {
                Id = $"routine-{iteration}",
                Name = "Focus race",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
                TriggerAppPath = @"C:\Apps\Target.exe",
            };
            var coordinator = new ApplicationTriggerCoordinator(
                [routine],
                (_, _) => Task.CompletedTask,
                Logger.Instance,
                monitor);
            coordinator.Start();
            Exception? focusException = null;

            Parallel.Invoke(
                () =>
                {
                    try
                    {
                        monitor.RaiseFocused(new WindowFocusEventArgs(
                            Environment.ProcessId,
                            "Target",
                            @"C:\Apps\Target.exe",
                            $"Target {iteration}"));
                    }
                    catch (Exception ex)
                    {
                        focusException = ex;
                    }
                },
                coordinator.Dispose);

            Assert.Null(focusException);
        }
    }

    [Theory]
    [InlineData("Spotify - Daily Mix", "Spotify*", true)]
    [InlineData("Discord", "Spot?fy", false)]
    public void MatchesTitlePattern_Wildcard_UsesExpectedMatching(
        string windowTitle,
        string pattern,
        bool expected)
    {
        using var coordinator = new ApplicationTriggerCoordinator(
            [],
            (_, _) => Task.CompletedTask,
            Logger.Instance,
            new FakeWindowFocusMonitor());
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            ApplicationTriggerTitlePattern = pattern,
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Wildcard,
        };

        bool matched = coordinator.MatchesTitlePatternForTests(routine, windowTitle);

        Assert.Equal(expected, matched);
    }

    private sealed class FakeWindowFocusMonitor : IWindowFocusMonitor
    {
        public event EventHandler<WindowFocusEventArgs>? WindowFocused;

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public void Start()
        {
            StartCallCount++;
        }

        public void Stop()
        {
            StopCallCount++;
        }

        public void RaiseFocused(WindowFocusEventArgs args)
        {
            WindowFocused?.Invoke(this, args);
        }

        public void Dispose()
        {
        }
    }

    private static async Task WaitForExecutionCountAsync(List<(string RoutineId, int ProcessId)> executions, int expectedCount)
    {
        await TestExecutionGuards.WaitUntilAsync(
            () =>
            {
                lock (executions)
                {
                    return executions.Count >= expectedCount;
                }
            },
            $"Timed out waiting for {expectedCount} execution(s).",
            timeout: TimeSpan.FromSeconds(10));
    }

}
