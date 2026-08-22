using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class ApplicationTriggerCoordinatorTests
{
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
    public async Task WindowFocus_WhenSameProcessFocusesDifferentTitle_TriggersAgain()
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
                ApplicationTriggerTitlePattern = "Discord",
                ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains,
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
            28776,
            "Discord",
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Voice - Discord"));

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
