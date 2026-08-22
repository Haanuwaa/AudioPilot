using System.Text.Json;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Tests.Services.Routines;

public sealed class MultipleRoutineTriggerTests
{
    [Fact]
    public void FilteredExpansion_PreservesIdentityValidationLimitAndCopyIsolation()
    {
        AudioRoutine routine = CreateRoutine();
        routine.Triggers = [new() { Id = "shared", Kind = RoutineTriggerKind.AudioPilotStartup },
            new() { Id = "shared", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Duplicate.exe" },
            new() { Id = "focus", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Player.exe", ApplicationMode = ApplicationTriggerMode.ProcessFocus }];
        for (int index = 3; index < 16; index++) routine.Triggers.Add(new() { Id = $"schedule-{index}", Kind = RoutineTriggerKind.Scheduled });
        routine.Triggers.Add(new() { Id = "over-limit", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Extra.exe" });
        AudioRoutine projected = Assert.Single(routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.Application));
        Assert.Equal("routine/focus", projected.RuntimeTriggerKey);
        projected.OutputDeviceId = "changed";
        projected.TriggerAppPath = @"C:\Other\Player.exe";
        Assert.Equal("out", routine.OutputDeviceId);
        Assert.Equal(@"C:\Apps\Player.exe", routine.Triggers[2].AppPath);
    }

    [Fact]
    public void Triggers_RoundTripWithIndependentIdsAndSharedActions()
    {
        var routine = CreateRoutine();
        var loaded = JsonSerializer.Deserialize<AudioRoutine>(JsonSerializer.Serialize(routine))!;
        Assert.Null(loaded.ValidateTriggers());
        AudioRoutine[] triggers = [.. loaded.ExpandAutomaticTriggers()];
        Assert.Equal(2, triggers.Length);
        Assert.Equal(["routine/first", "routine/steam"], triggers.Select(static trigger => trigger.RuntimeTriggerKey));
        Assert.All(triggers, trigger => { Assert.Equal("routine", trigger.Id); Assert.Equal("out", trigger.OutputDeviceId); Assert.True(trigger.RestorePreviousAudioOnDeactivate); Assert.Single(trigger.Triggers); });
        triggers[0].OutputDeviceId = "edited";
        Assert.Equal("out", loaded.OutputDeviceId);
        Assert.Equal(RoutineTriggerKind.Application, loaded.TriggerKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingTriggers_ApplyOnceAndRestoreOnceAfterTheLastEnds(bool reverse)
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine[] triggers = [.. CreateRoutine().ExpandAutomaticTriggers()];
        var original = new RoutineAudioRestoreSnapshot("original", "Speakers", "", "");
        int writes = 0;
        async Task Activate(AudioRoutine routine, int pid) => await lifetime.RunActivationAsync(routine, pid, token =>
        {
            if (!lifetime.TryJoinActiveRoutine(routine, pid, null, true, token))
            {
                writes++;
                lifetime.Register(routine, pid, original, cancellationToken: token);
            }
            return Task.FromResult(true);
        }, cancellationToken: TestContext.Current.CancellationToken);
        await Activate(triggers[0], 42);
        await Activate(triggers[1], 0);
        await Activate(triggers[1], 0);
        Assert.Equal(1, writes);
        Assert.Equal(2, lifetime.Sessions.Count);
        Assert.Single(lifetime.Sessions.Select(static session => session.ActivationSequence).Distinct());
        int restored = 0;
        var endings = lifetime.CaptureDeactivations(static _ => true).ToArray();
        if (reverse) Array.Reverse(endings);
        foreach (var ending in endings)
        {
            await lifetime.DeactivateAsync(ending, (session, restore) =>
            {
                if (restore) { restored++; Assert.Equal(original, session.RestoreSnapshot); }
                return Task.CompletedTask;
            });
        }
        Assert.Equal(1, restored);
        Assert.Empty(lifetime.Sessions);
    }

    [Fact]
    public async Task JoinedTrigger_DoesNotBecomeNewerThanAnotherRoutinesAudioChoice()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine[] triggers = [.. CreateRoutine().ExpandAutomaticTriggers()];
        lifetime.Register(triggers[0], 42, null, cancellationToken: TestContext.Current.CancellationToken);
        var other = triggers[0].Clone();
        other.Id = "other";
        lifetime.Register(other, 99, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(lifetime.TryJoinActiveRoutine(triggers[1], 0, null, true, TestContext.Current.CancellationToken));
        var restored = new List<string>();
        foreach (var ending in lifetime.CaptureDeactivations(static _ => true))
            await lifetime.DeactivateAsync(ending, (session, restore) => { if (restore) restored.Add(session.RoutineId); return Task.CompletedTask; });
        Assert.Equal("other", Assert.Single(restored));
    }

    [Fact]
    public void RemovingOneTrigger_KeepsTheOtherSessionAndItsOriginalSnapshot()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine[] triggers = [.. CreateRoutine().ExpandAutomaticTriggers()];
        var snapshot = new RoutineAudioRestoreSnapshot("original", "Speakers", "", "");
        lifetime.Register(triggers[0], 42, snapshot, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(lifetime.TryJoinActiveRoutine(triggers[1], 0, null, true, TestContext.Current.CancellationToken));
        var invalid = Assert.Single(lifetime.Synchronize([], [triggers[1]]));
        Assert.Equal(RoutineTriggerKind.Application, invalid.Session.TriggerKind);
        Assert.Equal(snapshot, lifetime.Sessions.Single(static session => session.TriggerKind == RoutineTriggerKind.SteamBigPicture).RestoreSnapshot);
    }

    [Fact]
    public void CancelledJoin_DoesNotAddASession_AndChangedActionsDoNotJoin()
    {
        var lifetime = new RoutineLifetimeService();
        AudioRoutine[] triggers = [.. CreateRoutine().ExpandAutomaticTriggers()];
        lifetime.Register(triggers[0], 42, null, cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => lifetime.TryJoinActiveRoutine(triggers[1], 0, null, true, cancellation.Token));
        triggers[1].OutputDeviceId = "different";
        Assert.False(lifetime.TryJoinActiveRoutine(triggers[1], 0, null, true, TestContext.Current.CancellationToken));
        Assert.Single(lifetime.Sessions);
    }

    [Fact]
    public void ValidationRejectsDuplicateIdsConfigurationsAndInvalidTriggers()
    {
        var routine = CreateRoutine();
        routine.Triggers.Add(routine.Triggers[0].Copy());
        Assert.Contains("unique", routine.ValidateTriggers());
        routine.Triggers[2] = routine.Triggers[2] with { Id = "other" };
        Assert.Contains("duplicate", routine.ValidateTriggers());
        routine.Triggers[2] = new RoutineTrigger { Id = "other", Kind = RoutineTriggerKind.Application, AppPath = "missing" };
        Assert.Contains("full .exe path", routine.ValidateTriggers());
    }

    [Fact]
    public void ValidationRejectsNetworkDuplicatesUsingTheSameComparisonAsNetworkMatching()
    {
        var routine = new AudioRoutine
        {
            Triggers = [new() { Kind = RoutineTriggerKind.Network, NetworkName = "Home" },
                new() { Kind = RoutineTriggerKind.Network, NetworkName = " home " }],
        };
        Assert.Contains("duplicate", routine.ValidateTriggers());
        routine.Triggers[1] = routine.Triggers[1] with { NetworkDirection = NetworkTriggerDirection.Disconnect };
        Assert.Null(routine.ValidateTriggers());
    }

    [Fact]
    public void OneShotSelectors_IncludeTriggersWithoutChangingThePrimaryTrigger()
    {
        var routine = CreateRoutine();
        routine.Triggers = [.. routine.Triggers, new() { Id = "startup", Kind = RoutineTriggerKind.AudioPilotStartup },
            new() { Id = "devices", Kind = RoutineTriggerKind.DeviceChange }];
        Assert.Equal("routine/startup", Assert.Single(AudioPilot.ViewModels.AppViewModel.GetAudioPilotStartupTriggeredRoutinesForExecution([routine])).RuntimeTriggerKey);
        Assert.Equal("routine/devices", Assert.Single(AudioPilot.ViewModels.AppViewModel.GetDeviceChangeTriggeredRoutinesForExecution([routine])).RuntimeTriggerKey);
        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.False(routine.EnforceTargetsOnDeviceChange);
    }

    private static AudioRoutine CreateRoutine() => new()
    {
        Id = "routine",
        Name = "Desk",
        Enabled = true,
        OutputDeviceId = "out",
        Triggers = [new RoutineTrigger { Id = "first", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Player.exe" }, new() { Id = "steam", Kind = RoutineTriggerKind.SteamBigPicture }],
        RestorePreviousAudioOnDeactivate = true,
    };
}
