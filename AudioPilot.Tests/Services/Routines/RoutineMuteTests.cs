using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineMuteTests
{
    private sealed class Endpoint(bool initial) : IRoutineMuteEndpoint
    {
        private bool _muted = initial;
        public int Writes;
        public int Disposals;
        public bool ThrowOnWrite;
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public Guid NotificationGuid { get; set; } = Guid.NewGuid();
        public event Action<Guid>? Changed;
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public bool Mute
        {
            get => _muted;
            set
            {
                if (ThrowOnWrite) throw new InvalidOperationException("Endpoint disconnected");
                _muted = value;
                Writes++;
                Changed?.Invoke(NotificationGuid);
            }
        }
        public void ExternalMute(bool muted) { _muted = muted; Changed?.Invoke(Guid.NewGuid()); }
        public void Dispose() => Disposals++;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Endpoint_RestoresOnlyItsChangeAndReleasesSubscription(bool capture)
    {
        var endpoint = new Endpoint(false);
        Guid originalContext = endpoint.NotificationGuid;
        var result = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, capture, "test");
        Assert.True(result.Success);
        Assert.True(endpoint.Mute);
        if (capture)
        {
            Assert.Equal(1, endpoint.Subscribers);
            using var restoration = new RoutineAudioRestoration([result.Change!], Logger.Instance);
            restoration.Complete(true);
            restoration.Complete(true);
            Assert.False(endpoint.Mute);
            Assert.Equal(2, endpoint.Writes);
        }
        else Assert.Null(result.Change);
        Assert.Equal(originalContext, endpoint.NotificationGuid);
        Assert.Equal(0, endpoint.Subscribers);
        Assert.Equal(1, endpoint.Disposals);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("hold")]
    [InlineData("deafen")]
    public void Restore_PreservesNewerChangesEvenWhenMuteReturnsToAppliedValue(string newer)
    {
        var endpoint = new Endpoint(true);
        string? blocked = null;
        var result = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, false, false, true, "test", (_, _) => blocked);
        if (newer == "manual") { endpoint.ExternalMute(true); endpoint.ExternalMute(false); }
        else blocked = newer;
        using var restoration = new RoutineAudioRestoration([result.Change!], Logger.Instance);
        restoration.Complete(true);
        Assert.False(endpoint.Mute);
        Assert.Equal(1, endpoint.Writes);
        Assert.Equal(0, endpoint.Subscribers);
    }

    [Fact]
    public void NewerRoutineNoOpStillSupersedesOlderRestoration()
    {
        var original = new Endpoint(false);
        var first = RoutineEndpointMuteService.Apply(() => original, Logger.Instance, true, true, true, "first");
        var newer = new Endpoint(true) { Id = original.Id };
        Assert.True(RoutineEndpointMuteService.Apply(() => newer, Logger.Instance, true, true, false, "manual").Success);
        using var restoration = new RoutineAudioRestoration([first.Change!], Logger.Instance);
        restoration.Complete(true);
        Assert.True(original.Mute);
        Assert.Equal(1, original.Writes);
        Assert.Equal(1, original.Disposals);
    }

    [Fact]
    public void Endpoint_BlockedNoOpAndFailedWritesDoNotRetainResources()
    {
        var blocked = RoutineEndpointMuteService.Apply(() => throw new InvalidOperationException("Must not open hardware"),
            Logger.Instance, false, false, true, "blocked", (_, _) => "Push-to-talk owns microphone");
        Assert.False(blocked.Success);
        var unchanged = new Endpoint(true);
        Assert.Null(RoutineEndpointMuteService.Apply(() => unchanged, Logger.Instance, true, true, true, "noop").Change);
        Assert.Equal(0, unchanged.Writes);
        Assert.Equal(1, unchanged.Disposals);
        var failing = new Endpoint(false) { ThrowOnWrite = true };
        Assert.Throws<InvalidOperationException>(() => RoutineEndpointMuteService.Apply(() => failing, Logger.Instance, true, true, true, "failed"));
        Assert.Equal(0, failing.Subscribers);
        Assert.Equal(1, failing.Disposals);
    }

    [Fact]
    public async Task Executor_UsesResolvedEndpointAndAppliesMuteAfterLevels()
    {
        var calls = new List<string>();
        var operations = Operations() with
        {
            GetActiveDevices = _ => [new() { Id = "fresh", StableId = "stable", Name = "Speaker" }],
            ApplyVolume = (_, id, _, _) => { calls.Add($"volume:{id}"); return true; },
            ApplyMute = (playback, id, mute, capture, _) =>
            {
                Assert.True(playback); Assert.True(mute); Assert.False(capture);
                calls.Add($"mute:{id}"); return new(true);
            },
        };
        var routine = new AudioRoutine { OutputDeviceId = "old", OutputDeviceStableId = "stable", MasterVolumePercent = 35, OutputMuteAction = RoutineMuteAction.Mute };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.True(result.OutputMuteSucceeded);
        Assert.Null(result.InputMuteSucceeded);
        Assert.Equal(["volume:fresh", "mute:fresh"], calls);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("deafen")]
    [InlineData("failed-switch")]
    [InlineData("invalid")]
    public async Task Executor_SkipsUnrequestedOrUnsafeMuteWrites(string mode)
    {
        var routine = new AudioRoutine { OutputMuteAction = mode == "invalid" ? (RoutineMuteAction)999 : mode == "unchanged" ? RoutineMuteAction.Unchanged : RoutineMuteAction.Unmute };
        if (mode == "failed-switch") routine.OutputDeviceId = "out";
        int writes = 0;
        var operations = Operations() with
        {
            SwitchDefaultAsync = _ => ValueTask.FromResult((false, (string?)null)),
            ApplyMute = (_, _, _, _, _) => { writes++; return new(true); },
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(Deafen: mode == "deafen"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, writes);
        Assert.Equal(mode == "unchanged", result.Success);
        if (mode != "unchanged") Assert.NotNull(result.MuteFailureDetail);
    }

    [Fact]
    public async Task PartialMuteFailure_RetainsSuccessfulRestorationAndReportsReason()
    {
        var endpoint = new Endpoint(false);
        var operations = Operations() with
        {
            ApplyMute = (playback, _, _, capture, _) => playback
                ? RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, capture, "partial")
                : new(false, FailureDetail: "Push-to-talk owns microphone"),
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new() { OutputMuteAction = RoutineMuteAction.Mute, InputMuteAction = RoutineMuteAction.Unmute },
            new(CaptureAudioRestoration: true), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.True(result.HasPartialSuccess);
        Assert.True(result.OutputMuteSucceeded);
        Assert.False(result.InputMuteSucceeded);
        Assert.Contains("Push-to-talk", result.MuteFailureDetail!);
        Assert.True(endpoint.Mute);
        Assert.NotNull(result.AudioRestoration);
        result.AudioRestoration.Complete(true);
        Assert.False(endpoint.Mute);
        Assert.Equal(1, endpoint.Disposals);
    }

    [Fact]
    public async Task ExplicitMuteOwnership_StopsLegacyVolumeRestorationFromWritingMute()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            RestorePreviousAudioOnDeactivate = true,
            MasterVolumePercent = 35,
            MicVolumePercent = 45,
            OutputMuteAction = RoutineMuteAction.Mute,
            InputMuteAction = RoutineMuteAction.Unmute,
        };
        var snapshot = AudioPilot.Coordinators.AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(routine,
            () => new("output", "Speaker", 70, false), () => new("input", "Mic", 80, true), Logger.Instance);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Value.PreviousOutputMuted);
        Assert.Null(snapshot.Value.PreviousInputMuted);
        var restored = new List<float>();
        var session = new RoutineStatefulSession("session", routine.Id, routine.Name, routine.TriggerKind, 1, true, snapshot);
        await AudioPilot.Coordinators.AppRoutineRestoreCoordinator.ExecuteRestoreAsync(session,
            new((_, _) => null, () => null, (_, _, _) => Task.CompletedTask, (_, _) => null, (_, _, _) => Task.CompletedTask,
                RestoreOutputVolumeAsync: (level, mute, _) => { Assert.Null(mute); restored.Add(level); return Task.CompletedTask; },
                RestoreInputVolumeAsync: (level, mute, _) => { Assert.Null(mute); restored.Add(level); return Task.CompletedTask; }), Logger.Instance);
        Assert.Equal([70f, 80f], restored);
    }

    [Fact]
    public async Task Executor_CancellationRestoresOwnedChangeAndReleasesSubscription()
    {
        using var cancellation = new CancellationTokenSource();
        var endpoint = new Endpoint(false);
        var operations = Operations() with
        {
            ApplyMute = (_, _, _, capture, _) =>
            {
                var result = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, capture, "cancel");
                cancellation.Cancel();
                return result;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(
            new() { OutputMuteAction = RoutineMuteAction.Mute, InputMuteAction = RoutineMuteAction.Unmute }, new(CaptureAudioRestoration: true), cancellationToken: cancellation.Token));
        Assert.False(endpoint.Mute);
        Assert.Equal(2, endpoint.Writes);
        Assert.Equal(1, endpoint.Disposals);
        Assert.Equal(0, endpoint.Subscribers);
    }

    [Fact]
    public async Task Lifetime_OrTriggersShareMuteOwnershipUntilLastDeactivation()
    {
        var endpoint = new Endpoint(false);
        var mute = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, true, "or");
        var restoration = new RoutineAudioRestoration([mute.Change!], Logger.Instance);
        var routine = new AudioRoutine { Id = "or", TriggerKind = RoutineTriggerKind.Application, TriggerAppPath = "game.exe", RestorePreviousAudioOnDeactivate = true, OutputMuteAction = RoutineMuteAction.Mute };
        var lifetime = new RoutineLifetimeService();
        lifetime.Register(routine, 42, new("", "", "", "", AudioRestoration: restoration), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(lifetime.TryJoinActiveRoutine(routine, 43, null, true, TestContext.Current.CancellationToken));
        foreach (var ending in lifetime.CaptureDeactivations(_ => true))
        {
            await lifetime.DeactivateAsync(ending, (_, _) => Task.CompletedTask);
            Assert.Equal(lifetime.Sessions.Count != 0, endpoint.Mute);
        }
        Assert.Equal(2, endpoint.Writes);
        Assert.Equal(1, endpoint.Disposals);
    }

    [Fact]
    public async Task Lifetime_ReplacedAndSupersededChangesReleaseWithoutRestoring()
    {
        var endpoint = new Endpoint(false);
        var mute = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, true, "old");
        var routine = new AudioRoutine { Id = "old", TriggerKind = RoutineTriggerKind.Application, RestorePreviousAudioOnDeactivate = true };
        var lifetime = new RoutineLifetimeService();
        lifetime.Register(routine, 42, new("", "", "", "", AudioRestoration: new([mute.Change!], Logger.Instance)), cancellationToken: TestContext.Current.CancellationToken);
        lifetime.Register(routine, 42, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, endpoint.Disposals);
        await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(_ => true)), (_, _) => Task.CompletedTask);
        Assert.True(endpoint.Mute);
        Assert.Equal(1, endpoint.Writes);
    }

    [Fact]
    public void Editor_CloneJsonNormalizationAndCliPreserveMuteOnlyRoutine()
    {
        using var editor = new RoutineEditorViewModel([], [])
        {
            Name = "Quiet",
            ShowInTrayMenu = true,
            OutputMuteAction = RoutineMuteAction.Mute,
            InputMuteAction = RoutineMuteAction.Unmute,
        };
        Assert.Null(editor.Validate());
        AudioRoutine routine = editor.BuildRoutine().Clone();
        Assert.True(routine.HasExecutionTarget);
        Assert.False(routine.HasVolumeTarget);
        var imported = RoutineTransferService.ParseSingleRoutine(JsonSerializer.Serialize(routine, SettingsJson.Options));
        var settings = new Settings { Routines = new() { Items = [imported] } };
        SettingsValidationService.Normalize(settings);
        using var reopened = new RoutineEditorViewModel([], [], Assert.Single(settings.Routines.Items));
        Assert.True(reopened.IsVolumeTargetsExpanded);
        Assert.Equal(RoutineMuteAction.Mute, reopened.OutputMuteAction);
        Assert.Equal(RoutineMuteAction.Unmute, reopened.InputMuteAction);
        Assert.Contains("Output mute: Mute", CliOutputFormatter.FormatRoutineDetails(imported, false));
        Assert.Contains("Microphone: Unmute", CliOutputFormatter.FormatRoutineRunResult(imported, null, null, false));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AudioRoutine>("{\"InputMuteAction\":999}", SettingsJson.Options));
    }

    private static RoutineExecutionOperations Operations() => new(
        _ => [], (_, target) => target,
        (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
        (_, _, _, _, _) => Task.CompletedTask,
        request => ValueTask.FromResult((true, (string?)request.Target.Name)),
        (_, target, _, _) => ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Applied, target.Name)),
        (_, _, _, _) => true);
}
