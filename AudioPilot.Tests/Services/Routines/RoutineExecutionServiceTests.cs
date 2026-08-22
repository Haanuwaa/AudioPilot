using System.Collections.Concurrent;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineExecutionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SlowAudioAction_DoesNotBlockCallingDispatcher(bool hasCondition)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var operations = CreateOperations() with
            {
                IsApplicationRunning = _ => true,
                ApplyVolume = (_, _, _, _) =>
                {
                    entered.TrySetResult();
                    return release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                },
            };
            Task<bool> responsive = Task.Run(async () =>
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Task
                        .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                    return true;
                }
                catch (TimeoutException) { return false; }
                finally { release.Set(); }
            }, TestContext.Current.CancellationToken);
            var routine = new AudioRoutine
            {
                MasterVolumePercent = 40,
                Conditions = new() { RunningAppPath = hasCondition ? @"C:\Apps\Player.exe" : string.Empty },
            };
            RoutineExecutionResult result;
            try
            {
                result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
                    routine, new(), cancellationToken: TestContext.Current.CancellationToken);
            }
            finally
            {
                release.Set();
                await responsive;
            }
            Assert.True(await responsive, "The routine blocked input dispatch while waiting for an audio operation.");
            Assert.True(result.Success);
        });
    }

    [Fact]
    public async Task TimeCondition_GatesBeforeHardwareQueriesAndAudioWrites()
    {
        int queries = 0, writes = 0;
        var operations = CreateOperations() with
        {
            UtcNow = () => new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            GetActiveDevices = _ => { queries++; return [new() { Id = "out" }]; },
            ApplyVolume = (_, _, _, _) => { writes++; return true; },
        };
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 50,
            Conditions = new()
            {
                Device = new() { Id = "out" },
                TimeWindow = new() { Start = new(22, 0), End = new(2, 0), TimeZoneId = "UTC" },
            }
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("routine-condition-outside-time-window", result.SkipCode);
        Assert.Equal(0, queries);
        Assert.Equal(0, writes);
        routine.Conditions = routine.Conditions with { TimeWindow = routine.Conditions.TimeWindow with { AllDay = true } };
        result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(1, queries);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("exact", false)]
    [InlineData("stable", false)]
    [InlineData("ambiguous", false)]
    [InlineData("failure", false)]
    public async Task UnavailableDeviceCondition_RequiresConfirmedAbsenceBeforeChangingAudio(string state, bool expected)
    {
        int writes = 0;
        var operations = CreateOperations() with
        {
            GetActiveDevices = playback =>
            {
                Assert.False(playback);
                return state switch
                {
                    "exact" => [new() { Id = "mic" }],
                    "stable" => [new() { Id = "new", StableId = "stable" }],
                    "ambiguous" => [new() { Id = "a", StableId = "stable" }, new() { Id = "b", StableId = "stable" }],
                    "failure" => throw new InvalidOperationException("Device enumeration unavailable"),
                    _ => [],
                };
            },
            ApplyVolume = (_, _, _, _) => { writes++; return true; },
        };
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 40,
            Conditions = new() { Device = new() { Id = "mic", StableId = "stable", Playback = false }, DeviceAvailable = false },
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected ? 1 : 0, writes);
        Assert.Equal(expected ? null : state is "failure" or "ambiguous" ? "routine-condition-unavailable" : "routine-condition-device-available", result.SkipCode);
    }

    [Theory]
    [InlineData("device", "routine-condition-device-unavailable")]
    [InlineData("app", "routine-condition-app-not-running")]
    [InlineData("network", "routine-condition-network-disconnected")]
    [InlineData("unknown", "routine-condition-unavailable")]
    [InlineData("throw", "routine-condition-unavailable")]
    public async Task Conditions_FailClosedBeforeAnyAudioAction(string failing, string code)
    {
        int writes = 0;
        var operations = CreateOperations() with
        {
            GetActiveDevices = _ => failing == "device" ? [] : [new() { Id = "out" }],
            IsApplicationRunning = _ => failing == "throw" ? throw new InvalidOperationException("query failed") : failing != "app",
            GetConnectedNetworks = () => failing == "unknown" ? null : failing == "network" ? [] : ["Home"],
            SwitchDefaultAsync = _ => { writes++; return ValueTask.FromResult((true, (string?)"out")); },
            ReconnectAsync = (_, _, _, _, _) => { writes++; return Task.FromResult(default(BluetoothReconnectAttemptResult)); },
            ApplyVolume = (_, _, _, _) => { writes++; return true; },
        };
        var routine = new AudioRoutine
        {
            OutputDeviceId = "out",
            MasterVolumePercent = 50,
            Conditions = new()
            { Device = new() { Id = "out" }, RunningAppPath = @"C:\Apps\Game.exe", ConnectedNetwork = "Home" }
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Skipped);
        Assert.False(result.Success);
        Assert.Equal(code, result.SkipCode);
        Assert.NotEmpty(result.SkipReason!);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Conditions_AllPassOrCancelBeforeActions_AndUnusedQueriesStayLazy()
    {
        using var cancellation = new CancellationTokenSource();
        int writes = 0;
        int queries = 0;
        bool cancel = false;
        var operations = CreateOperations() with
        {
            GetActiveDevices = _ => [new() { Id = "new", StableId = "stable" }],
            IsApplicationRunning = _ => { queries++; if (cancel) cancellation.Cancel(); return true; },
            GetConnectedNetworks = () => ["home"],
            ApplyVolume = (_, _, _, _) => { writes++; return true; },
        };
        var routine = new AudioRoutine { MasterVolumePercent = 40 };
        var executor = new RoutineExecutionService(operations, Logger.Instance);
        Assert.True((await executor.ExecuteAsync(routine, new(), cancellationToken: cancellation.Token)).Success);
        Assert.Equal(0, queries);
        routine.Conditions = new() { Device = new() { Id = "old", StableId = "stable" }, RunningAppPath = @"C:\Apps\Player.exe", ConnectedNetwork = "HOME" };
        Assert.True((await executor.ExecuteAsync(routine, new(), cancellationToken: cancellation.Token)).Success);
        Assert.Equal(1, queries);
        cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(routine, new(), cancellationToken: cancellation.Token));
        Assert.Equal(2, writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task AppRouting_WithoutTargetProcess_HasNoAudioSideEffects(int? processId)
    {
        var operations = CreateOperations() with
        {
            GetActiveDevices = _ => throw new InvalidOperationException("Unexpected endpoint lookup."),
            SwitchDefaultAsync = _ => throw new InvalidOperationException("Unexpected system switch."),
            SwitchApplicationAsync = (_, _, _, _) => throw new InvalidOperationException("Unexpected app switch."),
            ApplyVolume = (_, _, _, _) => throw new InvalidOperationException("Unexpected volume change."),
        };
        var routine = new AudioRoutine { SwitchOutputPerApp = true, TargetAppPath = "missing.exe", OutputDeviceId = "out", InputDeviceId = "in", MasterVolumePercent = 50 };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), processId, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal("Target application is not running.", result.OutputFailureDetail);
        Assert.Equal("Target application is not running.", result.InputFailureDetail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ResolvesStableIdentity_AndAppliesVolumeToResolvedEndpoint(bool playback)
    {
        var requests = new List<RoutineDeviceSwitchRequest>();
        var volumes = new List<(bool Playback, string? Id, int Percent)>();
        var operations = CreateOperations() with
        {
            GetActiveDevices = _ => [new CycleDevice { Id = "fresh-id", Name = "Renamed device", StableId = "stable" }],
            SwitchDefaultAsync = request => { requests.Add(request); return ValueTask.FromResult((true, (string?)request.Target.Name)); },
            ApplyVolume = (output, id, percent, _) => { volumes.Add((output, id, percent)); return true; },
        };
        var routine = new AudioRoutine
        {
            OutputDeviceId = playback ? "old-id" : "",
            InputDeviceId = playback ? "" : "old-id",
            OutputDeviceStableId = playback ? "stable" : null,
            InputDeviceStableId = playback ? null : "stable",
            MasterVolumePercent = playback ? 35 : null,
            MicVolumePercent = playback ? null : 45,
        };
        RoutineExecutionResult result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            routine, new(PreserveAudioLevels: true), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        RoutineDeviceSwitchRequest request = Assert.Single(requests);
        Assert.Equal("fresh-id", request.Target.Id);
        Assert.True(request.Options.PreserveAudioLevels);
        Assert.False(request.RestoreMasterVolume);
        Assert.False(request.RestoreMicVolume);
        Assert.Equal((playback, "fresh-id", playback ? 35 : 45), Assert.Single(volumes));
        Assert.Equal("old-id", playback ? routine.OutputDeviceId : routine.InputDeviceId);
    }

    [Fact]
    public async Task ExecuteAsync_ReconnectRefreshesIdentity_BeforeSwitchAndVolume()
    {
        bool reconnected = false;
        string? switchedId = null;
        string? volumeId = null;
        var operations = CreateOperations() with
        {
            GetActiveDevices = _ => reconnected ? [new CycleDevice { Id = "new-id", Name = "Speaker", StableId = "stable" }] : [],
            FindActiveDevice = (_, _) => null,
            ReconnectAsync = (_, _, _, _, _) => { reconnected = true; return Task.FromResult(new BluetoothReconnectAttemptResult(true, true, 1, 0)); },
            SwitchDefaultAsync = request => { switchedId = request.Target.Id; return ValueTask.FromResult((true, (string?)"Speaker")); },
            ApplyVolume = (_, id, _, _) => { volumeId = id; return true; },
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { OutputDeviceId = "old-id", OutputDeviceStableId = "stable", MasterVolumePercent = 25 },
            new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.True(result.OutputReconnectAttempted);
        Assert.True(result.OutputReconnectSucceeded);
        Assert.Equal("new-id", switchedId);
        Assert.Equal("new-id", volumeId);
    }

    [Fact]
    public async Task ExecuteAsync_OutputException_DoesNotSuppressInputOrExplicitVolumes()
    {
        var volumes = new List<bool>();
        var operations = CreateOperations() with
        {
            SwitchDefaultAsync = request => request.Playback ? throw new InvalidOperationException("gone") : ValueTask.FromResult((true, (string?)"Mic")),
            ApplyVolume = (playback, _, _, _) => { volumes.Add(playback); return true; },
        };
        RoutineExecutionResult result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { OutputDeviceId = "out", InputDeviceId = "in", MasterVolumePercent = 40, MicVolumePercent = 50 },
            new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.False(result.OutputSucceeded);
        Assert.True(result.InputSucceeded);
        Assert.True(result.HasPartialSuccess);
        Assert.Equal("Output switch threw InvalidOperationException.", result.OutputFailureDetail);
        Assert.Equal([true, false], volumes);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeException_IsReported_AndOtherVolumeStillApplies()
    {
        var operations = CreateOperations() with
        {
            ApplyVolume = (playback, _, _, _) => playback ? throw new InvalidOperationException("gone") : true,
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { MasterVolumePercent = 40, MicVolumePercent = 50 }, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.False(result.MasterVolumeSucceeded);
        Assert.True(result.MicVolumeSucceeded);
        Assert.Null(result.OutputSucceeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_DeferredRouting_RequiresALongLivedHost(bool allowDeferred)
    {
        var operations = CreateOperations() with
        {
            SwitchApplicationAsync = (_, _, _, _) => ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.DeferredNoAudio, "Speaker")),
            SwitchDefaultAsync = _ => throw new InvalidOperationException("Per-app routing must not change defaults"),
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { OutputDeviceId = "out", TriggerKind = RoutineTriggerKind.Application, TriggerAppPath = @"C:\Apps\Player.exe", SwitchOutputPerApp = true }, new(AllowDeferredRouting: allowDeferred),
            processId: 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(allowDeferred, result.Success);
        Assert.Equal(allowDeferred, result.AwaitingAppCompletion);
        Assert.False(result.AppOutputApplied);
        Assert.Contains("pending", result.OutputFailureDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessDisappears_DoesNotFallBackToSystemDeviceSwitch()
    {
        var operations = CreateOperations() with
        {
            SwitchApplicationAsync = (_, _, _, _) => ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null)),
            SwitchDefaultAsync = _ => throw new InvalidOperationException("Unexpected default switch"),
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { OutputDeviceId = "out", InputDeviceId = "in", TriggerKind = RoutineTriggerKind.Application, TriggerAppPath = @"C:\Apps\Player.exe", SwitchOutputPerApp = true }, new(),
            processId: 42, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.False(result.HasPerAppRoutingContinuation);
        Assert.Equal("Failed to apply per-app output routing.", result.OutputFailureDetail);
        Assert.Equal("Failed to apply per-app input routing.", result.InputFailureDetail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_CancellationBeforeOrAfterOutput_PreventsRemainingEffects(bool cancelBefore)
    {
        using var cancellation = new CancellationTokenSource();
        int writes = 0;
        var operations = CreateOperations() with
        {
            SwitchDefaultAsync = request => { writes++; cancellation.Cancel(); return ValueTask.FromResult((true, (string?)"Speaker")); },
            ApplyVolume = (_, _, _, _) => { writes++; return true; },
        };
        if (cancelBefore) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new AudioRoutine { OutputDeviceId = "out", InputDeviceId = "in", MasterVolumePercent = 40 }, new(), cancellationToken: cancellation.Token));
        Assert.Equal(cancelBefore ? 0 : 1, writes);
    }

    [Fact]
    public async Task ExecuteAsync_OverlappingExecutions_KeepIndependentSnapshotsAndResults()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumes = new ConcurrentQueue<(string? Id, int Percent)>();
        var operations = CreateOperations() with
        {
            SwitchDefaultAsync = async request =>
            {
                if (request.Target.Id == "first")
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
                return (true, request.Target.Id);
            },
            ApplyVolume = (_, id, percent, _) => { volumes.Enqueue((id, percent)); return true; },
        };
        var service = new RoutineExecutionService(operations, Logger.Instance);
        var first = new AudioRoutine { OutputDeviceId = "first", MasterVolumePercent = 20 };
        Task<RoutineExecutionResult> pending = service.ExecuteAsync(first, new(), cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.OutputDeviceId = "edited";
        first.MasterVolumePercent = 99;
        RoutineExecutionResult second;
        try
        {
            second = await service.ExecuteAsync(new AudioRoutine { OutputDeviceId = "second", MasterVolumePercent = 60 },
                new(), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally { release.TrySetResult(); }
        RoutineExecutionResult completed = await pending;
        Assert.True(completed.Success);
        Assert.Equal("first", completed.OutputDeviceName);
        Assert.Equal("second", second.OutputDeviceName);
        Assert.Equal(new (string?, int)[] { ("second", 60), ("first", 20) }, volumes.ToArray());
    }

    private static RoutineExecutionOperations CreateOperations() => new(
        _ => [],
        (_, target) => target,
        (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
        (_, _, _, _, _) => Task.CompletedTask,
        request => ValueTask.FromResult((true, (string?)request.Target.Name)),
        (_, target, _, _) => ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Applied, target.Name)),
        (_, _, _, _) => true);
}
